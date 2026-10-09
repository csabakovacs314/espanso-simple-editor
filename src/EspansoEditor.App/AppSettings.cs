using System.IO;
using System.Text.Json;

namespace EspansoEditor.App;

/// <summary>Small per-user settings, kept in %LOCALAPPDATA%\EspansoSimpleEditor\settings.json.</summary>
internal sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public bool AutoOpen { get; set; } = true;

    private static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // A broken settings file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not worth interrupting the user over.
        }
    }
}
