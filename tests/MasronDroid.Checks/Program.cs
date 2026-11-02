using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MasronDroid;

namespace MasronDroid.Checks;

internal sealed class FakeTools : IPlatformTools
{
    internal string Adb = "demo-01\tdevice model:Demo_Phone\n";
    internal string Fastboot = "";
    internal bool Locked;
    internal bool Available = true, FailAdbScan, Unconfirmed, Disabled;
    internal string? Failure;
    internal List<(string Tool, string[] Args)> Commands = [];
    public bool Has(string tool) => Available;
    public Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Commands.Add((tool, args.ToArray()));
        if (args[0] == "devices")
            return Task.FromResult(tool == "adb" && FailAdbScan ? new ToolResult(false, "test failure")
                : new ToolResult(true, tool == "adb" ? Adb : Fastboot));
        var cmd = args.Skip(2).ToArray();
        if (Failure != null) return Task.FromResult(new ToolResult(true, Failure));
        if (cmd.Contains("path")) return Task.FromResult(new ToolResult(true, "package:/data/app/demo/base.apk\npackage:/data/app/demo/split_config.apk"));
        if (cmd.Contains("sha256sum")) return Task.FromResult(new ToolResult(true, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 })) + "  " + cmd.Last()));
        if (cmd.FirstOrDefault() == "pull") File.WriteAllBytes(cmd.Last(), new byte[] { 1, 2 });
        if (cmd.Contains("dumpsys")) return Task.FromResult(new ToolResult(true, $"KeyguardServiceDelegate state:\n  showing={Locked.ToString().ToLowerInvariant()}"));
        if (cmd.Any(x => x.Contains("app_process / AppInventory")))
            return Task.FromResult(new ToolResult(true, string.Join("\n", new[] { "one", "two" }.Select(name =>
                System.Text.Json.JsonSerializer.Serialize(new { Name = name, Package = "org.example." + name, Enabled = true, Installed = true, System = false })))));
        if (cmd.Contains("disable-user")) Disabled = !Unconfirmed;
        if (cmd.Contains("enable")) Disabled = false;
        if (cmd.Contains("packages"))
            return Task.FromResult(new ToolResult(true, cmd.Contains("-d") == Disabled ? "package:org.example.sample\n" : ""));
        return Task.FromResult(new ToolResult(true, "OK"));
    }
}

internal static class Program
{
    private static int checks;
    private static void Check(bool yes, string name)
    {
        if (!yes) throw new InvalidOperationException(name);
        Console.WriteLine("PASS  " + name);
        checks++;
    }

