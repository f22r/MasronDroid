using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace MasronDroid;

internal static partial class RootingCatalog
{
    internal static async Task<(RootModule Module, string Hash)> InspectLocalModule(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a module ZIP file.");
        using var zip = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path);
        var prop = zip.Entries.SingleOrDefault(entry => entry.Key == "module.prop");
        if (prop == null || prop.Size > 65536) throw new InvalidDataException("ZIP must contain module.prop at its root.");
        using var reader = new StreamReader(prop.OpenEntryStream());
        char[] buffer = new char[65537];
        int length = await reader.ReadBlockAsync(buffer);
        if (length > 65536) throw new InvalidDataException("Module metadata is too large.");
        string metadata = new(buffer, 0, length);
        string Value(string key) => Regex.Match(metadata, @"(?m)^" + key + @"=([^\r\n]*)\r?$").Groups[1].Value.Trim();
        string id = Value("id"), code = Value("versionCode");
        if (!Regex.IsMatch(id, @"^[a-zA-Z][a-zA-Z0-9._-]{0,127}$")
            || code.Length > 64 || !Regex.IsMatch(code, @"^[0-9]+(?:\.[0-9]+)*$"))
            throw new InvalidDataException("Invalid module ID or versionCode.");
        string name = Value("name"), version = Value("version");
        await using var input = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
        _ = long.TryParse(code, out long numericCode);
        return (new(name.Length > 0 ? name : id, Value("description"), "Local", "", 0, version, id, numericCode), hash);
    }
}
