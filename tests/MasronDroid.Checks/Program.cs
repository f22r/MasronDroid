using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
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
        if (cmd.Contains("dumpsys")) return Task.FromResult(new ToolResult(true, $"KeyguardServiceDelegate state:\n  showing={Locked.ToString().ToLowerInvariant()}"));
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
            var start = (Button)window.FindName("ViewScreen");
            Until(() => ((ComboBox)window.FindName("Targets")).SelectedItem != null);
            var info = (TextBlock)window.FindName("DeviceHint");
            Console.WriteLine("Device card: " + ((TextBlock)window.FindName("DeviceHeadline")).Text);
            Console.WriteLine("Current status: " + info.Text);
            Until(() => start.IsEnabled);
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
                var log = (TextBox)window.FindName("Result");
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
            File.WriteAllBytes(apk, []); File.WriteAllBytes(zip, []);
            await Reject(() => bridge.Execute(target, DeviceAction.Install, zip), "InvalidFile");
            await bridge.Execute(target, DeviceAction.Install, apk);
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "install", "-r", apk]), "APK path with spaces stays one argument");
            fake.Adb = "demo-01\tsideload";
            await bridge.Execute(target with { State = "sideload" }, DeviceAction.Sideload, zip);
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "sideload", zip]), "Recovery ZIP targets sideload only");
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
            var button = Find<Button>(name);
            if (!button.IsEnabled) throw new Exception("Unexpected disabled control: " + name);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            Pump();
        }
        void Go(string page)
        {
            Find<RadioButton>("Nav" + page).IsChecked = true;
            Pump();
            Check(Find<StackPanel>(page + "Page").IsVisible, page + " navigation");
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
        Check(!Find<Button>("ViewScreen").IsEnabled, "Mirror disabled until a device is selected");
        targets.SelectedIndex = 0;
        fake.Locked = true;
        Click("ScanButton");
        Check(targets.SelectedItem != null && !Find<Button>("ViewScreen").IsEnabled && Find<TextBlock>("DeviceHint").Text.Contains("terkunci"), "Locked phone remains selected and prompts for local unlock");
        fake.Locked = false;
        Click("ScanButton");
        Check(Find<Button>("ViewScreen").IsEnabled, "Unlock restores access without disconnect");
        Click("ViewScreen");
        Check(Find<Border>("MirrorPane").IsVisible && Find<ScrollViewer>("PageScroll").IsVisible && mirror.Serial == "demo-01" && !Find<Button>("ViewScreen").IsEnabled, "Mirror opens once for selected ADB target");
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
            }
            finally { if (!video.HasExited) video.Kill(); video.WaitForExit(); }
        }
        foreach (var (key, code) in new[] { ("Back","4"), ("Home","3"), ("Recent","187") })
        {
            Click("Android" + key);
            Check(fake.Commands.Last().Args.SequenceEqual(["-s", "demo-01", "shell", "input", "keyevent", code]), "Embedded Android navigation " + key);
        }
        mirror.Finish.SetResult(new ToolResult(false, "simulated disconnect"));
        Pump();
        Check(Find<Button>("ViewScreen").IsEnabled && Find<TextBox>("Result").Text.Contains("simulated disconnect"), "Mirror failure is visible and retry is enabled");
        Go("Install");
        Check(!Find<Button>("InstallApk").IsEnabled, "Install needs a file");
        Go("Debloat");
        Click("LoadPackages");
        var list = Find<ListBox>("PackageList");
        Check(list.Items.Count == 1, "Load packages from selected device");
        Find<TextBox>("Filter").Text = "unmatched";
        Check(list.Items.Count == 0 && !Find<Button>("ChangePackage").IsEnabled, "Empty filtered package state");
        Find<TextBox>("Filter").Clear();
        list.SelectedIndex = 0;
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
        Click("ChangePackage");
        timer.Stop();
        Check(sawDialog && !fake.Disabled, "Cancel confirmation leaves package enabled");
        Find<ComboBox>("Languages").SelectedIndex = 1;
        Check(Find<TextBlock>("PageDescription").Text.StartsWith("Disable or re-enable"), "Switch to English in place");
        Snapshot(window, imageFolder, "debloat-en");
        Go("Fastboot");
        Check(Find<Button>("EnterBoot").IsEnabled && !Find<Button>("ReadProduct").IsEnabled, "Boot controls respect ADB mode");
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Go("Recovery");
        Snapshot(window, imageFolder, "recovery-en-small");
        Check(!Find<Button>("SendZip").IsEnabled, "Recovery sideload gated in Android mode");
        targets.SelectedIndex = 1;
        Go("Debloat");
        Check(list.Items.Count == 0 && !Find<Button>("LoadPackages").IsEnabled, "Target switch clears packages and disables unauthorized writes");
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
    public Action<nint>? Attach;
    public TaskCompletionSource<ToolResult> Finish = new();
    public Task<ToolResult> Watch(string serial, Action<nint> attach, CancellationToken stop)
    {
        Attach = attach;
        Serial = serial;
        return Finish.Task;
    }
}