    private static async Task Reject(Func<Task> action, string key)
    {
        try { await action(); }
        catch (BridgeException ex) when (ex.Key == key) { Check(true, key); return; }
        throw new Exception("Expected rejection: " + key);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayInfo { public int Size; public Bounds Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfoW(nint monitor, ref DisplayInfo info);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd, out Bounds rect);
    [DllImport("user32")] private static extern bool MoveWindow(nint hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32")] private static extern nint GetParent(nint hwnd);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--test-video-window"])
            {
                var app = new Application();
                return app.Run(new Window { Title = "Synthetic video surface", Left = -30000, Top = -30000,
                    Width = 240, Height = 320, Background = Brushes.Black, WindowStyle = WindowStyle.None });
            }
            if (args is ["--live-mirror"])
            {
                LiveMirror();
                return 0;
            }
            Commands().GetAwaiter().GetResult();
            UI(args.Length == 0 ? null : args.Single());
            Console.WriteLine($"{checks} checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [DllImport("user32")] private static extern nint GetWindow(nint hwnd, uint command);

    private static void LiveMirror()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window = new MainWindow();
        window.Show();
        try
        {
            void Until(Func<bool> condition)
            {
                var timer = Stopwatch.StartNew();
                while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(25)) { Pump(); Thread.Sleep(50); }
                if (!condition()) throw new Exception("Live mirror timed out");
            }
            Until(() => ((ComboBox)window.FindName("Targets")).SelectedItem != null);
            var info = (TextBlock)window.FindName("DeviceHint");
            Console.WriteLine("Device card: " + ((TextBlock)window.FindName("DeviceHeadline")).Text);
            Console.WriteLine("Current status: " + info.Text);
            var host = (MirrorHost)window.FindName("VideoSurface");
            Until(() => host.IsVisible && host.Handle != 0 && GetWindow(host.Handle, 5) != 0);
            var child = GetWindow(host.Handle, 5);
            Check(GetParent(child) == host.Handle, "Live scrcpy window attached for authorized device");
            var pane = (Border)window.FindName("MirrorPane");
            Check(Grid.GetColumn(pane) == 1 && pane.ActualHeight > pane.ActualWidth, "Live mirror occupies right portrait pane");
            foreach (string key in new[] { "Home", "Recent", "Back" })
            {
                var button = (Button)window.FindName("Android" + key);
                Until(() => button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => button.IsEnabled);
                var log = (TextBox)window.FindName("ConsoleOutput");
                Check(log.Text.EndsWith((string)button.ToolTip), "Live navigation command accepted: " + key);
            }
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static async Task Commands()
    {
        Check(AndroidBridge.ParseLock("KeyguardServiceDelegate state:\n  showing=true\n  secure=true") == true &&
            AndroidBridge.ParseLock("  mShowingLockscreen=false") == false && AndroidBridge.ParseLock("unknown") == null,
            "Distinguish locked, unlocked and unavailable keyguard state");
        Check(AndroidBridge.Parse("adb", "unit device model:SM_S908B device:e3q").Single() is { Model: "Samsung Galaxy S22 Ultra", Image: "b0s" }, "ADB-normalized Samsung model resolves to catalog entry");
        var galaxy = AndroidBridge.Parse("adb", "unit device model:SM-G960F device:starlte").Single();
        Check(galaxy.Model.Contains("Galaxy S9") && galaxy.Image == "starlte", "Offline marketing name and matched handset illustration");
        var fake = new FakeTools();
        var info = ScreenMirror.StartInfo(@"C:\tools\scrcpy.exe", @"C:\tools\scrcpy-server", @"C:\tools\adb.exe", "demo-01");
        Check(info.ArgumentList.SequenceEqual(["--serial", "demo-01", "--render-driver=software", "--no-clipboard-autosync", "--no-audio", "--window-borderless", "--window-x=-30000", "--window-y=-30000", "--window-title", "MasronDroid · demo-01"])
            && info.Environment["ADB"] == @"C:\tools\adb.exe" && info.Environment["SCRCPY_SERVER_PATH"] == @"C:\tools\scrcpy-server", "scrcpy uses explicit target and bundled ADB and interactive software rendering");
        var awakeInfo = ScreenMirror.StartInfo(@"C:\tools\scrcpy.exe", @"C:\tools\scrcpy-server", @"C:\toolsdb.exe", "demo-01", true);
        Check(awakeInfo.ArgumentList.Contains("--stay-awake") && !info.ArgumentList.Contains("--stay-awake"), "Stay-awake flag is scoped to opted-in mirror sessions");
        var bridge = new AndroidBridge(fake);
        var target = (await bridge.Discover()).Targets.Single();
        Check(target is { Serial: "demo-01", State: "device", Model: "Demo Phone" }, "ADB target and model");
        Check(AndroidBridge.Parse("adb", "List of devices attached\n* daemon started successfully *\na\tunauthorized\nb\toffline\n")
            .Select(x => x.State).SequenceEqual(["unauthorized", "offline"]), "Ignore server chatter, preserve blocked states");
        foreach (var state in new[] { "unauthorized", "offline" })
            Check(Enum.GetValues<DeviceAction>().All(a => !AndroidBridge.Allows(target with { State = state }, a)), state + " blocks all actions");
        fake.Fastboot = "serial-fastboot\tfastboot";
        var fast = (await bridge.Discover()).Targets.Last();
        await bridge.Execute(fast, DeviceAction.Product);
        Check(fake.Commands.Last().Args.SequenceEqual(["-s", "serial-fastboot", "getvar", "product"]), "Targeted Fastboot getvar");
        await bridge.Execute(fast, DeviceAction.System);
        Check(fake.Commands.Last().Args.SequenceEqual(["-s", "serial-fastboot", "reboot"]), "Fastboot reboot");
        await Reject(() => bridge.Execute(fast, DeviceAction.Install), "WrongMode");
        fake.FailAdbScan = true;
        var partial = await bridge.Discover();
        Check(partial.Errors.Count == 1 && partial.Targets.Single() == fast, "Partial scan preserves other transport");
        fake.FailAdbScan = false;
        fake.Adb = "another-device\tdevice";
        var before = fake.Commands.Count;
        await Reject(() => bridge.Execute(target, DeviceAction.Recovery), "TargetChanged");
        Check(fake.Commands.Skip(before).All(c => c.Args[0] == "devices"), "No write after target disconnect");
        fake.Adb = "demo-01\tdevice";
        await Reject(() => bridge.Execute(target, DeviceAction.Disable, "com.bad;reboot"), "InvalidValue");
        await bridge.Execute(target, DeviceAction.Disable, "org.example.sample");
        Check(fake.Disabled && fake.Commands.Last().Args.Contains("-d"), "Disable and read back user 0 state");
        await bridge.Execute(target, DeviceAction.Enable, "org.example.sample");
        Check(!fake.Disabled && fake.Commands.Last().Args.Contains("-e"), "Enable and read back user 0 state");
        fake.Unconfirmed = true;
        await Reject(() => bridge.Execute(target, DeviceAction.Disable, "org.example.sample"), "PackageUnconfirmed");
        fake.Failure = "Failure [INSTALL_FAILED_TEST]";
        await Reject(() => bridge.Execute(target, DeviceAction.PackagesEnabled), "ToolFailed");
        fake.Failure = null;
        await bridge.Execute(target, DeviceAction.Recovery);
        Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "reboot", "recovery"]), "Enter recovery");
        await bridge.Execute(target, DeviceAction.Bootloader);
        Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "reboot", "bootloader"]), "Enter bootloader");
        var dir = Directory.CreateTempSubdirectory("masrondroid-test-");
        try
        {
            var apk = Path.Combine(dir.FullName, "sample app.apk");
            var zip = Path.Combine(dir.FullName, "demo update.zip");
            File.WriteAllBytes(apk, []); using (System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) { }
            await Reject(() => bridge.Execute(target, DeviceAction.Install, zip), "InvalidFile");
            await bridge.Execute(target, DeviceAction.Install, apk);
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "install", "-r", apk]), "APK path with spaces stays one argument");
            fake.Adb = "demo-01\tsideload";
            await bridge.Execute(target with { State = "sideload" }, DeviceAction.Sideload, zip);
            var staged = fake.Commands.Last().Args.Last();
            Check(staged != zip && File.ReadAllBytes(staged).SequenceEqual(File.ReadAllBytes(zip)) && fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "sideload", staged]), "Recovery ZIP targets sideload only");
        }
        finally { foreach (var file in dir.GetFiles()) file.Delete(); dir.Delete(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Snapshot(Window window, string? folder, string name)
    {
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        window.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        png.Save(file);
    }

    private static void UI(string? imageFolder)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var fake = new FakeTools { Adb = "", Available = false };
        var mirror = new FakeMirror();
        var window = new MainWindow(new Preferences { Language = "id" }, fake, mirror, () => true);
        window.Show();
        Pump();
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            if (Find<FrameworkElement>(name) is ToggleButton toggle)
            {
                toggle.IsChecked = toggle.IsChecked != true;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                return;
            }
            var button = Find<Button>(name);
            if (!button.IsEnabled) throw new Exception("Unexpected disabled control: " + name);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            Pump();
        }
        void Go(string page)
        {
            Find<RadioButton>("Nav" + page).IsChecked = true;
            Pump();
            Check(Find<FrameworkElement>(page + "Page").IsVisible, page + " navigation");
        }

        if (imageFolder != null && Environment.GetEnvironmentVariable("MASRONDROID_GALLERY") == "1")
        {
            fake.Available = true;
            fake.Adb = "demo-01\tdevice model:Demo_Phone";
            fake.Locked = true;
            Find<ComboBox>("Languages").SelectedIndex = 1;
            window.Width = 1100;
            window.Height = 760;
            Click("ScanButton");
            foreach (var page in new[] { "Device", "Install", "Fastboot", "Recovery", "Console" })
            {
                Go(page);
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < 800) { Pump(); Thread.Sleep(15); }
                Snapshot(window, imageFolder, page == "Install" ? "application" : page.ToLowerInvariant());
            }
            window.Close();
            return;
        }
        Check(Find<TextBlock>("DeviceHeadline").Text == "ADB tidak tersedia", "Indonesian missing-tools state");
        Check(window.WindowStyle == WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(window) != null, "Integrated window chrome");
        Snapshot(window, imageFolder, "perangkat-id");
        fake.Available = true;
        Click("ScanButton");
        Check(Find<TextBlock>("DeviceHeadline").Text.Contains("MTP") && Find<TextBlock>("DeviceHint").Text.Contains("Izinkan"), "MTP-only setup guidance");
        Snapshot(window, imageFolder, "masrondroid-id");
        Click("MaximizeButton");
        Check(window.WindowState == WindowState.Maximized, "Maximize integrated window");
        var hwndMain = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var monitorInfo = new DisplayInfo { Size = Marshal.SizeOf<DisplayInfo>() };
        Check(GetMonitorInfoW(MonitorFromWindow(hwndMain, 2), ref monitorInfo) && GetWindowRect(hwndMain, out var bounds)
            && bounds.Bottom <= monitorInfo.Work.Bottom && bounds.Right <= monitorInfo.Work.Right
            && bounds.Left >= monitorInfo.Work.Left && bounds.Top >= monitorInfo.Work.Top, "Maximized window stays inside monitor work area");
        Click("MaximizeButton");
        Check(window.WindowState == WindowState.Normal, "Restore integrated window");
        fake.Adb = "demo-01\tdevice model:Demo_Phone\nsecond-demo\tunauthorized";
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (Find<ComboBox>("Targets").Items.Count == 0 && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(30); }
        var targets = Find<ComboBox>("Targets");
        Check(targets.Items.Count == 2 && targets.SelectedIndex == -1, "Multiple devices require explicit selection");
        Check(!Find<Border>("MirrorPane").IsVisible, "Mirror disabled until a device is selected");
        targets.SelectedIndex = 0;
        fake.Locked = true;
        Click("ScanButton");
        Check(targets.SelectedItem != null && Find<Border>("MirrorPane").IsVisible && !Find<MirrorHost>("VideoSurface").IsVisible && Find<TextBlock>("DeviceHint").Text.Contains("terkunci"), "Locked phone remains selected and prompts for local unlock");
        fake.Locked = false;
        Click("ScanButton");

        Check(Find<Border>("MirrorPane").IsVisible && Find<ScrollViewer>("PageScroll").IsVisible && mirror.Serial == "demo-01", "Mirror opens once for selected ADB target");
        var pane = Find<Border>("MirrorPane");
        var workspace = Find<Grid>("WorkspaceBody");
        Check(Math.Abs(pane.TranslatePoint(new Point(0, pane.ActualHeight), workspace).Y + 12 - workspace.ActualHeight) < 1,
            "Portrait pane leaves space below navigation");
        using (var video = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MasronDroid.Checks.exe"), "--test-video-window") { UseShellExecute = false })!)
        {
            try
            {
                var timeout = Stopwatch.StartNew();
                while (video.MainWindowHandle == 0 && timeout.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Thread.Sleep(30);
                    video.Refresh();
                }
                var surface = Find<MirrorHost>("VideoSurface");
                Check(surface.Handle != 0 && video.MainWindowHandle != 0, "Native host and external video window created");
                var hwnd = video.MainWindowHandle;
                mirror.Attach!(hwnd);
                Check(GetParent(hwnd) == surface.Handle, "External video window embedded inside WPF");
                MoveWindow(hwnd, 0, 0, 80, 120, true);
                var fitTimeout = Stopwatch.StartNew();
                bool fits;
                do
                {
                    Pump(); Thread.Sleep(25);
                    GetWindowRect(surface.Handle, out var hostBounds);
                    GetWindowRect(hwnd, out var videoBounds);
                    fits = videoBounds.Equals(hostBounds);
                } while (!fits && fitTimeout.Elapsed < TimeSpan.FromSeconds(3));
                Check(fits, "Video fills the host again after a late client resize");
            }
            finally { if (!video.HasExited) video.Kill(); video.WaitForExit(); }
        }
        foreach (var (key, code) in new[] { ("Back","4"), ("Home","3"), ("Recent","187"), ("Power","26"), ("VolumeDown","25"), ("VolumeUp","24") })
        {
            Click("Android" + key);
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "shell", "input", "keyevent", code]), "Embedded Android navigation " + key);
        }
        Click("AndroidCamera");
        Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "shell", "am", "start", "-a", "android.media.action.STILL_IMAGE_CAMERA"]), "Open camera for selected device");
        mirror.Finish.SetResult(new ToolResult(false, "simulated disconnect"));
        Pump();
        Check(Find<Border>("MirrorPane").IsVisible && Find<TextBox>("ConsoleOutput").Text.Contains("simulated disconnect"), "Mirror failure is visible and retry is enabled");
        Click("ScanButton");
        var toggle = Find<System.Windows.Controls.Primitives.ToggleButton>("StayAwake");
        foreach (bool desired in new[] { true, false })
        {
            toggle.IsChecked = desired;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var limit = Stopwatch.StartNew();
            while ((mirror.Awake != desired || !toggle.IsVisible) && limit.Elapsed < TimeSpan.FromSeconds(6)) { Pump(); Thread.Sleep(30); }
            Check(mirror.Awake == desired && toggle.IsVisible, "Switch restarts the selected mirror with stay-awake " + desired);
        }
        foreach (bool landscape in new[] { true, false })
        {
            Click("AndroidRotate");
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "shell", "wm", "user-rotation", "lock", landscape ? "1" : "0"]) && toggle.IsVisible && Find<ItemsControl>("DeviceFacts").IsVisible != landscape,
                "Rotation updates mirror and device information visibility: " + landscape);
            window.UpdateLayout();
            Check(targets.IsVisible && Find<TextBlock>("LandscapeDevice").IsVisible == landscape
                && !Find<DockPanel>("ConsolePage").IsVisible,
                "Rotation keeps the selector visible and the console hidden: " + landscape);
            var surface = Find<MirrorHost>("VideoSurface");
            Check(new[] { "Back", "Home", "Recent" }
                .Select(key => Find<Button>("Android" + key))
                .All(button => button.IsVisible && button.TranslatePoint(new Point(), surface).Y >= surface.ActualHeight),
                "Navigation is below the video: " + landscape);
            Check(new[] { "Camera", "Power", "VolumeDown", "VolumeUp", "Rotate" }
                .Select(key => Find<Button>("Android" + key))
                .All(button => button.IsVisible && button.TranslatePoint(new Point(0, button.ActualHeight), surface).Y <= 0),
                "Device controls are above the video: " + landscape);
        }
        fake.Locked = true;
        Click("ScanButton");
        Pump();
        Check(pane.IsVisible && Find<Button>("AndroidBack").IsVisible && !Find<MirrorHost>("VideoSurface").IsVisible,
            "Lock keeps the panel and controls visible with the video dark");
        fake.Locked = false;
        Click("ScanButton");
        Pump();
        var unlockDeadline = Stopwatch.StartNew();
        while (!Find<Button>("AndroidBack").IsEnabled && unlockDeadline.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Sleep(30); }
        Check(pane.IsVisible && Find<MirrorHost>("VideoSurface").IsVisible && Find<Button>("AndroidBack").IsEnabled,
            "Unlock restores the interactive mirror");
        mirror.Finish.SetResult(new ToolResult(true, ""));
        Pump();
        Go("Install");
        Check(Find<Button>("InstallApk").IsEnabled && !pane.IsVisible, "Application accepts APKs and hides the mirror");
        Click("ScanButton");
        var list = Find<ListBox>("InventoryList");
        var inventoryDeadline = DateTime.UtcNow.AddSeconds(8);
        while (list.Items.Count < 2 && DateTime.UtcNow < inventoryDeadline) { Pump(); Thread.Sleep(30); }
        Check(list.Items.Count == 2, "Inventory includes both simulated apps");
        foreach (var item in list.Items)
            item.GetType().GetProperty("Checked")!.SetValue(item, true);
        list.Items.Refresh();
        Find<ComboBox>("Languages").SelectedIndex = 1;
        Check(Find<Button>("BatchUninstall").IsEnabled, "Both checked apps can be removed");
        bool sawDialog = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<Window>().SingleOrDefault(w => w.Owner == window);
            if (dialog == null) return;
            timer.Stop();
            sawDialog = true;
            Snapshot(dialog, imageFolder, "konfirmasi-id");
            dialog.DialogResult = false;
        };
        timer.Start();
        Click("BatchUninstall");
        timer.Stop();
        Check(sawDialog && !fake.Commands.Any(c => c.Args.Contains("uninstall")), "Cancel batch leaves both packages installed");
        Find<ComboBox>("Languages").SelectedIndex = 1;
        var approve = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        approve.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Owner == window);
            if (dialog == null) return;
            approve.Stop();
            dialog.DialogResult = true;
        };
        approve.Start();
        Click("BatchUninstall");
        var batchDeadline = DateTime.UtcNow.AddSeconds(8);
        while (fake.Commands.Count(c => c.Args.Contains("uninstall")) < 2 && DateTime.UtcNow < batchDeadline) { Pump(); Thread.Sleep(20); }
        Check(fake.Commands.Count(c => c.Args.Contains("uninstall") && c.Args.Contains("--user")) == 2, "Confirmed batch removes both apps for the main profile");
        var backup = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Backups"), "org.example.one_*.mdbackup").OrderDescending().First();
        Task.Run(() => new AndroidBridge(fake).Execute(AndroidBridge.Parse("adb", fake.Adb).First(), DeviceAction.RestoreBackup, backup)).GetAwaiter().GetResult();
        Check(fake.Commands.Any(c => c.Args.Contains("install-multiple") && c.Args.Count(a => a.EndsWith(".apk")) == 2), "Restore verified base and split APKs together");
        Go("Console");
        Find<TextBox>("AdbCommand").Text = "adb version";
        var input = Find<TextBox>("AdbCommand");
        input.Focus();
        input.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(input)!, Environment.TickCount, System.Windows.Input.Key.Enter)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        var commandTimeout = Stopwatch.StartNew();
        while (Find<Button>("StopCommand").IsEnabled && commandTimeout.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Sleep(30); }
        Check(Find<TextBox>("ConsoleOutput").Text.Contains("Android Debug Bridge version"), "Manual ADB output reaches the console");
        Go("Fastboot");
        Check(Find<Button>("EnterBoot").IsEnabled && !Find<Button>("ReadProduct").IsEnabled, "Boot controls respect ADB mode");
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Go("Recovery");
        Snapshot(window, imageFolder, "recovery-en-small");
        Check(!Find<Button>("SendZip").IsEnabled, "Recovery sideload gated in Android mode");
        targets.SelectedIndex = 1;
        Go("Install");
        Check(list.Items.Count == 0 && !Find<Button>("BatchUninstall").IsEnabled, "Target switch clears packages and disables unauthorized writes");
        Go("Device");
        Check(Find<TextBlock>("DeviceHint").Text.Contains("Unlock the screen"), "Actionable unauthorized message");
        window.Close();
        app.Shutdown();
    }
}

internal sealed class FakeMirror : IScreenMirror
{
    public bool Available => true;
    public string? Serial;
    public bool Awake;
    public Action<nint>? Attach;
    public TaskCompletionSource<ToolResult> Finish = new();
    public Task<ToolResult> Watch(string serial, bool stayAwake, Action<nint> attach, CancellationToken stop)
    {
        Awake = stayAwake;
        if (Finish.Task.IsCompleted) Finish = new();
        Attach = attach;
        Serial = serial;
        return Finish.Task.WaitAsync(stop);
    }
}
