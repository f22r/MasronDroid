using System.IO;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace MasronDroid;

internal sealed record RootRuntime(string Engine, string Version, bool Access, bool RecoveryInstall, RootModule[] Modules);

public sealed partial class AndroidBridge
{
    internal async Task<RootRuntime> ReadRootRuntime(Target target, bool requestAccess = false)
    {
        if (target is not { Tool: "adb", State: "device" or "recovery" }) throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        const string query = """
            echo "RuntimeUid=$(id -u)"
            if [ "$KSU" = true ] || [ -x /data/adb/ksud ]; then
              echo RuntimeEngine=KernelSU
              echo "RuntimeVersion=$(/data/adb/ksud -V 2>/dev/null)"
            elif [ "$APATCH" = true ] || [ -x /data/adb/apd ]; then
              echo RuntimeEngine=APatch
              echo "RuntimeVersion=$(/data/adb/apd -V 2>/dev/null)"
            elif [ -f /data/adb/magisk/util_functions.sh ]; then
              echo RuntimeEngine=Magisk
              echo "RuntimeVersion=$(sed -n 's/^MAGISK_VER=//p' /data/adb/magisk/util_functions.sh | head -n 1)"
            fi
            for folder in /data/adb/modules/* /data/adb/modules_update/*; do
              [ -f "$folder/module.prop" ] || continue
              echo __MASRON_MODULE__
              head -c 65536 "$folder/module.prop"
              echo
              [ -f "$folder/disable" ] && echo masronState=disabled
              [ -f "$folder/remove" ] && echo masronState=removed
              case "$folder" in /data/adb/modules_update/*) echo masronPending=1;; esac
              echo __MASRON_END__
            done
            echo RuntimeEnd=1
            """;
        string androidQuery = query.ReplaceLineEndings("\n");
        string script = target.State == "device" ? "su -c '" + androidQuery.Replace("'", "'\''") + "'" : androidQuery;
        ToolResult result;
        try { result = await tools.Run("adb", ["-s", target.Serial, "shell", script], TimeSpan.FromSeconds(requestAccess ? 90 : 30)); }
        catch (BridgeException ex) when (ex.Key == "TimedOut") { result = new(false, ex.Message); }
        if (!result.Ok || !Regex.IsMatch(result.Output, @"(?m)^RuntimeUid=0\r?$") || !result.Output.Contains("RuntimeEnd=1"))
        {
            if (target.State == "device")
            {
                await VerifyTarget(target);
                var hint = await ReadFacts(target, """
                    v=$(magisk -v 2>/dev/null)
                    [ -n "$v" ] && echo "HintMagisk=$v"
                    v=$(su -v 2>/dev/null)
                    echo "HintSu=$v"
                    """, TimeSpan.FromSeconds(8));
                string versionHint = hint.GetValueOrDefault("HintSu", "");
                string engineHint = Regex.IsMatch(versionHint, "KernelSU", RegexOptions.IgnoreCase) ? "KernelSU"
                    : Regex.IsMatch(versionHint, "APatch", RegexOptions.IgnoreCase) ? "APatch"
                    : hint.ContainsKey("HintMagisk") ? "Magisk" : "";
                if (engineHint.Length > 0)
                    return new(engineHint, engineHint == "Magisk" ? hint["HintMagisk"] : versionHint, false, false, []);
            }
            throw new BridgeException("ModuleNeedsRoot", result.Output);
        }
        string Value(string text, string key) => Regex.Match(text, @"(?m)^" + key + @"=(.*)\r?$").Groups[1].Value.Trim().Trim('"', '\'');
        string engine = Value(result.Output, "RuntimeEngine"), version = Value(result.Output, "RuntimeVersion");
        var modules = new List<RootModule>();
        foreach (Match block in Regex.Matches(result.Output, @"(?s)__MASRON_MODULE__\r?\n(.*?)__MASRON_END__"))
        {
            string text = block.Groups[1].Value, id = Value(text, "id");
            if (!Regex.IsMatch(id, @"^[a-zA-Z][a-zA-Z0-9._-]{0,127}$")) continue;
            _ = long.TryParse(Value(text, "versionCode"), out long code);
            string name = Value(text, "name"), state = Value(text, "masronState");
            modules.Add(new(name.Length > 0 ? name : id, Value(text, "description") + (state.Length > 0 ? " · " + state : ""),
                engine, "", 0, Value(text, "version"), id, code, Value(text, "updateJson"), true,
                Pending: Value(text, "masronPending") == "1"));
        }
        bool recovery = false;
        if (target.State == "recovery" && engine == "Magisk")
        {
            var profile = await ReadRecoveryProfile(target);
            var data = await ReadFacts(target, """
                echo "ModuleData=$(test -w /data/adb && test -r /data/adb/magisk/util_functions.sh && echo 1)"
                """, TimeSpan.FromSeconds(5));
            recovery = profile?.TwrpCli == true && data.GetValueOrDefault("ModuleData") == "1";
        }
        var merged = modules.GroupBy(module => module.Id).Select(group =>
        {
            var active = group.First();
            var pending = group.FirstOrDefault(module => module.Pending);
            return pending == null ? active : active with { Pending = true,
                Version = active == pending ? pending.Version : active.Version + " → " + pending.Version };
        }).ToArray();
        return new(engine, version, engine.Length > 0, recovery, merged);
    }

