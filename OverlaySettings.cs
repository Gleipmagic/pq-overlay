using System.Text.Json;
using System.IO;

namespace EQMobOverlay;

public sealed class OverlaySettings
{
    public double OffsetX { get; set; } = 30;
    public double OffsetY { get; set; } = 70;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQMobOverlay", "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsPath)) ?? new OverlaySettings();
        }
        catch { }
        return new OverlaySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
