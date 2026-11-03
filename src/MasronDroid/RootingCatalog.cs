using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;

namespace MasronDroid;

internal sealed record RootModule(string Name, string Description, string Source, string Url, int Stars, string Version = "",
    string Id = "", long VersionCode = 0, string UpdateJson = "", bool Installed = false, ModuleAsset? UpdateAsset = null, long LatestCode = 0, bool Pending = false)
{
    public bool HasUpdate => Installed && !Pending && UpdateAsset != null && LatestCode > VersionCode;
}
internal sealed record ModuleAsset(string Name, string Url, string? Sha256, long Size);
internal sealed record MagiskRelease(string Version, string FileName, string Url, string Sha256, long Size);
internal sealed record RootModuleCache(int Schema, DateTimeOffset? SavedAt, bool Complete, int NextSource, int NextPage, RootModule[] Modules);

internal static partial class RootingCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(90) };

    static RootingCatalog() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("MasronDroid/0.0.5");

    internal static string ModuleCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasronDroid", "magisk-modules.json");

    internal static RootModuleCache? LoadModuleCache(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 10 * 1024 * 1024) throw new InvalidDataException("Module cache is too large.");
        var cache = JsonSerializer.Deserialize<RootModuleCache>(File.ReadAllText(path));
        if (cache == null || cache.Schema != 1 || cache.NextSource is < 1 or > 4 || cache.NextPage is < 1 or > 10
            || cache.Complete != (cache.NextSource == 4)
            || cache.Modules == null || cache.Modules.Length > 10000
            || cache.Modules.Any(module => module == null || string.IsNullOrWhiteSpace(module.Name)
                || module.Description == null || string.IsNullOrWhiteSpace(module.Source)
                || !Uri.TryCreate(module.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
                || uri.AbsolutePath.Trim('/').Split('/').Length != 2))
            throw new InvalidDataException("Invalid module cache.");
        return cache;
    }

    internal static void SaveModuleCache(string path, RootModuleCache cache)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<RootModule[]> Modules(int source, string search, int page)
    {
        string scope = source switch
        {
            1 => "org:Magisk-Modules-Alt-Repo",
            2 => "org:Magisk-Modules-Repo",
            3 => "topic:magisk-module",
            _ => "org:Magisk-Modules-Alt-Repo org:Magisk-Modules-Repo topic:magisk-module"
        };
        // GitHub combines qualifiers of different kinds with AND. Use broad topic
        // discovery separately from the two organization catalogs.
        if (source == 0)
        {
            var batches = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => Modules(index, search, page)));
            return batches.SelectMany(rows => rows).DistinctBy(row => row.Url).OrderByDescending(row => row.Stars).ToArray();
        }
        if (source is 1 or 2)
        {
            string org = source == 1 ? "Magisk-Modules-Alt-Repo" : "Magisk-Modules-Repo";
            using var orgJson = JsonDocument.Parse(await Client.GetStringAsync(
                $"https://api.github.com/orgs/{org}/repos?per_page=100&page={page}&sort=updated"));
            return orgJson.RootElement.EnumerateArray().Select(ParseModule).ToArray();
        }
        string query = scope + " " + search.Trim();
        using var json = JsonDocument.Parse(await Client.GetStringAsync(
            "https://api.github.com/search/repositories?per_page=100&sort=updated&page=" + page + "&q=" + Uri.EscapeDataString(query)));
        return json.RootElement.GetProperty("items").EnumerateArray().Select(ParseModule).ToArray();
    }

    private static RootModule ParseModule(JsonElement repo) => new(
        repo.GetProperty("name").GetString()!, repo.GetProperty("description").GetString() ?? "",
        repo.GetProperty("owner").GetProperty("login").GetString()!,
        repo.GetProperty("html_url").GetString()!, repo.GetProperty("stargazers_count").GetInt32());

    public static async Task<ModuleAsset[]> ModuleAssets(RootModule module)
    {
        return (await ModuleRelease(module, true)).Assets;
    }

    public static async Task<(string Path, string Hash, string Id)> DownloadModule(ModuleAsset asset)
    {
        if (!ValidModuleDownload(asset.Url) || Path.GetFileName(asset.Name) != asset.Name
            || !asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected module asset.");
        string folder = Path.Combine(AppContext.BaseDirectory, "RootingPackages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, asset.Name);
        using var response = await Client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        string hash;
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(path)) { await input.CopyToAsync(output); }
            if (asset.Size >= 0 && new FileInfo(path).Length != asset.Size) throw new InvalidDataException("Module size mismatch.");
            await using (var input = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
            if (asset.Sha256 != null && !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Module SHA-256 mismatch.");
            using var zip = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path);
            var prop = zip.Entries.SingleOrDefault(entry => entry.Key == "module.prop")
                ?? throw new InvalidDataException("ZIP must contain module.prop at its root.");
            if (prop.Size > 65536) throw new InvalidDataException("Invalid module metadata.");
            using var reader = new StreamReader(prop.OpenEntryStream());
            char[] buffer = new char[65537];
            int length = await reader.ReadBlockAsync(buffer);
            if (length > 65536) throw new InvalidDataException("Invalid module metadata.");
            string metadata = new(buffer, 0, length);
            var id = System.Text.RegularExpressions.Regex.Match(metadata, @"(?m)^id=([a-zA-Z][a-zA-Z0-9._-]*)\s*$").Groups[1].Value;
            if (id.Length == 0 || id.Length > 128) throw new InvalidDataException("Invalid module ID.");
            return (path, hash, id);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public static async Task<MagiskRelease> Latest()
    {
        using var json = JsonDocument.Parse(await Client.GetStringAsync("https://api.github.com/repos/topjohnwu/Magisk/releases/latest"));
        var release = json.RootElement;
        var asset = release.GetProperty("assets").EnumerateArray().First(a =>
            a.GetProperty("name").GetString() is { } name && name.StartsWith("Magisk-", StringComparison.Ordinal) && name.EndsWith(".apk", StringComparison.Ordinal));
        string digest = asset.GetProperty("digest").GetString() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71) throw new InvalidDataException("Missing release SHA-256.");
        return new(release.GetProperty("tag_name").GetString()!, asset.GetProperty("name").GetString()!,
            asset.GetProperty("browser_download_url").GetString()!, digest[7..], asset.GetProperty("size").GetInt64());
    }

    public static async Task<string> Download(MagiskRelease release)
    {
        if (!release.Url.StartsWith("https://github.com/topjohnwu/Magisk/releases/download/", StringComparison.Ordinal)
            || Path.GetFileName(release.FileName) != release.FileName) throw new InvalidDataException("Unexpected release asset.");
        string folder = Path.Combine(AppContext.BaseDirectory, "RootingPackages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string apk = Path.Combine(folder, release.FileName);
        string temporary = apk + ".partial";
        try
        {
            using var response = await Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(temporary)) { await input.CopyToAsync(output); }
            if (new FileInfo(temporary).Length != release.Size) throw new InvalidDataException("Release size mismatch.");
            await using (var input = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release SHA-256 mismatch.");
            File.Move(temporary, apk);
            string zip = Path.ChangeExtension(apk, ".zip");
            File.Copy(apk, zip);
            return zip;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
