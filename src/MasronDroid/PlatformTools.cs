using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MasronDroid;

public record ToolResult(bool Ok, string Output);
public interface IPlatformTools
{
    bool Has(string tool);
    Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout);
}

public sealed class PlatformTools : IPlatformTools
{
    internal string? Find(string tool)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", tool + ".exe");
        return File.Exists(path) ? path : null;
    }

    public bool Has(string tool) => Find(tool) != null;

    internal static async Task<int> StreamCommand(string tool, string arguments, string? serial, Action<string> append, CancellationToken stop)
    {
        var executable = new PlatformTools().Find(tool) ?? throw new BridgeException("ToolMissing", tool);
        var start = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true
        };
        if (serial != null) start.Environment["ANDROID_SERIAL"] = serial;
        using var child = Process.Start(start) ?? throw new InvalidOperationException(executable);
        child.StandardInput.Close();
        using var cancel = stop.Register(() =>
        {
            try { if (!child.HasExited) child.Kill(true); }
            catch (InvalidOperationException) { }
        });
        var lines = System.Threading.Channels.Channel.CreateBounded<string>(512);
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                await lines.Writer.WriteAsync(line).ConfigureAwait(false);
        }
        var readers = Task.WhenAll(Task.Run(() => Read(child.StandardOutput)), Task.Run(() => Read(child.StandardError)));
        while (true)
        {
            var batch = new List<string>();
            int length = 0;
            while (length < 3500 && batch.Count < 100 && lines.Reader.TryRead(out var line))
            {
                batch.Add(line);
                length += line.Length + 2;
            }
            if (batch.Count > 0) append(string.Join(Environment.NewLine, batch));
            if (readers.IsCompleted && !lines.Reader.TryPeek(out _)) break;
            await Task.Delay(batch.Count == 0 ? 60 : 1);
        }
        await readers;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    public async Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var file = Find(tool) ?? throw new BridgeException("ToolMissing", tool);
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = new Process { StartInfo = start };
        try
        {
            child.Start();
            using var timer = new CancellationTokenSource(timeout);
            var output = child.StandardOutput.ReadToEndAsync(timer.Token);
            var error = child.StandardError.ReadToEndAsync(timer.Token);
            try
            {
                await child.WaitForExitAsync(timer.Token);
                var parts = await Task.WhenAll(output, error);
                return new(child.ExitCode == 0, string.Join(Environment.NewLine, parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim());
            }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                throw new BridgeException("TimedOut", tool);
            }
        }
        catch (Win32Exception ex) { throw new BridgeException("CannotRun", ex.Message); }
    }
}
