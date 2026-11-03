using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MasronDroid;

internal static partial class RootingCatalog
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Time, string Version, long Code, ModuleAsset Asset)> InstalledUpdates = [];

    internal static async Task<RootModule> CheckInstalledUpdate(RootModule module, bool fresh = false)
    {
        if (module.Pending) return module;
        if (!Uri.TryCreate(module.UpdateJson, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("github.com" or "raw.githubusercontent.com" or "gitlab.com" or "api.github.com")
            || uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return module;
        if (fresh || !InstalledUpdates.TryGetValue(module.UpdateJson, out var saved)
            || DateTimeOffset.UtcNow - saved.Time > TimeSpan.FromHours(6))
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            for (int read; (read = await stream.ReadAsync(buffer, limit.Token)) > 0;)
            {
                if (memory.Length + read > 65536) throw new InvalidDataException("Module update metadata too large.");
                memory.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(memory.ToArray());
            var root = json.RootElement;
            string url = root.GetProperty("zipUrl").GetString() ?? "";
            if (!ValidModuleDownload(url)) return module;
            string name = Uri.UnescapeDataString(new Uri(url).Segments.Last());
            if (Path.GetFileName(name) != name || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return module;
            saved = (DateTimeOffset.UtcNow, root.GetProperty("version").GetString() ?? "",
                root.GetProperty("versionCode").GetInt64(), new(name, url, null, -1));
            InstalledUpdates[module.UpdateJson] = saved;
        }
        var assetUri = new Uri(saved.Asset.Url);
        return module with { LatestCode = saved.Code, UpdateAsset = saved.Asset,
            Url = assetUri.Scheme + "://" + assetUri.Host + "/" + string.Join("/", assetUri.AbsolutePath.Trim('/').Split('/').Take(2)),
            Version = saved.Code > module.VersionCode ? module.Version + " → " + saved.Version : module.Version };
    }
}