    internal async Task<string> InstallRootModule(Target target, string file, string expectedId, string? expectedHash = null)
    {
        if (!Regex.IsMatch(expectedId, @"^[a-zA-Z][a-zA-Z0-9._-]{0,127}$")) throw new BridgeException("InvalidValue");
        RequireFile(file, ".zip");
        string expectedCode;
        using (var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(file))
        {
            var prop = archive.Entries.SingleOrDefault(entry => entry.Key == "module.prop");
            if (prop == null || prop.Size > 65536) throw new BridgeException("InvalidFile");
            using var reader = new StreamReader(prop.OpenEntryStream());
            char[] buffer = new char[65537];
            int length = await reader.ReadBlockAsync(buffer);
            if (length > 65536) throw new BridgeException("InvalidFile");
            string metadata = new(buffer, 0, length);
            if (!Regex.IsMatch(metadata, @"(?m)^id=" + Regex.Escape(expectedId) + @"\r?$")) throw new BridgeException("ModuleUpdateMismatch");
            expectedCode = Regex.Match(metadata, @"(?m)^versionCode=([0-9]+(?:\.[0-9]+)*)\r?$").Groups[1].Value;
            if (expectedCode.Length == 0 || expectedCode.Length > 64) throw new BridgeException("InvalidFile");
        }
        var runtime = await ReadRootRuntime(target, true);
        if (!runtime.Access || target.State == "recovery" && !runtime.RecoveryInstall)
            throw new BridgeException(target.State == "device" ? "ModuleRootDenied" : "ModuleRecoveryUnavailable");
        string staged = await StageSideload(target, file);
        if (expectedHash != null)
        {
            await using var content = File.OpenRead(staged);
            string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(content));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ModuleFileChanged");
        }
        if (target.State == "recovery")
        {
            using var zip = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(staged);
            if (!zip.Entries.Any(entry => entry.Key == "META-INF/com/google/android/update-binary")
                || !zip.Entries.Any(entry => entry.Key == "META-INF/com/google/android/updater-script"))
                throw new BridgeException("ModuleNotFlashable");
        }
        string remote = (target.State == "recovery" ? "/tmp/" : "/data/local/tmp/") + "masrondroid-module-" + Guid.NewGuid().ToString("N") + ".zip";
        try
        {
            await VerifyTarget(target);
            var pushed = await tools.Run("adb", ["-s", target.Serial, "push", staged, remote], TimeSpan.FromMinutes(2));
            if (!pushed.Ok) throw new BridgeException("ToolFailed", pushed.Output);
            (string Boot, int Lines)? cursor = target.State == "recovery" ? await RecoveryCursor(target) : null;
            await VerifyTarget(target);
            string command = target.State == "recovery" ? "twrp install " + remote : runtime.Engine switch
            {
                "Magisk" => "magisk --install-module " + remote,
                "KernelSU" => "/data/adb/ksud module install " + remote,
                "APatch" => "/data/adb/apd module install " + remote,
                _ => throw new BridgeException("ModuleNeedsRoot")
            };
            CommandStarting?.Invoke(command);
            var installed = await tools.Run("adb", ["-s", target.Serial, "shell",
                target.State == "recovery" ? command : "su -c '" + command + "'"], TimeSpan.FromMinutes(5));
            if (!installed.Ok) throw new BridgeException("ToolFailed", installed.Output);
            if (cursor != null)
            {
                var after = await RecoveryCursor(target);
                if (after.Boot != cursor.Value.Boot || after.Lines <= cursor.Value.Lines) throw new BridgeException("ModuleInstallUnconfirmed", installed.Output);
                var log = await tools.Run("adb", ["-s", target.Serial, "shell", "tail", "-n", "+" + (cursor.Value.Lines + 1), "/tmp/recovery.log"], TimeSpan.FromSeconds(8));
                if (!log.Ok || !Regex.IsMatch(log.Output, @"Updater process ended with RC=0")
                    || Regex.IsMatch(log.Output, @"(?im)Error installing zip|Installation aborted|Updater process ended with (?:RC=[1-9]|ERROR:)"))
                    throw new BridgeException("ModuleInstallUnconfirmed", log.Output);
            }
            string check = "for p in /data/adb/modules_update/" + expectedId + "/module.prop /data/adb/modules/" + expectedId
                + "/module.prop; do [ -f \"$p\" ] && cat \"$p\"; done";
            var verify = await tools.Run("adb", ["-s", target.Serial, "shell",
                target.State == "recovery" ? check : "su -c '" + check + "'"], TimeSpan.FromSeconds(8));
            if (!verify.Ok || !Regex.IsMatch(verify.Output, @"(?m)^id=" + Regex.Escape(expectedId) + @"\r?$")
                || !Regex.IsMatch(verify.Output, @"(?m)^versionCode=" + Regex.Escape(expectedCode) + @"\r?$"))
                throw new BridgeException("ModuleInstallUnconfirmed", verify.Output);
            return installed.Output + Environment.NewLine + verify.Output;
        }
        finally
        {
            try { await tools.Run("adb", ["-s", target.Serial, "shell", "rm", "-f", remote], TimeSpan.FromSeconds(5)); }
            catch (BridgeException) { }
        }
    }

    internal async Task OpenRootManager(Target target, string engine)
    {
        if (target is not { Tool: "adb", State: "device" }) throw new BridgeException("WrongMode");
        string[] packages = engine switch
        {
            "Magisk" => ["com.topjohnwu.magisk"],
            "KernelSU" => ["com.rifsxd.ksunext", "me.weishu.kernelsu"],
            "APatch" => ["me.bmax.apatch"],
            _ => []
        };
        foreach (string package in packages)
        {
            await VerifyTarget(target);
            var resolved = await tools.Run("adb", ["-s", target.Serial, "shell", "cmd", "package", "resolve-activity",
                "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", package], TimeSpan.FromSeconds(8));
            string component = resolved.Output.Split('\n').Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith(package + "/", StringComparison.Ordinal)
                    && Regex.IsMatch(line, @"^[a-zA-Z0-9_.$]+/[a-zA-Z0-9_.$]+$")) ?? "";
            if (!resolved.Ok || component.Length == 0) continue;
            await VerifyTarget(target);
            var opened = await tools.Run("adb", ["-s", target.Serial, "shell", "am", "start", "-n", component], TimeSpan.FromSeconds(8));
            if (!opened.Ok || opened.Output.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ModuleManagerMissing", opened.Output);
            return;
        }
        throw new BridgeException("ModuleManagerMissing");
    }
}
