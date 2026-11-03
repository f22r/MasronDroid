using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace MasronDroid;

public partial class MainWindow
{
    private MagiskRelease? magiskRelease;
    private string? magiskZip;
    private readonly List<RootModule> rootModules = [];
    private readonly List<RootModule> rootHighlights = [.. RootingCatalog.Highlights];
    private bool rootHighlightsLoaded, rootHighlightsFetching;
    private string? rootHighlightsError;
    private int rootPage, rootSource;
    private string rootQuery = "";
    private bool rootLatestChecking, rootStatusReading;
    private DateTime nextMagiskLatestRead, nextMagiskStatusRead;
    private Target? rootStatusTarget;
    private string rootDaemonKey = "RootStatusUnknown", rootDaemonVersion = "";
    private bool rootAppInstalled;
    private string rootAppVersion = "";
    private readonly HashSet<string> knownMagiskDevices = [];
    private bool MagiskActive => Selected is { Tool: "adb" } target &&
        (knownMagiskDevices.Contains(target.Serial)
        || rootStatusTarget == target && rootDaemonKey == "RootDaemonLoaded"
        || rootRuntimeTarget == target && rootRuntime is { Engine: "Magisk" });

    private static Version? MagiskVersion(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(),
            @"^v?(\d+\.\d+(?:\.\d+){0,2})(?::.*)?$");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private void RenderMagiskVersions(bool canInstall)
    {
        bool current = rootStatusTarget == Selected && rootDaemonKey != "RootStatusUnknown"
            && Selected is { Tool: "adb", State: "device" };
        bool installed = current && rootAppInstalled;
        var local = installed ? MagiskVersion(rootAppVersion) : null;
        var latest = magiskRelease == null ? null : MagiskVersion(magiskRelease.Version);
        bool update = local != null && latest != null && local < latest;
        bool upToDate = local != null && latest != null && local >= latest;
        RootInstalledVersion.Text = !current ? T("RootVersionUnavailable") : !installed ? T("RootAppAbsent")
            : rootAppVersion.Length == 0 ? T("RootVersionUnavailable") : rootAppVersion;
        RootLatestVersion.Text = magiskRelease?.Version ?? T("RootVersionUnavailable");
        RootUpdateStatus.Text = T(update ? "RootUpdateAvailable" : upToDate ? "RootUpToDate"
            : installed ? "RootVersionUnconfirmed" : "RootOfficialRelease");
        RootUpdateBadge.Background = new System.Windows.Media.SolidColorBrush(
            update ? System.Windows.Media.Color.FromRgb(255, 242, 216) : System.Windows.Media.Color.FromRgb(232, 245, 239));
        RootUpdateStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            update ? System.Windows.Media.Color.FromRgb(137, 83, 14) : System.Windows.Media.Color.FromRgb(39, 106, 80));
        RootApkAction.Text = T(update ? "RootUpdateApk" : upToDate ? "RootUpToDate" : "RootInstallApkShort");
        RootApkInstall.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, RootApkAction.Text);
        RootApkInstall.IsEnabled = canInstall && !upToDate;
    }

    private async Task CheckLatestMagiskAuto()
    {
        if (rootLatestChecking || DateTime.UtcNow < nextMagiskLatestRead) return;
        rootLatestChecking = true;
        RenderActivity();
        nextMagiskLatestRead = DateTime.UtcNow.AddMinutes(5);
        RootReleaseStatus.Text = T("RootCheckingLatest");
        try
        {
            var latest = await RootingCatalog.Latest();
            if (!IsLoaded) return;
            if (magiskRelease?.Sha256 != latest.Sha256) magiskZip = null;
            magiskRelease = latest;
            RootReleaseStatus.Text = $"Magisk {latest.Version} · {latest.Size / 1048576d:0.#} MB · topjohnwu/Magisk";
            Render();
            _ = CheckRootTransfer(Selected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            nextMagiskLatestRead = DateTime.UtcNow.AddSeconds(30);
            if (IsLoaded)
            {
                RootReleaseStatus.Text = T("RootError", ex.Message);
                Report("RootError", ex.Message);
            }
        }
        finally { rootLatestChecking = false; RenderActivity(); }
    }

    private async Task UpdateMagiskStatus(Target? target)
    {
        if (rootStatusTarget != target)
        {
            rootStatusTarget = target;
            rootDaemonKey = "RootStatusUnknown";
            rootDaemonVersion = "";
            rootAppInstalled = false;
            rootAppVersion = "";
            nextMagiskStatusRead = DateTime.MinValue;
        }
        if (rootStatusReading || DateTime.UtcNow < nextMagiskStatusRead) return;
        if (target is not { Tool: "adb", State: "device" }) return;
        rootStatusReading = true;
        RenderActivity();
        nextMagiskStatusRead = DateTime.UtcNow.AddSeconds(15);
        try
        {
            var status = await bridge.ReadMagiskStatus(target);
            if (Selected != target) return;
            (rootDaemonKey, rootDaemonVersion, rootAppInstalled, rootAppVersion) = status;
            if (status.State == "RootDaemonLoaded") knownMagiskDevices.Add(target.Serial);
            else knownMagiskDevices.Remove(target.Serial);
        }
        catch (BridgeException)
        {
            if (Selected == target)
            {
                rootDaemonKey = "RootStatusUnknown";
                rootDaemonVersion = rootAppVersion = "";
                rootAppInstalled = false;
            }
        }
        finally
        {
            rootStatusReading = false;
            if (IsLoaded) Render();
        }
    }

    private async Task RootJob(Func<Task> operation)
    {
        await Work(async () =>
        {
            try { await operation(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                RootModuleStatus.Text = T("RootError", ex.Message);
                Report("RootError", ex.Message);
            }
        });
    }

    private async void RootLatestClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await RootJob(async () =>
        {
            var latest = await RootingCatalog.Latest();
            if (magiskRelease?.Sha256 != latest.Sha256) magiskZip = null;
            magiskRelease = latest;
            RootReleaseStatus.Text = $"Magisk {latest.Version} · {latest.Size / 1048576d:0.#} MB · topjohnwu/Magisk";
            Report("RootLatestChecked", "", latest.Version);
        });
    }

    private async void RootDownloadClick(object sender, RoutedEventArgs e)
    {
        if (busy || magiskRelease == null) return;
        await RootJob(async () =>
        {
            RootReleaseStatus.Text = T("RootDownloading");
            magiskZip = await RootingCatalog.Download(magiskRelease);
            RootReleaseStatus.Text = T("RootVerified", magiskRelease.Version);
            RootReleaseStatus.ToolTip = Path.GetFileName(magiskZip) + Environment.NewLine + "SHA-256 · " + magiskRelease.Sha256;
            Report("RootVerified", RootReleaseStatus.ToolTip.ToString()!, magiskRelease.Version);
        });
    }

    private void RootInfoClick(object sender, RoutedEventArgs e) =>
        ShowInformation(T("Rooting"), T("RootRecoveryHint") + Environment.NewLine + Environment.NewLine + T("RootModulesHint"));

    private async void RootApkInstallClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" } target) return;
        await RootJob(async () =>
        {
            var latest = await RootingCatalog.Latest();
            if (magiskRelease?.Sha256 != latest.Sha256) magiskZip = null;
            magiskRelease = latest;
            RootReleaseStatus.Text = $"Magisk {latest.Version}";
            nextMagiskStatusRead = DateTime.MinValue;
            await UpdateMagiskStatus(target);
            if (Selected != target) throw new BridgeException("TargetChanged");
            var installed = rootAppInstalled ? MagiskVersion(rootAppVersion) : null;
            var available = MagiskVersion(latest.Version);
            if (installed != null && available != null && installed >= available) { Report("RootUpToDate"); return; }
            if (!Confirm(T("RootApkInstall"), T("InstallRisk"), target, $"Magisk {latest.Version} · topjohnwu/Magisk")) return;
            RootReleaseStatus.Text = T("RootDownloading");
            magiskZip = await RootingCatalog.Download(latest);
            string apk = Path.ChangeExtension(magiskZip, ".apk");
            Report("Done", await bridge.Execute(target, DeviceAction.Install, apk), "RootApkInstall");
            RootReleaseStatus.Text = T("RootApkInstalled", latest.Version);
            nextMagiskStatusRead = DateTime.MinValue;
            await UpdateMagiskStatus(target);
        });
    }

    private string? rootTransferredPath, rootTransferSerial, rootTransferViewSerial;
    private string? rootTransferredHash, rootTransferCheckedHash;
    private Target? rootTransferCheckedTarget, rootTransferReadTarget;
    private DateTime nextRootTransferRead;
    private bool rootTransferReading;
    private bool RootTransferPresent => rootTransferredPath != null && rootTransferSerial == Selected?.Serial
        && magiskRelease != null && rootTransferredHash == magiskRelease.Sha256;

    private void RootInstallMethodChanged(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            Render();
            if (RootStorageMethod.IsChecked == true) _ = CheckRootTransfer(Selected);
        }
    }

    private async Task CheckRootTransfer(Target? target, bool force = false)
    {
        if (busy || rootTransferReading || RootStorageMethod.IsChecked != true
            || target is not { Tool: "adb", State: "device" } || magiskRelease is not { } release) return;
        if (!force && rootTransferReadTarget == target && rootTransferCheckedHash == release.Sha256
            && DateTime.UtcNow < nextRootTransferRead) return;
        rootTransferReadTarget = target;
        rootTransferCheckedHash = release.Sha256;
        nextRootTransferRead = DateTime.UtcNow.AddSeconds(30);
        rootTransferReading = true;
        RootTransferStatus.Text = T("RootTransferChecking");
        Render();
        try
        {
            string? path = await bridge.FindTransferredFlashable(target, Path.ChangeExtension(release.FileName, ".zip"), release.Sha256);
            if (Selected != target || magiskRelease?.Sha256 != release.Sha256) return;
            rootTransferCheckedTarget = target;
            rootTransferCheckedHash = release.Sha256;
            nextRootTransferRead = DateTime.UtcNow.AddSeconds(30);
            rootTransferredPath = path;
            rootTransferSerial = target.Serial;
            rootTransferredHash = path == null ? null : release.Sha256;
            RootTransferStatus.Text = path == null ? T("RootTransferMissing") : T("RootTransferPresent", path);
        }
        catch (BridgeException ex)
        {
            if (Selected == target)
            {
                rootTransferCheckedTarget = null;
                rootTransferredPath = rootTransferredHash = null;
                RootTransferStatus.Text = T("OperationFailed") + " " + T(ex.Key, "adb");
                ReportError(ex);
            }
        }
        finally { rootTransferReading = false; if (IsLoaded) Render(); }
    }

    private async Task<(MagiskRelease Release, string File)> PrepareRootPackage()
    {
        var release = magiskRelease ?? await RootingCatalog.Latest();
        magiskRelease = release;
        if (magiskZip == null)
        {
            RootReleaseStatus.Text = T("RootDownloading");
            magiskZip = await RootingCatalog.Download(release);
        }
        string file = magiskZip;
        await using var input = File.OpenRead(file);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Magisk SHA-256 mismatch.");
        return (release, file);
    }

    private async void RootTransferClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" } target) return;
        await RootJob(async () =>
        {
            RootTransferStatus.Text = T("RootTransferring");
            try
            {
                var release = magiskRelease ?? await RootingCatalog.Latest();
                magiskRelease = release;
                string? path = await bridge.FindTransferredFlashable(target, Path.ChangeExtension(release.FileName, ".zip"), release.Sha256);
                bool alreadyPresent = path != null;
                if (path == null)
                {
                    var (_, file) = await PrepareRootPackage();
                    path = await bridge.TransferFlashableToStorage(target, file);
                }
                if (Selected != target) throw new BridgeException("TargetChanged");
                rootTransferSerial = target.Serial;
                rootTransferredPath = path;
                rootTransferredHash = rootTransferCheckedHash = release.Sha256;
                rootTransferCheckedTarget = target;
                rootTransferReadTarget = target;
                nextRootTransferRead = DateTime.UtcNow.AddSeconds(30);
                RootTransferStatus.Text = T(alreadyPresent ? "RootTransferPresent" : "RootTransferComplete", path!);
                Report(alreadyPresent ? "RootTransferPresent" : "RootTransferComplete", "", path!);
            }
            catch (Exception ex)
            {
                RootTransferStatus.Text = ex is BridgeException failure ? T(failure.Key) : T("RootError", ex.Message);
                throw;
            }
        });
    }

    private async void RootInstallClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "adb", State: "device" or "recovery" or "sideload" } target
            || target.State == "sideload" && !SupportedRootRecovery) return;
        if (MagiskActive)
        {
            if (target.State == "sideload") return;
            await RootJob(async () =>
            {
                if (!Confirm(T("RootUninstall"), T("RootUninstallRisk"), target, "Magisk")) return;
                var (_, file) = await PrepareRootPackage();
                if (Selected != target) throw new BridgeException("TargetChanged");
                string output;
                try
                {
                    output = await bridge.UninstallMagiskRecovery(target, file, stage =>
                    {
                        OperationLabel.Text = RootAutoStatus.Text = T(stage);
                        RootAutoStatus.Visibility = Visibility.Visible;
                    });
                }
                catch (BridgeException ex)
                {
                    RootAutoStatus.Text = T(ex.Key);
                    RootAutoStatus.Visibility = Visibility.Visible;
                    throw;
                }
                knownMagiskDevices.Remove(target.Serial);
                rootRuntime = null;
                installedRootModules.Clear();
                nextMagiskStatusRead = nextRootRuntimeRead = DateTime.MinValue;
                await RefreshTargets(false);
                await UpdateMagiskStatus(Selected);
                await UpdateRootRuntime(Selected, true);
                RootAutoStatus.Text = T("RootUninstallComplete");
                RootAutoStatus.Visibility = Visibility.Visible;
                Report("RootUninstallComplete", output);
            });
            return;
        }
        RootAutoStatus.Text = "";
        await RootJob(async () =>
        {
            var (release, file) = await PrepareRootPackage();
            if (Selected != target) throw new BridgeException("TargetChanged");
            if (!Confirm(T("RootFlashMagisk"), T("RootAutoRisk"), target, $"Magisk {release.Version}" + Environment.NewLine + file)) return;
            MagiskRecoveryResult result;
            try
            {
                result = await bridge.FlashMagiskRecovery(target, file, release.Version, stage =>
                {
                    OperationLabel.Text = RootAutoStatus.Text = T(stage);
                    RootAutoStatus.Visibility = Visibility.Visible;
                }, SupportedRootRecovery);
            }
            catch (BridgeException ex)
            {
                RootAutoStatus.Text = T(ex.Key);
                RootAutoStatus.Visibility = Visibility.Visible;
                throw;
            }
            string key = result.RootVerified ? "RootFlowComplete" : result.RebootRequested ? "RootFlowRootPending" : "RootFlowRebootManual";
            await RefreshTargets(false);
            RootAutoStatus.Text = T(key, release.Version);
            RootAutoStatus.Visibility = Visibility.Visible;
            Report(key, result.Output, release.Version);
        });
    }

    private bool rootFetching;
    private bool rootCatalogComplete;
    private string? rootCatalogError;
    private bool rootCatalogLoaded;
    private int rootNextSource = 1, rootNextPage = 1;
    private string rootCacheFile = RootingCatalog.ModuleCachePath;
    private DateTimeOffset? rootCatalogSavedAt;

    private void RootSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized) FilterRootModules();
    }

    private string? rootRecoverySerial, rootRecoveryName;
    private DateTime rootRecoveryChecked;
    private bool rootRecoveryReading;
    private DateTime nextRootRecoveryRead;

    private bool SupportedRootRecovery => Selected is { Tool: "adb", State: "recovery" or "sideload" } target
        && rootRecoverySerial == target.Serial && rootRecoveryName != null
        && DateTime.UtcNow - rootRecoveryChecked < TimeSpan.FromMinutes(10);

    private async Task UpdateRootRecovery(Target? target)
    {
        if (target is { } selected && (selected.Serial != rootRecoverySerial || selected.State is not ("recovery" or "sideload")))
        {
            rootRecoverySerial = rootRecoveryName = null;
            rootRecoveryChecked = DateTime.MinValue;
        }
        if (rootRecoveryReading || target is not { Tool: "adb", State: "recovery" }
            || DateTime.UtcNow < nextRootRecoveryRead) return;
        rootRecoveryReading = true;
        RenderActivity();
        nextRootRecoveryRead = DateTime.UtcNow.AddSeconds(10);
        try
        {
            var name = await bridge.ReadSupportedRecovery(target);
            if (Selected != target) return;
            rootRecoverySerial = target.Serial;
            rootRecoveryName = name;
            rootRecoveryChecked = DateTime.UtcNow;
        }
        catch (BridgeException)
        {
            rootRecoverySerial = rootRecoveryName = null;
        }
        finally
        {
            rootRecoveryReading = false;
            if (IsLoaded) Render();
        }
    }

    private void RootSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) FilterRootModules();
    }

    private async void RootWorkspaceTabChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        PageScroll.ScrollToTop();
        if (RootWorkspaceModules.IsChecked != true) return;
        if (RootInstalledTab.IsChecked == true) { await UpdateRootRuntime(Selected); return; }
        if (RootHighlightsTab.IsChecked == true) await LoadRootHighlights();
        else await LoadRootModules(1);
    }

    private async void RootModuleTabChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        FilterRootModules();
        if (RootInstalledTab.IsChecked == true) { await UpdateRootRuntime(Selected); return; }
        if (RootHighlightsTab.IsChecked == true) await LoadRootHighlights();
        else await LoadRootModules(1);
    }

    private async Task LoadRootHighlights(bool fresh = false)
    {
        if (rootHighlightsFetching || (rootHighlightsLoaded && !fresh)) return;
        rootHighlightsFetching = true;
        rootHighlightsLoaded = true;
        rootHighlightsError = null;
        FilterRootModules();
        try
        {
            foreach (var module in RootingCatalog.Highlights)
            {
                try
                {
                    var release = await RootingCatalog.ModuleRelease(module, fresh);
                    if (!IsLoaded) return;
                    int index = rootHighlights.FindIndex(row => row.Url == module.Url);
                    rootHighlights[index] = module with { Version = release.Version };
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                    or System.Text.Json.JsonException or InvalidOperationException)
                {
                    rootHighlightsError = (rootHighlightsError == null ? "" : rootHighlightsError + Environment.NewLine)
                        + module.Name + ": " + ex.Message;
                }
                if (IsLoaded) FilterRootModules();
            }
        }
        finally
        {
            rootHighlightsFetching = false;
            if (IsLoaded) FilterRootModules();
        }
        if (!fresh && rootHighlightsError != null && IsLoaded)
            Report("RootCatalogPartial", rootHighlightsError, rootHighlights.Count);
    }

    private void FilterRootModules()
    {
        rootQuery = RootSearch.Text.Trim();
        rootSource = RootSource.SelectedIndex;
        bool highlights = RootHighlightsTab.IsChecked == true, installed = RootInstalledTab.IsChecked == true;
        RootSource.Visibility = highlights || installed ? Visibility.Collapsed : Visibility.Visible;
        RootSourceColumn.Width = new GridLength(highlights || installed ? 0 : 190);
        var rows = (installed ? installedRootModules : highlights ? rootHighlights : rootModules).Where(module => installed || highlights || rootSource switch
        {
            1 => module.Source == "Magisk-Modules-Alt-Repo",
            2 => module.Source == "Magisk-Modules-Repo",
            3 => module.Source is not ("Magisk-Modules-Alt-Repo" or "Magisk-Modules-Repo"),
            _ => true
        }).Where(module => rootQuery.Length == 0 || (module.Name + " " + module.Description + " " + module.Source)
            .Contains(rootQuery, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        RootModuleList.ItemsSource = rows;
        RenderActivity();
        if (!busy) RootModuleStatus.Text = T("RootCatalogCount", rows.Length);
        string? error = installed ? null : highlights ? rootHighlightsError : rootCatalogError;
        RootModuleStatus.ToolTip = error;
        RootCatalogRefresh.IsEnabled = !busy && !rootFetching && !rootHighlightsFetching && !rootRuntimeReading;
        RootCatalogRefresh.ToolTip = rootFetching || rootHighlightsFetching ? T("RootCatalogSyncing")
            : T("RootCatalogRefreshHint") + (error == null ? "" : Environment.NewLine + error);
    }

    private void SaveRootCatalog() => RootingCatalog.SaveModuleCache(rootCacheFile,
        new(1, rootCatalogSavedAt, rootCatalogComplete, rootNextSource, rootNextPage, rootModules.ToArray()));

    private async void RootCatalogRefreshClick(object sender, RoutedEventArgs e)
    {
        if (RootInstalledTab.IsChecked == true)
        {
            await RootJob(async () =>
            {
                await UpdateRootRuntime(Selected, true);
                if (rootRuntime != null && rootRuntimeTarget == Selected)
                    Report("RootCatalogUpdated", "", installedRootModules.Count);
                else Report("ModuleNeedsRoot");
            });
            return;
        }
        if (busy || rootFetching || rootHighlightsFetching) return;
        await Work(async () =>
        {
            bool highlights = RootHighlightsTab.IsChecked == true;
            if (highlights) await LoadRootHighlights(true);
            else await LoadRootModules(1, true);
            string? error = highlights ? rootHighlightsError : rootCatalogError;
            Report(error == null ? "RootCatalogUpdated" : "RootCatalogPartial", error ?? "",
                highlights ? rootHighlights.Count : rootModules.Count);
        });
    }

    private async Task LoadRootModules(int unusedPage, bool manual = false)
    {
        if (rootFetching || (rootCatalogLoaded && !manual)) return;
        if (!rootCatalogLoaded)
        {
            rootCatalogLoaded = true;
            rootPage = 1;
            try
            {
                var cache = RootingCatalog.LoadModuleCache(rootCacheFile);
                if (cache != null)
                {
                    rootModules.AddRange(cache.Modules.DistinctBy(module => module.Url, StringComparer.OrdinalIgnoreCase));
                    rootCatalogComplete = cache.Complete;
                    rootNextSource = cache.NextSource;
                    rootNextPage = cache.NextPage;
                    rootCatalogSavedAt = cache.SavedAt;
                    if (!cache.Complete) rootCatalogError = T("RootCatalogIncomplete");
                    FilterRootModules();
                    if (!manual) return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                rootCatalogError = T("RootCatalogCacheError", ex.Message);
                FilterRootModules();
                if (!manual) return;
            }
        }
        if (rootCatalogComplete)
        {
            rootNextSource = rootNextPage = 1;
            rootCatalogComplete = false;
        }
        rootFetching = true;
        rootCatalogError = null;
        FilterRootModules();
        try
        {
            int startSource = rootNextSource, startPage = rootNextPage;
            for (int source = startSource; source <= 3; source++)
            {
                for (int pageNumber = source == startSource ? startPage : 1; pageNumber <= 10; pageNumber++)
                {
                    var rows = await RootingCatalog.Modules(source, "", pageNumber);
                    if (!IsLoaded) return;
                    foreach (var row in rows)
                    {
                        int index = rootModules.FindIndex(old => string.Equals(old.Url, row.Url, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) rootModules[index] = row;
                        else rootModules.Add(row);
                    }
                    rootPage = pageNumber;
                    bool lastPage = rows.Length < 100 || pageNumber == 10;
                    rootNextSource = lastPage ? source + 1 : source;
                    rootNextPage = lastPage ? 1 : pageNumber + 1;
                    rootCatalogComplete = rootNextSource > 3;
                    rootCatalogSavedAt = DateTimeOffset.UtcNow;
                    SaveRootCatalog();
                    // Search API exposes at most 1,000 topic results.
                    if (source == 3 && pageNumber == 10) rootCatalogError = T("RootSearchLimit");
                    FilterRootModules();
                    if (lastPage) break;
                }
            }
            rootCatalogComplete = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            rootCatalogError = ex.Message;
            rootPage = Math.Max(1, rootPage);
        }
        finally
        {
            rootFetching = false;
            try { SaveRootCatalog(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rootCatalogError = T("RootCatalogCacheError", ex.Message);
            }
            if (IsLoaded) FilterRootModules();
        }
    }

    private void FitRootModules(object sender, EventArgs e)
    {
        if (!initialized || !RootModuleList.IsVisible || PageScroll.ViewportHeight <= 0) return;
        double top = RootModuleList.TranslatePoint(new Point(), PageScroll).Y + PageScroll.VerticalOffset;
        double height = Math.Max(180, Math.Floor(PageScroll.ViewportHeight - top - 44));
        if (Math.Abs(RootModuleList.Height - height) > 1) RootModuleList.Height = height;
    }

    private async Task<ModuleAsset?> ChooseRootModuleAsset(RootModule module)
    {
        if (module.Installed) return module.UpdateAsset;
        var release = await RootingCatalog.ModuleRelease(module, true);
        int index = rootHighlights.FindIndex(row => row.Url == module.Url);
        if (index >= 0) rootHighlights[index] = rootHighlights[index] with { Version = release.Version };
        FilterRootModules();
        var assets = release.Assets.OrderBy(asset => asset.Name.Contains("debug", StringComparison.OrdinalIgnoreCase) ? 2
            : asset.Name.Contains("-inject", StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains("-nopif", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
        if (assets.Length == 0) throw new InvalidDataException(T("RootNoZip"));
        if (assets.Length == 1) return assets[0];
        var dialog = new Window { Owner = this, Title = T("RootChooseZip"), Width = 480,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var choice = new ComboBox { ItemsSource = assets, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 14) };
        panel.Children.Add(new TextBlock { Text = module.Name + " · " + release.Version, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(choice);
        var accept = new Button { Content = T("RootDownloadShort"), HorizontalAlignment = HorizontalAlignment.Right };
        accept.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(accept);
        dialog.Content = panel;
        return ShowAppDialog(dialog) == true ? choice.SelectedItem as ModuleAsset : null;
    }

    private async void RootModuleDownloadClick(object sender, RoutedEventArgs e)
    {
        if (busy || (sender as Button)?.Tag is not RootModule module) return;
        await RootJob(async () =>
        {
            RootModuleStatus.Text = T("RootModulePreparing", module.Name);
            var asset = await ChooseRootModuleAsset(module);
            if (asset == null) return;
            var download = await RootingCatalog.DownloadModule(asset);
            RootModuleStatus.Text = T("RootModuleDownloaded", module.Name, download.Path);
            Report("RootModuleDownloaded", "", module.Name, download.Path);
        });
    }

    private async void RootModuleInstallClick(object sender, RoutedEventArgs e)
    {
        if (busy || (sender as Button)?.Tag is not RootModule module
            || !CanInstallRootModule || Selected is not { } target || module.Installed && !module.HasUpdate) return;
        await RootJob(async () =>
        {
            RootModuleStatus.Text = T("RootModulePreparing", module.Name);
            if (module.Installed)
            {
                module = await RootingCatalog.CheckInstalledUpdate(module, true);
                if (!module.HasUpdate) { Report("RootUpToDate"); return; }
            }
            var selected = module.Installed ? module.UpdateAsset : await ChooseRootModuleAsset(module);
            if (selected == null) return;
            var download = await RootingCatalog.DownloadModule(selected);
            if (module.Installed && download.Id != module.Id) throw new BridgeException("ModuleUpdateMismatch");
            if (Selected != target) throw new BridgeException("TargetChanged");
            string details = module.Name + Environment.NewLine + module.Url + Environment.NewLine
                + selected.Name + Environment.NewLine + "SHA-256 · " + download.Hash;
            if (!Confirm(T("RootModuleInstall"), T("RootModuleRisk"), target, details)) return;
            RootModuleStatus.Text = T("RootModuleInstalling", module.Name);
            if (target.State == "device" && !RootModuleCompatible)
                OperationLabel.Text = RootModuleStatus.Text = T("ModuleNeedsRoot");
            Report("Done", await bridge.InstallRootModule(target, download.Path, download.Id, download.Hash), "RootModuleInstall");
            RootModuleStatus.Text = T("RootModuleInstalled", module.Name);
            await UpdateRootRuntime(target, true);
        });
    }

    private async void RootLocalInstallClick(object sender, RoutedEventArgs e)
    {
        if (!CanInstallRootModule || Selected is not { } target) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = T("ZIP") + " (*.zip)|*.zip", CheckFileExists = true, Title = T("ModuleInstallFile")
        };
        if (picker.ShowDialog(this) != true) return;
        await RootJob(async () =>
        {
            OperationLabel.Text = T("ModuleInspectFile");
            var (module, hash) = await RootingCatalog.InspectLocalModule(picker.FileName);
            if (Selected != target) throw new BridgeException("TargetChanged");
            string details = module.Name + " · " + module.Version + Environment.NewLine + module.Id
                + Environment.NewLine + picker.FileName + Environment.NewLine + "SHA-256 · " + hash;
            if (!Confirm(T("ModuleInstallFile"), T("RootModuleRisk"), target, details)) return;
            OperationLabel.Text = RootModuleStatus.Text = T("RootModuleInstalling", module.Name);
            if (target.State == "device" && !RootModuleCompatible) OperationLabel.Text = T("ModuleNeedsRoot");
            Report("Done", await bridge.InstallRootModule(target, picker.FileName, module.Id, hash), "RootModuleInstall");
            RootModuleStatus.Text = T("RootModuleInstalled", module.Name);
            await UpdateRootRuntime(target, true);
        });
    }

    private void RootLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string address || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host is not ("github.com" or "gitlab.com" or "topjohnwu.github.io")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Report("RootError", ex.Message); }
    }
}
