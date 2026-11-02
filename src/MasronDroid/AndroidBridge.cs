using System.IO;
using System.Text.RegularExpressions;

namespace MasronDroid;

public sealed class BridgeException(string key, string detail = "") : Exception(detail)
{
    public string Key => key;
}

public record Target(string Tool, string Serial, string State, string Model, string? Image = null)
{
    public override string ToString() => (Model.Length > 0 ? Model + "  ·  " : "") + Serial + "  ·  " + State;
}

public enum DeviceAction { Install, PackagesEnabled, PackagesDisabled, Disable, Enable, Bootloader, Recovery, System, Product, Sideload }
public record Discovery(IReadOnlyList<Target> Targets, IReadOnlyList<BridgeException> Errors);

public sealed class AndroidBridge(IPlatformTools tools)
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
            targets.Add(new(tool, words[0], words[1], identity.Name, identity.Image));
        }
        return targets;
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
        ("adb", "device") => action is not (DeviceAction.Product or DeviceAction.Sideload),
        ("adb", "recovery") => action is DeviceAction.System or DeviceAction.Bootloader,
        ("adb", "sideload") => action == DeviceAction.Sideload,
        ("fastboot", "fastboot") => action is DeviceAction.Product or DeviceAction.System or DeviceAction.Bootloader,
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
        var keycode = key switch { "Back" => "4", "Home" => "3", "Recent" => "187", _ => throw new BridgeException("InvalidValue") };
        await VerifyTarget(target);
        if (await IsLocked(target) == true) throw new BridgeException("UnlockPhone");
        var result = await tools.Run("adb", ["-s", target.Serial, "shell", "input", "keyevent", keycode], TimeSpan.FromSeconds(5));
        if (!result.Ok) throw new BridgeException("ToolFailed", result.Output);
    }

    public async Task VerifyTarget(Target expected)
    {
        var current = await List(expected.Tool);
        if (current.Count(x => x.Serial == expected.Serial && x.State == expected.State) != 1)
            throw new BridgeException("TargetChanged");
    }

    public async Task<string> Execute(Target expected, DeviceAction action, string value = "")
    {
        if (!Allows(expected, action)) throw new BridgeException("WrongMode");
        await VerifyTarget(expected);
        string[] args = action switch
        {
            DeviceAction.Install => ["install", "-r", RequireFile(value, ".apk")],
            DeviceAction.Sideload => ["sideload", RequireFile(value, ".zip")],
            DeviceAction.PackagesEnabled => ["shell", "pm", "list", "packages", "-e", "--user", "0"],
            DeviceAction.PackagesDisabled => ["shell", "pm", "list", "packages", "-d", "--user", "0"],
            DeviceAction.Disable or DeviceAction.Enable when IsPackage(value) =>
                ["shell", "pm", action == DeviceAction.Disable ? "disable-user" : "enable", "--user", "0", value],
            DeviceAction.Product => ["getvar", "product"],
            DeviceAction.Bootloader => ["reboot", "bootloader"],
            DeviceAction.Recovery => ["reboot", "recovery"],
            DeviceAction.System => ["reboot"],
            _ => throw new BridgeException("InvalidValue")
        };
        CommandStarting?.Invoke(expected.Tool + " -s " + expected.Serial + " " + string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg)));
        var longTransfer = action is DeviceAction.Install or DeviceAction.Sideload;
        var result = await tools.Run(expected.Tool, ["-s", expected.Serial, .. args],
            TimeSpan.FromSeconds(longTransfer ? 600 : 20));
        if (!result.Ok || Regex.IsMatch(result.Output, @"^(?:Failure\s*\[|Error:|Exception occurred)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            throw new BridgeException("ToolFailed", result.Output);
        if (action is DeviceAction.Disable or DeviceAction.Enable)
        {
            var verify = action == DeviceAction.Disable ? DeviceAction.PackagesDisabled : DeviceAction.PackagesEnabled;
            if (!Packages(await Execute(expected, verify)).Contains(value, StringComparer.Ordinal))
                throw new BridgeException("PackageUnconfirmed", value);
        }
        return result.Output;
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
