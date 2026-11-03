using System.Windows;
using System.Windows.Controls;

namespace MasronDroid;

public partial class MainWindow
{
    private Dictionary<string, bool> runningApps = new(StringComparer.Ordinal);
    private Target? runningAppsTarget;
    private DateTime nextRunningRead;
    private bool runningAppsReading, runningAppsKnown;

    private async Task RefreshRunningApps(Target? target, bool force = false)
    {
        if (runningAppsTarget != target)
        {
            runningAppsTarget = target;
            runningApps.Clear();
            runningAppsKnown = false;
            nextRunningRead = DateTime.MinValue;
        }
        if (target is not { Tool: "adb", State: "device" } || runningAppsReading
            || (!force && DateTime.UtcNow < nextRunningRead)) return;
        runningAppsReading = true;
        RenderActivity();
        nextRunningRead = DateTime.UtcNow.AddSeconds(10);
        try
        {
            var snapshot = await bridge.ReadRunningApps(target);
            if (Selected != target) return;
            runningApps = snapshot;
            runningAppsKnown = true;
        }
        catch (BridgeException)
        {
            if (Selected == target) runningAppsKnown = false;
        }
        finally
        {
            runningAppsReading = false;
            RenderActivity();
            if (IsLoaded) ApplyInventoryFilter();
        }
    }

    private void InventoryTabChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        foreach (var row in inventory) row.Checked = false;
        ApplyInventoryFilter();
        if (RunningAppsTab.IsChecked == true) _ = RefreshRunningApps(Selected, true);
    }

    private async void RefreshApplicationsClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" } target) return;
        await Work(async () =>
        {
            while (inventoryLoading || runningAppsReading) await Task.Delay(100);
            await RefreshRunningApps(target, true);
            await LoadInventory(target);
            await RefreshFacts(target, generation, true);
            Report("ApplicationsRefreshed");
        });
    }

    private async void CleanRamClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" } target) return;
        await Work(async () =>
        {
            while (inventoryLoading || runningAppsReading) await Task.Delay(100);
            if (inventory.Count == 0) await LoadInventory(target);
            await RefreshRunningApps(target, true);
        });
        if (Selected != target) return;
        NavInstall.IsChecked = true;
        RunningAppsTab.IsChecked = true;
        ApplyInventoryFilter();
        var candidates = inventory.Where(row => row.CanReleaseMemory).ToArray();
        if (candidates.Length == 0)
        {
            Report(runningAppsKnown ? "NoRamCandidates" : "RunningAppsUnavailable");
            return;
        }
        foreach (var row in inventory) row.Checked = row.CanReleaseMemory;
        ApplyInventoryFilter();
        BatchClick(BatchReleaseMemory, new RoutedEventArgs());
    }
}
