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
    internal string MagiskFacts = "";
    internal string AwakePower = "0", AwakeTimeout = "30000";
    internal long? InventoryCacheBytes;
    internal Queue<string> RunningReports = new();
    internal string ThirdPartyApps = "package:org.example.sample\n";
    internal string RecoveryFacts = "FoxBoot=1\nFoxVersion=Unofficial\nTwrpCli=1\n";
    internal bool AutoSideload, SideloadEndpointConflict;
    internal bool StorageWritable = true, CorruptTransferHash;
    internal string StoredFile = "";
    internal Queue<string> SettingsScreens = new();
    internal List<(string Tool, string[] Args)> Commands = [];
    public bool Has(string tool) => Available;
    public Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Commands.Add((tool, args.ToArray()));
        if (args[0] == "devices")
            return Task.FromResult(tool == "adb" && FailAdbScan ? new ToolResult(false, "test failure")
                : new ToolResult(true, tool == "adb" ? Adb : Fastboot));
        var cmd = args.Skip(2).ToArray();
        if (cmd.Any(x => x.Contains("AwakePower="))) return Task.FromResult(new ToolResult(true, "AwakePower=" + AwakePower + "\nAwakeTimeout=" + AwakeTimeout));
        if (cmd.Any(x => x.Contains("settings put global stay_on_while_plugged_in")))
        {
            var script = string.Join(" ", cmd);
            AwakePower = System.Text.RegularExpressions.Regex.Match(script, @"stay_on_while_plugged_in (\d+)").Groups[1].Value;
            AwakeTimeout = System.Text.RegularExpressions.Regex.Match(script, @"screen_off_timeout (\d+)").Groups[1].Value;
            return Task.FromResult(new ToolResult(true, ""));
        }
        if (cmd.Any(x => x.Contains("RuntimeUid="))) return Task.FromResult(new ToolResult(true, "RuntimeUid=0\nRuntimeEnd=1"));
        if (Failure != null) return Task.FromResult(new ToolResult(true, Failure));
        if (cmd.Any(x => x.Contains("StoredFile=%s")))
        {
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 })).ToLowerInvariant();
            return Task.FromResult(new ToolResult(true, StoredFile.Length > 0 && cmd.Any(x => x.Contains(hash))
                ? "StoredFile=" + StoredFile : ""));
        }
        if (cmd.Any(x => x.Contains("StorageReady=")))
            return Task.FromResult(new ToolResult(StorageWritable, StorageWritable ? "StorageReady=1" : ""));
        if (cmd.Any(x => x.Contains("FoxBoot="))) return Task.FromResult(new ToolResult(true, RecoveryFacts));
        if (cmd.Any(x => x.Contains("SideloadEndpointConflict=")))
            return Task.FromResult(new ToolResult(true, SideloadEndpointConflict ? "SideloadEndpointConflict=1" : ""));
        if (cmd.Any(x => x.Contains("setprop ctl.stop adbd")))
        {
            Adb = "demo-01\tsideload\n";
            return Task.FromResult(new ToolResult(false, "closed"));
        }
        if (AutoSideload && (cmd.SequenceEqual(["shell", "twrp", "sideload"]) || cmd.SequenceEqual(["reboot", "sideload"])))
        {
            if (!SideloadEndpointConflict) Adb = "demo-01\tsideload\n";
            return Task.FromResult(new ToolResult(false, "closed"));
        }
        if (cmd.Any(x => x.Contains("activity processes")))
            return Task.FromResult(new ToolResult(true, RunningReports.Count > 0 ? RunningReports.Dequeue() : "ACTIVITY MANAGER RUNNING PROCESSES\n"));
        if (cmd.Contains("-3")) return Task.FromResult(new ToolResult(true, ThirdPartyApps));
        if (cmd.Any(x => x.Contains("uiautomator dump")))
            return Task.FromResult(new ToolResult(true, SettingsScreens.Count > 0 ? SettingsScreens.Dequeue() : "<hierarchy/>"));
        if (cmd.Any(x => x.Contains("Daemon=$v"))) return Task.FromResult(new ToolResult(true, MagiskFacts));
        if (cmd.Contains("path")) return Task.FromResult(new ToolResult(true, "package:/data/app/demo/base.apk\npackage:/data/app/demo/split_config.apk"));
        if (cmd.Contains("sha256sum")) return Task.FromResult(new ToolResult(true, (CorruptTransferHash ? new string('0', 64)
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 }))) + "  " + cmd.Last()));
        if (cmd.FirstOrDefault() == "pull") File.WriteAllBytes(cmd.Last(), new byte[] { 1, 2 });
        if (cmd.Contains("dumpsys")) return Task.FromResult(new ToolResult(true, $"KeyguardServiceDelegate state:\n  showing={Locked.ToString().ToLowerInvariant()}"));
        if (cmd.Any(x => x.Contains("app_process / AppInventory")))
            return Task.FromResult(new ToolResult(true, string.Join("\n", new[] { "one", "two" }.Select(name =>
                System.Text.Json.JsonSerializer.Serialize(new { Name = name, Package = "org.example." + name, Enabled = true, Installed = true, System = false, CacheBytes = InventoryCacheBytes })))));
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
    [DllImport("user32")] private static extern nint GetFocus();

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
            if (args is ["--mirror-input-checks"])
            {
                MirrorInputChecks();
                Console.WriteLine($"{checks} mirror input checks passed.");
                return 0;
            }
            if (args is ["--root-transfer-checks"])
            {
                RootTransferChecks().GetAwaiter().GetResult();
                SideloadFlowUI();
                Console.WriteLine($"{checks} root transfer checks passed.");
                return 0;
            }
            if (args is ["--root-flow-checks"])
            {
                checks += RootFlowChecks.Run().GetAwaiter().GetResult();
                SideloadFlowUI();
                Console.WriteLine($"{checks} automatic root flow checks passed.");
                return 0;
            }
            if (args is ["--root-device-checks"])
            {
                checks += RootDeviceChecks.Run().GetAwaiter().GetResult();
                HighlightsUI();
                Console.WriteLine($"{checks} root device checks passed.");
                return 0;
            }
            if (args is ["--tricky-store-check"])
            {
                var module = RootingCatalog.Highlights.Single(item => item.Name == "Tricky Store");
                var release = RootingCatalog.ModuleRelease(module, true).GetAwaiter().GetResult();
                var download = RootingCatalog.DownloadModule(release.Assets.Single()).GetAwaiter().GetResult();
                Check(download.Id.Length > 0 && File.Exists(download.Path), "Official Tricky Store ZIP metadata and download verified");
                var installed = module with { Id = download.Id, Version = "0", VersionCode = 0, Installed = true,
                    UpdateJson = "https://raw.githubusercontent.com/5ec1cff/TrickyStore/release/update.json" };
                var update = RootingCatalog.CheckInstalledUpdate(installed, true).GetAwaiter().GetResult();
                Check(update.HasUpdate && update.UpdateAsset != null && update.LatestCode > 0,
                    "Installed-module updateJson discovers Tricky Store " + release.Version);
                Console.WriteLine($"{checks} live Tricky Store checks passed.");
                return 0;
            }
            if (args is ["--root-highlights-live"])
            {
                foreach (var module in RootingCatalog.Highlights)
                {
                    var release = RootingCatalog.ModuleRelease(module, true).GetAwaiter().GetResult();
                    Check(release.Assets.Length > 0 && release.Assets.All(asset => RootingCatalog.ValidModuleDownload(asset.Url)),
                        module.Name + " · " + release.Version + " · upstream ZIP assets");
                    if (module.Name is "Simple BShield" or "Shamiko" or "Always Strong")
                    {
                        var download = RootingCatalog.DownloadModule(release.Assets[0]).GetAwaiter().GetResult();
                        Check(File.Exists(download.Path) && download.Id.Length > 0, module.Name + " · real ZIP download and metadata verification");
                    }
                    var cached = RootingCatalog.ModuleRelease(module).GetAwaiter().GetResult();
                    Check(cached.CheckedAt == release.CheckedAt, module.Name + " · cached metadata reused");
                }
                return 0;
            }
            if (args is ["--root-highlights-checks"])
            {
                HighlightsUI();
                return 0;
            }
            if (args is ["--feedback-ui-checks"])
            {
                FeedbackUI();
                return 0;
            }
            if (args is ["--sideload-mode-checks"])
            {
                SideloadModeChecks().GetAwaiter().GetResult();
                SideloadFlowUI();
                Console.WriteLine($"{checks} sideload mode checks passed.");
                return 0;
            }
            if (args is ["--catalog-cache-checks"])
            {
                CatalogCacheChecks();
                Console.WriteLine($"{checks} catalog cache checks passed.");
                return 0;
            }
            if (args is ["--memory-ui-checks"])
            {
                InventoryRefreshChecks(true);
                Console.WriteLine($"{checks} memory UI checks passed.");
                return 0;
            }
            if (args is ["--memory-checks"])
            {
                MemoryChecks().GetAwaiter().GetResult();
                Console.WriteLine($"{checks} memory checks passed.");
                return 0;
            }
            if (args is ["--inventory-refresh-checks"])
            {
                InventoryRefreshChecks();
                Console.WriteLine($"{checks} inventory refresh checks passed.");
                return 0;
            }
            if (args is ["--cache-checks"])
            {
                CacheSettingsChecks().GetAwaiter().GetResult();
                Console.WriteLine($"{checks} cache checks passed.");
                return 0;
            }
            if (args is ["--battery-checks"])
            {
                BatteryHealthUI();
                Console.WriteLine($"{checks} battery checks passed.");
                return 0;
            }
            if (args is ["--magisk-checks"])
            {
                MagiskChecks().GetAwaiter().GetResult();
                MagiskVersionUI();
                Console.WriteLine($"{checks} Magisk checks passed.");
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

    private static App CreateTestApp() => new()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown,
        Resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MasronDroid;component/Theme.xaml", UriKind.Absolute)
        }
    };

    private static void MirrorInputChecks()
    {
        var app = CreateTestApp();
        var input = new TextBox();
        var host = new MirrorHost { Focusable = true, Height = 320, Width = 240 };
        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(host);
        var window = new Window { Content = panel, Width = 320, Height = 440 };
        using var video = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MasronDroid.Checks.exe"), "--test-video-window") { UseShellExecute = false })!;
        try
        {
            window.Show();
            var sink = (System.Windows.Interop.IKeyboardInputSink)host;
            var traversal = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First);
            Check(!sink.TabInto(traversal), "Empty mirror does not consume keyboard focus");
            var timeout = Stopwatch.StartNew();
            while (video.MainWindowHandle == 0 && timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                Thread.Sleep(30);
                video.Refresh();
            }
            Check(video.MainWindowHandle != 0, "External keyboard target created");
            host.Attach(video.MainWindowHandle);
            window.Activate();
            Pump();
            Check(sink.TabInto(traversal) && GetFocus() == video.MainWindowHandle,
                "Tab transfers native keyboard focus into the embedded external process");
            input.Focus();
            Check(GetFocus() != video.MainWindowHandle && input.IsKeyboardFocused,
                "Desktop textbox takes focus back from the mirror");
            host.Visibility = Visibility.Hidden;
            Check(!sink.TabInto(traversal) && input.IsKeyboardFocused, "Hidden mirror cannot capture keyboard focus");
            host.Visibility = Visibility.Visible;
            Pump();
            Check(sink.TabInto(traversal), "Visible mirror can receive keyboard focus again");
            host.Forget();
            Check(!sink.TabInto(traversal), "Disconnected mirror does not forward keyboard focus");
        }
        finally
        {
            if (!video.HasExited) video.Kill();
            video.WaitForExit();
            window.Close();
            app.Shutdown();
        }
    }

    private static void LiveMirror()
    {
        var app = CreateTestApp();
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

    private static async Task RootTransferChecks()
    {
        string folder = Directory.CreateTempSubdirectory("MasronDroid-transfer-").FullName;
        string zip = Path.Combine(folder, "Magisk-v31.1.zip");
        await File.WriteAllBytesAsync(zip, [1, 2]);
        try
        {
            var fake = new FakeTools();
            var bridge = new AndroidBridge(fake);
            var target = (await bridge.Discover()).Targets.Single();
            string remote = await bridge.TransferFlashableToStorage(target, zip);
            Check(remote.StartsWith("/sdcard/Download/MasronDroid/") && remote.EndsWith("/Magisk-v31.1.zip"),
                "Transfer returns a ZIP path on internal storage");
            Check(fake.Commands.Where(command => command.Args.Any(arg => arg.Contains("StorageReady=1")))
                .All(command => command.Args.All(arg => !arg.Contains('\r'))),
                "Storage probe uses Android LF line endings instead of Windows CRLF");
            Check(fake.Commands.Any(c => c.Args.Contains("push") && c.Args.Last().EndsWith(".zip.partial"))
                && fake.Commands.Any(c => c.Args.Contains("mv") && c.Args.Last() == remote),
                "ZIP is staged and only finalized after verification");
            Check(fake.Commands.All(c => !c.Args.Contains("sideload") && !c.Args.Contains("reboot")
                && !c.Args.Contains("install") && !c.Args.Contains("twrp")),
                "Transfer does not change mode, reboot or flash anything");
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 }));
            var present = new FakeTools { StoredFile = "/sdcard/Download/MasronDroid/a1b2c3d4/Magisk-v31.1.zip" };
            var presentBridge = new AndroidBridge(present);
            Check(await presentBridge.FindTransferredFlashable(target, "Magisk-v31.1.zip", expected) == present.StoredFile,
                "A fresh bridge detects an existing verified ZIP on the phone");
            Check(await presentBridge.TransferFlashableToStorage(target, zip) == present.StoredFile
                && present.Commands.All(command => !command.Args.Contains("push") && !command.Args.Contains("mkdir")),
                "An existing matching ZIP is reused without another upload");
            Check(await presentBridge.FindTransferredFlashable(target, "Magisk-v31.1.zip", new string('0', 64)) == null,
                "A different release hash does not count as ready");
            present.StoredFile = "/sdcard/Download/other/Magisk-v31.1.zip";
            Check(await presentBridge.FindTransferredFlashable(target, "Magisk-v31.1.zip", expected) == null,
                "A response outside the MasronDroid folder is rejected");
            present.StoredFile = "";
            Check(await presentBridge.FindTransferredFlashable(target, "Magisk-v31.1.zip", expected) == null,
                "Deleted files are detected as missing");
            var unavailable = new FakeTools { StorageWritable = false };
            await Reject(() => new AndroidBridge(unavailable).TransferFlashableToStorage(target, zip), "RootAndroidStorageUnavailable");
            Check(unavailable.Commands.All(c => !c.Args.Contains("push")), "Unavailable storage stops before uploading");
            var corrupt = new FakeTools { CorruptTransferHash = true };
            await Reject(() => new AndroidBridge(corrupt).TransferFlashableToStorage(target, zip), "RootTransferHashFailed");
            Check(corrupt.Commands.All(c => !c.Args.Contains("mv")) && corrupt.Commands.Any(c => c.Args.Contains("rm")),
                "Corrupt transfer is not finalized and its temporary file is removed");
            await Reject(() => bridge.TransferFlashableToStorage(target with { State = "sideload" }, zip), "WrongMode");
            var recovery = new FakeTools { Adb = "demo-01\trecovery\n" };
            var recoveryBridge = new AndroidBridge(recovery);
            target = (await recoveryBridge.Discover()).Targets.Single();
            Check((await recoveryBridge.TransferFlashableToStorage(target, zip)).EndsWith(".zip"),
                "Supported recovery can transfer without entering sideload");
            var unknown = new FakeTools { Adb = "demo-01\trecovery\n", RecoveryFacts = "" };
            await Reject(() => new AndroidBridge(unknown).TransferFlashableToStorage(target, zip), "RootRecoveryRequired");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void FeedbackUI()
    {
        var app = CreateTestApp();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window = new MainWindow(new Preferences { Language = "en" }, new FakeTools { Available = false }, new FakeMirror(), () => false);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        try
        {
            window.Width = 1100;
            window.Height = 800;
            window.Show();
            Pump();
            var finish = new TaskCompletionSource();
            Func<Task> operation = async () =>
            {
                await finish.Task;
                Call("Report", "Done", "Verified test output", new object[] { "Install" });
            };
            var work = (Task)Call("Work", operation, "Busy")!;
            Check(((Grid)window.FindName("OperationOverlay")).IsVisible
                && ((ProgressBar)window.FindName("ActivityBar")).IsVisible, "An operation shows the blocking loading card and activity bar");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "operation-loading");
            finish.SetResult();
            while (!work.IsCompleted) Pump();
            work.GetAwaiter().GetResult();
            Check(!((Grid)window.FindName("OperationOverlay")).IsVisible, "Loading clears after completion");
            object Notice() => typeof(MainWindow).GetField("lastNotice", flags)!.GetValue(window)!;
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Success",
                "A successful operation records a success modal");
            Func<Task> failure = () => throw new BridgeException("TimedOut");
            var failed = (Task)Call("Work", failure, "Busy")!;
            while (!failed.IsCompleted) Pump();
            failed.GetAwaiter().GetResult();
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Error"
                && !((Grid)window.FindName("OperationOverlay")).IsVisible, "Failure records an error modal and releases loading");
            Func<Task> malformed = () => throw new FormatException("Invalid package metadata");
            var malformedWork = (Task)Call("Work", malformed, "Busy")!;
            while (!malformedWork.IsCompleted) Pump();
            malformedWork.GetAwaiter().GetResult();
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Error",
                "Unexpected operation errors also use the custom result flow");
            Call("Report", "BatchResult", "One application needs attention.", new object[] { 2, 1, 0 });
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Warning", "A partially failed batch is not reported as successful");
            Func<Task> verificationFailed = () =>
            {
                Call("Report", "OperationFailed", "Readback failed", Array.Empty<object>());
                Call("Report", "BatchResult", "", new object[] { 2, 0, 0 });
                return Task.CompletedTask;
            };
            var verification = (Task)Call("Work", verificationFailed, "Busy")!;
            while (!verification.IsCompleted) Pump();
            verification.GetAwaiter().GetResult();
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Error",
                "A batch success count cannot hide a verification error");
            Call("Report", "ConsoleCancelled", "", new object[] { "adb" });
            Check((string)Notice().GetType().GetProperty("Kind")!.GetValue(Notice())! == "Information",
                "Console cancellation uses an information modal");
            foreach (var kind in new[] { "Success", "Error", "Warning", "Information" })
            {
                var dialog = (Window)Call("CreateNoticeDialog", "Sample operation result", "Package details and command output.", kind)!;
                bool seen = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                timer.Tick += (_, _) =>
                {
                    if (!dialog.IsVisible) return;
                    timer.Stop();
                    seen = dialog.WindowStyle == WindowStyle.None && dialog.Content is Border { CornerRadius.TopLeft: 14 }
                        && ((Border)window.FindName("ModalShade")).IsVisible;
                    Snapshot(dialog, Path.GetFullPath("artifacts/verification"), "modal-" + kind.ToLowerInvariant());
                    dialog.DialogResult = true;
                };
                timer.Start();
                Call("ShowAppDialog", dialog, true);
                Check(seen, kind + " uses the custom modal shell and dimmed owner");
            }
            Check(((Border)window.FindName("ModalShade")).Visibility == Visibility.Collapsed,
                "Closing a modal restores the workspace");
            object previous = Notice();
            var choice = new Window { Title = "Choose a package", Width = 420, SizeToContent = SizeToContent.Height,
                Content = new TextBlock { Text = "Sample package", Margin = new Thickness(20) } };
            bool choiceSeen = false;
            var choiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            choiceTimer.Tick += (_, _) =>
            {
                if (!choice.IsVisible) return;
                choiceTimer.Stop();
                choiceSeen = choice.WindowStyle == WindowStyle.None && !((Grid)window.FindName("OperationOverlay")).IsVisible;
                choice.DialogResult = false;
            };
            choiceTimer.Start();
            Func<Task> choose = () => { Call("ShowAppDialog", choice, false); return Task.CompletedTask; };
            var cancelled = (Task)Call("Work", choose, "Busy")!;
            while (!cancelled.IsCompleted) Pump();
            cancelled.GetAwaiter().GetResult();
            Check(choiceSeen && ReferenceEquals(previous, Notice()),
                "A choice dialog suspends loading and cancellation does not show a false success");
            Console.WriteLine($"{checks} feedback UI checks passed.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void HighlightsUI()
    {
        var app = CreateTestApp();
        var window = new MainWindow(new Preferences { Language = "en" }, new FakeTools { Available = false }, new FakeMirror(), () => false);
        try
        {
            window.Width = 1100;
            window.Height = 850;
            window.Show();
            Pump();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
            Set("rootHighlightsLoaded", true);
            Set("rootCatalogLoaded", true);
            Set("nextMagiskLatestRead", DateTime.MaxValue);
            Set("initialized", false);
            ((RadioButton)window.FindName("NavRooting")).IsChecked = true;
            Set("page", "Rooting");
            Set("initialized", true);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("FilterRootModules", flags)!.Invoke(window, null);
            var list = (ListBox)window.FindName("RootModuleList");
            Check(((Border)window.FindName("RootInstallCard")).IsVisible
                && !((Border)window.FindName("RootModulesCard")).IsVisible, "Root tab isolates installation from the catalog");
            foreach (var name in new[] { "ConsoleOutput", "FastOutput", "RecoveryOutput", "RootOutput" })
            {
                var output = (TextBox)window.FindName(name);
                Check(output.TextWrapping == TextWrapping.Wrap && output.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                    && output.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, name + " wraps with vertical scrolling only");
            }
            typeof(MainWindow).GetMethod("AppendLine", flags)!.Invoke(window, ["Long command output: " + new string('x', 600)]);
            Pump();
            var rootOutput = (TextBox)window.FindName("RootOutput");
            Check(rootOutput.IsVisible && rootOutput.LineCount > 1 && rootOutput.ExtentWidth <= rootOutput.ViewportWidth + 1,
                "Root console receives shared output and wraps long unbroken text");
            Check(!((TextBox)window.FindName("RootInput")).IsEnabled, "Root command input requires an ADB target");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-root-tab");
            ((RadioButton)window.FindName("RootWorkspaceModules")).IsChecked = true;
            Pump();
            Check(!((Border)window.FindName("RootInstallCard")).IsVisible
                && ((Border)window.FindName("RootModulesCard")).IsVisible, "Magisk Module tab isolates the catalog");
            Check(!((Border)window.FindName("RootConsole")).IsVisible, "Root console stays outside the module catalog");
            Check(list.Items.Count == 9 && list.Items.Cast<RootModule>().Any(row => row.Name == "Shamiko")
                && list.Items.Cast<RootModule>().Any(row => row.Name == "Tricky Store"),
                "Highlights contains all nine requested projects");
            Check(((ComboBox)window.FindName("RootSource")).Visibility == Visibility.Collapsed,
                "Highlights hides unrelated source filtering");
            ((TextBox)window.FindName("RootSearch")).Text = "shamiko";
            Check(list.Items.Count == 1 && ((RootModule)list.Items[0]).Name == "Shamiko", "Typing filters Highlights immediately");
            ((TextBox)window.FindName("RootSearch")).Text = "";
            ((RadioButton)window.FindName("RootAllModulesTab")).IsChecked = true;
            Check(list.Items.Count == 0 && ((ComboBox)window.FindName("RootSource")).IsVisible,
                "All modules uses its independent saved catalog");
            ((RadioButton)window.FindName("RootHighlightsTab")).IsChecked = true;
            ((ScrollViewer)window.FindName("PageScroll")).ScrollToEnd();
            Pump();
            IEnumerable<DependencyObject> Walk(DependencyObject node)
            {
                yield return node;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    foreach (var child in Walk(VisualTreeHelper.GetChild(node, i))) yield return child;
            }
            var download = Walk(list).OfType<Button>().FirstOrDefault(button => button.Tag is RootModule
                && Walk(button).OfType<TextBlock>().Any(text => text.Text == "Download"));
            Check(download?.IsEnabled == true, "Module download is available without a connected phone");
            Check(!RootingCatalog.ValidModuleDownload("https://github.com.evil.test/a/releases/download/x/m.zip")
                && !RootingCatalog.ValidModuleDownload("http://github.com/a/b/releases/download/x/m.zip"),
                "Module downloads reject lookalike hosts and insecure URLs");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-highlights");
            Check(!((RadioButton)window.FindName("RootInstalledTab")).IsVisible, "Installed is hidden when no modules are detected");
            var moduleTarget = new Target("adb", "installed-phone", "device", "Simulated phone");
            Set("initialized", false);
            var moduleTargets = (ComboBox)window.FindName("Targets");
            moduleTargets.ItemsSource = new[] { moduleTarget };
            moduleTargets.SelectedIndex = 0;
            Set("initialized", true);
            var installedRows = (List<RootModule>)typeof(MainWindow).GetField("installedRootModules", flags)!.GetValue(window)!;
            var installedModule = new RootModule("Installed example", "Module update available", "KernelSU",
                "https://github.com/example/module", 0, "1.0 → 2.0", "example.module", 1, Installed: true,
                UpdateAsset: new("module.zip", "https://github.com/example/module/releases/download/v2/module.zip", null, 2), LatestCode: 2);
            installedRows.Add(installedModule);
            Set("rootRuntimeTarget", moduleTarget);
            Set("rootRuntime", new RootRuntime("KernelSU", "1.0", true, false, [installedModule]));
            Set("nextRootRuntimeRead", DateTime.MaxValue);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            ((RadioButton)window.FindName("RootInstalledTab")).IsChecked = true;
            Pump();
            Check(((RadioButton)window.FindName("RootInstalledTab")).IsVisible && list.Items.Count == 1
                && ((RootModule)list.Items[0]).Id == "example.module", "Installed appears only for detected modules");
            Check(((TextBlock)window.FindName("RootEngineStatus")).Text.Contains("KernelSU")
                && (bool)list.Tag && Walk(list).OfType<Button>().Any(button => Equals(button.Content, "Update") && button.IsEnabled),
                "Detected root manager enables a verified module update independently of APK installation");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-installed-modules");
            installedRows.Clear();
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Pump();
            Check(!((RadioButton)window.FindName("RootInstalledTab")).IsVisible
                && ((RadioButton)window.FindName("RootHighlightsTab")).IsChecked == true,
                "Removing the final module hides Installed and returns to Highlights");
            Set("initialized", false);
            ((RadioButton)window.FindName("NavDevice")).IsChecked = true;
            Set("page", "Device");
            Set("initialized", true);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Pump();
            Check(((ToggleButton)window.FindName("StayAwake")).IsVisible
                && ((ToggleButton)window.FindName("StayAwake")).IsChecked == true,
                "Keep screen on defaults to enabled in Device Connection without a mirror");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "device-connection-screen-awake");
            Set("initialized", false);
            ((RadioButton)window.FindName("NavRooting")).IsChecked = true;
            ((RadioButton)window.FindName("RootWorkspaceRoot")).IsChecked = true;
            Set("page", "Rooting");
            Set("rootRuntime", null!);
            Set("rootStatusTarget", moduleTarget);
            Set("rootDaemonKey", "RootDaemonLoaded");
            Set("rootDaemonVersion", "31.1:MAGISK:R");
            Set("initialized", true);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Pump();
            Check(((TextBlock)window.FindName("RootActionText")).Text == "Uninstall Magisk & Clean Root"
                && ((Button)window.FindName("RootInstall")).IsEnabled
                && ((Border)window.FindName("RootMethodTabs")).Visibility == Visibility.Collapsed,
                "Active Magisk replaces flash with uninstall and hides installation methods");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-uninstall-magisk");
            Set("rootDaemonKey", "RootAppOnly");
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Pump();
            Check(((TextBlock)window.FindName("RootActionText")).Text == "Flash Magisk"
                && ((Border)window.FindName("RootMethodTabs")).Visibility == Visibility.Visible,
                "An APK alone does not block initial Magisk root installation");
            ((RadioButton)window.FindName("RootWorkspaceModules")).IsChecked = true;
            foreach (string engine in new[] { "Magisk", "KernelSU" })
            {
                Set("rootRuntimeTarget", moduleTarget);
                Set("rootRuntime", new RootRuntime(engine, "1.0", false, false, []));
                typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
                Pump();
                Check((bool)list.Tag && Walk(list).OfType<Button>().Any(button => button.IsEnabled
                    && button.Content is string label && label.Contains("Install") && button.Tag is RootModule),
                    engine + " enables catalog Install before Shell root authorization");
                Check(((Button)window.FindName("RootLocalInstall")).IsVisible
                    && ((Button)window.FindName("RootLocalInstall")).IsEnabled,
                    engine + " enables installation from a local module ZIP");
                Check(((WrapPanel)window.FindName("RootAccessActions")).IsVisible
                    && ((Button)window.FindName("RootManagerOpen")).IsEnabled
                    && ((Button)window.FindName("RootAccessCheck")).IsEnabled,
                    engine + " exposes manual authorization and access verification actions");
            }
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-module-install-ready");
            Set("rootRuntime", null!);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Pump();
            Check(!(bool)list.Tag, "Manager APK alone keeps module installation disabled");
            Check(!((Button)window.FindName("RootLocalInstall")).IsEnabled, "Local ZIP installation requires a detected root runtime");
            Console.WriteLine($"{checks} Highlights UI checks passed.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void SideloadFlowUI()
    {
        var app = CreateTestApp();
        var fake = new FakeTools { Available = false, Adb = "demo-01\trecovery\n" };
        var window = new MainWindow(new Preferences { Language = "en" }, fake, new FakeMirror(), () => false);
        try
        {
            window.Width = 1100;
            window.Height = 800;
            window.Show();
            Pump();
            fake.Available = true;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
            var targets = (ComboBox)window.FindName("Targets");
            var target = new Target("adb", "demo-01", "recovery", "Demo Phone");
            targets.ItemsSource = new[] { target };
            targets.SelectedIndex = 0;
            Set("rootRecoverySerial", "demo-01");
            Set("rootRecoveryName", "OrangeFox");
            Set("rootRecoveryChecked", DateTime.UtcNow);
            Set("nextMagiskLatestRead", DateTime.MaxValue);
            Set("initialized", false);
            ((RadioButton)window.FindName("NavRooting")).IsChecked = true;
            Set("page", "Rooting");
            Set("initialized", true);
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(((Button)window.FindName("RootInstall")).IsEnabled, "Recovery enables the automatic Flash Magisk flow");
            ((RadioButton)window.FindName("RootStorageMethod")).IsChecked = true;
            Check(((StackPanel)window.FindName("RootTransferPanel")).IsVisible
                && !((WrapPanel)window.FindName("RootSideloadPanel")).IsVisible
                && !((Button)window.FindName("RootTransferPackage")).IsEnabled
                && ((Button)window.FindName("RootTransferAndroid")).IsVisible,
                "Recovery offers Start Android before internal-storage transfer");
            fake.Adb = "demo-01\tdevice\n";
            targets.ItemsSource = new[] { target with { State = "device" } };
            targets.SelectedIndex = 0;
            string releaseHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 })).ToLowerInvariant();
            Set("magiskRelease", new MagiskRelease("v31.1", "Magisk-v31.1.apk", "https://github.com/topjohnwu/Magisk", releaseHash, 2));
            void CheckCopy()
            {
                var check = (Task)typeof(MainWindow).GetMethod("CheckRootTransfer", flags)!.Invoke(window, [targets.SelectedItem, true])!;
                while (!check.IsCompleted) Pump();
                check.GetAwaiter().GetResult();
            }
            CheckCopy();
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(((Button)window.FindName("RootTransferPackage")).IsEnabled
                && !((Button)window.FindName("RootTransferRecovery")).IsEnabled
                && !((Button)window.FindName("RootTransferAndroid")).IsVisible,
                "Android enables transfer; recovery waits for a verified copy");
            fake.StoredFile = "/sdcard/Download/MasronDroid/a1b2c3d4/Magisk-v31.1.zip";
            CheckCopy();
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(((Button)window.FindName("RootTransferRecovery")).IsEnabled
                && !((Button)window.FindName("RootTransferPackage")).IsEnabled
                && ((TextBlock)window.FindName("RootTransferStatus")).Text.Contains(fake.StoredFile),
                "Detecting an existing ZIP disables transfer and enables recovery");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-storage-transfer");
            fake.StoredFile = "";
            CheckCopy();
            Check(((Button)window.FindName("RootTransferPackage")).IsEnabled
                && !((Button)window.FindName("RootTransferRecovery")).IsEnabled,
                "Deleting the copied ZIP enables transfer again");
            ((RadioButton)window.FindName("RootSideloadMethod")).IsChecked = true;
            fake.Adb = "demo-01\tsideload\n";
            targets.ItemsSource = new[] { target with { State = "sideload" } };
            targets.SelectedIndex = 0;
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(((Button)window.FindName("RootInstall")).IsEnabled,
                "Verified sideload enables Magisk installation even before the package is downloaded");
            Check(!((Button)window.FindName("RootTransferPackage")).IsEnabled, "Storage transfer is disabled in sideload mode");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-sideload-flow");
            targets.SelectedIndex = -1;
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(!((Button)window.FindName("RootInstall")).IsEnabled,
                "Lost USB keeps installation disabled");
            targets.ItemsSource = new[] { target with { Serial = "different-device", State = "sideload" } };
            targets.SelectedIndex = 0;
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(!((Button)window.FindName("RootInstall")).IsEnabled, "An unrelated sideload device cannot use remembered recovery authorization");
            targets.ItemsSource = new[] { target with { State = "device" } };
            targets.SelectedIndex = 0;
            typeof(MainWindow).GetMethod("Render", flags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
            Check(((Button)window.FindName("RootInstall")).IsEnabled, "Android enables one-click recovery installation");
            window.Width = 790;
            window.Height = 560;
            ((ComboBox)window.FindName("Languages")).SelectedIndex = 0;
            Pump();
            Check(System.Windows.Automation.AutomationProperties.GetName((Button)window.FindName("RootInstall")) == "Flash Magisk",
                "Flash action has a localized accessible name");
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-auto-narrow-id");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static async Task SideloadModeChecks()
    {
        var fox = new FakeTools { Adb = "demo-01\trecovery\n", AutoSideload = true };
        var bridge = new AndroidBridge(fox);
        var target = (await bridge.Discover()).Targets.Single();
        await bridge.Execute(target, DeviceAction.StartSideload);
        Check(fox.Commands.Any(c => c.Args.SequenceEqual(["-s", "demo-01", "shell", "twrp", "sideload"]))
            && !fox.Commands.Any(c => c.Args.SequenceEqual(["-s", "demo-01", "reboot", "sideload"])),
            "OrangeFox automatic CLI sideload is restored");
        var twrp = new FakeTools { Adb = "demo-01\trecovery\n", AutoSideload = true, RecoveryFacts = "TwrpBoot=1\nTwrpVersion=3.7.1\nTwrpCli=1\n" };
        await new AndroidBridge(twrp).Execute(target, DeviceAction.StartSideload);
        Check(twrp.Commands.Any(c => c.Args.Contains("twrp")), "TWRP uses the same supported CLI path");
        Check(twrp.Commands.Last().Args[0] == "devices", "Actual sideload transport is verified after ADB closes");
        var stock = new FakeTools { Adb = "demo-01\trecovery\n", AutoSideload = true, RecoveryFacts = "" };
        await new AndroidBridge(stock).Execute(target, DeviceAction.StartSideload);
        Check(stock.Commands.Any(c => c.Args.SequenceEqual(["-s", "demo-01", "reboot", "sideload"])),
            "Other recoveries retain the generic sideload request");
        Check(!fox.Commands.Concat(twrp.Commands).Concat(stock.Commands).Any(c => c.Args.Any(a => a.Contains("ctl.stop adbd"))),
            "Normal sideload transitions never stop normal ADB");
        var conflict = new FakeTools { Adb = "demo-01\trecovery\n", AutoSideload = true, SideloadEndpointConflict = true };
        await new AndroidBridge(conflict).Execute(target, DeviceAction.StartSideload);
        Check(conflict.Adb.Contains("\tsideload") && conflict.Commands.Count(c => c.Args.Any(a => a.Contains("ctl.stop adbd"))) == 1,
            "Confirmed recovery endpoint conflict releases normal ADB once and verifies sideload");
        await Reject(() => bridge.Execute(target with { State = "device" }, DeviceAction.StartSideload), "WrongMode");
        Check(fox.Commands.All(c => !c.Args.Skip(2).SequenceEqual(["sideload"]) && !c.Args.Contains("install")),
            "Mode transition never sends or installs a package");
    }

    private static void CatalogCacheChecks()
    {
        var app = CreateTestApp();
        string folder = Directory.CreateTempSubdirectory("MasronDroid-catalog-").FullName;
        string original = Path.Combine(folder, "catalog.json"), saved = Path.Combine(folder, "saved.json");
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var catalogType = typeof(MainWindow).Assembly.GetType("MasronDroid.RootingCatalog")!;
        var staticFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var windows = new List<MainWindow>();
        try
        {
            File.WriteAllText(original, System.Text.Json.JsonSerializer.Serialize(new
            {
                Schema = 1, SavedAt = DateTimeOffset.UtcNow, Complete = false, NextSource = 2, NextPage = 4,
                Modules = new[] { new { Name = "Example", Description = "Local module", Source = "Magisk-Modules-Repo",
                    Url = "https://github.com/Magisk-Modules-Repo/example", Stars = 42 } }
            }));
            var cache = catalogType.GetMethod("LoadModuleCache", staticFlags)!.Invoke(null, [original]);
            catalogType.GetMethod("SaveModuleCache", staticFlags)!.Invoke(null, [saved, cache]);
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(saved)))
            {
                Check(json.RootElement.GetProperty("Modules")[0].GetProperty("Url").GetString() ==
                    "https://github.com/Magisk-Modules-Repo/example", "Module source link persists on disk");
                Check(json.RootElement.GetProperty("NextSource").GetInt32() == 2
                    && json.RootElement.GetProperty("NextPage").GetInt32() == 4, "Partial fetch cursor persists");
            }
            MainWindow Open(string path)
            {
                var window = new MainWindow(new Preferences { Language = "en" }, new FakeTools { Available = false }, new FakeMirror(), () => false);
                windows.Add(window);
                typeof(MainWindow).GetField("rootCacheFile", flags)!.SetValue(window, path);
                typeof(MainWindow).GetField("initialized", flags)!.SetValue(window, false);
                ((RadioButton)window.FindName("RootAllModulesTab")).IsChecked = true;
                var task = (Task)typeof(MainWindow).GetMethod("LoadRootModules", flags)!.Invoke(window, [1, false])!;
                Check(task.IsCompletedSuccessfully, "Saved catalog loads synchronously without a network fetch");
                return window;
            }
            var first = Open(saved);
            Check(((ListBox)first.FindName("RootModuleList")).Items.Count == 1, "Saved catalog populates the module list");
            File.Delete(saved);
            var repeat = (Task)typeof(MainWindow).GetMethod("LoadRootModules", flags)!.Invoke(first, [1, false])!;
            Check(repeat.IsCompletedSuccessfully && ((ListBox)first.FindName("RootModuleList")).Items.Count == 1,
                "Accidental repeat trigger reuses in-memory catalog without reading or fetching again");
            var reopened = Open(original);
            Check((int)typeof(MainWindow).GetField("rootNextPage", flags)!.GetValue(reopened)! == 4,
                "New application instance restores the saved resume position");
            File.WriteAllText(saved, "{broken");
            var damaged = Open(saved);
            Check(((ListBox)damaged.FindName("RootModuleList")).Items.Count == 0
                && ((TextBlock)damaged.FindName("RootModuleStatus")).ToolTip?.ToString()?.Contains("could not") == true,
                "Damaged cache reports an error instead of silently refetching");
            Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "Atomic cache save leaves no temporary files");
        }
        finally
        {
            foreach (var window in windows) window.Close();
            Directory.Delete(folder, true);
            app.Shutdown();
        }
    }

    private static async Task MemoryChecks()
    {
        const string cached = """
            ACTIVITY MANAGER RUNNING PROCESSES
              *APP* UID 10123 ProcessRecord{abc 123:org.example.sample/u0a123}
                user #0 uid=10123
                packageList={org.example.sample}
                pid=123
                curProcState=19 mRepProcState=19
            """;
        var foreground = cached.Replace("curProcState=19", "curProcState=2");
        Check(AndroidBridge.ParseRunningApps(cached).GetValueOrDefault("org.example.sample"), "Cached user process is eligible");
        Check(!AndroidBridge.ParseRunningApps(cached + "\n" + foreground).GetValueOrDefault("org.example.sample"),
            "Any active process protects the whole package");
        Check(AndroidBridge.ParseRunningApps(cached.Replace("user #0", "user #10")).Count == 0,
            "Other user profiles are excluded");
        Check(AndroidBridge.ParseRunningApps(cached.Replace("pid=123", "pid=0")).Count == 0,
            "Non-running records are excluded");
        var fake = new FakeTools { RunningReports = new Queue<string>([cached, "ACTIVITY MANAGER RUNNING PROCESSES\n"]) };
        var bridge = new AndroidBridge(fake);
        var target = (await bridge.Discover()).Targets.Single();
        await bridge.Execute(target, DeviceAction.ReleaseMemory, "org.example.sample");
        Check(fake.Commands.Any(c => c.Args.SequenceEqual(["-s", "demo-01", "shell", "am", "kill", "--user", "0", "org.example.sample"]))
            && fake.Commands.All(c => !c.Args.Contains("force-stop") && !c.Args.Contains("clear")),
            "Memory cleanup uses serial-bound safe background kill without force-stop or data clearing");
        var protectedTools = new FakeTools { RunningReports = new Queue<string>([foreground]) };
        var protectedBridge = new AndroidBridge(protectedTools);
        await Reject(() => protectedBridge.Execute(target, DeviceAction.ReleaseMemory, "org.example.sample"), "RamAppProtected");
        Check(protectedTools.Commands.All(c => !c.Args.Contains("kill")), "Foreground app is not killed");
        var systemTools = new FakeTools { ThirdPartyApps = "" };
        await Reject(() => new AndroidBridge(systemTools).Execute(target, DeviceAction.ReleaseMemory, "org.example.sample"), "RamAppProtected");
        Check(systemTools.Commands.All(c => !c.Args.Contains("kill")), "System app is not killed");
        var restarted = new FakeTools { RunningReports = new Queue<string>([cached, cached]) };
        await Reject(() => new AndroidBridge(restarted).Execute(target, DeviceAction.ReleaseMemory, "org.example.sample"), "RamReleaseUnconfirmed");
        await Reject(() => bridge.Execute(target, DeviceAction.ReleaseMemory, "org.bad;reboot"), "InvalidValue");
        await Reject(() => bridge.Execute(target with { State = "recovery" }, DeviceAction.ReleaseMemory, "org.example.sample"), "WrongMode");
    }

    private static void InventoryRefreshChecks(bool memory = false)
    {
        var app = CreateTestApp();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var fake = new FakeTools { Available = false, InventoryCacheBytes = 409 * 1048576L };
        var window = new MainWindow(new Preferences { Language = "en" }, fake, new FakeMirror(), () => true);
        try
        {
            window.Show();
            Pump();
            fake.Available = true;
            var target = new Target("adb", "demo-01", "device", "Demo Phone");
            var targets = (ComboBox)window.FindName("Targets");
            targets.ItemsSource = new[] { target };
            targets.SelectedIndex = 0;
            Pump();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            void Reload(bool sizesOnly)
            {
                var task = (Task)typeof(MainWindow).GetMethod("LoadInventory", flags)!.Invoke(window, [target, sizesOnly])!;
                var timeout = Stopwatch.StartNew();
                while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(10); }
                if (task.IsFaulted) throw task.Exception!;
                Check(task.IsCompletedSuccessfully, "Inventory read completes");
            }
            var list = (ItemsControl)window.FindName("InventoryList");
            string Cache() => (string)list.Items[0].GetType().GetProperty("Cache")!.GetValue(list.Items[0])!;
            Reload(false);
            Check(Cache() == "409 MB", "Initial cache snapshot is displayed");
            var row = list.Items[0];
            row.GetType().GetProperty("Checked")!.SetValue(row, true);
            fake.InventoryCacheBytes = 126976;
            Reload(true);
            Check(Cache() == "124 KB", "Size refresh replaces old cache with the current measured size");
            Check((bool)list.Items[0].GetType().GetProperty("Checked")!.GetValue(list.Items[0])!, "Background refresh preserves package selection");
            var next = (DateTime)typeof(MainWindow).GetField("nextInventory", flags)!.GetValue(window)!;
            Check(next > DateTime.UtcNow && next < DateTime.UtcNow.AddSeconds(31), "Next size refresh is scheduled within 30 seconds");
            if (memory)
            {
                const string running = """
                    ACTIVITY MANAGER RUNNING PROCESSES
                      *APP* UID 10123 ProcessRecord{abc 123:org.example.one/u0a123}
                        user #0 uid=10123
                        packageList={org.example.one}
                        pid=123
                        curProcState=19
                      *APP* UID 10124 ProcessRecord{def 124:org.example.two/u0a124}
                        user #0 uid=10124
                        packageList={org.example.two}
                        pid=124
                        curProcState=2
                    """;
                fake.RunningReports = new Queue<string>([running, running, running]);
                var task = (Task)typeof(MainWindow).GetMethod("RefreshRunningApps", flags)!.Invoke(window, [target, true])!;
                while (!task.IsCompleted) Pump();
                task.GetAwaiter().GetResult();
                var facts = (Dictionary<string, string>)typeof(MainWindow).GetField("facts", flags)!.GetValue(window)!;
                facts["Ram"] = "2.8 / 3.5 GiB";
                facts["RamPercent"] = "80";
                facts["Storage"] = "46.8 / 52.3 GiB";
                facts["StorageRemaining"] = "5.3 GiB";
                typeof(MainWindow).GetMethod("RenderDeviceFacts", flags)!.Invoke(window, null);
                Check(((TextBlock)window.FindName("AppStorageRemaining")).Text == "5.3 GiB", "Application shows reported available storage");
                window.Width = 1100;
                window.Height = 800;
                Snapshot(window, Path.GetFullPath("artifacts/verification"), "device-memory-storage");
                ((RadioButton)window.FindName("NavInstall")).IsChecked = true;
                ((RadioButton)window.FindName("RunningAppsTab")).IsChecked = true;
                Check(list.Items.Count == 2, "Running tab includes active and cached installed packages");
                var ramCard = (Border)window.FindName("RunningRamCard");
                Check(ramCard.IsVisible && ((TextBlock)window.FindName("RunningRamValue")).Text == "2.8 / 3.5 GiB"
                    && ((TextBlock)window.FindName("RunningRamPercent")).Text == "80%"
                    && ((ProgressBar)window.FindName("RunningRamProgress")).Value == 80,
                    "Running apps RAM card shows device usage, total and percentage");
                Check(ramCard.IsAncestorOf((Button)window.FindName("ApplicationCleanRam")),
                    "Clean RAM is inside the usage card");
                Check((bool)list.Items[0].GetType().GetProperty("CanReleaseMemory")!.GetValue(list.Items[0])!
                    && !(bool)list.Items[1].GetType().GetProperty("CanReleaseMemory")!.GetValue(list.Items[1])!,
                    "Only cached user apps are selectable for memory release");
                Check(((Button)window.FindName("RefreshApplications")).IsEnabled, "Refresh button is enabled for the connected ADB target");
                Check(new[] { "InstallApk", "RestoreApps", "BatchCache", "BatchDisable", "BatchEnable", "BatchUninstall" }
                    .All(name => ((Button)window.FindName(name)).Visibility == Visibility.Collapsed),
                    "Running apps hides installation, backup and package-management actions");
                var checkAll = (CheckBox)window.FindName("InventoryCheckAll");
                checkAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(checkAll.IsChecked == true && ((Button)window.FindName("BatchReleaseMemory")).IsEnabled
                    && !(bool)list.Items[1].GetType().GetProperty("Checked")!.GetValue(list.Items[1])!,
                    "Header checkbox selects eligible running apps while protected apps stay unchecked");
                Snapshot(window, Path.GetFullPath("artifacts/verification"), "application-memory-storage");
                ((RadioButton)window.FindName("AllAppsTab")).IsChecked = true;
                Check(((Button)window.FindName("InstallApk")).IsVisible && !((Button)window.FindName("ApplicationCleanRam")).IsVisible,
                    "All applications restores package actions and hides RAM cleanup");
                list.Items[0].GetType().GetProperty("Checked")!.SetValue(list.Items[0], true);
                typeof(MainWindow).GetMethod("UpdateBatchButtons", flags)!.Invoke(window, null);
                Check(checkAll.IsChecked == null, "Header checkbox indicates a partial selection");
            }
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static async Task CacheSettingsChecks()
    {
        static string Screen(string size, string id = "button2", string label = "Clear cache", string app = "Example") =>
            $"""
            <hierarchy><node package="com.android.settings" resource-id="com.android.settings:id/entity_header_title" text="{app}"/>
            <node><node text="Data" resource-id="android:id/title"/><node text="0 B" resource-id="android:id/summary"/></node>
            <node><node text="Cache" resource-id="android:id/title"/><node text="{size}" resource-id="android:id/summary"/></node>
            <node package="com.android.settings" resource-id="com.android.settings:id/button1" text="Clear data" bounds="[0,100][100,200]" enabled="true"/>
            <node package="com.android.settings" resource-id="com.android.settings:id/{id}" text="{label}" bounds="[100,100][200,200]" enabled="true"/>
            </hierarchy>
            """;
        async Task<FakeTools> Run(params string[] screens)
        {
            var fake = new FakeTools { SettingsScreens = new Queue<string>(screens) };
            var bridge = new AndroidBridge(fake);
            var target = (await bridge.Discover()).Targets.Single();
            await bridge.ClearCacheInSettings(target, "org.example.sample", "Example");
            return fake;
        }
        var samsung = await Run(Screen("549 MB"), Screen("0 B"));
        Check(samsung.Commands.Count(c => c.Args.Contains("tap")) == 1
            && samsung.Commands.Single(c => c.Args.Contains("tap")).Args.TakeLast(2).SequenceEqual(["150", "150"]),
            "Samsung button2 is pressed; Clear data is never selected");
        var retry = await Run("ERROR: null root node", Screen("549 MB"), Screen("0 B"));
        Check(retry.Commands.Count(c => c.Args.Any(a => a.Contains("uiautomator dump"))) == 3,
            "Transient empty Settings tree is retried");
        var aosp = await Run(Screen("10 MB", "text2", "Hapus cache"), Screen("0 byte", "text2", "Hapus cache"));
        Check(aosp.Commands.Count(c => c.Args.Contains("tap")) == 1, "AOSP Indonesian cache action remains supported");
        var empty = await Run(Screen("0 B"));
        Check(empty.Commands.All(c => !c.Args.Contains("tap")), "Already empty cache succeeds without a tap");
        const string appInfo = """
            <hierarchy>
              <node package="com.android.settings" resource-id="com.android.settings:id/entity_header_title" text="Example"/>
              <node package="com.android.settings" scrollable="true" bounds="[0,200][1080,2000]"/>
            </hierarchy>
            """;
        const string scrolled = """
            <hierarchy><node clickable="true">
              <node package="com.android.settings" text="Penyimpanan" bounds="[50,800][400,900]"/>
            </node></hierarchy>
            """;
        var hiddenStorage = await Run(appInfo, scrolled, Screen("549 MB"), Screen("0 B"));
        Check(hiddenStorage.Commands.Count(c => c.Args.Contains("swipe")) == 1
            && hiddenStorage.Commands.Count(c => c.Args.Contains("tap")) == 2,
            "Samsung storage below the fold is found by a bounded scroll before cache clearing");
        var stillFull = new FakeTools { SettingsScreens = new Queue<string>(Enumerable.Repeat(Screen("549 MB"), 4)) };
        var blocked = new AndroidBridge(stillFull);
        var target = (await blocked.Discover()).Targets.Single();
        await Reject(() => blocked.ClearCacheInSettings(target, "org.example.sample", "Example"), "CacheUnconfirmed");
        Check(stillFull.Commands.Count(c => c.Args.Contains("tap")) == 1,
            "Zero Data size cannot falsely confirm nonzero cache");
        var wrong = new FakeTools { SettingsScreens = new Queue<string>([Screen("1 MB", app: "Other")]) };
        var wrongBridge = new AndroidBridge(wrong);
        target = (await wrongBridge.Discover()).Targets.Single();
        await Reject(() => wrongBridge.ClearCacheInSettings(target, "org.example.sample", "Example"), "CacheUiUnsupported");
        Check(wrong.Commands.All(c => !c.Args.Contains("tap")), "Wrong app identity stops before any tap");
        var locked = new FakeTools { Locked = true };
        var lockedBridge = new AndroidBridge(locked);
        target = (await lockedBridge.Discover()).Targets.Single();
        await Reject(() => lockedBridge.ClearCacheInSettings(target, "org.example.sample", "Example"), "UnlockPhone");
        Check(locked.Commands.All(c => !c.Args.Contains("tap")), "Locked phone cannot trigger cache controls");
    }

    private static async Task MagiskChecks()
    {
        var fake = new FakeTools { MagiskFacts = "App=1\nAppVersion=30.7\nDaemon=30.6:MAGISK:R\n" };
        var bridge = new AndroidBridge(fake);
        var target = (await bridge.Discover()).Targets.Single();
        Check(await bridge.ReadMagiskStatus(target) == ("RootDaemonLoaded", "30.6:MAGISK:R", true, "30.7"),
            "APK version is independent of active Magisk version");
        fake.MagiskFacts = "App=1\nAppVersion=31.1\n";
        Check(await bridge.ReadMagiskStatus(target) == ("RootAppOnly", "", true, "31.1"),
            "Installed APK is detected without root");
        fake.MagiskFacts = "";
        Check(await bridge.ReadMagiskStatus(target) == ("RootNotDetected", "", false, ""),
            "Missing APK has no invented installed version");
        Check(await bridge.ReadMagiskStatus(target with { State = "recovery" }) == ("RootStatusUnknown", "", false, ""),
            "Recovery cannot reuse Android APK status");
        var parse = typeof(MainWindow).GetMethod("MagiskVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Version? VersionOf(string value) => (Version?)parse.Invoke(null, [value]);
        Check(VersionOf("v31.1") == VersionOf("31.1.0") && VersionOf("30.7") < VersionOf("v31.1"),
            "Release tags normalize and older versions offer update");
        Check(VersionOf("31.10") > VersionOf("31.9") && VersionOf("32.0") > VersionOf("v31.1"),
            "Version ordering is numeric and prevents downgrade");
        Check(VersionOf("canary") == null && VersionOf("31.1-beta") == null && VersionOf("") == null,
            "Unrecognized builds are not declared up to date");
    }

    private static void BatteryHealthUI()
    {
        var app = CreateTestApp();
        var window = new MainWindow(new Preferences { Language = "en" }, new FakeTools { Available = false }, new FakeMirror(), () => true);
        try
        {
            window.Show();
            Pump();
            var targets = (ComboBox)window.FindName("Targets");
            targets.ItemsSource = new[] { new Target("adb", "demo-01", "device", "Demo Phone") };
            targets.SelectedIndex = 0;
            Pump();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var facts = new Dictionary<string, string> { ["Brand"] = "samsung", ["ASOC"] = "91", ["BatteryHealth"] = "2" };
            typeof(MainWindow).GetField("facts", flags)!.SetValue(window, facts);
            string Value()
            {
                typeof(MainWindow).GetMethod("RenderDeviceFacts", flags)!.Invoke(window, null);
                var rows = ((ItemsControl)window.FindName("DeviceFacts")).Items.Cast<object>();
                var health = rows.Single(row => (string)row.GetType().GetProperty("Label")!.GetValue(row)! == "Battery health");
                return (string)health.GetType().GetProperty("Value")!.GetValue(health)!;
            }
            Check(Value() == "≈91%", "Samsung M21 ASOC-only report displays an estimate");
            facts["BSOH"] = "92.5";
            Check(Value() == "92.5%", "BSOH remains preferred over ASOC");
            facts.Remove("BSOH");
            facts["ASOC"] = "-1";
            Check(!Value().Contains('%'), "Unsupported ASOC cannot become a health percentage");
            facts["ASOC"] = "0";
            Check(!Value().Contains('%'), "Zero ASOC is not treated as measured battery death");
            facts["ASOC"] = "91";
            facts["Brand"] = "other";
            Check(!Value().Contains('%'), "Samsung ASOC policy is not assumed for unrelated manufacturers");
            facts["FullCapacity"] = "4500000";
            facts["DesignCapacity"] = "5000000";
            Check(Value() == "≈90%", "Existing capacity-ratio fallback is preserved");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void MagiskVersionUI()
    {
        var app = CreateTestApp();
        var window = new MainWindow(new Preferences { Language = "en" }, new FakeTools { Available = false }, new FakeMirror(), () => true);
        try
        {
            window.Width = 1100;
            window.Height = 800;
            window.Show();
            Pump();
            T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            var target = new Target("adb", "demo-01", "device", "Demo Phone");
            var targets = Find<ComboBox>("Targets");
            targets.ItemsSource = new[] { target };
            targets.SelectedIndex = 0;
            Pump();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
            void RenderVersions() => typeof(MainWindow).GetMethod("RenderMagiskVersions", flags)!.Invoke(window, [true]);
            var releaseType = typeof(MainWindow).Assembly.GetType("MasronDroid.MagiskRelease")!;
            Set("magiskRelease", Activator.CreateInstance(releaseType, ["v31.1", "Magisk.apk", "https://github.com/topjohnwu/Magisk", "", 0L])!);
            Set("rootStatusTarget", target);
            Set("rootDaemonKey", "RootAppOnly");
            Set("rootAppInstalled", true);
            Set("rootAppVersion", "30.7");
            RenderVersions();
            Check(Find<TextBlock>("RootInstalledVersion").Text == "30.7" && Find<TextBlock>("RootApkAction").Text == "Update APK"
                && Find<Button>("RootApkInstall").IsEnabled, "Older APK shows installed version and update action");
            Find<FrameworkElement>("DevicePage").Visibility = Visibility.Collapsed;
            Find<FrameworkElement>("RootingPage").Visibility = Visibility.Visible;
            window.UpdateLayout();
            Snapshot(window, Path.GetFullPath("artifacts/verification"), "rooting-update");
            Set("rootAppVersion", "31.1");
            RenderVersions();
            Check(Find<TextBlock>("RootUpdateStatus").Text == "Up to date" && !Find<Button>("RootApkInstall").IsEnabled,
                "Current APK shows up-to-date state and disables unnecessary installation");
            Set("rootDaemonKey", "RootStatusUnknown");
            Set("rootAppVersion", "");
            RenderVersions();
            Check(Find<TextBlock>("RootInstalledVersion").Text == "Not available", "Failed status read does not claim APK is absent");
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
        var app = CreateTestApp();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
        var restoreDeadline = DateTime.UtcNow.AddSeconds(2);
        while (window.WindowState != WindowState.Normal && DateTime.UtcNow < restoreDeadline) { Pump(); Thread.Sleep(20); }
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
            while ((fake.AwakePower != (desired ? "7" : "0") || !toggle.IsVisible) && limit.Elapsed < TimeSpan.FromSeconds(6)) { Pump(); Thread.Sleep(30); }
            var operationDone = Stopwatch.StartNew();
            while ((bool)typeof(MainWindow).GetField("busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!
                && operationDone.Elapsed < TimeSpan.FromSeconds(6)) { Pump(); Thread.Sleep(30); }
            Check(fake.AwakePower == (desired ? "7" : "0") && toggle.IsVisible && !mirror.Awake,
                "Device switch applies screen-on settings independently of the mirror: " + desired);
        }
        foreach (bool landscape in new[] { true, false })
        {
            int rotationStart = fake.Commands.Count;
            Click("AndroidRotate");
            var rotationDeadline = Stopwatch.StartNew();
            while (Find<ItemsControl>("DeviceFacts").IsVisible == landscape && rotationDeadline.Elapsed < TimeSpan.FromSeconds(8))
            { Pump(); Thread.Sleep(30); }
            Check(fake.Commands.Skip(rotationStart).Any(command => command.Args.SequenceEqual(["-s", "demo-01", "shell", "wm", "user-rotation", "lock", landscape ? "1" : "0"])) && toggle.IsVisible != landscape && Find<ItemsControl>("DeviceFacts").IsVisible != landscape,
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
        var completionDeadline = DateTime.UtcNow.AddSeconds(8);
        while ((bool)typeof(MainWindow).GetField("busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!
            && DateTime.UtcNow < completionDeadline) { Pump(); Thread.Sleep(20); }
        Check(!(bool)typeof(MainWindow).GetField("busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!,
            "Batch verification finishes before starting another operation");
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
