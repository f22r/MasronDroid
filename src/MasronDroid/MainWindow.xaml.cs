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
    private CancellationTokenSource? consoleCommand;
    private AndroidBridge bridge;
    private IScreenMirror mirror;
    private CancellationTokenSource? mirrorSession;
    private string? lockedSerial, pictureKey, stayAwakeSerial;
    private static readonly System.Net.Http.HttpClient photos = new() { Timeout = TimeSpan.FromSeconds(7) };
    private string? unlockSerial;
    private string? mirrorSerial;
    private bool updatingTargets;
    private readonly bool persistent;
    private Dictionary<string, string> strings = new();
    private string page = "Device";
    private string zip = "";
    private bool initialized, busy, partialScan;
    private readonly System.Windows.Threading.DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<bool> detectMtp;
    private bool autoScanning, mtp, odin;
    private int generation;
    private readonly Queue<string> log = new();
    private string resultKey = "Idle", resultDetail = "";
    private object[] resultArgs = [];
    private Target? Selected => Targets.SelectedItem is Target { Tool: not "wifi" } active ? active : null;

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
        foreach (var entry in (options.History ?? []).TakeLast(200)) log.Enqueue(entry.Replace("\u00e2\u20ac\u00a6", "\u2026").Replace("\u00c2\u00b7", "\u00b7"));
        ConsoleOutput.Text = string.Join(Environment.NewLine, log);
        if (options.WindowWidth > 0 && options.WindowHeight > 0)
        {
            Width = Math.Clamp(options.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(options.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        }
        if (options.Maximized) WindowState = WindowState.Maximized;
        var lastPage = Navigation.Children.OfType<RadioButton>().FirstOrDefault(x => Equals(x.Tag, options.Page == "Debloat" ? "Install" : options.Page));
        (lastPage ?? NavDevice).IsChecked = true;
        Render();
        Loaded += async (_, _) => await Work(() => RefreshTargets(true), "Scanning");
        monitor.Tick += async (_, _) =>
        {
            if (busy || autoScanning || !IsEnabled) return;
            autoScanning = true;
            try
            {
                if (page == "Device" && bridge.Has("adb") && DateTime.UtcNow >= nextWirelessScan)
                {
                    nextWirelessScan = DateTime.UtcNow.AddSeconds(5);
                    await RefreshWireless();
                }
                await RefreshTargets(false, true);
            }
            finally { autoScanning = false; }
        };
        Loaded += (_, _) => monitor.Start();
        Closed += (_, _) => { monitor.Stop(); consoleCommand?.Cancel(); };
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
        if (page != "Device") mirrorSession?.Cancel();

        Render();
        PageScroll.ScrollToTop();
    }

    private void ScrollConsole(object sender, TextChangedEventArgs e) => QueueConsoleScroll((TextBox)sender);

    private void ShowConsoleTail(object sender, DependencyPropertyChangedEventArgs e) => QueueConsoleScroll((TextBox)sender);

    private static void QueueConsoleScroll(TextBox box)
    {
        if (box.IsVisible)
            box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(box.ScrollToEnd));
    }

    private void ClearConsole(object sender, RoutedEventArgs e)
    {
        log.Clear();
        ConsoleOutput.Clear();
        preferences.History = [];
        SavePreferences();
    }

    private void ConsoleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        RunConsole(sender, e);
    }

    private void StopConsole(object sender, RoutedEventArgs e) => consoleCommand?.Cancel();

    private async void RunConsole(object sender, RoutedEventArgs e)
    {
        bool fastboot = sender == FastInput;
        var input = fastboot ? FastInput : sender == RecoveryInput ? RecoveryInput : AdbCommand;
        string tool = fastboot ? "fastboot" : "adb";
        var command = input.Text.Trim();
        if (consoleCommand != null || command.Length == 0) return;
        var match = System.Text.RegularExpressions.Regex.Match(command, "^" + tool + @"(?:\.exe)?(?:\s+|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) command = command[match.Length..];
        if (fastboot && Selected?.Tool != "fastboot" && command is not ("devices" or "--help" or "--version"))
        {
            AppendLine(T("FastConsoleTarget"));
            return;
        }
        using var stop = new CancellationTokenSource();
        consoleCommand = stop;
        input.Clear();
        input.Focus();
        var serial = Selected is { } device && device.Tool == tool ? device.Serial : null;
        AppendLine((serial == null ? "" : "[" + serial + "] ") + "> " + tool + " " + command);
        Render();
        try
        {
            int code = await PlatformTools.StreamCommand(tool, command, serial, AppendLine, stop.Token);
            if (!stop.IsCancellationRequested) AppendLine(tool + ": " + code);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or BridgeException)
        { AppendLine(ex.Message); }
        finally { consoleCommand = null; if (IsLoaded) Render(); }
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
        log.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line[..Math.Min(line.Length, 4096)]}");
        if (log.Count > 200) log.Dequeue();
        ConsoleOutput.Text = string.Join(Environment.NewLine, log);

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
        bool downloadMode = await Task.Run(MtpDevices.IsOdinPresent);
        bool onlyMtp = found.Targets.Count == 0 && await Task.Run(detectMtp);
        if (automatic && (revision != generation || !IsLoaded || !IsEnabled)) return;
        var old = Targets.Items.Cast<Target>().ToArray();
        var detected = wifiServices.Where(service => !found.Targets.Any(active => active.Tool == "adb" &&
            (active.Serial == service.Device.Serial || active.Serial.StartsWith(service.Instance + "._adb-tls-", StringComparison.Ordinal))))
            .Select(service => service.Device).Distinct();
        var ordered = found.Targets.Concat(detected).OrderBy(x => x.Tool).ThenBy(x => x.Serial).ToArray();
        WifiPanelButton.Visibility = Targets.SelectedItem is Target { Tool: "wifi" } || wifiServices.Count == 0 && !found.Targets.Any(t => t.Serial.Contains(':') || t.Serial.Contains("._adb-tls-"))
            ? Visibility.Visible : Visibility.Collapsed;
        bool changed = !old.SequenceEqual(ordered);
        bool failed = found.Errors.Count > 0;
        bool statusChanged = changed || downloadMode != odin || onlyMtp != mtp || failed != partialScan;
        odin = downloadMode;
        mtp = onlyMtp;
        partialScan = failed;
        if (changed)
        {
            var previous = Targets.SelectedItem as Target;
            updatingTargets = true;
            Targets.ItemsSource = ordered;
            Targets.SelectedItem = ordered.FirstOrDefault(x => x.Serial == previous?.Serial && x.Tool == previous.Tool)
                ?? (ordered.Length == 1 ? ordered[0] : null);
            updatingTargets = false;
            CheckMirrorTarget();
            if (Targets.SelectedItem is Target { Tool: "wifi" } pending && previous != pending)
                _ = Dispatcher.InvokeAsync(() => SelectWireless(pending), System.Windows.Threading.DispatcherPriority.Background);
        }
        if (showResult || statusChanged)
        {
            if (failed) ReportError(found.Errors[0]);
            else if (!bridge.Has("adb")) Report("NoScanTools");
            else if (odin) Report("OdinHint");
            else if (mtp) Report("MtpHint");
            else Report("Scanned", string.Join(Environment.NewLine, ordered.Select(x => x.Details)), ordered.Length);
        }
        var target = Selected;
        bool? locked = target is { Tool: "adb", State: "device" } ? await bridge.IsLocked(target) : null;
        if (!IsLoaded || revision != generation || Selected != target) return;
        if (locked == true && lockedSerial != target!.Serial)
        {
            lockedSerial = target.Serial;
            if (mirrorSerial == target.Serial) mirrorSession?.Cancel();
            Report("UnlockPhone");
            statusChanged = true;
        }
        else if (lockedSerial != null && (locked == false || target?.Serial != lockedSerial))
        {
            if (locked == false && unlockSerial == mirrorSerial && unlockSerial != null)
                mirrorSession?.Cancel();
            nextMirrorAttempt = DateTime.MinValue;
            lockedSerial = unlockSerial = null;
            statusChanged = true;
        }
        if (target is { Tool: "adb", State: "device" } && (lockedSerial == null || unlockSerial == target.Serial) &&
            mirrorSession == null && mirror.Available && page == "Device" &&
            (!automatic || DateTime.UtcNow >= nextMirrorAttempt))
        {
            nextMirrorAttempt = DateTime.MinValue;
            mirrorFailure = null;
            mirrorSerial = target.Serial;
            mirrorSession = new CancellationTokenSource();
            Render();
            MirrorPane.UpdateLayout();
            Report("MirrorStarting", "", target.Serial);
            _ = ObserveMirror(target.Serial, mirrorSession);
        }
        if (!automatic || statusChanged) Render();
        if (page == "Install" && target is { Tool: "adb", State: "device" } &&
            !inventoryLoading && (!automatic || DateTime.UtcNow >= nextInventory))
            _ = LoadInventory(target);
        if (((target is { Tool: "adb", State: "device" } && (page is "Device" or "Install" or "Fastboot")) || (target is { Tool: "fastboot", State: "fastboot" } && (page is "Device" or "Fastboot"))) &&
            !readingFacts && (!automatic || DateTime.UtcNow >= nextFactsRead))
            _ = RefreshFacts(target, revision, automatic);
    }

    private bool readingFacts;

    private async Task RefreshFacts(Target target, int revision, bool automatic)
    {
        readingFacts = true;
        nextFactsRead = DateTime.UtcNow.AddSeconds(15);
        try
        {
            var snapshot = target.Tool == "fastboot" ? await bridge.ReadBootState(target) : await bridge.ReadDeviceInfo(target);
            if (Selected != target || !IsLoaded || revision != generation) return;
            var previousBrand = facts.GetValueOrDefault("Brand");
            foreach (var (key, value) in snapshot) facts[key] = value;
            if (previousBrand != facts.GetValueOrDefault("Brand") && inventory.Count > 0) ApplyInventoryFilter();
            RenderDeviceFacts();
            if (target.Tool == "fastboot" || page == "Fastboot") return;
            if (!automatic || DateTime.UtcNow >= nextAppsRead)
            {
                var apps = await bridge.ReadAppTotals(target);
                if (Selected != target || !IsLoaded || revision != generation) return;
                foreach (var key in AppGroups)
                {
                    facts.Remove(key);
                    facts.Remove(key + "Size");
                }
                foreach (var (key, value) in apps) facts[key] = value;
                if (apps.ContainsKey("UserAppsSize") && apps.ContainsKey("SystemAppsSize"))
                    nextAppsRead = DateTime.UtcNow.AddMinutes(5);
                RenderDeviceFacts();
            }
        }
        catch (BridgeException ex)
        {
            if (Selected == target && IsLoaded && revision == generation) ReportError(ex);
        }
        finally { readingFacts = false; }
    }

    private bool wifiPending;
    private string wifiMessage = "";
    private DateTime nextWirelessScan;

    private List<(string Instance, Target Device)> wifiServices = [];

    private async Task RefreshWireless()
    {
        try { wifiServices = await bridge.DiscoverWireless(); }
        catch (BridgeException) { wifiServices = []; }
        if (WifiPopup.IsOpen && Targets.SelectedItem is Target { Tool: "wifi" } selected &&
            System.Net.IPEndPoint.TryParse(selected.Serial, out var endpoint))
        {
            var pair = wifiServices.Select(s => s.Device).FirstOrDefault(t => t.State == "pair" &&
                System.Net.IPEndPoint.TryParse(t.Serial, out var found) && found.Address.Equals(endpoint.Address));
            if (pair != null && !PairAddressBox.IsKeyboardFocused) PairAddressBox.Text = pair.Serial;
        }
    }

    private void OpenWifi(object sender, RoutedEventArgs e)
    {
        WifiStatus.Text = "";
        WifiPopup.IsOpen = true;
    }

    private string? qrInstance;

    private void ResetQr()
    {
        qrInstance = null;
        QrImage.Source = null;
        QrPanel.Visibility = Visibility.Collapsed;
        QrButton.Visibility = ManualWifi.Visibility = Visibility.Visible;
    }

    private void ShowManual(object sender, RoutedEventArgs e)
    {
        ResetQr();
        WifiStatus.Text = "";
    }

    private void WifiPopupClosed(object sender, EventArgs e)
    {
        ResetQr();
        WifiCode.Clear();
    }

    private async void StartQr(object sender, RoutedEventArgs e)
    {
        string instance = "studio-" + Guid.NewGuid().ToString("N")[..16];
        string code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        qrInstance = instance;
        using var data = QRCoder.QRCodeGenerator.GenerateQrCode($"WIFI:T:ADB;S:{instance};P:{code};;", QRCoder.QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCoder.PngByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(6));
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        QrImage.Source = bitmap;
        QrPanel.Visibility = Visibility.Visible;
        ManualWifi.Visibility = QrButton.Visibility = Visibility.Collapsed;
        WifiStatus.Text = "";
        var deadline = DateTime.UtcNow.AddMinutes(2);
        try
        {
            while (qrInstance == instance && DateTime.UtcNow < deadline)
            {
                var services = await bridge.DiscoverWireless();
                if (qrInstance != instance) return;
                var pairing = services.FirstOrDefault(s => s.Instance == instance && s.Device.State == "pair");
                if (pairing.Device != null)
                {
                    WifiStatus.Text = T("WifiWorking");
                    await bridge.Wireless(pairing.Device.Serial, code);
                    if (qrInstance != instance) return;
                    ResetQr();
                    WifiStatus.Text = wifiMessage = T("WifiPaired");
                    AppendLine(wifiMessage);
                    nextWirelessScan = DateTime.MinValue;
                    return;
                }
                await Task.Delay(1000);
            }
            if (qrInstance != instance) return;
            ResetQr();
            WifiStatus.Text = T("WifiQrExpired");
        }
        catch (BridgeException ex)
        {
            if (qrInstance != instance) return;
            ResetQr();
            WifiStatus.Text = ex.Key == "TimedOut" ? T("WifiTimeout") : ex.Key == "ToolFailed" ? ex.Message : T(ex.Key, "adb");
        }
    }

    private async void SelectWireless(Target target)
    {
        if (qrInstance != null || !Equals(Targets.SelectedItem, target)) return;
        if (target.State == "pair")
        {
            PairAddressBox.Text = target.Serial;
            WifiPopup.IsOpen = true;
            WifiCode.Focus();
            return;
        }
        if (busy) return;
        ConnectAddressBox.Text = target.Serial;
        await SubmitWifi(false);
    }

    private async void WifiClick(object sender, RoutedEventArgs e)
    {
        await SubmitWifi(Equals(((Button)sender).Tag, "pair"));
    }

    private async Task SubmitWifi(bool pair)
    {
        if (busy) return;
        string address = (pair ? PairAddressBox : ConnectAddressBox).Text.Trim();
        string? code = pair ? WifiCode.Password : null;
        if (pair) WifiCode.Clear();
        if (wifiPending) return;
        wifiPending = true;
        WifiForm.IsEnabled = false;
        var initialSelection = Targets.SelectedItem;
        try
        {
            wifiMessage = WifiStatus.Text = T("WifiWorking");
            Render();
            try
            {
                await bridge.Wireless(address, code);
                WifiStatus.Text = T(pair ? "WifiPaired" : "WifiConnected");
                AppendLine(WifiStatus.Text);
                wifiMessage = WifiStatus.Text;
                if (!pair) WifiPopup.IsOpen = false;
                if (busy || !Equals(initialSelection, Targets.SelectedItem)) return;
                await RefreshTargets(false, true);
                if (!pair && Targets.Items.Cast<Target>().FirstOrDefault(t => t.Tool == "adb" && t.Serial == address) is { } wireless)
                    Targets.SelectedItem = wireless;
            }
            catch (BridgeException ex)
            {
                WifiStatus.Text = ex.Key == "TimedOut" ? T("WifiTimeout") : ex.Key == "ToolFailed" ? ex.Message : T(ex.Key, "adb");
                wifiMessage = WifiStatus.Text;
                ReportError(ex);
            }
        }
        finally
        {
            wifiPending = false;
            WifiForm.IsEnabled = true;
            Render();
        }
    }

    private async void ScanClick(object sender, RoutedEventArgs e) => await Work(() => RefreshTargets(true), "Scanning");

    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        mirrorFailure = null;
        if (!updatingTargets) CheckMirrorTarget();
        nextMirrorAttempt = DateTime.MinValue;
        if (!updatingTargets && Targets.SelectedItem is Target { Tool: "wifi" } pending) SelectWireless(pending);
        Render();
    }

    private void CheckMirrorTarget()
    {
        if (Selected?.Serial != unlockSerial) unlockSerial = null;
        if (Selected?.Serial != rotatedSerial) rotatedSerial = null;
        if (Selected?.Serial != stayAwakeSerial) stayAwakeSerial = null;
        if (Selected?.Serial != mirrorSerial || Selected?.State != "device") mirrorSession?.Cancel();
    }

    private Target? factsTarget;
    private Dictionary<string, string> facts = new();
    private DateTime nextFactsRead, nextAppsRead;

    private void BatteryInfoClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string explanation })
            MessageBox.Show(this, explanation, T("BatteryHealth"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed record InventoryEntry(string Package, string Name, bool System, bool Enabled, bool Installed, string? Icon, long? Bytes = null);
    private static readonly Lazy<Dictionary<string, string[]>> ratings = new(() =>
    {
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("MasronDroid.debloat.gz")!;
        using var data = new System.IO.Compression.GZipStream(resource, System.IO.Compression.CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(data)!;
    });

    private sealed record InventoryRow(InventoryEntry Entry, ImageSource? Image)
    {
        private string[]? rating;
        public void MatchRating(string? group)
        {
            if (!Entry.System) return;
            rating = group == null ? null : ratings.Value.GetValueOrDefault(group + ":" + Entry.Package);
            rating ??= ratings.Value.GetValueOrDefault(Entry.Package);
        }
        public string Level => rating?[0] ?? "0";
        public string Size => Entry.Bytes switch
        {
            null => "—",
            >= 1073741824 => $"{Entry.Bytes / 1073741824d:0.##} GB",
            >= 1048576 => $"{Entry.Bytes / 1048576d:0.##} MB",
            >= 1024 => $"{Entry.Bytes / 1024d:0.##} KB",
            _ => $"{Entry.Bytes} B"
        };
        public string? RatingTip => rating == null ? null : rating[1] + Environment.NewLine + rating[2];
        public bool Checked { get; set; }
    }
    private List<InventoryRow> inventory = [];
    private DateTime nextInventory;
    private bool inventoryLoading;
    private int inventoryRevision;

    private async Task LoadInventory(Target target)
    {
        int revision = inventoryRevision;
        inventoryLoading = true;
        InventoryStatus.Text = T("AppLoading");
        try
        {
            string output = await bridge.ReadAppList(target);
            var rows = await Task.Run(() =>
            {
                var result = new List<InventoryRow>();
                foreach (var line in output.Split('\n').Where(l => l.StartsWith('{')))
                {
                    var entry = JsonSerializer.Deserialize<InventoryEntry>(line);
                    if (entry == null || string.IsNullOrEmpty(entry.Package)) continue;
                    System.Windows.Media.Imaging.BitmapImage? icon = null;
                    if (entry.Icon is { Length: > 0 })
                    {
                        using var stream = new MemoryStream(Convert.FromBase64String(entry.Icon));
                        icon = new();
                        icon.BeginInit();
                        icon.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        icon.StreamSource = stream;
                        icon.EndInit();
                        icon.Freeze();
                    }
                    result.Add(new(entry, icon));
                }
                if (result.Count == 0) throw new InvalidDataException();
                return result.OrderBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            });
            if (Selected != target || revision != inventoryRevision) return;
            var selectedPackages = inventory.Where(x => x.Checked).Select(x => x.Entry.Package).ToHashSet();
            foreach (var row in rows) row.Checked = selectedPackages.Contains(row.Entry.Package);
            inventory = rows;
            RenderDeviceFacts();
            nextInventory = DateTime.UtcNow.AddMinutes(5);
            ApplyInventoryFilter();
        }
        catch (Exception ex)
        {
            if (Selected != target) return;
            nextInventory = DateTime.UtcNow.AddSeconds(30);
            InventoryStatus.Text = T("AppReadError");
            if (ex is BridgeException problem) ReportError(problem);
        }
        finally { inventoryLoading = false; }
    }

    private void InventoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) { ApplyInventoryFilter(); RenderDeviceFacts(); }
    }

    private void ClearInventorySearch(object sender, RoutedEventArgs e)
    {
        InventorySearch.Clear();
        InventorySearch.Focus();
    }

    private void InventorySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplyInventoryFilter();
    }

    private void ApplyInventoryFilter()
    {
        string? group = facts.GetValueOrDefault("Brand", "").Trim().ToLowerInvariant() switch
        {
            "vivo" or "iqoo" => "vivo",
            "oppo" or "realme" => "oppo",
            "itel" or "infinix" or "tecno" or "transsion" => "transsion",
            "samsung" => "samsung",
            "xiaomi" or "redmi" or "poco" => "xiaomi",
            _ => null
        };
        foreach (var row in inventory) row.MatchRating(group);
        var rows = inventory.Where(x => InventoryFilter.SelectedIndex switch
        {
            1 => x.Entry.Installed && x.Entry.Enabled,
            2 => x.Entry.Installed && x.Entry.System,
            3 => x.Entry.Installed && !x.Entry.System,
            4 => x.Entry.Installed && !x.Entry.Enabled,
            5 => !x.Entry.Installed,
            _ => x.Entry.Installed
        }).ToList();
        string query = InventorySearch.Text.Trim();
        if (query.Length > 0)
            rows = rows.Where(x => x.Entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || x.Entry.Package.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        InventoryList.ItemsSource = InventorySort.SelectedIndex switch
        {
            1 => rows.OrderByDescending(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => rows.OrderBy(x => x.Entry.Package, StringComparer.OrdinalIgnoreCase),
            3 => rows.OrderByDescending(x => x.Level).ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase),
            4 => rows.OrderBy(x => x.Level == "0").ThenBy(x => x.Level).ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.AsEnumerable()
        };
        UpdateBatchButtons();
        InventoryStatus.Text = Selected is not { Tool: "adb", State: "device" } ? T("NeedAdb")
            : inventory.Count == 0 ? T("AppLoading") : T("AppResults", rows.Count);
    }

    private static readonly string[] AppGroups = ["TotalApps", "SystemApps", "UserApps", "DisabledApps", "RemovedApps"];

    private void RenderDeviceFacts()
    {
        if (factsTarget != Selected)
        {
            factsTarget = Selected;
            facts.Clear();
            inventory.Clear();
            BatchStatus.Text = "";
            ApplyInventoryFilter();
            nextInventory = DateTime.MinValue;
            nextFactsRead = nextAppsRead = DateTime.MinValue;
        }
        if (page == "Install")
            AppSummary.ItemsSource = AppGroups.Select(key =>
            {
                var group = inventory.Where(x => key switch
                {
                    "RemovedApps" => !x.Entry.Installed,
                    "SystemApps" => x.Entry.Installed && x.Entry.System,
                    "UserApps" => x.Entry.Installed && !x.Entry.System,
                    "DisabledApps" => x.Entry.Installed && !x.Entry.Enabled,
                    _ => x.Entry.Installed
                }).ToList();
                string count = facts.GetValueOrDefault(key, "—");
                string size = facts.GetValueOrDefault(key + "Size", "—");
                if (inventory.Count > 0)
                {
                    count = group.Count.ToString();
                    if (group.All(x => x.Entry.Bytes.HasValue))
                    {
                        double mb = group.Sum(x => x.Entry.Bytes ?? 0) / 1048576d;
                        size = mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F1} MB";
                    }
                }
                return new { Label = T(key), Count = count, Size = size };
            }).ToArray();
        var rows = new Dictionary<string, string>();
        string healthHint = "";
        if (Selected is { } target)
        {
            foreach (var key in new[] { "Ram", "Storage", "Battery", "BatteryHealth", "Hardware", "UserApps", "SystemApps", "Rom", "Android" })
            {
                if (key == "BatteryHealth")
                {
                    if (facts.Count == 0) continue;
                    bool valid = decimal.TryParse(facts.GetValueOrDefault("BSOH"), System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out var health) && health is >= 0 and <= 100;
                    rows[key] = valid ? $"{health:0.##}%" : T("HealthUnavailable");
                    healthHint = valid ? T("BSOHHint", rows[key]) : T("NoBSOHHint");
                    if (int.TryParse(facts.GetValueOrDefault("ASOC"), out var asoc) && asoc is >= 0 and <= 100)
                        healthHint += Environment.NewLine + T("ASOCHint", asoc);
                    if (int.TryParse(facts.GetValueOrDefault(key), out var status) && status is >= 1 and <= 7)
                        healthHint += Environment.NewLine + T("ReportedCondition", T("Health" + status));
                }
                else if ((key == "UserApps" || key == "SystemApps") && facts.TryGetValue(key, out var count))
                    rows[key] = count + "  |  APK: " + facts.GetValueOrDefault(key + "Size", T("HealthUnavailable"));
                else if (facts.TryGetValue(key, out var value)) rows[key] = value;
            }
            rows["BootState"] = T(facts.GetValueOrDefault("BootState", "BootUnknown"));
            rows["Serial"] = target.Serial;
        }
        var state = facts.GetValueOrDefault("BootState", "BootUnknown");
        var bootStyle = state switch
        {
            "BootLocked" => ("#205F50", "#E3F4EC", "M5,10 H19 V21 H5 Z M8,10 V7 A4,4 0 0 1 16,7 V10 M12,14 V17"),
            "BootOpen" => ("#875412", "#FFF1D7", "M5,10 H19 V21 H5 Z M8,10 V7 A4,4 0 0 1 16,7 M12,14 V17"),
            _ => ("#506070", "#EDF1F4", "M12,2 L20,5 V12 Q20,17 12,22 Q4,17 4,12 V5 Z")
        };
        var converter = new System.Windows.Media.BrushConverter();
        FastbootLockText.Text = T(state);
        BootGlyph.Text = state switch { "BootLocked" => "\uE72E", "BootOpen" => "\uE785", _ => "\uE946" };
        BootGlyph.Foreground = FastbootLockText.Foreground = (System.Windows.Media.Brush)converter.ConvertFromString(bootStyle.Item1)!;
        BootBadge.Background = (System.Windows.Media.Brush)converter.ConvertFromString(bootStyle.Item2)!;
        UnlockBoot.Visibility = Selected?.Tool == "fastboot" && facts.GetValueOrDefault("CanUnlock") == "1" ? Visibility.Visible : Visibility.Collapsed;
        FlashPartition.Visibility = Selected?.Tool == "fastboot" && facts.GetValueOrDefault("BootState") == "BootOpen" ? Visibility.Visible : Visibility.Collapsed;
        FactsCard.Visibility = Selected == null ? Visibility.Collapsed : Visibility.Visible;
        DeviceFacts.ItemsSource = rows.Select(row =>
        {
            var (accent, tint, icon) = row.Key switch
            {
                "BootState" => bootStyle,
                "Ram" => ("#6353A6", "#F0EDFA", "M4,7 H20 V16 H4 Z M7,10 V13 M11,10 V13 M15,10 V13 M7,16 V19 M11,16 V19 M15,16 V19"),
                "Storage" => ("#306B99", "#EAF3FA", "M5,5 H19 L21,15 V19 H3 V15 Z M3,14 H21 M15,17 H18"),
                "Battery" => ("#19734D", "#E4F5EC", "M3,7 H19 V17 H3 Z M21,10 V14 M7,10 V14 M11,10 V14"),
                "BatteryHealth" => ("#A14561", "#FBEAF0", "M12,20 L4,12 C-1,5 7,1 12,7 C17,1 25,5 20,12 Z M6,12 H9 L11,9 L13,15 L15,12 H18"),
                "Hardware" => ("#6353A6", "#F0EDFA", "M5,5 H19 V19 H5 Z M9,9 H15 V15 H9 Z M8,2 V5 M16,2 V5 M8,19 V22 M16,19 V22"),
                "UserApps" or "SystemApps" => ("#A35F16", "#FFF2DF", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z"),
                "Rom" => ("#14767C", "#E4F4F4", "M12,3 L22,8 L12,13 L2,8 Z M3,12 L12,17 L21,12 M3,16 L12,21 L21,16"),
                "Android" => ("#47732B", "#EFF5E7", "M4,16 A8,8 0 0 1 20,16 Z M7,8 L5,4 M17,8 L19,4 M8,12 H9 M15,12 H16"),
                _ => ("#526577", "#ECF1F5", "M5,5 V19 M8,5 V19 M12,5 V19 M16,5 V19 M19,5 V19")
            };
            string? percent = row.Key switch
            {
                "Ram" => facts.GetValueOrDefault("RamPercent"),
                "Battery" => row.Value.TrimEnd('%'),
                _ => null
            };
            bool metered = int.TryParse(percent, out int level) && level is >= 0 and <= 100;
            var hint = row.Key == "BatteryHealth" ? healthHint : "";
            return new { Hint = hint, HintVisibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed, Label = T(row.Key), row.Value, Accent = accent, Tint = tint, Icon = icon,
                Level = metered ? level : 0, MeterVisibility = metered ? Visibility.Visible : Visibility.Collapsed };
        }).ToArray();
    }

    private void Render()
    {
        if (!initialized) return;
        PageTitle.Text = T(page == "Install" ? "ApplicationMenu" : page);
        PageDescription.Text = T(page + "Desc");
        foreach (var panel in new[] { DevicePage, InstallPage, FastbootPage, RecoveryPage })
            panel.Visibility = panel.Name == page + "Page" ? Visibility.Visible : Visibility.Collapsed;
        OdinNotice.Visibility = odin ? Visibility.Visible : Visibility.Collapsed;
        TargetHint.Text = T(Targets.Items.Count == 0 ? (odin ? "OdinTitle" : mtp ? "WaitingAdb" : "EmptyTarget") : "PickTarget");
        var wifi = Targets.SelectedItem as Target;
        bool wifiSelected = wifi?.Tool == "wifi";
        if (wifiSelected) WifiPanelButton.Visibility = Visibility.Visible;
        TargetHint.Visibility = Targets.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        bool adb = bridge.Has("adb");
        _ = SetPicture(Selected?.Image);
        RenderDeviceFacts();
        string? reason = !adb ? T("NoToolsHero") : !mirror.Available ? T("MirrorMissing")
            : Selected == null ? T(odin ? "OdinHint" : mtp ? "MtpHint" : Targets.Items.Count > 0 ? "PickTarget" : "EmptyHint")
            : Selected.State is "unauthorized" or "authorizing" ? T("AuthHint")
            : Selected.State == "offline" ? T("ModeOffline") + ". " + T("OfflineHint")
            : Selected.Tool != "adb" || Selected.State != "device" ? ModeText(Selected) + ". " + T("NeedAdb")
            : Selected.Serial == lockedSerial ? T("UnlockPhone") : mirrorFailure;
        if (wifiSelected) reason = null;
        MirrorReason.Text = reason;
        MirrorReason.Visibility = page != "Device" || string.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible;
        DeviceHeadline.ToolTip = Selected?.Model;
        DeviceHeadline.Text = wifiSelected ? wifi!.SelectorText : Selected is { } device ? (device.Model.Length > 0 ? device.Model : device.SelectorText)
            : T(Targets.Items.Count > 0 ? "PickTarget" : odin ? "OdinTitle" : !adb ? "NoToolsHero" : mtp ? "MtpTitle" : "EmptyHero");
        DeviceHint.Text = wifiSelected ? (wifiMessage.Length > 0 ? wifiMessage : T("WifiGuide")) : lockedSerial != null && Selected?.Serial == lockedSerial ? T("PhoneLocked") : Selected is { } active ? ModeText(active) + "   ·   " + T(active.State switch
        {
            "unauthorized" or "authorizing" => "AuthHint",
            "offline" => "OfflineHint",
            _ => "ReadyHint"
        }) : T(odin && Targets.Items.Count == 0 ? "OdinHint" : partialScan ? "ScanError" : Targets.Items.Count > 0 ? "MultiHint" : !adb ? "NoToolsHint" : mtp ? "MtpHint" : "EmptyHint");
        var panelSerial = Selected is { Tool: "adb", State: "device" } ? Selected.Serial : null;
        bool viewing = panelSerial != null && page == "Device";
        bool dark = (lockedSerial == panelSerial && unlockSerial != panelSerial) || mirrorSession == null || mirrorSerial != panelSerial;
        VideoSurface.Visibility = dark ? Visibility.Hidden : Visibility.Visible;
        bool console = page == "Console";
        bool landscape = viewing && !console && rotatedSerial == panelSerial;
        ConsolePage.Visibility = console ? Visibility.Visible : Visibility.Collapsed;
        AdbCommand.IsReadOnly = !adb;
        RecoveryInput.IsReadOnly = !adb;

        StopCommand.IsEnabled = consoleCommand != null;
        Grid.SetRowSpan(ManagementPane, landscape ? 1 : 2);
        Grid.SetColumnSpan(ManagementPane, landscape ? 2 : 1);
        PageScroll.Visibility = (landscape || console) ? Visibility.Collapsed : Visibility.Visible;
        PageDescription.Visibility = landscape ? Visibility.Collapsed : Visibility.Visible;
        LandscapeDevice.Visibility = landscape ? Visibility.Visible : Visibility.Collapsed;
        MirrorPane.Margin = new Thickness(landscape ? 8 : 0, landscape ? 0 : 36, 8, 12);
        Grid.SetColumn(MirrorPane, landscape ? 0 : 1);
        Grid.SetColumnSpan(MirrorPane, landscape ? 2 : 1);
        AndroidWake.Visibility = viewing && Selected is { State: "device", Tool: "adb" } && Selected.Serial == lockedSerial ? Visibility.Visible : Visibility.Collapsed;
        AndroidWake.IsEnabled = !busy && !navigationPending;
        MirrorPane.Visibility = viewing && !console ? Visibility.Visible : Visibility.Collapsed;
        StayAwake.IsEnabled = viewing && !dark && mirrorSession is { IsCancellationRequested: false };
        StayAwake.IsChecked = viewing && panelSerial == stayAwakeSerial;
        FitMirrorColumn();
        AndroidCamera.IsEnabled = AndroidPower.IsEnabled = AndroidVolumeUp.IsEnabled = AndroidVolumeDown.IsEnabled = AndroidRotate.IsEnabled =
            AndroidHome.IsEnabled = AndroidBack.IsEnabled = AndroidRecent.IsEnabled = !busy && !navigationPending && viewing && Selected?.Serial == mirrorSerial && lockedSerial != mirrorSerial;
        MirrorTitle.Text = Selected?.SelectorText ?? T("Mirror");
        ZipName.Text = zip.Length > 0 ? Path.GetFileName(zip) : T("NoFile");
        bool Allow(DeviceAction action) => !busy && AndroidBridge.Allows(Selected, action);
        ScanButton.IsEnabled = !busy;
        Targets.IsEnabled = !busy && Targets.Items.Count > 0;
        Navigation.IsEnabled = Languages.IsEnabled = PageScroll.IsEnabled = !busy;
        UpdateBatchButtons();
        RestartPhone.IsEnabled = Allow(DeviceAction.Restart);
        ShutdownPhone.IsEnabled = Allow(DeviceAction.Shutdown);
        FlashPartition.IsEnabled = Allow(DeviceAction.Product);
        ReadSlot.IsEnabled = Allow(DeviceAction.ReadSlot);
        ReadBootVersion.IsEnabled = Allow(DeviceAction.BootVersion);
        EnterBoot.IsEnabled = Allow(DeviceAction.Bootloader);
        ReadProduct.IsEnabled = Allow(DeviceAction.Product);
        LeaveBoot.IsEnabled = Selected?.Tool == "fastboot" && Allow(DeviceAction.System);
        RecoveryMode.Text = Selected is { } recoveryTarget ? ModeText(recoveryTarget) : T("NoTarget");
        RecoveryBoot.IsEnabled = Selected?.State == "recovery" && Allow(DeviceAction.Bootloader);
        RecoveryQueries.IsEnabled = Allow(DeviceAction.RecoveryLog);
        if (Selected != recoveryResultTarget)
        {
            RecoveryResult.Clear();
            RecoveryResult.Visibility = Visibility.Collapsed;
        }
        EnterRecovery.IsEnabled = Allow(DeviceAction.Recovery);
        LeaveRecovery.IsEnabled = Selected?.State == "recovery" && Allow(DeviceAction.System);
        StartSideload.Visibility = Selected is { Tool: "adb", State: "recovery" } ? Visibility.Visible : Visibility.Collapsed;
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
        var dialog = new OpenFileDialog { Filter = T("ZIP") + " (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            zip = dialog.FileName;
            Render();
        }
    }

    private void StayAwakeClick(object sender, RoutedEventArgs e)
    {
        if (mirrorSession == null || mirrorSerial == null || mirrorSession.IsCancellationRequested) return;
        stayAwakeSerial = StayAwake.IsChecked == true ? mirrorSerial : null;
        mirrorSession.Cancel();
    }

    private DateTime nextMirrorAttempt;
    private string? mirrorFailure;
    private string? rotatedSerial;
    private async void RotateClick(object sender, RoutedEventArgs e)
    {
        if (busy || navigationPending || Selected is not { } target || target.Serial != mirrorSerial ||
            mirrorSession is not { IsCancellationRequested: false } session) return;
        bool landscape = rotatedSerial != target.Serial;
        navigationPending = true;
        Render();
        try
        {
            await bridge.Navigate(target, landscape ? "Landscape" : "Portrait");
            if (mirrorSession == session && Selected?.Serial == target.Serial && !session.IsCancellationRequested)
                rotatedSerial = landscape ? target.Serial : null;
        }
        catch (BridgeException ex) { ReportError(ex); }
        finally { navigationPending = false; Render(); }
    }
    private bool navigationPending;

    private void FitAppList(object sender, EventArgs e)
    {
        if (!InventoryList.IsVisible || PageScroll.ViewportHeight <= 0) return;
        double top = InventoryList.TranslatePoint(new Point(), PageScroll).Y + PageScroll.VerticalOffset;
        double height = Math.Max(160, Math.Floor(PageScroll.ViewportHeight - top - 32));
        if (Math.Abs(InventoryList.Height - height) > 1) InventoryList.Height = height;
    }

    private void WorkspaceResized(object sender, SizeChangedEventArgs e) => FitMirrorColumn();

    private void FitMirrorColumn()
    {
        if (MirrorColumn == null || VideoSurface == null) return;
        double width = MirrorPane.Visibility != Visibility.Visible ? 0 : Math.Min(WorkspaceBody.ActualWidth * 0.43,
            Math.Max(1, VideoSurface.ActualHeight) * VideoSurface.AspectRatio + MirrorPane.Margin.Right + MirrorPane.Padding.Left + MirrorPane.Padding.Right);
        MirrorColumn.Width = new GridLength(width);
    }

    private async void AndroidNavClick(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        if (busy || navigationPending || Selected is not { } target) return;
        if (key == "Wake" ? target.Serial != lockedSerial : mirrorSession == null || target.Serial != mirrorSerial) return;
        navigationPending = true;
        Render();
        try
        {
            await bridge.Navigate(target, key);
            if (key == "Wake" && Selected?.Serial == target.Serial)
            {
                unlockSerial = target.Serial;
                nextMirrorAttempt = DateTime.MinValue;
            }
            AppendLine(T("Android" + key));
        }
        catch (BridgeException ex) { ReportError(ex); }
        finally { navigationPending = false; Render(); }
    }

    private async Task ObserveMirror(string serial, CancellationTokenSource session)
    {
        try
        {
            var result = await mirror.Watch(serial, stayAwakeSerial == serial, window =>
            {
                if (session.IsCancellationRequested || Selected?.Serial != serial || mirrorSession != session) return;
                VideoSurface.Attach(window);
                FitMirrorColumn();
            }, session.Token);
            if (!session.IsCancellationRequested)
            {
                if (Selected is { State: "device" } target && target.Serial == serial && await bridge.IsLocked(target) == true)
                {
                    lockedSerial = serial;
                    Report("UnlockPhone");
                }
                else if (result.Ok) Report("MirrorClosed", "", serial);
                else
                {
                    mirrorFailure = result.Output.Split('\n').LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim()
                        ?? result.Output.Trim().Split('\n').LastOrDefault();
                    if (string.IsNullOrWhiteSpace(mirrorFailure)) mirrorFailure = T("MirrorFailed");
                    Report("MirrorFailed", result.Output);
                }
            }
        }
        catch (BridgeException ex) { if (!session.IsCancellationRequested) { ReportError(ex); mirrorFailure = ex.Message.Length > 0 ? ex.Message : T(ex.Key); } }
        catch (Exception ex) { if (!session.IsCancellationRequested) { mirrorFailure = ex.Message; Report("MirrorFailed", ex.Message); } }
        finally { if (!session.IsCancellationRequested) nextMirrorAttempt = DateTime.UtcNow.AddSeconds(8); VideoSurface.Forget(); mirrorSerial = null; mirrorSession = null; session.Dispose(); if (IsLoaded) Render(); }
    }

    private void InventoryRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DockPanel panel && panel.Children[0] is CheckBox box && !box.IsMouseOver)
        {
            box.SetCurrentValue(CheckBox.IsCheckedProperty, box.IsChecked != true);
            UpdateBatchButtons();
            e.Handled = true;
        }
    }

    private void InventoryChecked(object sender, RoutedEventArgs e) => UpdateBatchButtons();

    private void UpdateBatchButtons()
    {
        var selected = inventory.Where(x => x.Checked).ToArray();
        bool ready = !busy && Selected is { Tool: "adb", State: "device" };
        RestoreApps.IsEnabled = ready;
        InstallApk.IsEnabled = ready;
        bool installed = ready && selected.Length > 0 && selected.All(x => x.Entry.Installed);
        BatchUninstall.IsEnabled = installed;
        BatchDisable.IsEnabled = installed && selected.All(x => x.Entry.Enabled);
        BatchEnable.IsEnabled = installed && selected.All(x => !x.Entry.Enabled);
        BatchCount.Text = T("BatchCount", selected.Length);
    }

    private static string BackupPackage(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int suffix = name.LastIndexOf('_');
        int date = suffix > 0 ? name.LastIndexOf('_', suffix - 1) : -1;
        return date > 0 ? name[..date] : name;
    }

    private string[] PickLocalBackups()
    {
        string[] files;
        try
        {
            files = Directory.Exists(AndroidBridge.BackupFolder)
                ? Directory.GetFiles(AndroidBridge.BackupFolder, "*.mdbackup") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BatchStatus.Text = ex.Message;
            return [];
        }
        var dialog = new Window { Owner = this, Title = T("RestoreBackup"), Width = 460, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var choices = new StackPanel();
        var heading = new TextBlock { Text = T(files.Length == 0 ? "NoBackups" : "ChooseBackups"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var accept = new Button { Content = T("RestoreBackup"), IsEnabled = false, Style = (Style)FindResource("Primary") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = T("Cancel"), IsCancel = true, Margin = new Thickness(0,0,8,0) });
        buttons.Children.Add(accept);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        foreach (var group in files.GroupBy(BackupPackage).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var row = inventory.FirstOrDefault(x => x.Entry.Package == group.Key);
            var entry = row?.Entry;
            ImageSource? icon = row?.Image;
            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(latest);
                if (archive.GetEntry("icon.png") is { Length: > 0 and <= 262144 } image)
                {
                    using var stream = image.Open();
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.DecodePixelWidth = 40;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    icon = bitmap;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FormatException or UnauthorizedAccessException) { }
            var label = entry == null || entry.Name == group.Key ? group.Key : entry.Name + "  ·  " + group.Key;
            var content = new DockPanel();
            FrameworkElement thumbnail = icon != null ? new System.Windows.Controls.Image { Source = icon } :
                new TextBlock { Text = "\uECAA", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 24, TextAlignment = TextAlignment.Center };
            thumbnail.Width = thumbnail.Height = 32;
            thumbnail.Margin = new Thickness(8, 0, 10, 0);
            content.Children.Add(thumbnail);
            content.Children.Add(new TextBlock { Text = label + Environment.NewLine + File.GetLastWriteTime(latest).ToString("g"), TextWrapping = TextWrapping.Wrap });
            var check = new CheckBox { Content = content, VerticalContentAlignment = VerticalAlignment.Center,
                Tag = latest, Margin = new Thickness(2,5,2,5) };
            check.Click += (_, _) => accept.IsEnabled = choices.Children.OfType<CheckBox>().Any(x => x.IsChecked == true);
            choices.Children.Add(check);
        }
        panel.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? choices.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray() : [];
    }

    private async void BatchClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } device) return;
        var action = Enum.Parse<DeviceAction>((string)((Button)sender).Tag);
        string[] values;
        if (action == DeviceAction.RestoreBackup)
        {
            var backups = PickLocalBackups();
            if (backups.Length == 0) return;
            values = backups;
        }
        else if (action == DeviceAction.Install)
        {
            var picker = new OpenFileDialog { Filter = "APK (*.apk)|*.apk", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            values = picker.FileNames;
        }
        else values = inventory.Where(x => x.Checked).Select(x => x.Entry.Package).ToArray();
        string label = T(action.ToString());
        var details = action is DeviceAction.Disable or DeviceAction.Uninstall
            ? inventory.Where(x => x.Checked).Select(x => x.Entry.Package + "  (" + T("Risk" + x.Level) + ")")
            : action == DeviceAction.RestoreBackup ? values.Select(path => BackupPackage(path)) : values.AsEnumerable();
        if (values.Length == 0 || !Confirm(label, T(action + "Risk"), device, string.Join(Environment.NewLine, details), action is DeviceAction.Enable or DeviceAction.Disable ? values : null)) return;
        if (Selected != device) return;
        await Work(async () =>
        {
            int success = 0, failed = 0;
            foreach (string value in values)
            {
                BatchStatus.Text = $"{label} ({success + failed + 1}/{values.Length})";
                try
                {
                    var backupIcon = action == DeviceAction.Uninstall ? inventory.FirstOrDefault(x => x.Entry.Package == value)?.Entry.Icon : null;
                    await bridge.Execute(device, action, value, backupIcon);
                    success++;
                    var row = inventory.FirstOrDefault(x => x.Entry.Package == value);
                    if (row != null && Selected == device)
                    {
                        row.Checked = false;
                        if (action == DeviceAction.Uninstall)
                        {
                            inventoryRevision++;
                            inventory[inventory.IndexOf(row)] = row with { Entry = row.Entry with { Installed = false } };
                            ApplyInventoryFilter();
                            RenderDeviceFacts();
                        }
                    }
                }
                catch (BridgeException ex)
                {
                    failed++;
                    AppendLine(value);
                    ReportError(ex);
                    if (ex.Key == "TargetChanged") break;
                }
            }
            BatchStatus.Text = T("BatchResult", success, failed, values.Length - success - failed);
            AppendLine(BatchStatus.Text);
            InventoryList.Items.Refresh();
            nextInventory = nextFactsRead = nextAppsRead = DateTime.MinValue;
        });
    }

    private async void UnlockBootClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "fastboot" } target || !Confirm(T("UnlockBoot"), T("UnlockRisk"), target, "")) return;
        await Work(async () =>
        {
            Report("Done", await bridge.UnlockBootloader(target), "UnlockBoot");
            await RefreshTargets(false);
        });
    }

    private async void FlashPartitionClick(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Tool: "fastboot" } target) return;
        await Work(async () =>
        {
            var partitions = await bridge.ReadFlashPartitions(target);
            if (partitions.Length == 0) throw new BridgeException("FlashNoPartitions");
            var dialog = new Window { Owner = this, Title = T("FlashImage"), Width = 390, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = T("FlashPartitionLabel"), FontWeight = FontWeights.SemiBold });
            var choice = new ComboBox { ItemsSource = partitions, Margin = new Thickness(0,8,0,14) };
            panel.Children.Add(choice);
            var next = new Button { Content = T("FlashPickFile"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
            choice.SelectionChanged += (_, _) => next.IsEnabled = choice.SelectedItem != null;
            if (partitions.Length == 1) choice.SelectedIndex = 0;
            next.Click += (_, _) => dialog.DialogResult = true;
            panel.Children.Add(next);
            dialog.Content = panel;
            if (dialog.ShowDialog() != true || choice.SelectedItem is not string partition) return;
            var file = new OpenFileDialog { Filter = "Android image (*.img)|*.img", Title = T("FlashPickFile") };
            if (file.ShowDialog(this) != true || Selected != target) return;
            if (!Confirm(T("FlashImage"), T("FlashRisk"), target, partition + Environment.NewLine + file.FileName)) return;
            try { Report("Done", await bridge.FlashImageFile(target, partition, file.FileName), "FlashImage"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new BridgeException("ToolFailed", ex.Message); }
        });
    }

    private async void ActionClick(object sender, RoutedEventArgs e)
    {
        var action = Enum.Parse<DeviceAction>((string)((Button)sender).Tag);
        await RunAction(action, action == DeviceAction.Sideload ? zip : "");
    }

    private Target? recoveryResultTarget;

    private async Task RunAction(DeviceAction action, string value)
    {
        if (busy || Selected is not { } device || !AndroidBridge.Allows(device, action)) return;
        var (label, risk) = action switch
        {
            DeviceAction.StartSideload => ("StartSideload", "StartSideloadHint"),
            DeviceAction.Install => ("Install", "InstallRisk"), DeviceAction.Sideload => ("Sideload", "SideloadRisk"),
            DeviceAction.Disable => ("Disable", "DisableRisk"), DeviceAction.Enable => ("Enable", "EnableRisk"),
            DeviceAction.Bootloader => ("ToBoot", "RebootRisk"), DeviceAction.Recovery => ("ToRecovery", "RebootRisk"),
            DeviceAction.RecoveryLog or DeviceAction.RecoveryStorage or DeviceAction.RecoveryFiles => (action.ToString(), ""),
            DeviceAction.ReadSlot => ("ReadSlot", ""),
            DeviceAction.BootVersion => ("BootVersion", ""),
            DeviceAction.Restart => ("RestartPhone", "RestartPhoneRisk"),
            DeviceAction.Shutdown => ("ShutdownPhone", "ShutdownPhoneRisk"),
            DeviceAction.System => ("ToSystem", "RebootRisk"), _ => ("Product", "")
        };
        if (risk.Length > 0 && !Confirm(T(label), T(risk), device, value)) return;
        await Work(async () =>
        {
            bool recoveryQuery = action is DeviceAction.RecoveryLog or DeviceAction.RecoveryStorage or DeviceAction.RecoveryFiles;
            if (recoveryQuery) RecoveryResult.Visibility = Visibility.Collapsed;
            var output = await bridge.Execute(device, action, value);
            if (recoveryQuery && Selected == device)
            {
                recoveryResultTarget = device;
                RecoveryResult.Text = output;
                RecoveryResult.Visibility = Visibility.Visible;
            }
            nextInventory = nextAppsRead = nextFactsRead = DateTime.MinValue;
            Report("Done", output, label);
            if (action is DeviceAction.Bootloader or DeviceAction.Recovery or DeviceAction.System or DeviceAction.Sideload or DeviceAction.StartSideload or DeviceAction.Restart or DeviceAction.Shutdown)
                await RefreshTargets(false);
        });
    }

    private bool Confirm(string label, string risk, Target target, string value, string[]? packages = null)
    {
        var dialog = new Window
        {
            Title = T("ConfirmTitle"), Width = 490, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 14, Foreground = Foreground,
            WindowStyle = WindowStyle.None, Background = Brushes.White
        };
        var body = new StackPanel { Margin = new Thickness(24) };
        bool removal = label == T("Uninstall");
        bool shutdown = label == T("ShutdownPhone");
        var accent = new SolidColorBrush((removal || shutdown) ? Color.FromRgb(166, 52, 49) : Color.FromRgb(21, 101, 119));
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush((removal || shutdown) ? Color.FromRgb(253, 234, 231) : Color.FromRgb(228, 242, 244)),
            Child = new TextBlock { Text = shutdown ? "\uE7E8" : removal ? "\uE74D" : "\uE946", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        heading.Children.Add(new TextBlock { Text = label, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = risk, Margin = new Thickness(0, 12, 0, 16), TextWrapping = TextWrapping.Wrap });
        var deviceInfo = new StackPanel();
        deviceInfo.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(target.Model) ? target.Serial : target.Model, FontWeight = FontWeights.SemiBold });
        deviceInfo.Children.Add(new TextBlock { Text = target.Serial + "  ·  " + target.Tool.ToUpperInvariant(), FontSize = 12, Foreground = Brushes.SlateGray });
        body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(235, 244, 245)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9), Child = deviceInfo });
        if (value.Length > 0)
        {
            var entries = new StackPanel();
            var lines = value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var text = new TextBlock { Text = lines[i], TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(2, 4, 2, 4), VerticalAlignment = VerticalAlignment.Center };
                if (packages == null)
                {
                    entries.Children.Add(text);
                    continue;
                }
                var app = inventory.FirstOrDefault(x => x.Entry.Package == packages[i]);
                var item = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                FrameworkElement thumbnail = app?.Image is { } icon
                    ? new System.Windows.Controls.Image { Source = icon }
                    : new TextBlock { Text = "\uECAA", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 24, TextAlignment = TextAlignment.Center };
                thumbnail.Width = thumbnail.Height = 32;
                thumbnail.Margin = new Thickness(0, 0, 8, 0);
                item.Children.Add(thumbnail);
                item.Children.Add(text);
                entries.Children.Add(item);
            }
            body.Children.Add(new ScrollViewer { Margin = new Thickness(0, 10, 0, 0), MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = entries });
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = label, Style = (Style)FindResource("Primary") };
        if (removal || shutdown) accept.Background = accent;
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
        if (busy) { e.Cancel = true; Report("CloseBusy"); return; }
        preferences.History = log.ToArray();
        preferences.Page = page;
        preferences.Maximized = WindowState == WindowState.Maximized;
        preferences.WindowWidth = RestoreBounds.Width;
        preferences.WindowHeight = RestoreBounds.Height;
        SavePreferences();
    }
}
