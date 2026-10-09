using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConfluencePublisher;

/// <summary>
/// What the publisher remembers between runs, in %LOCALAPPDATA%\ConfluencePublisher\settings.json.
/// The API token is only kept when asked, and then encrypted with Windows DPAPI for the current Windows user,
/// so the file is useless on another account or PC.
/// </summary>
internal sealed class PublisherSettings
{
    public string Site { get; set; } = "";
    public string Email { get; set; } = "";
    public string? ProtectedToken { get; set; }
    public string LastFile { get; set; } = "";
    public string LastParent { get; set; } = "";
    public string LastParentPath { get; set; } = "";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConfluencePublisher.v1");
    private static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static PublisherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<PublisherSettings>(File.ReadAllText(FilePath)) ?? new PublisherSettings()).Tidy();
        }
        catch (Exception ex)
        {
            App.Log("Settings could not be read, starting fresh:\n" + ex.Message);
        }
        return new PublisherSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log("Settings could not be saved:\n" + ex.Message);
        }
    }

    // Methods, not a property, so the plain token can never end up in settings.json through the JSON serializer.
    public string? GetToken()
    {
        if (string.IsNullOrEmpty(ProtectedToken)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null; // Another Windows user or PC: ask for the token again.
        }
    }

    public void SetToken(string? value) => ProtectedToken = string.IsNullOrEmpty(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>A hand-edited or damaged file can hold nulls; the window expects text.</summary>
    private PublisherSettings Tidy()
    {
        Site ??= "";
        Email ??= "";
        LastFile ??= "";
        LastParent ??= "";
        LastParentPath ??= "";
        return this;
    }
}
