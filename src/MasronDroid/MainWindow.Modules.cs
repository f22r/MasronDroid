using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;

namespace MasronDroid;

public partial class MainWindow
{
    private Target? rootRuntimeTarget;
    private RootRuntime? rootRuntime;
    private bool rootRuntimeReading;
    private DateTime nextRootRuntimeRead;
    private readonly List<RootModule> installedRootModules = [];

    private bool RootModuleCompatible => rootRuntimeTarget == Selected && rootRuntime is { Access: true }
        && (Selected is { Tool: "adb", State: "device" } || Selected is { Tool: "adb", State: "recovery" } && rootRuntime.RecoveryInstall);
    private bool CanRequestModuleRoot => Selected is { Tool: "adb", State: "device" } &&
        (MagiskActive || rootRuntimeTarget == Selected && rootRuntime?.Engine is "Magisk" or "KernelSU" or "APatch");
    private bool CanInstallRootModule => !busy && (RootModuleCompatible || CanRequestModuleRoot);

    private async void RootManagerOpenClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" } target) return;
        string engine = rootRuntimeTarget == target ? rootRuntime?.Engine ?? "" : "";
        if (engine.Length == 0 && MagiskActive) engine = "Magisk";
        await Work(async () =>
        {
            await bridge.OpenRootManager(target, engine);
            Report("ModuleManagerOpened");
        });
    }

    private async void RootAccessCheckClick(object sender, RoutedEventArgs e)
    {
        if (busy || rootRuntimeReading || Selected is not { Tool: "adb", State: "device" } target) return;
        await Work(async () =>
        {
            OperationLabel.Text = T("ModuleCheckingDevice");
            await UpdateRootRuntime(target, true);
            Report(RootModuleCompatible ? "ModuleRootGranted" : "ModuleRootDenied");
        });
    }

    private async Task UpdateRootRuntime(Target? target, bool fresh = false)
    {
        if (rootRuntimeTarget != target)
        {
            rootRuntimeTarget = target;
            rootRuntime = null;
            installedRootModules.Clear();
            nextRootRuntimeRead = DateTime.MinValue;
            if (RootInstalledTab.IsChecked == true) RootHighlightsTab.IsChecked = true;
        }
        if (rootRuntimeReading || target is not { Tool: "adb", State: "device" or "recovery" }
            || !fresh && DateTime.UtcNow < nextRootRuntimeRead) return;
        rootRuntimeReading = true;
        nextRootRuntimeRead = DateTime.UtcNow.AddSeconds(30);
        Render();
        try
        {
            var detected = await bridge.ReadRootRuntime(target);
            if (Selected != target) return;
            rootRuntime = detected;
            if (detected is { Engine: "Magisk", Access: true }) knownMagiskDevices.Add(target.Serial);
            installedRootModules.Clear();
            installedRootModules.AddRange(detected.Modules);
            FilterRootModules();
            Render();
            using var parallel = new SemaphoreSlim(3);
            await Task.WhenAll(detected.Modules.Select(async module =>
            {
                await parallel.WaitAsync();
                try
                {
                    var updated = await RootingCatalog.CheckInstalledUpdate(module, fresh);
                    if (Selected != target) return;
                    int index = installedRootModules.FindIndex(item => item.Id == module.Id);
                    if (index >= 0) installedRootModules[index] = updated.Pending
                        ? updated with { Description = updated.Description + " · " + T("ModulePendingReboot") } : updated;
                    FilterRootModules();
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException)
                { AppendLine(T("ModuleUpdateCheckFailed", module.Name, ex.Message)); }
                finally { parallel.Release(); }
            }));
        }
        catch (BridgeException ex)
        {
            if (Selected == target) { rootRuntime = null; installedRootModules.Clear(); }
            AppendLine(T(ex.Key));
        }
        finally
        {
            rootRuntimeReading = false;
            if (Selected == target && installedRootModules.Count == 0 && RootInstalledTab.IsChecked == true)
                RootHighlightsTab.IsChecked = true;
            if (IsLoaded) { FilterRootModules(); Render(); }
        }
    }

    private void RenderRootRuntime()
    {
        RootDeviceStatus.Text = Selected is { } target ? ModeText(target) : T(odin ? "OdinTitle" : "NoTarget");
        RootEngineStatus.Text = rootRuntimeTarget == Selected && rootRuntime is { Engine.Length: > 0 } runtime
            ? runtime.Engine + (runtime.Version.Length > 0 ? " · " + runtime.Version : "")
            : rootStatusTarget == Selected && rootDaemonKey == "RootDaemonLoaded"
                ? "Magisk" + (rootDaemonVersion.Length > 0 ? " · " + rootDaemonVersion : "") : T("RootEngineUnknown");
        RootModuleAvailability.Text = T(rootRuntimeReading ? "ModuleCheckingDevice"
            : RootModuleCompatible ? Selected?.State == "recovery" ? "ModuleRecoveryReady" : "ModuleAndroidReady"
            : CanRequestModuleRoot ? "ModuleRootPrompt"
            : Selected is { Tool: "adb", State: "device" } ? "ModuleNeedsRoot"
            : Selected is { Tool: "adb", State: "recovery" } ? "ModuleRecoveryUnavailable" : "ModuleModeUnsupported");
        RootModuleAvailability.Foreground = new SolidColorBrush(RootModuleCompatible ? Color.FromRgb(39, 106, 80) : Color.FromRgb(120, 93, 44));
        RootModuleList.Tag = CanInstallRootModule;
        RootLocalInstall.IsEnabled = CanInstallRootModule;
        RootAccessActions.Visibility = CanRequestModuleRoot && !RootModuleCompatible ? Visibility.Visible : Visibility.Collapsed;
        RootManagerOpen.IsEnabled = !busy;
        RootAccessCheck.IsEnabled = !busy && !rootRuntimeReading;
        RootInstalledTab.Visibility = installedRootModules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (installedRootModules.Count == 0 && RootInstalledTab.IsChecked == true) RootHighlightsTab.IsChecked = true;
    }
}
