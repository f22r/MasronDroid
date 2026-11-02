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
