using System.IO;
using System.Text.Json;

namespace MasronDroid;

internal sealed record RootModuleRelease(string Version, ModuleAsset[] Assets, DateTimeOffset CheckedAt);

internal static partial class RootingCatalog
{
    internal static readonly RootModule[] Highlights =
    [
        new("Zygisk Next", "Standalone Zygisk implementation", "LSPosed", "https://github.com/LSPosed/ZygiskNext", 0),
        new("Vector", "Modern Xposed framework", "JingMatrix", "https://github.com/JingMatrix/Vector", 0),
        new("Shamiko", "Zygisk companion module", "LSPosed", "https://github.com/LSPosed/LSPosed.github.io", 0),
        new("Tricky Store", "Android keystore module", "5ec1cff", "https://github.com/5ec1cff/TrickyStore", 0),
        new("HMA-OSS", "App-list privacy module", "frknkrc44", "https://github.com/frknkrc44/HMA-OSS", 0),
        new("Simple BShield", "Simple BShield module", "DeepLunaria", "https://github.com/DeepLunaria/BShieldExperimentation", 0),
        new("Play Integrity Fork", "Play Integrity module", "osm0sis", "https://github.com/osm0sis/PlayIntegrityFork", 0),
        new("Always Strong", "Combined integrity module", "Evokerr · GitLab", "https://gitlab.com/Evokerr/AlwaysStrong", 0),
        new("Integrity Box", "System environment toolkit", "MeowDump", "https://github.com/MeowDump/Integrity-Box", 0)
    ];

    private static string ReleaseCachePath => Path.Combine(Path.GetDirectoryName(ModuleCachePath)!, "module-releases.json");
    private static readonly Dictionary<string, RootModuleRelease> ModuleReleases = LoadModuleReleases();
    private static readonly SemaphoreSlim ReleaseGate = new(1, 1);

    private static Dictionary<string, RootModuleRelease> LoadModuleReleases()
    {
        try
        {
            if (!File.Exists(ReleaseCachePath) || new FileInfo(ReleaseCachePath).Length > 10 * 1024 * 1024) return [];
            return JsonSerializer.Deserialize<Dictionary<string, RootModuleRelease>>(File.ReadAllText(ReleaseCachePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static bool ValidModuleDownload(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && ((uri.Host == "github.com" && uri.AbsolutePath.Contains("/releases/download/"))
            || (uri.Host == "gitlab.com" && uri.AbsolutePath.StartsWith(
                "/api/v4/projects/87739512/packages/generic/AlwaysStrong/", StringComparison.Ordinal)));

    internal static ModuleAsset[] ReadGithubAssets(JsonElement release, bool simpleBshield)
    {
        return release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() is { } name
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && (!simpleBshield || name.StartsWith("simple-bshield", StringComparison.OrdinalIgnoreCase)))
            .Select(asset => new ModuleAsset(asset.GetProperty("name").GetString()!,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.TryGetProperty("digest", out var digest) && digest.GetString()?.StartsWith("sha256:") == true
                    ? digest.GetString()![7..] : null,
                asset.GetProperty("size").GetInt64())).ToArray();
    }

    internal static async Task<RootModuleRelease> ModuleRelease(RootModule module, bool fresh = false)
    {
        await ReleaseGate.WaitAsync();
        try { return await ReadModuleRelease(module, fresh); }
        finally { ReleaseGate.Release(); }
    }

    private static async Task<RootModuleRelease> ReadModuleRelease(RootModule module, bool fresh)
    {
        var uri = new Uri(module.Url);
        bool gitlab = module.Url == "https://gitlab.com/Evokerr/AlwaysStrong";
        if ((!gitlab && uri.Host != "github.com") || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.AbsolutePath.Trim('/').Split('/').Length != 2)
            throw new InvalidDataException("Unexpected repository URL.");
        if (!fresh && ModuleReleases.TryGetValue(module.Url, out var cached) && cached != null
            && cached.CheckedAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromHours(6)
            && cached.Assets != null && cached.Assets.All(asset => asset != null
                && !string.IsNullOrWhiteSpace(asset.Name) && ValidModuleDownload(asset.Url)))
            return cached;

        bool simple = module.Url == "https://github.com/DeepLunaria/BShieldExperimentation";
        string api = gitlab ? "https://gitlab.com/api/v4/projects/Evokerr%2FAlwaysStrong/releases?per_page=1"
            : "https://api.github.com/repos" + uri.AbsolutePath + (simple ? "/releases?per_page=100" : "/releases/latest");
        using var response = await Client.GetAsync(api);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement release = json.RootElement;
        ModuleAsset[] assets;
        if (gitlab)
        {
            release = release.EnumerateArray().First();
            assets = release.GetProperty("assets").GetProperty("links").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                .Select(asset => new ModuleAsset(asset.GetProperty("name").GetString()!,
                    asset.GetProperty("url").GetString()!, null, -1)).ToArray();
        }
        else
        {
            if (simple) release = release.EnumerateArray().First(item => !item.GetProperty("prerelease").GetBoolean()
                && !item.GetProperty("draft").GetBoolean() && ReadGithubAssets(item, true).Length > 0);
            assets = ReadGithubAssets(release, simple);
        }
        if (assets.Any(asset => !ValidModuleDownload(asset.Url))) throw new InvalidDataException("Unexpected release asset URL.");
        var result = new RootModuleRelease(release.GetProperty("tag_name").GetString()!, assets, DateTimeOffset.UtcNow);
        ModuleReleases[module.Url] = result;
        Directory.CreateDirectory(Path.GetDirectoryName(ReleaseCachePath)!);
        string temporary = ReleaseCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ModuleReleases));
            File.Move(temporary, ReleaseCachePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }
}
