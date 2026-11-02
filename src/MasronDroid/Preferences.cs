using System.IO;
using System.Text.Json;

namespace MasronDroid;

public sealed class Preferences
{
    private static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MasronDroid", "preferences.json");
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "id";

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double LogHeight { get; set; }
    public bool Maximized { get; set; }
    public string Page { get; set; } = "Device";
    public string[] History { get; set; } = [];

    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FileName)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        string temp = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FileName, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
