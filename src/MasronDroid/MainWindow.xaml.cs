using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace MasronDroid;

public partial class MainWindow : Window
{
    private readonly Preferences preferences;
    private AndroidBridge bridge;
    private IScreenMirror mirror;
    private CancellationTokenSource? mirrorSession;
    private string? lockedSerial, resumeSerial, pictureKey, stayAwakeSerial;
    private static readonly System.Net.Http.HttpClient photos = new() { Timeout = TimeSpan.FromSeconds(7) };
    private string? mirrorSerial;
    private bool updatingTargets;
    private readonly bool persistent;
    private Dictionary<string, string> strings = new();
    private string page = "Device";
    private string apk = "", zip = "";
    private string[] packages = [];
    private bool initialized, busy, packagesLoaded, partialScan;
    private readonly System.Windows.Threading.DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<bool> detectMtp;
    private bool autoScanning, mtp;
    private int generation;
    private readonly Queue<string> log = new();
    private string resultKey = "Idle", resultDetail = "";
    private object[] resultArgs = [];
    private Target? Selected => Targets.SelectedItem as Target;

    public MainWindow() : this(Preferences.Load(), null) { }

    public MainWindow(Preferences options, IPlatformTools? testTools, IScreenMirror? testMirror = null, Func<bool>? mtpProbe = null)
    {
        detectMtp = mtpProbe ?? MtpDevices.IsPresent;
        preferences = options;
        persistent = testTools == null;
        var tools = new PlatformTools();
        bridge = new AndroidBridge(testTools ?? tools);
        mirror = testMirror ?? new ScreenMirror(tools);
        InitializeComponent();
        bridge.CommandStarting += line => AppendLine("> " + line);
        SourceInitialized += (_, _) => WindowWorkArea.Install(this);
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        AppVersion.Text = $"MasronDroid  {version.ToString(3)}";
        if (persistent) Loaded += async (_, _) => await CheckForUpdate();
        SetLanguage(options.Language);
        Languages.SelectedIndex = options.Language == "en" ? 1 : 0;
        initialized = true;
        NavDevice.IsChecked = true;
        Render();
        Loaded += async (_, _) => await Work(() => RefreshTargets(true), "Scanning");
        monitor.Tick += async (_, _) =>
        {
            if (busy || autoScanning || !IsEnabled) return;
            autoScanning = true;
            try { await RefreshTargets(false, true); }
            finally { autoScanning = false; }
        };
        Loaded += (_, _) => monitor.Start();
        Closed += (_, _) => monitor.Stop();
        Closing += OnClosing;
        Closed += (_, _) => mirrorSession?.Cancel();
        StateChanged += (_, _) =>
        {
            bool large = WindowState == WindowState.Maximized;

            MaximizeButton.Content = large ? "\uE923" : "\uE922";
            MaximizeButton.SetResourceReference(ToolTipProperty, large ? "Restore" : "Maximize");
            MaximizeButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, large ? "Restore" : "Maximize");
        };
    }

    private async Task CheckForUpdate()
    {
        try
        {
            var updater = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/f22r/MasronDroid", null, false));
            if (!updater.IsInstalled) return;
            var next = await updater.CheckForUpdatesAsync();
            if (next == null) return;
            await updater.DownloadUpdatesAsync(next);
            Report("UpdateReady");
        }
        catch (Exception ex) { if (IsLoaded) Report("UpdateUnavailable", ex.Message); }
    }

    private async Task SetPicture(string? key)
    {
        if (key == pictureKey) return;
        pictureKey = key;
        PhonePicture.Source = null;
        PhonePicture.Visibility = Visibility.Collapsed;
        PhoneOutline.Visibility = Visibility.Visible;
        if (key == null) return;
        try
        {
            var url = "https://raw.githubusercontent.com/LineageOS/lineage_wiki/bf49224b3a8838ad67f42e014435d5feeae5a668/images/devices/" + Uri.EscapeDataString(key) + ".png";
            var data = await photos.GetByteArrayAsync(url);
            if (key != pictureKey || !IsLoaded) return;
            using var stream = new MemoryStream(data);
            var picture = new System.Windows.Media.Imaging.BitmapImage();
            picture.BeginInit(); picture.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            picture.DecodePixelHeight = 210; picture.StreamSource = stream; picture.EndInit(); picture.Freeze();
            PhonePicture.Source = picture;
            PhonePicture.Visibility = Visibility.Visible;
            PhoneOutline.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or NotSupportedException or FormatException) { }
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private string T(string key, params object[] args)
    {
        if (key == "CountPkgs" && args is [1]) key = "OnePackage";
        return args.Length == 0 ? strings[key] : string.Format(strings[key], args);
    }

    private void SetLanguage(string code)
    {
        code = code == "en" ? "en" : "id";
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"MasronDroid.Locales.{code}.json")!;
        strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        foreach (var (key, value) in strings) Resources[key] = value;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(code);
        preferences.Language = code;
    }

    private void SavePreferences()
    {
        if (!persistent) return;
        try { preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report("PrefsFailed"); }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || Languages.SelectedItem is not ComboBoxItem choice) return;
        SetLanguage((string)choice.Tag);
        Render();
        SavePreferences();
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        page = (string)((RadioButton)sender).Tag;

        Render();
        PageScroll.ScrollToTop();
    }

    private void Report(string key, string detail = "", params object[] args)
    {
        (resultKey, resultDetail, resultArgs) = (key, detail, args);
        AppendResult();
    }

    private void AppendResult()
    {
        var summary = resultKey == "Done" ? T("Done", T((string)resultArgs[0])) : T(resultKey, resultArgs);
        string line = summary + (resultDetail.Length > 0 ? Environment.NewLine + resultDetail : "");
        AppendLine(line);
    }

    private void AppendLine(string line)
    {
        log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line[..Math.Min(line.Length, 4096)]}");
        if (log.Count > 200) log.Dequeue();
        Result.Text = string.Join(Environment.NewLine, log);
        Result.ScrollToEnd();
    }

    private void ReportError(BridgeException ex)
    {
        if (ex.Key is "ToolMissing" or "TimedOut") Report(ex.Key, "", ex.Message);
        else Report(ex.Key, ex.Message);
    }

    private async Task Work(Func<Task> job, string pending = "Busy")
    {
        if (busy) return;
        generation++;
        busy = true;
        Render();
        Report(pending);
        try { await job(); }
        catch (BridgeException ex) { ReportError(ex); }
        finally { busy = false; Render(); }
    }

    private async Task RefreshTargets(bool showResult, bool automatic = false)
    {
        int revision = generation;
        var found = await bridge.Discover();
        bool onlyMtp = found.Targets.Count == 0 && detectMtp();
        if (automatic && (revision != generation || !IsLoaded || !IsEnabled)) return;
        var old = Targets.Items.Cast<Target>().ToArray();
        var ordered = found.Targets.OrderBy(x => x.Tool).ThenBy(x => x.Serial).ToArray();
        bool changed = !old.SequenceEqual(ordered);
        bool failed = found.Errors.Count > 0;
        bool statusChanged = changed || onlyMtp != mtp || failed != partialScan;
        mtp = onlyMtp;
        partialScan = failed;
        if (changed)
        {
            var previous = Selected;
            updatingTargets = true;
            Targets.ItemsSource = ordered;
            Targets.SelectedItem = ordered.FirstOrDefault(x => x.Serial == previous?.Serial && x.Tool == previous.Tool)
                ?? (previous == null && ordered.Length == 1 ? ordered[0] : null);
            updatingTargets = false;
            CheckMirrorTarget();
        }
        if (showResult || statusChanged)
        {
            if (failed) ReportError(found.Errors[0]);
            else if (!bridge.Has("adb")) Report("NoScanTools");
            else if (mtp) Report("MtpHint");
            else Report("Scanned", string.Join(Environment.NewLine, ordered.Select(x => x.ToString())), ordered.Length);
        }
        var target = Selected;
        bool? locked = target is { Tool: "adb", State: "device" } ? await bridge.IsLocked(target) : null;
        if (!IsLoaded || revision != generation || Selected != target) return;
        if (locked == true && lockedSerial != target!.Serial)
        {
            lockedSerial = target.Serial;
            if (mirrorSerial == target.Serial) { resumeSerial = target.Serial; mirrorSession?.Cancel(); }
            Report("UnlockPhone");
            statusChanged = true;
        }
        else if (lockedSerial != null && (locked == false || target?.Serial != lockedSerial))
        {
            lockedSerial = null;
            statusChanged = true;
        }
        if (locked != true && lockedSerial == null && resumeSerial != null && resumeSerial == target?.Serial && mirrorSession == null && !busy)
        {
            resumeSerial = null;
            MirrorClick(ViewScreen, new RoutedEventArgs());
        }
        if (!automatic || statusChanged) Render();
    }

    private async void ScanClick(object sender, RoutedEventArgs e) => await Work(() => RefreshTargets(true), "Scanning");

    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        if (!updatingTargets) CheckMirrorTarget();
        if (!updatingTargets && Selected?.Serial != resumeSerial) resumeSerial = null;
        ClearPackages();
        Render();
    }

    private void CheckMirrorTarget()
    {
        if (Selected?.Serial != stayAwakeSerial) stayAwakeSerial = null;
        if (Selected?.Serial != mirrorSerial || Selected?.State != "device") mirrorSession?.Cancel();
    }

    private void ClearPackages()
    {
        packages = [];
        packagesLoaded = false;
        PackageList.ItemsSource = null;
        Filter.Clear();
    }

    private void Render()
    {
        if (!initialized) return;
        PageTitle.Text = T(page);
        PageDescription.Text = T(page + "Desc");
        foreach (var panel in new[] { DevicePage, InstallPage, DebloatPage, FastbootPage, RecoveryPage })
            panel.Visibility = panel.Name == page + "Page" ? Visibility.Visible : Visibility.Collapsed;
        TargetHint.Text = T(Targets.Items.Count == 0 ? (mtp ? "WaitingAdb" : "EmptyTarget") : "PickTarget");
        TargetHint.Visibility = Selected == null ? Visibility.Visible : Visibility.Collapsed;
        bool adb = bridge.Has("adb");
        _ = SetPicture(Selected?.Image);
        ViewScreen.IsEnabled = Selected?.Serial != lockedSerial && !busy && mirrorSession == null && mirror.Available && adb && Selected is { Tool: "adb", State: "device" };
        ViewScreen.Content = T(mirrorSession == null ? "Mirror" : "Mirroring");
        ViewScreen.ToolTip = T(!mirror.Available ? "MirrorMissing" : "MirrorHint");
        DeviceHeadline.Text = Selected is { } device ? (device.Model.Length > 0 ? device.Model : device.Serial)
            : T(Targets.Items.Count > 0 ? "PickTarget" : !adb ? "NoToolsHero" : mtp ? "MtpTitle" : "EmptyHero");
        DeviceHint.Text = lockedSerial != null && Selected?.Serial == lockedSerial ? T("UnlockPhone") : Selected is { } active ? ModeText(active) + "   ·   " + T(active.State switch
        {
            "unauthorized" or "authorizing" => "AuthHint",
            "offline" => "OfflineHint",
            _ => "ReadyHint"
        }) : T(partialScan ? "ScanError" : Targets.Items.Count > 0 ? "MultiHint" : !adb ? "NoToolsHint" : mtp ? "MtpHint" : "EmptyHint");
        bool viewing = mirrorSession != null;
        MirrorPane.Visibility = viewing ? Visibility.Visible : Visibility.Collapsed;
        StayAwake.IsChecked = mirrorSerial != null && mirrorSerial == stayAwakeSerial;
        FitMirrorColumn();
        AndroidHome.IsEnabled = AndroidBack.IsEnabled = AndroidRecent.IsEnabled = !busy && !navigationPending && viewing && Selected?.Serial == mirrorSerial && lockedSerial != mirrorSerial;
        MirrorTitle.Text = Selected?.Model ?? T("Mirror");
        ApkName.Text = apk.Length > 0 ? Path.GetFileName(apk) : T("NoFile");
        ZipName.Text = zip.Length > 0 ? Path.GetFileName(zip) : T("NoFile");
        bool Allow(DeviceAction action) => !busy && AndroidBridge.Allows(Selected, action);
        ScanButton.IsEnabled = !busy;
        Targets.IsEnabled = !busy && Targets.Items.Count > 0;
        Navigation.IsEnabled = Languages.IsEnabled = PageScroll.IsEnabled = !busy;
        InstallApk.IsEnabled = apk.Length > 0 && Allow(DeviceAction.Install);
        bool disabledList = PackageMode.SelectedIndex == 1;
        LoadPackages.IsEnabled = Allow(DeviceAction.PackagesEnabled);
        Filter.IsEnabled = packagesLoaded && !busy;
        ChangePackage.Content = T(disabledList ? "Enable" : "Disable");
        ChangePackage.IsEnabled = LoadPackages.IsEnabled && PackageList.SelectedItem is string;
        PackageCount.Text = !packagesLoaded ? T("LoadFirst") : PackageList.Items.Count == 0 ? T("NoMatches") : T("CountPkgs", PackageList.Items.Count);
        EnterBoot.IsEnabled = Allow(DeviceAction.Bootloader);
        ReadProduct.IsEnabled = Allow(DeviceAction.Product);
        LeaveBoot.IsEnabled = Selected?.Tool == "fastboot" && Allow(DeviceAction.System);
        EnterRecovery.IsEnabled = Allow(DeviceAction.Recovery);
        LeaveRecovery.IsEnabled = Selected?.State == "recovery" && Allow(DeviceAction.System);
        SendZip.IsEnabled = zip.Length > 0 && Allow(DeviceAction.Sideload);

    }

    private string ModeText(Target device) => device.State switch
    {
        "device" => T("ModeDevice"), "recovery" => T("ModeRecovery"), "sideload" => T("ModeSideload"),
        "unauthorized" or "authorizing" => T("ModeUnauthorized"), "offline" => T("ModeOffline"),
        "fastboot" => T("Fastboot"), _ => T("ModeOther", device.State)
    };

    private void PickFile(object sender, RoutedEventArgs e)
    {
        bool isApk = (string)((Button)sender).Tag == "apk";
        var filter = isApk ? T("APK") + " (*.apk)|*.apk" : T("ZIP") + " (*.zip)|*.zip";
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            if (isApk) apk = dialog.FileName;
            else zip = dialog.FileName;
            Render();
        }
    }

    private async void MirrorClick(object sender, RoutedEventArgs e)
    {
        if (busy || mirrorSession != null || !mirror.Available || Selected is not { Tool: "adb", State: "device" } target) return;
        await Work(async () =>
        {
            await bridge.VerifyTarget(target);
            if (await bridge.IsLocked(target) == true)
            {
                lockedSerial = target.Serial;
                Report("UnlockPhone");
                return;
            }
            mirrorSerial = target.Serial;
            mirrorSession = new CancellationTokenSource();
            Render();
            MirrorPane.UpdateLayout();
            Report("MirrorStarting", "", target.Serial);
            _ = ObserveMirror(target.Serial, mirrorSession);
        });
    }

    private void StayAwakeClick(object sender, RoutedEventArgs e)
    {
        if (mirrorSession == null || mirrorSerial == null || mirrorSession.IsCancellationRequested) return;
        stayAwakeSerial = StayAwake.IsChecked == true ? mirrorSerial : null;
        resumeSerial = mirrorSerial;
        mirrorSession.Cancel();
    }

    private bool navigationPending;

    private void WorkspaceResized(object sender, SizeChangedEventArgs e) => FitMirrorColumn();

    private void FitMirrorColumn()
    {
        if (MirrorColumn == null) return;
        double width = mirrorSession == null ? 0 : Math.Min(WorkspaceBody.ActualWidth * 0.43,
            Math.Max(120, (WorkspaceBody.ActualHeight - 80) * 0.56 + 24));
        MirrorColumn.Width = new GridLength(width);
    }

    private async void AndroidNavClick(object sender, RoutedEventArgs e)
    {
        if (busy || navigationPending || mirrorSession == null || Selected is not { } target || target.Serial != mirrorSerial) return;
        navigationPending = true;
        Render();
        try
        {
            string key = (string)((Button)sender).Tag;
            await bridge.Navigate(target, key);
            AppendLine(T("Android" + key));
        }
        catch (BridgeException ex) { ReportError(ex); }
        finally { navigationPending = false; Render(); }
    }

    private async Task ObserveMirror(string serial, CancellationTokenSource session)
    {
        try
        {
            var result = await mirror.Watch(serial, stayAwakeSerial == serial, VideoSurface.Attach, session.Token);
            if (!session.IsCancellationRequested)
            {
                if (Selected is { State: "device" } target && target.Serial == serial && await bridge.IsLocked(target) == true)
                {
                    lockedSerial = resumeSerial = serial;
                    Report("UnlockPhone");
                }
                else if (result.Ok) Report("MirrorClosed", "", serial);
                else Report("MirrorFailed", result.Output);
            }
        }
        catch (BridgeException ex) { if (!session.IsCancellationRequested) ReportError(ex); }
        catch (Exception ex) { if (!session.IsCancellationRequested) Report("MirrorFailed", ex.Message); }
        finally { VideoSurface.Forget(); mirrorSerial = null; mirrorSession = null; session.Dispose(); if (IsLoaded) Render(); }
    }

    private void PackageModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        ClearPackages();
        Render();
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        PackageList.ItemsSource = packages.Where(p => p.Contains(Filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Render();
    }

    private void PackageSelected(object sender, SelectionChangedEventArgs e) { if (initialized) Render(); }

    private async Task FetchPackages(Target device, bool showResult)
    {
        ClearPackages();
        var action = PackageMode.SelectedIndex == 1 ? DeviceAction.PackagesDisabled : DeviceAction.PackagesEnabled;
        packages = AndroidBridge.Packages(await bridge.Execute(device, action));
        packagesLoaded = true;
        ApplyFilter();
        if (showResult) Report("CountPkgs", "", packages.Length);
    }

    private async void LoadClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } device) await Work(() => FetchPackages(device, true));
    }

    private async void PackageClick(object sender, RoutedEventArgs e)
    {
        if (PackageList.SelectedItem is string package)
            await RunAction(PackageMode.SelectedIndex == 1 ? DeviceAction.Enable : DeviceAction.Disable, package);
    }

    private async void ActionClick(object sender, RoutedEventArgs e)
    {
        var action = Enum.Parse<DeviceAction>((string)((Button)sender).Tag);
        await RunAction(action, action == DeviceAction.Install ? apk : action == DeviceAction.Sideload ? zip : "");
    }

    private async Task RunAction(DeviceAction action, string value)
    {
        if (busy || Selected is not { } device || !AndroidBridge.Allows(device, action)) return;
        var (label, risk) = action switch
        {
            DeviceAction.Install => ("Install", "InstallRisk"), DeviceAction.Sideload => ("Sideload", "SideloadRisk"),
            DeviceAction.Disable => ("Disable", "DisableRisk"), DeviceAction.Enable => ("Enable", "EnableRisk"),
            DeviceAction.Bootloader => ("ToBoot", "RebootRisk"), DeviceAction.Recovery => ("ToRecovery", "RebootRisk"),
            DeviceAction.System => ("ToSystem", "RebootRisk"), _ => ("Product", "")
        };
        if (risk.Length > 0 && !Confirm(T(label), T(risk), device, value)) return;
        await Work(async () =>
        {
            var output = await bridge.Execute(device, action, value);
            Report("Done", output, label);
            if (action is DeviceAction.Enable or DeviceAction.Disable) await FetchPackages(device, false);
            if (action is DeviceAction.Bootloader or DeviceAction.Recovery or DeviceAction.System or DeviceAction.Sideload)
                await RefreshTargets(false);
        });
    }

    private bool Confirm(string label, string risk, Target target, string value)
    {
        var dialog = new Window
        {
            Title = T("ConfirmTitle"), Width = 440, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 14, Foreground = Foreground,
            WindowStyle = WindowStyle.None, Background = Brushes.White
        };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = label, FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = risk, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap });
        var details = target.ToString() + (value.Length > 0 ? Environment.NewLine + value : "");
        body.Children.Add(new TextBlock { Text = details, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = label, Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        body.Children.Add(buttons);
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog, new() { CaptionHeight = 22,
            GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        dialog.Content = body;
        dialog.ContentRendered += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Report("CloseBusy"); }
    }
}
