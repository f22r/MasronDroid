using System.IO;
using System.Windows;

namespace MasronDroid;

public partial class MainWindow
{
    private readonly HashSet<string> screenAwakeApplied = [];
    private readonly Dictionary<string, DateTime> nextScreenAwake = [];
    private bool screenAwakeUpdating;

    private bool WantsScreenAwake(string serial) => preferences.ScreenAwakeChoices.GetValueOrDefault(serial, true);

    private async Task ApplyScreenAwake(Target target, bool enabled)
    {
        if (enabled)
        {
            if (!preferences.ScreenAwakeOriginal.ContainsKey(target.Serial))
            {
                preferences.ScreenAwakeOriginal[target.Serial] = await bridge.ReadScreenPower(target);
            }
            if (persistent) preferences.Save();
            await bridge.SetScreenPower(target, new("7", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)), true);
            screenAwakeApplied.Add(target.Serial);
        }
        else
        {
            if (preferences.ScreenAwakeOriginal.TryGetValue(target.Serial, out var original))
            {
                await bridge.SetScreenPower(target, original, false);
                preferences.ScreenAwakeOriginal.Remove(target.Serial);
                if (persistent) preferences.Save();
            }
            screenAwakeApplied.Remove(target.Serial);
        }
    }

    private async Task RefreshScreenAwake(IEnumerable<Target> targets)
    {
        if (busy || screenAwakeUpdating) return;
        var android = targets.Where(target => target is { Tool: "adb", State: "device" }).ToArray();
        screenAwakeApplied.RemoveWhere(serial => !android.Any(target => target.Serial == serial));
        screenAwakeUpdating = true;
        try
        {
            foreach (var target in android)
            {
                bool enabled = WantsScreenAwake(target.Serial);
                if (enabled && screenAwakeApplied.Contains(target.Serial)
                    || !enabled && !preferences.ScreenAwakeOriginal.ContainsKey(target.Serial)
                    || nextScreenAwake.GetValueOrDefault(target.Serial) > DateTime.UtcNow) continue;
                nextScreenAwake[target.Serial] = DateTime.UtcNow.AddSeconds(30);
                try { await ApplyScreenAwake(target, enabled); }
                catch (BridgeException ex) { ReportError(ex); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report("PrefsFailed"); }
            }
        }
        finally { screenAwakeUpdating = false; if (IsLoaded) Render(); }
    }

    private async void StayAwakeClick(object sender, RoutedEventArgs e)
    {
        if (busy || screenAwakeUpdating || Selected is not { Tool: "adb", State: "device" } target) return;
        bool enabled = StayAwake.IsChecked == true;
        await Work(async () =>
        {
            preferences.ScreenAwakeChoices[target.Serial] = enabled;
            if (persistent) preferences.Save();
            await ApplyScreenAwake(target, enabled);
            Report(enabled ? "StayAwakeEnabled" : "StayAwakeDisabled");
        });
    }
}
