namespace MasronDroid;

public sealed record ScreenPowerSettings(string PluggedIn, string Timeout);

public sealed partial class AndroidBridge
{
    private static bool ValidPowerValue(string value) => value == "null"
        || int.TryParse(value, out int number) && number >= 0;

    public async Task<ScreenPowerSettings> ReadScreenPower(Target target)
    {
        if (target is not { Tool: "adb", State: "device" }) throw new BridgeException("WrongMode");
        await VerifyTarget(target);
        var facts = await ReadFacts(target, """
            echo "AwakePower=$(settings get global stay_on_while_plugged_in)"
            echo "AwakeTimeout=$(settings get system screen_off_timeout)"
            """, TimeSpan.FromSeconds(8));
        string power = facts.GetValueOrDefault("AwakePower", ""), timeout = facts.GetValueOrDefault("AwakeTimeout", "");
        if (!ValidPowerValue(power) || !ValidPowerValue(timeout)) throw new BridgeException("StayAwakeFailed");
        return new(power, timeout);
    }

    public async Task SetScreenPower(Target target, ScreenPowerSettings settings, bool wake)
    {
        if (target is not { Tool: "adb", State: "device" }) throw new BridgeException("WrongMode");
        if (!ValidPowerValue(settings.PluggedIn) || !ValidPowerValue(settings.Timeout)) throw new BridgeException("InvalidValue");
        await VerifyTarget(target);
        string Set(string space, string key, string value) => value == "null" ? $"settings delete {space} {key}"
            : $"settings put {space} {key} {value}";
        string query = Set("global", "stay_on_while_plugged_in", settings.PluggedIn) + "\n"
            + Set("system", "screen_off_timeout", settings.Timeout);
        CommandStarting?.Invoke("adb -s " + target.Serial + " shell " + query.Replace('\n', ' '));
        var changed = await tools.Run("adb", ["-s", target.Serial, "shell", query], TimeSpan.FromSeconds(8));
        if (!changed.Ok || await ReadScreenPower(target) != settings) throw new BridgeException("StayAwakeFailed", changed.Output);
        if (wake)
        {
            await VerifyTarget(target);
            var result = await tools.Run("adb", ["-s", target.Serial, "shell", "input", "keyevent", "224"], TimeSpan.FromSeconds(5));
            if (!result.Ok) throw new BridgeException("StayAwakeFailed", result.Output);
        }
    }
}
