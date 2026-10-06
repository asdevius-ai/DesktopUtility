using System.IO;

namespace DesktopUtility.Host.Core;

/// <summary>Well-known per-user locations used by the host.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopUtility");

    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string LogFile => Path.Combine(LogsDir, "desktoputility.log");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string TrustedDevicesFile => Path.Combine(DataDir, "trusted_devices.json");
}
