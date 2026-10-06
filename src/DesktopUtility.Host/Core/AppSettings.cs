using System.IO;
using System.Text.Json;

namespace DesktopUtility.Host.Core;

/// <summary>User preferences persisted between runs.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>"extended" (second monitor) or "remote" (touchpad on the main display).</summary>
    public string Mode { get; set; } = "extended";

    /// <summary>"low", "medium" or "high".</summary>
    public string Quality { get; set; } = "medium";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Could not read settings, using defaults: {ex.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Could not save settings: {ex.Message}");
        }
    }
}
