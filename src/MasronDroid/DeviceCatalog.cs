using System.IO.Compression;
using System.Text.Json;

namespace MasronDroid;

internal static class DeviceCatalog
{
    private static readonly Lazy<ILookup<string, string[]>> Models = new(() =>
    {
        using var source = typeof(DeviceCatalog).Assembly.GetManifestResourceStream("MasronDroid.Assets.device-catalog.json.gz")!;
        using var zip = new GZipStream(source, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<string[][]>(zip)!.ToLookup(r => Normalize(r[0]), StringComparer.OrdinalIgnoreCase);
    });

    private static string Normalize(string value) => value.Replace('_', ' ').Replace('-', ' ').Trim();

    public static (string Name, string? Image) Match(string model, string device)
    {
        var candidates = Models.Value[Normalize(model)].ToArray();
        var exact = candidates.Where(x => x[1].Equals(device, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) candidates = exact;
        var names = candidates.Select(r => r[3].StartsWith(r[2], StringComparison.OrdinalIgnoreCase)
            ? r[3] : (r[2] + " " + r[3]).Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length != 1) return (model, null);
        var image = candidates.Select(r => r[4]).Distinct().ToArray();
        return (names[0], image.Length == 1 && image[0].Length > 0 ? image[0] : null);
    }
}
