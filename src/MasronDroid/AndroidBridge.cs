using System.IO;
using System.Text.RegularExpressions;

namespace MasronDroid;

public sealed class BridgeException(string key, string detail = "") : Exception(detail)
{
    public string Key => key;
}

public record Target(string Tool, string Serial, string State, string Model, string? Image = null, string ModelCode = "")
{
    public string SelectorText => ModelCode.Length > 0 ? ModelCode : Serial;

    public override string ToString() => SelectorText;
    public string Details => (Model.Length > 0 ? Model + "  ·  " : "") + Serial + "  ·  " + State;
}

public enum DeviceAction { AppSettings, ClearCache, RestoreBackup, Install, Uninstall, PackagesEnabled, PackagesDisabled, Disable, Enable, Bootloader, Recovery, System, Product, Sideload, Restart, Shutdown, ReadSlot, BootVersion, RecoveryLog, RecoveryStorage, RecoveryFiles, StartSideload, ReleaseMemory }
public record Discovery(IReadOnlyList<Target> Targets, IReadOnlyList<BridgeException> Errors);

public sealed partial class AndroidBridge(IPlatformTools tools)
{
    public event Action<string>? CommandStarting;
    public bool Has(string tool) => tools.Has(tool);

    public static List<Target> Parse(string tool, string output)
    {
        var targets = new List<Target>();
        foreach (var line in output.Split('\n'))
        {
            var words = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || !Regex.IsMatch(words[0], @"^[A-Za-z0-9][A-Za-z0-9._:\[\]%-]*$")) continue;
            if (tool == "fastboot" ? words[1] != "fastboot" :
                words[1] is not ("device" or "recovery" or "sideload" or "offline" or "unauthorized" or "rescue" or "bootloader" or "connecting" or "authorizing")) continue;
            var model = words.Skip(2).FirstOrDefault(x => x.StartsWith("model:", StringComparison.Ordinal));
            var code = words.Skip(2).FirstOrDefault(x => x.StartsWith("device:", StringComparison.Ordinal));
            var name = model?[6..].Replace('_', ' ') ?? "";
            var identity = name.Length == 0 ? (Name: "", Image: (string?)null) : DeviceCatalog.Match(name, code?[7..] ?? "");
            var modelCode = model?[6..] ?? code?[7..] ?? "";
            if (modelCode.StartsWith("SM_", StringComparison.OrdinalIgnoreCase))
                modelCode = "SM-" + modelCode[3..];
            targets.Add(new(tool, words[0], words[1], identity.Name, identity.Image, modelCode));
        }
        return targets;
    }

    public async Task<List<(string Instance, Target Device)>> DiscoverWireless()
    {
        var result = await tools.Run("adb", ["mdns", "services"], TimeSpan.FromSeconds(4));
        if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
        var services = new List<(string, Target)>();
        foreach (var line in result.Output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !System.Net.IPEndPoint.TryParse(fields[2], out var endpoint) || endpoint.Port == 0) continue;
            string? mode = fields[1].TrimEnd('.') switch
            {
                "_adb-tls-pairing._tcp" => "pair",
                "_adb-tls-connect._tcp" => "connect",
                _ => null
            };
            if (mode != null) services.Add((fields[0], new Target("wifi", fields[2], mode, "", null, "Wi-Fi  " + fields[2])));
        }
        return services;
    }

    public async Task Wireless(string address, string? code)
    {
        if (!System.Net.IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port == 0 ||
            !Regex.IsMatch(address, @"^(?:[0-9.]+|\[[0-9a-fA-F:%]+\]):[0-9]+$"))
            throw new BridgeException("WifiInvalidAddress");
        if (code != null && !Regex.IsMatch(code, @"\A[0-9]{6}\z"))
            throw new BridgeException("WifiCodeLabel");
        var result = await tools.Run("adb", code == null ? ["connect", address] : ["pair", address, code], TimeSpan.FromSeconds(code == null ? 8 : 30));
        var reply = result.Output.Trim();
        bool success = code != null ? reply.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)
            : Regex.IsMatch(reply, @"(?im)^(?:already )?connected to ");
        if (!result.Ok || !success)
            throw new BridgeException("ToolFailed", code == null ? reply : reply.Replace(code, "******"));
    }

    private async Task<List<Target>> List(string tool)
    {
        string[] args = tool == "adb" ? ["devices", "-l"] : ["devices"];
        var result = await tools.Run(tool, args, TimeSpan.FromSeconds(12));
        if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
        return Parse(tool, result.Output);
    }

    public async Task<Discovery> Discover()
    {
        var targets = new List<Target>();
        var errors = new List<BridgeException>();
        foreach (var tool in new[] { "adb", "fastboot" })
        {
            if (!Has(tool)) continue;
            try { targets.AddRange(await List(tool)); }
            catch (BridgeException ex) { errors.Add(ex); }
        }
        return new(targets, errors);
    }

    public static bool Allows(Target? target, DeviceAction action) => (target?.Tool, target?.State) switch
    {
        ("adb", "device") => action is not (DeviceAction.Product or DeviceAction.Sideload or DeviceAction.ReadSlot or DeviceAction.BootVersion or DeviceAction.RecoveryLog or DeviceAction.RecoveryStorage or DeviceAction.RecoveryFiles or DeviceAction.StartSideload),
        ("adb", "recovery") => action is DeviceAction.System or DeviceAction.Bootloader or DeviceAction.RecoveryLog or DeviceAction.RecoveryStorage or DeviceAction.RecoveryFiles or DeviceAction.StartSideload,
        ("adb", "sideload") => action == DeviceAction.Sideload,
        ("fastboot", "fastboot") => action is DeviceAction.Product or DeviceAction.ReadSlot or DeviceAction.BootVersion or DeviceAction.System or DeviceAction.Bootloader,
        _ => false
    };

    public static bool IsPackage(string value) => Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*\z");

    public static bool? ParseLock(string policy)
    {
        var flag = Regex.Match(policy, @"(?im)^\s*(?:mShowingLockscreen|mKeyguardShowing|isKeyguardShowing)=(true|false)\b");
        if (!flag.Success)
            flag = Regex.Match(policy, @"(?im)^\s*KeyguardServiceDelegate[^\n]*\n(?:[ 	]+[^\n]*\n){0,12}?[ 	]+showing=(true|false)\b");
        return flag.Success ? bool.Parse(flag.Groups[1].Value) : null;
    }

    public async Task<bool?> IsLocked(Target target)
    {
        try
        {
            var result = await tools.Run("adb", ["-s", target.Serial, "shell", "dumpsys", "window", "policy"], TimeSpan.FromSeconds(4));
            return result.Ok ? ParseLock(result.Output) : null;
        }
        catch (BridgeException) { return null; }
    }

    public async Task Navigate(Target target, string key)
    {
        if (target is not { Tool: "adb", State: "device" }) throw new BridgeException("WrongMode");
        string[] command = key switch
        {
            "Landscape" => ["wm", "user-rotation", "lock", "1"],
            "Portrait" => ["wm", "user-rotation", "lock", "0"],
            "Camera" => ["am", "start", "-a", "android.media.action.STILL_IMAGE_CAMERA"],
            "Power" => ["input", "keyevent", "26"],
            "Wake" => ["input", "keyevent", "224"],
            "VolumeUp" => ["input", "keyevent", "24"],
            "VolumeDown" => ["input", "keyevent", "25"],
            "Back" => ["input", "keyevent", "4"],
            "Home" => ["input", "keyevent", "3"],
            "Recent" => ["input", "keyevent", "187"],
            _ => throw new BridgeException("InvalidValue")
        };
        await VerifyTarget(target);
        if (key != "Wake" && await IsLocked(target) == true) throw new BridgeException("UnlockPhone");
        var result = await tools.Run("adb", ["-s", target.Serial, "shell", .. command], TimeSpan.FromSeconds(5));
        if (result.Output.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ToolFailed", result.Output);
        if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
    }

    public async Task<Dictionary<string, string>> ReadDeviceInfo(Target target)
    {
        const string query = """
            echo "Brand=$(getprop ro.product.brand)"
            echo "MarketName=$(getprop ro.product.marketname)"
            cpu=$(getprop ro.soc.model); [ -n "$cpu" ] || cpu=$(getprop ro.board.platform)
            echo "Hardware=$cpu"
            echo "PropFlashLocked=$(getprop ro.boot.flash.locked)"
            echo "PropDeviceState=$(getprop ro.boot.vbmeta.device_state)"
            echo "PropVerifiedBoot=$(getprop ro.boot.verifiedbootstate)"
            for f in /proc/bootconfig /proc/cmdline; do
              if [ -r "$f" ]; then
                tr ' ' '\n' < "$f" | grep -E '^androidboot\.(flash.locked|vbmeta.device_state|verifiedbootstate)=' | sed 's/^/KernelBoot=/'
              fi
            done
            echo "Android=$(getprop ro.build.version.release)"
            echo "ApiLevel=$(getprop ro.build.version.sdk)"
            echo "SecurityPatch=$(getprop ro.build.version.security_patch)"
            echo "BuildId=$(getprop ro.build.display.id)"
            echo "Rom=$(getprop ro.modversion)"
            echo "CrDroid=$(getprop ro.crdroid.version)"
            echo "Lineage=$(getprop ro.lineage.version)"
            echo "OneUi=$(getprop ro.build.version.oneui)"
            echo "Abi=$(getprop ro.product.cpu.abilist)"
            awk '/^MemTotal:/ {t=$2} /^MemAvailable:/ {a=$2} END {if(t && a) printf "Ram=%.1f / %.1f GiB\nRamPercent=%.0f\n",(t-a)/1048576,t/1048576,100*(t-a)/t}' /proc/meminfo
            df -k /data | awk 'NR>1 && $2+0>0 {printf "Storage=%.1f / %.1f GiB\nStorageRemaining=%.1f GiB\n",$3/1048576,$2/1048576,$4/1048576}'
            dumpsys battery | awk '$1=="health:" {print "BatteryHealth=" $2} $1=="mSavedBatteryAsoc:" {print "ASOC=" $2} $1=="mSavedBatteryBsoh:" {print "BSOH=" $2} $1=="Maximum" && $2=="capacity:" {print "FullCapacity=" $3} $1=="Design" && $2=="capacity:" {print "DesignCapacity=" $3} $1=="level:" {l=$2} $1=="scale:" {s=$2} END {if(s>0) printf "Battery=%d%%\n",100*l/s}'
            wm size | awk '/size:/ {v=$NF} END {if(v) print "Resolution=" v}'
            """;
        var values = await ReadFacts(target, query, TimeSpan.FromSeconds(10));
        var boot = ResolveBootState(values.GetValueOrDefault("KernelBoot", ""), values);
        values["BootState"] = boot.State;
        values["BootEvidence"] = boot.Detail;
        var build = values.GetValueOrDefault("BuildId", "");
        var noble = Regex.Match(build, @"(?:^|[._-])NOBLE([0-9]+(?:\.[0-9]+)*)(?:_([A-Za-z]+))?", RegexOptions.IgnoreCase);
        if (noble.Success)
            values["Rom"] = $"Noble ROM {noble.Groups[1].Value} {noble.Groups[2].Value}".TrimEnd();
        else if (values.TryGetValue("CrDroid", out var crDroid)) values["Rom"] = $"crDroid {crDroid}";
        else if (!values.ContainsKey("Rom"))
        {
            if (values.TryGetValue("Lineage", out var lineage)) values["Rom"] = $"LineageOS {lineage}";
            else if (int.TryParse(values.GetValueOrDefault("OneUi"), out int ui) && ui >= 10000)
                values["Rom"] = $"One UI {ui / 10000}.{ui / 100 % 100}" + (ui % 100 == 0 ? "" : $".{ui % 100}");
            else if (build.Length > 0) values["Rom"] = build;
        }
        return values;
    }

    public Task<Dictionary<string, string>> ReadAppTotals(Target target)
    {
        const string query = """
            app_group() {
                key=$1
                packages=$2
                count=$(printf '%s\n' "$packages" | grep -c '^package:')
                echo "$key=$count"
                paths=$(printf '%s\n' "$packages" | sed -n 's/^package://;s/=[^=]*$//p' | sort -u)
                [ -n "$paths" ] || { echo "${key}Size=0 MB"; return; }
                printf '%s\n' "$paths" | while IFS= read -r base; do
                    printf '%s\0' "$base"
                    for split in "${base%/*}"/split_*.apk; do [ -f "$split" ] && printf '%s\0' "$split"; done
                done | xargs -0 stat -c %s 2>&1 | awk -v key="$key" '/^[0-9]+$/ {sum+=$1; next} {bad=1} END {if (!bad) {mb=sum/1048576; if (mb>=1024) printf "%sSize=%.2f GB\n",key,mb/1024; else printf "%sSize=%.1f MB\n",key,mb}}'
            }
            user=$(pm list packages -f -3 --user 0) || exit 1
            system=$(pm list packages -f -s --user 0) || exit 1
            installed=$(printf '%s\n%s\n' "$user" "$system")
            app_group UserApps "$user"
            app_group SystemApps "$system"
            app_group TotalApps "$installed"
            disabled=$(pm list packages -f -d --user 0) && app_group DisabledApps "$disabled"
            known=$(pm list packages -u -f --user 0) || exit 1
            removed=$(printf '%s\n#\n%s\n' "$installed" "$known" | awk '/^#$/ {known=1;next} /^package:/ {name=$0; sub(/^.*=/,"",name); if (!known) active[name]=1; else if (!(name in active)) print}')
            app_group RemovedApps "$removed"
            """;
        return ReadFacts(target, query, TimeSpan.FromSeconds(30));
    }

    private async Task<Dictionary<string, string>> ReadFacts(Target target, string query, TimeSpan timeout)
    {
        var result = await tools.Run("adb", ["-s", target.Serial, "shell", query.ReplaceLineEndings("\n")], timeout);
        if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
        var values = new Dictionary<string, string>();
        foreach (var line in result.Output.Split('\n'))
        {
            var pair = line.Trim().Split('=', 2);
            if (pair.Length == 2 && pair[1].Length > 0)
                values[pair[0]] = pair[0] == "KernelBoot" && values.TryGetValue(pair[0], out var prior)
                    ? prior + "\n" + pair[1] : pair[1];
        }
        return values;
    }

    public async Task<string> ReadAppList(Target target, bool sizesOnly = false)
    {
        string remote = "/data/local/tmp/masrondroid-" + Guid.NewGuid().ToString("N") + ".jar";
        string local = Path.Combine(AppContext.BaseDirectory, "Assets", "app-inventory.jar");
        try
        {
            var push = await tools.Run("adb", ["-s", target.Serial, "push", local, remote], TimeSpan.FromSeconds(15));
            if (!push.Ok) throw new BridgeException("ToolFailed", push.Output);
            var list = await tools.Run("adb", ["-s", target.Serial, "shell",
                $"chmod 444 {remote} && CLASSPATH={remote} app_process / AppInventory{(sizesOnly ? " --sizes-only" : "")}"], TimeSpan.FromSeconds(90));
            if (!list.Ok) throw new BridgeException("ToolFailed", list.Output);
            return list.Output;
        }
        finally
        {
            try { await tools.Run("adb", ["-s", target.Serial, "shell", "rm", "-f", remote], TimeSpan.FromSeconds(4)); }
            catch (BridgeException) { }
        }
    }

    public async Task VerifyTarget(Target expected)
    {
        var current = await List(expected.Tool);
        if (current.Count(x => x.Serial == expected.Serial && x.State == expected.State) != 1)
            throw new BridgeException("TargetChanged");
    }

    internal static (string State, string Detail) ResolveBootState(string kernel, Dictionary<string, string> properties)
    {
        var kernelStates = Regex.Matches(kernel, @"androidboot\.(flash\.locked|vbmeta\.device_state|verifiedbootstate)\s*=\s*""?([a-z0-9]+)")
            .Select(match => Signal(match.Groups[1].Value, match.Groups[2].Value)).Where(state => state.Length > 0).Distinct().ToArray();
        var propertyStates = new[] {
            Signal("flash.locked", properties.GetValueOrDefault("PropFlashLocked", "")),
            Signal("vbmeta.device_state", properties.GetValueOrDefault("PropDeviceState", "")),
            Signal("verifiedbootstate", properties.GetValueOrDefault("PropVerifiedBoot", ""))
        }.Where(state => state.Length > 0).Distinct().ToArray();
        var states = kernelStates.Length > 0 ? kernelStates : propertyStates;
        string state = states.Length == 1 ? states[0] : "BootUnknown";
        string detail = (kernelStates.Length > 0 ? "Kernel boot parameters" : "Android properties")
            + "\n" + kernel + "\n"
            + string.Join("\n", properties.Where(pair => pair.Key.StartsWith("Prop", StringComparison.Ordinal)).Select(pair => pair.Key + "=" + pair.Value));
        if (kernelStates.Length > 0 && propertyStates.Any(value => value != state)) detail = "BootConflictHint\n" + detail;
        return (state, detail.Trim());

        static string Signal(string key, string value) => (key, value) switch
        {
            ("flash.locked", "0") or ("vbmeta.device_state", "unlocked") or ("verifiedbootstate", "orange") => "BootOpen",
            ("flash.locked", "1") or ("vbmeta.device_state", "locked") or ("verifiedbootstate", "green") or ("verifiedbootstate", "yellow") => "BootLocked",
            _ => ""
        };
    }

    public async Task<Dictionary<string, string>> ReadBootState(Target target)
    {
        if (target.Tool != "fastboot" || target.State != "fastboot") throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        var status = await tools.Run("fastboot", ["-s", target.Serial, "getvar", "unlocked"], TimeSpan.FromSeconds(5));
        string state = status.Ok ? Regex.Match(status.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?unlocked:\s*(yes|true|1|no|false|0)\s*$").Groups[1].Value.ToLowerInvariant() : "";
        var result = new Dictionary<string, string>
        {
            ["BootState"] = state switch
            {
                "yes" or "true" or "1" => "BootOpen",
                "no" or "false" or "0" => "BootLocked",
                _ => "BootUnknown"
            },
            ["CanUnlock"] = "0",
            ["OemUnlock"] = "OemUnknown"
        };
        var mode = await tools.Run("fastboot", ["-s", target.Serial, "getvar", "is-userspace"], TimeSpan.FromSeconds(5));
        bool bootloaderMode = mode.Ok && Regex.IsMatch(mode.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?is-userspace:\s*no\s*$");
        var ability = await tools.Run("fastboot", ["-s", target.Serial, "flashing", "get_unlock_ability"], TimeSpan.FromSeconds(5));
        var permission = ability.Ok ? Regex.Match(ability.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?get_unlock_ability:\s*([01])\s*$").Groups[1].Value : "";
        result["OemUnlock"] = permission switch { "1" => "OemAllowed", "0" => "OemBlocked", _ => "OemUnknown" };
        if (result["BootState"] == "BootLocked" && permission == "1" && bootloaderMode)
            result["CanUnlock"] = "1";
        return result;
    }

    public async Task<string> UnlockBootloader(Target target)
    {
        if (target.Tool != "fastboot" || target.State != "fastboot" || (await ReadBootState(target))["CanUnlock"] != "1")
            throw new BridgeException("UnlockUnavailable");
        await VerifyTarget(target);
        CommandStarting?.Invoke("fastboot -s " + target.Serial + " flashing unlock");
        var result = await tools.Run("fastboot", ["-s", target.Serial, "flashing", "unlock"], TimeSpan.FromMinutes(2));
        if (!result.Ok || result.Output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ToolFailed", result.Output);
        return result.Output;
    }

    public async Task<string[]> ReadFlashPartitions(Target target)
    {
        if (target.Tool != "fastboot" || target.State != "fastboot") throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        var info = await tools.Run("fastboot", ["-s", target.Serial, "getvar", "all"], TimeSpan.FromSeconds(20));
        if (!info.Ok) throw new BridgeException("ToolFailed", info.Output);
        if (Regex.IsMatch(info.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?unlocked:\s*(?:no|false|0)\s*$"))
            throw new BridgeException("FlashLocked");
        if ((await ReadBootState(target))["BootState"] != "BootOpen") throw new BridgeException("FlashUnverified");
        var matches = Regex.Matches(info.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?partition-size:((?:boot|init_boot|vendor_boot|recovery|dtbo|vbmeta|system|system_ext|product|vendor|odm)(?:_[ab])?):\s*(?:0x0*[1-9a-f][0-9a-f]*|0*[1-9][0-9]*)\s*$");
        var userspace = Regex.IsMatch(info.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?is-userspace:\s*yes\s*$");
        return matches.Select(m => m.Groups[1].Value).Where(name => userspace || !Regex.IsMatch(name, @"^(?:system|system_ext|product|vendor|odm)(?:_[ab])?$")
            || Regex.IsMatch(info.Output, @"(?im)^\s*(?:\(bootloader\)\s*)?is-logical:" + Regex.Escape(name) + @":\s*no\s*$")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<string> FlashImageFile(Target target, string partition, string source)
    {
        RequireFile(source, ".img");
        var folder = Path.Combine(AppContext.BaseDirectory, "FlashImages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, Path.GetFileName(source));
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (input.Length == 0) throw new BridgeException("InvalidFile");
            await input.CopyToAsync(output);
        }
        if (!(await ReadFlashPartitions(target)).Contains(partition, StringComparer.Ordinal))
            throw new BridgeException("FlashNoPartitions");
        CommandStarting?.Invoke($"fastboot -s {target.Serial} flash {partition} \"{copy}\"");
        var result = await tools.Run("fastboot", ["-s", target.Serial, "flash", partition, copy], TimeSpan.FromMinutes(15));
        if (!result.Ok || result.Output.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ToolFailed", result.Output);
        return result.Output;
    }

    internal static string BackupFolder => Path.Combine(AppContext.BaseDirectory, "Backups");
    private sealed record BackupManifest(string Package, string[] Hashes);

    private async Task<string> BackupCommand(Target target, params string[] args)
    {
        var result = await tools.Run("adb", ["-s", target.Serial, .. args], TimeSpan.FromMinutes(10));
        if (!result.Ok || result.Output.Contains("Failure [", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ToolFailed", result.Output);
        return result.Output.Trim();
    }

    private static async Task<string> HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file));
    }

    private async Task SaveBackup(Target target, string package, string? icon)
    {
        var output = await BackupCommand(target, "shell", "pm", "path", "--user", "0", package);
        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        if (paths.Length == 0 || paths.Any(line => !Regex.IsMatch(line, @"^package:/[A-Za-z0-9_./+=~@%-]+\.apk$")))
            throw new BridgeException("BackupIncomplete", package);
        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(BackupFolder, "." + id);
        string temporary = staging + ".partial";
        try
        {
            Directory.CreateDirectory(staging);
            var hashes = new List<string>();
            foreach (var line in paths)
            {
                string remote = line[8..];
                string hash = (await BackupCommand(target, "shell", "sha256sum", remote)).Split(' ')[0];
                string local = Path.Combine(staging, hashes.Count + ".apk");
                await BackupCommand(target, "pull", remote, local);
                if (!File.Exists(local) || !hash.Equals(await HashFile(local), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("BackupIncomplete", package);
                hashes.Add(hash);
            }
            if (!string.IsNullOrEmpty(icon))
                await File.WriteAllBytesAsync(Path.Combine(staging, "icon.png"), Convert.FromBase64String(icon));
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(new BackupManifest(package, hashes.ToArray())));
            await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(staging, temporary, System.IO.Compression.CompressionLevel.NoCompression, false));
            File.Move(temporary, Path.Combine(BackupFolder, $"{package}_{DateTime.Now:yyyyMMdd-HHmmss}_{id[..8]}.mdbackup"));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<string> RestoreBackup(Target target, string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(RequireFile(path, ".mdbackup"));
        using var manifest = archive.GetEntry("manifest.json")?.Open() ?? throw new InvalidDataException();
        var info = await System.Text.Json.JsonSerializer.DeserializeAsync<BackupManifest>(manifest);
        if (info == null || !IsPackage(info.Package ?? "") || info.Hashes is not { Length: > 0 and <= 512 })
            throw new InvalidDataException();
        string folder = Directory.CreateTempSubdirectory("MasronDroid-").FullName;
        try
        {
            var args = new List<string> { info.Hashes.Length == 1 ? "install" : "install-multiple", "-r", "--user", "0" };
            for (int i = 0; i < info.Hashes.Length; i++)
            {
                var name = i + ".apk";
                var file = Path.Combine(folder, name);
                using var input = archive.GetEntry(name)?.Open() ?? throw new InvalidDataException();
                using (var destination = File.Create(file)) await input.CopyToAsync(destination);
                if (!string.Equals(info.Hashes[i], await HashFile(file), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                args.Add(file);
            }
            await VerifyTarget(target);
            return await BackupCommand(target, args.ToArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    public async Task ClearCacheInSettings(Target target, string package, string appName)
    {
        if (!IsPackage(package) || target.Tool != "adb" || target.State != "device") throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        if (await IsLocked(target) != false) throw new BridgeException("UnlockPhone");
        await Execute(target, DeviceAction.AppSettings, package);
        var details = await ReadSettingsUi(target);
        if (!SettingsAppMatches(details, appName)) throw new BridgeException("CacheUiUnsupported");
        System.Xml.Linq.XElement? storage = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (FindClearCacheButton(details) != null) break;
            storage = details.Descendants("node").Where(node => (string?)node.Attribute("package") == "com.android.settings")
                .FirstOrDefault(node => (string?)node.Attribute("text") is "Penyimpanan & cache" or "Penyimpanan" or "Storage & cache" or "Storage"
                    && node.Ancestors("node").Any(parent => (string?)parent.Attribute("clickable") == "true"));
            if (storage != null) break;
            var scroll = details.Descendants("node").FirstOrDefault(node =>
                (string?)node.Attribute("package") == "com.android.settings" && (string?)node.Attribute("scrollable") == "true");
            if (scroll == null || attempt == 4) break;
            await ScrollSettingsNode(target, scroll);
            details = await ReadSettingsUi(target);
        }
        if (storage != null) await TapSettingsNode(target, storage);
        var screen = storage == null ? details : await ReadSettingsUi(target);
        if (!SettingsAppMatches(screen, appName)) throw new BridgeException("CacheUiUnsupported");
        var cache = FindClearCacheButton(screen);
        if (cache == null) throw new BridgeException("CacheUiUnsupported");
        if (SettingsCacheIsZero(screen)) return;
        if ((string?)cache.Attribute("enabled") == "false") throw new BridgeException("CacheUnconfirmed");
        await TapSettingsNode(target, cache);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var after = await ReadSettingsUi(target);
            if (!SettingsAppMatches(after, appName)) throw new BridgeException("CacheUiUnsupported");
            if (SettingsCacheIsZero(after)) return;
            await Task.Delay(250);
        }
        throw new BridgeException("CacheUnconfirmed");
    }

    private static bool SettingsAppMatches(System.Xml.Linq.XDocument screen, string appName) =>
        screen.Descendants("node").Any(node => (string?)node.Attribute("package") == "com.android.settings"
            && ((string?)node.Attribute("resource-id"))?.EndsWith(":id/entity_header_title") == true
            && string.Equals((string?)node.Attribute("text"), appName, StringComparison.Ordinal));

    private static System.Xml.Linq.XElement? FindClearCacheButton(System.Xml.Linq.XDocument screen) =>
        screen.Descendants("node").FirstOrDefault(node =>
            (string?)node.Attribute("package") == "com.android.settings"
            && ((string?)node.Attribute("resource-id") is { } id
                && (id.EndsWith(":id/action2") || id.EndsWith(":id/text2") || id.EndsWith(":id/button2")))
            && new[] { (string?)node.Attribute("text"), (string?)node.Attribute("content-desc") }
                .Any(label => string.Equals(label?.Trim(), "Clear cache", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(label?.Trim(), "Hapus cache", StringComparison.OrdinalIgnoreCase)));

    private static bool SettingsCacheIsZero(System.Xml.Linq.XDocument screen)
    {
        foreach (var node in screen.Descendants("node"))
        {
            string? id = (string?)node.Attribute("resource-id");
            if (id?.EndsWith(":id/cache_size") == true && IsZero((string?)node.Attribute("text"))) return true;
            if (!string.Equals((string?)node.Attribute("text"), "Cache", StringComparison.OrdinalIgnoreCase)) continue;
            var row = node.Parent;
            if (row != null && row.Descendants("node").Any(summary =>
                ((string?)summary.Attribute("resource-id"))?.EndsWith(":id/summary") == true
                && IsZero((string?)summary.Attribute("text")))) return true;
        }
        return false;

        static bool IsZero(string? value) => value != null
            && Regex.IsMatch(value.Trim(), @"^0(?:[.,]0+)?\s*(?:B|bytes?|[KMGT]i?B)$", RegexOptions.IgnoreCase);
    }

    private async Task<System.Xml.Linq.XDocument> ReadSettingsUi(Target target)
    {
        string remote = "/data/local/tmp/masrondroid-ui-" + Guid.NewGuid().ToString("N") + ".xml";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await VerifyTarget(target);
            var xml = await tools.Run("adb", ["-s", target.Serial, "shell",
                $"uiautomator dump --compressed {remote} >/dev/null && cat {remote}; rc=$?; rm -f {remote}; exit $rc"], TimeSpan.FromSeconds(15));
            if (xml.Ok)
            {
                try { return System.Xml.Linq.XDocument.Parse(xml.Output); }
                catch (System.Xml.XmlException) { }
            }
            if (await IsLocked(target) == true) throw new BridgeException("UnlockPhone");
            await Task.Delay(200);
        }
        throw new BridgeException("CacheUiUnsupported");
    }

    private async Task TapSettingsNode(Target target, System.Xml.Linq.XElement node)
    {
        if ((string?)node.Attribute("package") != "com.android.settings") throw new BridgeException("CacheUiUnsupported");
        var bounds = Regex.Match((string?)node.Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
        if (!bounds.Success) throw new BridgeException("CacheUiUnsupported");
        await VerifyTarget(target);
        int x = (int.Parse(bounds.Groups[1].Value) + int.Parse(bounds.Groups[3].Value)) / 2;
        int y = (int.Parse(bounds.Groups[2].Value) + int.Parse(bounds.Groups[4].Value)) / 2;
        var result = await tools.Run("adb", ["-s", target.Serial, "shell", "input", "tap", x.ToString(), y.ToString()], TimeSpan.FromSeconds(5));
        if (!result.Ok) throw new BridgeException("CacheUiUnsupported", result.Output);
        await Task.Delay(150);
    }

    private async Task ScrollSettingsNode(Target target, System.Xml.Linq.XElement node)
    {
        if ((string?)node.Attribute("package") != "com.android.settings"
            || (string?)node.Attribute("scrollable") != "true") throw new BridgeException("CacheUiUnsupported");
        var bounds = Regex.Match((string?)node.Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
        if (!bounds.Success) throw new BridgeException("CacheUiUnsupported");
        int left = int.Parse(bounds.Groups[1].Value), top = int.Parse(bounds.Groups[2].Value);
        int right = int.Parse(bounds.Groups[3].Value), bottom = int.Parse(bounds.Groups[4].Value);
        if (right <= left || bottom - top < 100) throw new BridgeException("CacheUiUnsupported");
        int x = (left + right) / 2, inset = (bottom - top) / 5;
        await VerifyTarget(target);
        var result = await tools.Run("adb", ["-s", target.Serial, "shell", "input", "swipe",
            x.ToString(), (bottom - inset).ToString(), x.ToString(), (top + inset).ToString(), "300"], TimeSpan.FromSeconds(5));
        if (!result.Ok) throw new BridgeException("CacheUiUnsupported", result.Output);
        await Task.Delay(150);
    }

    public async Task<string> InstallMagiskModule(Target target, string file)
    {
        if (target.Tool != "adb" || target.State != "device") throw new BridgeException("WrongMode");
        RequireFile(file, ".zip");
        await VerifyTarget(target);
        var root = await tools.Run("adb", ["-s", target.Serial, "shell", "su", "-c", "id -u"], TimeSpan.FromSeconds(30));
        if (!root.Ok || root.Output.Trim() != "0") throw new BridgeException("ModuleNeedsRoot");
        var magisk = await tools.Run("adb", ["-s", target.Serial, "shell", "su", "-c", "magisk -v"], TimeSpan.FromSeconds(10));
        if (!magisk.Ok || string.IsNullOrWhiteSpace(magisk.Output)) throw new BridgeException("ModuleNeedsRoot");
        string remote = "/data/local/tmp/masrondroid-module-" + Guid.NewGuid().ToString("N") + ".zip";
        try
        {
            var push = await tools.Run("adb", ["-s", target.Serial, "push", file, remote], TimeSpan.FromMinutes(2));
            if (!push.Ok) throw new BridgeException("ToolFailed", push.Output);
            await VerifyTarget(target);
            CommandStarting?.Invoke("magisk --install-module " + remote);
            var install = await tools.Run("adb", ["-s", target.Serial, "shell", "su", "-c", "magisk --install-module " + remote], TimeSpan.FromMinutes(5));
            if (!install.Ok) throw new BridgeException("ToolFailed", install.Output);
            return install.Output;
        }
        finally { await tools.Run("adb", ["-s", target.Serial, "shell", "rm", "-f", remote], TimeSpan.FromSeconds(5)); }
    }

    public async Task<(string State, string Version, bool AppInstalled, string AppVersion)> ReadMagiskStatus(Target target)
    {
        if (target.Tool != "adb" || target.State != "device") return ("RootStatusUnknown", "", false, "");
        await VerifyTarget(target);
        var result = await ReadFacts(target, """
            v=$(magisk -v 2>/dev/null); rc=$?
            if [ "$rc" = 0 ] && [ -n "$v" ]; then echo "Daemon=$v"; fi
            if pm path com.topjohnwu.magisk 2>/dev/null | grep -q '^package:'; then
              echo App=1
              dumpsys package com.topjohnwu.magisk 2>/dev/null | sed -n 's/^[[:space:]]*versionName=/AppVersion=/p' | head -n 1
            fi
            """, TimeSpan.FromSeconds(8));
        bool installed = result.GetValueOrDefault("App") == "1";
        string appVersion = result.GetValueOrDefault("AppVersion") ?? "";
        if (result.TryGetValue("Daemon", out var version)) return ("RootDaemonLoaded", version, installed, appVersion);
        return (installed ? "RootAppOnly" : "RootNotDetected", "", installed, appVersion);
    }

    public async Task<string?> ReadSupportedRecovery(Target target)
        => (await ReadRecoveryProfile(target))?.Name;

    public async Task<RecoveryProfile?> ReadRecoveryProfile(Target target)
    {
        if (target.Tool != "adb" || target.State != "recovery") return null;
        await VerifyTarget(target);
        var props = await ReadFacts(target, """
            echo "FoxBoot=$(getprop ro.orangefox.boot)"
            echo "FoxVersion=$(getprop ro.orangefox.version)"
            echo "TwrpBoot=$(getprop ro.twrp.boot)"
            echo "TwrpVersion=$(getprop ro.twrp.version)"
            echo "TwrpCli=$(command -v twrp >/dev/null 2>&1 && echo 1 || echo 0)"
            echo "PbrpVersion=$(getprop ro.pbrp.version)"
            echo "ShrpVersion=$(getprop ro.shrp.version)"
            echo "RecoveryName=$(getprop ro.recovery.name)"
            echo "RecoveryBuild=$(getprop ro.build.display.id)"
            echo "RecoveryMod=$(getprop ro.modversion)"
            """, TimeSpan.FromSeconds(5));
        if (props.GetValueOrDefault("FoxBoot") == "1" && props.TryGetValue("FoxVersion", out var fox))
            return new("OrangeFox " + fox, props.GetValueOrDefault("TwrpCli") == "1");
        foreach (var (key, label) in new[] { ("PbrpVersion", "PitchBlack"), ("ShrpVersion", "SkyHawk") })
            if (props.TryGetValue(key, out var version)) return new(label + " " + version, props.GetValueOrDefault("TwrpCli") == "1");
        if (props.GetValueOrDefault("TwrpBoot") == "1" && props.TryGetValue("TwrpVersion", out var twrp))
            return new("TWRP " + twrp, props.GetValueOrDefault("TwrpCli") == "1");
        string identity = string.Join(" ", new[] { "RecoveryName", "RecoveryBuild", "RecoveryMod" }.Select(key => props.GetValueOrDefault(key, "")));
        var family = Regex.Match(identity, @"(?i)lineage|infinity|crdroid|pixelos|pixelexperience|evolution|derpfest|pitchblack|skyhawk|redwolf");
        if (family.Success) return new(family.Value + " Recovery", props.GetValueOrDefault("TwrpCli") == "1");
        if (props.GetValueOrDefault("TwrpCli") == "1") return new("TWRP-compatible recovery", true);
        return null;
    }

    public async Task CheckCacheSupport(Target target)
    {
        await VerifyTarget(target);
        var help = await tools.Run("adb", ["-s", target.Serial, "shell", "pm", "help"], TimeSpan.FromSeconds(10));
        if (!help.Ok || !Regex.IsMatch(help.Output, @"(?m)^\s*clear\s+[^\r\n]*--cache-only"))
            throw new BridgeException("CacheUnsupported");
    }

    public async Task<string?> FindTransferredFlashable(Target target, string name, string expectedHash)
    {
        if (target is not { Tool: "adb", State: "device" or "recovery" }) throw new BridgeException("WrongMode");
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\.zip\z", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(expectedHash, @"\A[0-9a-fA-F]{64}\z")) throw new BridgeException("InvalidFile");
        await VerifyTarget(target);
        var values = await ReadFacts(target, $$"""
            count=0
            for f in /sdcard/Download/MasronDroid/????????/{{name}}; do
              [ -f "$f" ] || continue
              count=$((count + 1))
              [ "$count" -le 32 ] || break
              actual=$(sha256sum "$f" 2>/dev/null)
              actual=${actual%% *}
              if [ "$actual" = '{{expectedHash.ToLowerInvariant()}}' ]; then
                printf 'StoredFile=%s\n' "$f"
                break
              fi
            done
            """, TimeSpan.FromSeconds(20));
        await VerifyTarget(target);
        string? path = values.GetValueOrDefault("StoredFile");
        return path != null && Regex.IsMatch(path, @"\A/sdcard/Download/MasronDroid/[0-9a-fA-F]{8}/" + Regex.Escape(name) + @"\z")
            ? path : null;
    }

    public async Task<string> TransferFlashableToStorage(Target target, string file)
    {
        if (target is not { Tool: "adb", State: "device" or "recovery" }) throw new BridgeException("WrongMode");
        file = RequireFile(file, ".zip");
        string name = Path.GetFileName(file);
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\.zip\z", RegexOptions.IgnoreCase)
            || new FileInfo(file).Length == 0) throw new BridgeException("InvalidFile");
        await VerifyTarget(target);
        if (target.State == "recovery" && await ReadSupportedRecovery(target) == null)
            throw new BridgeException("RootRecoveryRequired");
        var storage = await tools.Run("adb", ["-s", target.Serial, "shell", """
            p=$(readlink -f /sdcard)
            case "$p" in /data/media|/data/media/*|/storage/*) ;; *) exit 1;; esac
            [ -d /sdcard ] && [ -w /sdcard ] || exit 1
            fs=$(df -k /sdcard | awk 'NR>1 && $2+0>0 {print $1; exit}')
            [ -n "$fs" ] && [ "$fs" != tmpfs ] && [ "$fs" != rootfs ] || exit 1
            echo StorageReady=1
            """.ReplaceLineEndings("\n")], TimeSpan.FromSeconds(10));
        if (!storage.Ok || !storage.Output.Contains("StorageReady=1"))
            throw new BridgeException(target.State == "device" ? "RootAndroidStorageUnavailable" : "RootStorageUnavailable");
        string? existing = await FindTransferredFlashable(target, name, await HashFile(file));
        if (existing != null) return existing;
        string folder = "/sdcard/Download/MasronDroid/" + Guid.NewGuid().ToString("N")[..8];
        string remote = folder + "/" + name, partial = remote + ".partial";
        async Task<string> Run(string[] args, TimeSpan timeout)
        {
            await VerifyTarget(target);
            var result = await tools.Run("adb", ["-s", target.Serial, .. args], timeout);
            if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
            return result.Output.Trim();
        }
        try
        {
            await Run(["shell", "mkdir", "-p", folder], TimeSpan.FromSeconds(10));
            await Run(["push", file, partial], TimeSpan.FromMinutes(2));
            var hash = await Run(["shell", "sha256sum", partial], TimeSpan.FromSeconds(20));
            if (!string.Equals(hash.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                await HashFile(file), StringComparison.OrdinalIgnoreCase)) throw new BridgeException("RootTransferHashFailed");
            await Run(["shell", "mv", partial, remote], TimeSpan.FromSeconds(10));
            return remote;
        }
        finally
        {
            try { await Run(["shell", "rm", "-f", partial], TimeSpan.FromSeconds(5)); }
            catch (BridgeException) { }
        }
    }

    private async Task<string> EnterSideload(Target target, TimeSpan? wait = null)
    {
        var recovery = await ReadRecoveryProfile(target);
        bool customRecovery = recovery?.TwrpCli == true;
        CommandStarting?.Invoke("adb -s " + target.Serial + (customRecovery ? " shell twrp sideload" : " reboot sideload"));
        string output = "";
        try
        {
            var result = await tools.Run("adb", customRecovery
                ? ["-s", target.Serial, "shell", "twrp", "sideload"]
                : ["-s", target.Serial, "reboot", "sideload"], TimeSpan.FromSeconds(5));
            output = result.Output;
        }
        catch (BridgeException ex) when (ex.Key == "TimedOut") { }
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool endpointRecovered = false;
        while (elapsed.Elapsed < (wait ?? TimeSpan.FromSeconds(30)))
        {
            var devices = await List("adb");
            var current = devices.SingleOrDefault(device => device.Serial == target.Serial);
            if (current?.State == "sideload") return output;
            if (customRecovery && !endpointRecovered && current?.State == "recovery" && elapsed.Elapsed >= TimeSpan.FromSeconds(2))
            {
                try
                {
                    var facts = await ReadFacts(current, """
                        if [ "$(getprop sys.usb.config)" = sideload ] && pidof minadbd >/dev/null && pidof adbd >/dev/null &&
                          tail -n 30 /tmp/recovery.log | grep -q 'cannot open control endpoint.*Device or resource busy'; then
                          echo SideloadEndpointConflict=1
                        fi
                        """, TimeSpan.FromSeconds(5));
                    if (facts.GetValueOrDefault("SideloadEndpointConflict") == "1")
                    {
                        await VerifyTarget(current);
                        endpointRecovered = true;
                        CommandStarting?.Invoke("Recovery USB: releasing normal adbd for active minadbd sideload");
                        await tools.Run("adb", ["-s", current.Serial, "shell",
                            "if [ \"$(getprop sys.usb.config)\" = sideload ] && pidof minadbd >/dev/null; then setprop ctl.stop adbd; fi"], TimeSpan.FromSeconds(5));
                    }
                }
                catch (BridgeException ex) when (ex.Key is "TimedOut" or "ToolFailed" or "TargetChanged") { }
            }
            if (current != null && current.State is not ("recovery" or "offline"))
                throw new BridgeException("SideloadNotReady", output);
            await Task.Delay(500);
        }
        throw new BridgeException("SideloadNotReady", output);
    }

    public static Dictionary<string, bool> ParseRunningApps(string output)
    {
        var apps = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(output, @"(?ms)^\s*\*APP\*[^\n]*\n.*?(?=^\s*\*APP\*|\z)"))
        {
            if (!Regex.IsMatch(block.Value, @"(?m)^\s*user #0\b")
                || !Regex.IsMatch(block.Value, @"(?m)^\s*pid=[1-9]\d*\b")) continue;
            var state = Regex.Match(block.Value, @"\bcurProcState=(\d+)\b");
            bool cached = state.Success && int.TryParse(state.Groups[1].Value, out int value) && value is >= 16 and <= 19;
            var packages = Regex.Match(block.Value, @"packageList=\{([^}]+)\}");
            foreach (var package in packages.Groups[1].Value.Split(',').Select(p => p.Trim()).Where(IsPackage))
                apps[package] = cached && apps.GetValueOrDefault(package, true);
        }
        return apps;
    }

    public async Task<Dictionary<string, bool>> ReadRunningApps(Target target)
    {
        if (target is not { Tool: "adb", State: "device" }) throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        var result = await tools.Run("adb", ["-s", target.Serial, "shell",
            """dumpsys activity processes | grep -E '^ACTIVITY MANAGER|^[[:space:]]*\*APP\*|^[[:space:]]*(user #|packageList=|pid=|curProcState=)'"""], TimeSpan.FromSeconds(12));
        if (!result.Ok || !result.Output.Contains("ACTIVITY MANAGER"))
            throw new BridgeException("RunningAppsUnavailable");
        return ParseRunningApps(result.Output);
    }

    private async Task<string> ReleaseAppMemory(Target target, string package)
    {
        if (!IsPackage(package)) throw new BridgeException("InvalidValue");
        var userApps = Packages(await Execute(target, DeviceAction.PackagesEnabled)).ToHashSet(StringComparer.Ordinal);
        var thirdParty = await tools.Run("adb", ["-s", target.Serial, "shell", "pm", "list", "packages", "-3", "--user", "0"], TimeSpan.FromSeconds(10));
        if (!thirdParty.Ok || !userApps.Contains(package) || !Packages(thirdParty.Output).Contains(package))
            throw new BridgeException("RamAppProtected");
        var running = await ReadRunningApps(target);
        if (!running.ContainsKey(package)) return "";
        if (!running[package]) throw new BridgeException("RamAppProtected");
        await VerifyTarget(target);
        CommandStarting?.Invoke("adb -s " + target.Serial + " shell am kill --user 0 " + package);
        var killed = await tools.Run("adb", ["-s", target.Serial, "shell", "am", "kill", "--user", "0", package], TimeSpan.FromSeconds(10));
        if (!killed.Ok) throw new BridgeException("ToolFailed", killed.Output);
        if ((await ReadRunningApps(target)).ContainsKey(package)) throw new BridgeException("RamReleaseUnconfirmed");
        return killed.Output;
    }

    public async Task<string> Execute(Target expected, DeviceAction action, string value = "", string? icon = null, bool cacheCapabilityChecked = false)
    {
        if (!Allows(expected, action)) throw new BridgeException("WrongMode");
        await VerifyTarget(expected);
        try
        {
            if (action == DeviceAction.ClearCache)
            {
                if (!IsPackage(value)) throw new BridgeException("InvalidValue");
                if (!cacheCapabilityChecked) await CheckCacheSupport(expected);
            }
            if (action == DeviceAction.RestoreBackup) return await RestoreBackup(expected, value);
            if (action == DeviceAction.ReleaseMemory) return await ReleaseAppMemory(expected, value);
            if (action == DeviceAction.StartSideload) return await EnterSideload(expected);
            if (action == DeviceAction.Uninstall && IsPackage(value))
            {
                await SaveBackup(expected, value, icon);
                await VerifyTarget(expected);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            throw new BridgeException("ToolFailed", ex.Message);
        }
        string[] args = action switch
        {
            // NEW_TASK | CLEAR_TASK prevents reusing another app's Settings screen and scroll position.
            DeviceAction.AppSettings when IsPackage(value) => ["shell", "am", "start", "-W", "-f", "0x10008000", "--user", "0", "-a", "android.settings.APPLICATION_DETAILS_SETTINGS", "-d", "package:" + value],
            DeviceAction.ClearCache => ["shell", "timeout", "8s", "pm", "clear", "--user", "0", "--cache-only", value],
            DeviceAction.Install => ["install", "-r", RequireFile(value, ".apk")],
            DeviceAction.Uninstall when IsPackage(value) => ["shell", "pm", "uninstall", "-k", "--user", "0", value],
            DeviceAction.Sideload => ["sideload", await StageSideload(expected, value)],
            DeviceAction.PackagesEnabled => ["shell", "pm", "list", "packages", "-e", "--user", "0"],
            DeviceAction.PackagesDisabled => ["shell", "pm", "list", "packages", "-d", "--user", "0"],
            DeviceAction.Disable or DeviceAction.Enable when IsPackage(value) =>
                ["shell", "pm", action == DeviceAction.Disable ? "disable-user" : "enable", "--user", "0", value],
            DeviceAction.RecoveryLog => ["shell", "cat", "/tmp/recovery.log"],
            DeviceAction.RecoveryStorage => ["shell", "df", "-h"],
            DeviceAction.RecoveryFiles => ["shell", "ls", "-la", "/"],
            DeviceAction.ReadSlot => ["getvar", "current-slot"],
            DeviceAction.BootVersion => ["getvar", "version-bootloader"],
            DeviceAction.Product => ["getvar", "product"],
            DeviceAction.Bootloader => ["reboot", "bootloader"],
            DeviceAction.Recovery => ["reboot", "recovery"],
            DeviceAction.System or DeviceAction.Restart => ["reboot"],
            DeviceAction.Shutdown => ["shell", "reboot", "-p"],
            _ => throw new BridgeException("InvalidValue")
        };
        CommandStarting?.Invoke(expected.Tool + " -s " + expected.Serial + " " + string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg)));
        var longTransfer = action is DeviceAction.Install or DeviceAction.Sideload;
        var result = await tools.Run(expected.Tool, ["-s", expected.Serial, .. args],
            TimeSpan.FromSeconds(longTransfer ? 600 : 20));
        if (action == DeviceAction.ClearCache && !result.Ok) throw new BridgeException("CacheUnconfirmed", result.Output);
        if (!result.Ok || Regex.IsMatch(result.Output, @"^(?:Failure\s*\[|Error:|Exception occurred)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            throw new BridgeException("ToolFailed", result.Output);
        if (action == DeviceAction.ClearCache && result.Output.Trim() != "Success") throw new BridgeException("ToolFailed", result.Output);
        if (action == DeviceAction.Uninstall)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var check = await tools.Run("adb", ["-s", expected.Serial, "shell", "pm", "list", "packages", "--user", "0", value], TimeSpan.FromSeconds(20));
            if (!check.Ok || !result.Output.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("PackageUnconfirmed", value);
            if (Packages(check.Output).Contains(value, StringComparer.Ordinal))
                throw new BridgeException("PackageStillInstalled", value);
        }
        if (action is DeviceAction.Disable or DeviceAction.Enable)
        {
            var verify = action == DeviceAction.Disable ? DeviceAction.PackagesDisabled : DeviceAction.PackagesEnabled;
            if (!Packages(await Execute(expected, verify)).Contains(value, StringComparer.Ordinal))
                throw new BridgeException("PackageUnconfirmed", value);
        }
        return result.Output;
    }

    private async Task<string> StageSideload(Target target, string path)
    {
        var source = RequireFile(path, ".zip");
        var folder = Path.Combine(AppContext.BaseDirectory, "SideloadPackages", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var staged = Path.Combine(folder, Path.GetFileName(source));
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length == 0) throw new BridgeException("InvalidFile");
                await input.CopyToAsync(output);
            }
            await VerifyTarget(target);
            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeException("ToolFailed", ex.Message);
        }
    }

    private static string RequireFile(string value, string extension)
    {
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value) ||
            !extension.Equals(Path.GetExtension(value), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidFile");
        return value;
    }

    public static string[] Packages(string output) => output.Split('\n').Select(s => s.Trim())
        .Where(s => s.StartsWith("package:", StringComparison.Ordinal)).Select(s => s[8..])
        .Where(IsPackage).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
