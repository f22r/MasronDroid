using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MasronDroid;

public interface IScreenMirror
{
    bool Available { get; }
    Task<ToolResult> Watch(string serial, bool stayAwake, Action<nint> attach, CancellationToken stop);
}

public sealed class ScreenMirror(PlatformTools tools) : IScreenMirror
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy");
    private static readonly string Client = Path.Combine(Folder, "scrcpy.exe");
    private static readonly string Server = Path.Combine(Folder, "scrcpy-server");
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    public bool Available => File.Exists(Client) && File.Exists(Server);

    public static ProcessStartInfo StartInfo(string client, string server, string adb, string serial, bool stayAwake = false)
    {
        var start = new ProcessStartInfo(client)
        {
            WorkingDirectory = Path.GetDirectoryName(client)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "--serial", serial, "--render-driver=software", "--no-clipboard-autosync", "--no-audio", "--window-borderless", "--window-x=-30000", "--window-y=-30000", "--window-title", "MasronDroid · " + serial })
            start.ArgumentList.Add(arg);
        if (stayAwake) start.ArgumentList.Add("--stay-awake");
        start.Environment["ADB"] = adb;
        start.Environment["SCRCPY_SERVER_PATH"] = server;
        return start;
    }

    public async Task<ToolResult> Watch(string serial, bool stayAwake, Action<nint> attach, CancellationToken stop)
    {
        if (!Available) throw new BridgeException("MirrorMissing");
        var adb = tools.Find("adb") ?? throw new BridgeException("ToolMissing", "adb");
        using var child = new Process { StartInfo = StartInfo(Client, Server, adb, serial, stayAwake) };
        try
        {
            child.Start();
            nint display = 0;
            using var registration = stop.Register(() =>
            {
                try { if (display != 0) PostMessageW(display, 0x0010, 0, 0); else if (!child.HasExited) child.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            });
            var stdout = child.StandardOutput.ReadToEndAsync(stop);
            var stderr = child.StandardError.ReadToEndAsync(stop);
            var started = Stopwatch.StartNew();
            try
            {
                while (!stop.IsCancellationRequested && !child.HasExited)
                {
                    child.Refresh();
                    if (child.MainWindowHandle != 0)
                    {
                        display = child.MainWindowHandle;
                        attach(display);
                        break;
                    }
                    if (started.Elapsed > TimeSpan.FromSeconds(20)) throw new BridgeException("MirrorTimeout");
                    await Task.Delay(50);
                }
                try { await child.WaitForExitAsync(stop); }
                catch (OperationCanceledException)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await child.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException) { if (!child.HasExited) child.Kill(); }
                    return new(true, "");
                }
            }
            finally
            {
                if (!child.HasExited) child.Kill();
            }
            var messages = await Task.WhenAll(stdout, stderr);
            return new(child.ExitCode == 0, string.Join(Environment.NewLine, messages.Where(s => !string.IsNullOrWhiteSpace(s))).Trim());
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return new(true, ""); }
        catch (Win32Exception ex) { throw new BridgeException("CannotRun", ex.Message); }
    }
}
