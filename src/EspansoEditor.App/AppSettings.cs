using System.IO;
using System.Text.Json;

namespace EspansoEditor.App;

/// <summary>Small per-user settings, kept in %LOCALAPPDATA%\EspansoSimpleEditor\settings.json.</summary>
internal sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public bool AutoOpen { get; set; } = true;

    /// <summary>
    /// When "Only as a whole word" is switched on by itself: "needed" (when a trigger would block a longer one),
    /// "always" (every new snippet) or "never".
    /// </summary>
    public string WholeWordMode { get; set; } = WholeWord.Needed;

    private static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.WholeWordMode = (s.WholeWordMode ?? "").Trim().ToLowerInvariant() switch
                {
                    WholeWord.Always => WholeWord.Always,
                    WholeWord.Never => WholeWord.Never,
                    _ => WholeWord.Needed,
                };
                return s;
            }
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

/// <summary>The choices for <see cref="AppSettings.WholeWordMode"/>.</summary>
internal static class WholeWord
{
    public const string Needed = "needed";
    public const string Always = "always";
    public const string Never = "never";
}
