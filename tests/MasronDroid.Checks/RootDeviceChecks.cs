using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MasronDroid;

namespace MasronDroid.Checks;

internal static class RootDeviceChecks
{
    private sealed class Transport : IPlatformTools
    {
        internal string Mode = "device", Engine = "Magisk", Power = "0", Timeout = "30000";
        internal string InstalledCode = "2";
        internal bool Installed, Denied, StaleVersion, FailLog, HintRoot, GrantOnRequest;
        internal List<string> Calls = [];
        public bool Has(string tool) => true;
        public Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout)
        {
            string command = string.Join(" ", args);
            Calls.Add(command);
            if (command.Contains("RuntimeUid=") && GrantOnRequest && timeout >= TimeSpan.FromSeconds(90)) Denied = false;
            if (args[0] == "devices") return Task.FromResult(new ToolResult(true, "root-phone\t" + Mode + "\n"));
            if (args[0] != "-s" || args[1] != "root-phone") throw new Exception("Unscoped operation");
            string output = "";
            if (command.Contains("AwakePower=")) output = "AwakePower=" + Power + "\nAwakeTimeout=" + Timeout;
            else if (command.Contains("settings put"))
            {
                Power = Regex.Match(command, @"stay_on_while_plugged_in (\d+)").Groups[1].Value;
                Timeout = Regex.Match(command, @"screen_off_timeout (\d+)").Groups[1].Value;
            }
            else if (command.Contains("RuntimeUid=")) output = "RuntimeUid=" + (Denied ? "2000" : "0")
                + "\nRuntimeEngine=" + Engine + "\nRuntimeVersion=1.0\n__MASRON_MODULE__\nid=example.module\nname=Example\nversion=1.0\nversionCode=1\n__MASRON_END__\nRuntimeEnd=1";
            else if (command.Contains("HintMagisk=") && HintRoot) output = Engine == "Magisk" ? "HintMagisk=31.1:MAGISK:R" : "HintSu=2.0:KernelSU";
            else if (command.Contains("resolve-activity")) output = args[^1] + "/.MainActivity";
            else if (command.Contains("FoxBoot=")) output = "FoxBoot=1\nFoxVersion=R12\nTwrpCli=1";
            else if (command.Contains("ModuleData=")) output = "ModuleData=1";
            else if (command.Contains("Lines=$(wc")) output = "Boot=same\nLines=" + (Installed ? 30 : 10);
            else if (command.Contains("twrp install") || command.Contains("--install-module") || command.Contains("module install"))
                Installed = true;
            else if (command.Contains("tail -n")) output = "Updater process ended with RC=" + (FailLog ? "1" : "0");
            else if (command.Contains("for p in /data/adb/modules_update/"))
                output = "id=example.module\nversionCode=" + (StaleVersion ? "1" : InstalledCode);
            return Task.FromResult(new ToolResult(true, output));
        }
    }

    internal static async Task<int> Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        var target = new Target("adb", "root-phone", "device", "Simulated");
        var fake = new Transport();
        var bridge = new AndroidBridge(fake);
        var original = await bridge.ReadScreenPower(target);
        await bridge.SetScreenPower(target, new("7", "2147483647"), true);
        Check(fake.Power == "7" && fake.Timeout == "2147483647", "Screen-on settings apply without a mirror");
        await bridge.SetScreenPower(target, original, false);
        Check(fake.Power == "0" && fake.Timeout == "30000", "Disabling restores exact original power and timeout");
        try { await bridge.SetScreenPower(target, new("0; reboot", "30000"), false); throw new Exception("Unsafe setting accepted"); }
        catch (BridgeException ex) when (ex.Key == "InvalidValue") { Check(true, "Power-setting command injection is rejected"); }
        string folder = Directory.CreateTempSubdirectory("MasronDroid-root-devices-").FullName;
        string path = Path.Combine(folder, "module.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var item in new[] { ("module.prop", "id=example.module\nversion=2.0\nversionCode=2\n"),
                ("META-INF/com/google/android/update-binary", "#!/sbin/sh\n"),
                ("META-INF/com/google/android/updater-script", "#MAGISK\n") })
            { using var writer = new StreamWriter(zip.CreateEntry(item.Item1).Open()); writer.Write(item.Item2); }
        }
        try
        {
            foreach (var engine in new[] { "Magisk", "KernelSU", "APatch" })
            {
                fake = new() { Engine = engine };
                bridge = new(fake);
                var state = await bridge.ReadRootRuntime(target);
                Check(state.Engine == engine && state.Access && state.Modules.Single().Version == "1.0", engine + " version and installed module are detected");
                await bridge.InstallRootModule(target, path, "example.module");
                string expected = engine == "Magisk" ? "magisk --install-module" : engine == "KernelSU" ? "/data/adb/ksud module install" : "/data/adb/apd module install";
                Check(fake.Calls.Any(call => call.Contains(expected)), engine + " uses its own module installer");
            }
            fake = new() { Mode = "recovery" }; bridge = new(fake);
            await bridge.InstallRootModule(target with { State = "recovery" }, path, "example.module");
            Check(fake.Calls.Any(call => call.Contains("twrp install")), "Supported recovery uses ZIP flashing and fresh log verification");
            foreach (var engine in new[] { "Magisk", "KernelSU" })
            {
                fake = new() { Engine = engine, Denied = true, HintRoot = true };
                bridge = new(fake);
                var hint = await bridge.ReadRootRuntime(target);
                Check(hint.Engine == engine && !hint.Access && hint.Modules.Length == 0,
                    engine + " is detected before Shell root authorization without inventing installed modules");
                try { await bridge.InstallRootModule(target, path, "example.module"); throw new Exception("Denied root installed a module"); }
                catch (BridgeException ex) when (ex.Key == "ModuleRootDenied")
                { Check(!fake.Installed, engine + " explains manual Shell authorization without installing on denial"); }
                fake.GrantOnRequest = true;
                await bridge.InstallRootModule(target, path, "example.module");
                Check(fake.Installed && !fake.Denied, engine + " installs after granting the requested Shell root access");
            }
            fake = new(); bridge = new(fake);
            await bridge.OpenRootManager(target, "Magisk");
            Check(fake.Calls.Any(call => call.Contains("am start -n com.topjohnwu.magisk/.MainActivity"))
                && !fake.Calls.Any(call => call.Contains("su -c") || call.Contains("--sqlite")),
                "Opening the root manager never grants root or changes policies");
            foreach (var scenario in new[] { "denied", "stale", "failed-recovery" })
            {
                fake = new() { Denied = scenario == "denied", StaleVersion = scenario == "stale",
                    FailLog = scenario == "failed-recovery", Mode = scenario == "failed-recovery" ? "recovery" : "device" };
                try { await new AndroidBridge(fake).InstallRootModule(target with { State = fake.Mode }, path, "example.module"); throw new Exception("Failure accepted: " + scenario); }
                catch (BridgeException ex) when (ex.Key is "ModuleNeedsRoot" or "ModuleRootDenied" or "ModuleInstallUnconfirmed")
                { Check(true, scenario + " cannot report installation success"); }
            }
            try { await new AndroidBridge(new Transport()).InstallRootModule(target, path, "other.module"); throw new Exception("Wrong module accepted"); }
            catch (BridgeException ex) when (ex.Key == "ModuleUpdateMismatch") { Check(true, "Update module ID must match the installed module"); }
            var local = await RootingCatalog.InspectLocalModule(path);
            Check(local.Module.Id == "example.module" && local.Module.Version == "2.0" && local.Hash.Length == 64,
                "Local ZIP inspection supplies module identity, version and SHA-256");
            fake = new(); bridge = new(fake);
            try { await bridge.InstallRootModule(target, path, "example.module", new string('0', 64)); throw new Exception("Changed ZIP accepted"); }
            catch (BridgeException ex) when (ex.Key == "ModuleFileChanged")
            { Check(!fake.Installed && !fake.Calls.Any(call => call.Contains(" push ")), "Changed ZIP never reaches the phone"); }
            string decimalPath = Path.Combine(folder, "decimal.zip");
            using (var zip = ZipFile.Open(decimalPath, ZipArchiveMode.Create))
            { using var writer = new StreamWriter(zip.CreateEntry("module.prop").Open()); writer.Write("id=example.module\nname=Local example\nversion=Beta 8.3\nversionCode=8.3\n"); }
            local = await RootingCatalog.InspectLocalModule(decimalPath);
            fake = new() { InstalledCode = "8.3" };
            await new AndroidBridge(fake).InstallRootModule(target, decimalPath, local.Module.Id, local.Hash);
            Check(fake.Installed && local.Module.VersionCode == 0 && local.Module.Version == "Beta 8.3",
                "Nonstandard decimal versionCode is preserved without changing the original ZIP");
            fake = new() { InstalledCode = "8x3" };
            try { await new AndroidBridge(fake).InstallRootModule(target, decimalPath, local.Module.Id, local.Hash); throw new Exception("Unmatched decimal code accepted"); }
            catch (BridgeException ex) when (ex.Key == "ModuleInstallUnconfirmed")
            { Check(true, "Decimal version readback requires an exact match"); }
            return count;
        }
        finally { Directory.Delete(folder, true); }
    }
}
