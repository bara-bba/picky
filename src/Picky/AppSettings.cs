using System.IO;
using System.Text.Json;

namespace Picky;

/// <summary>
/// User settings persisted to %APPDATA%\Picky\settings.json.
/// Currently just the folder captures are auto-saved into.
/// </summary>
internal sealed class AppSettings
{
    public string CaptureFolder { get; set; } = DefaultCaptureFolder;

    /// <summary>Preferred global-capture hotkey (name of a <see cref="HotKeyDef"/> preset).</summary>
    public string HotKey { get; set; } = "Win+Shift+S";

    /// <summary>Accent color (hex) used across the GUI and the snip selection box.</summary>
    public string AccentColor { get; set; } = "#0078D4";

    /// <summary>Default annotation (pen) color in the editor.</summary>
    public string DefaultColor { get; set; } = "#E81123";

    private static string DefaultCaptureFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Picky");

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Picky",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.CaptureFolder))
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings — fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string EnsureCaptureFolder()
    {
        Directory.CreateDirectory(CaptureFolder);
        return CaptureFolder;
    }
}
