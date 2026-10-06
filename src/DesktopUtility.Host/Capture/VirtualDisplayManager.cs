using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DesktopUtility.Host.Capture;

/// <summary>
/// Controls the usbmmidd_v2 indirect display driver that provides the virtual second monitor.
/// deviceinstaller64.exe needs administrator rights, so each command is launched elevated (one UAC prompt).
/// </summary>
public sealed class VirtualDisplayManager
{
    private const string DriverRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\usbmmIdd";
    private const string DriverInf = "usbmmidd.inf";
    private const string DriverHardwareId = "usbmmidd";
    private const string InstallerExe = "deviceinstaller64.exe";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly string? _driverDirectory = FindDriverDirectory();

    public bool IsActive { get; private set; }

    public VirtualDisplayManager()
    {
        Console.WriteLine($"[VirtualDisplay] Driver directory: {_driverDirectory ?? "NOT FOUND"}");
    }

    public bool IsDriverInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DriverRegistryPath, false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds a virtual monitor to Windows, installing the driver first if necessary.</summary>
    public async Task<bool> EnableAsync()
    {
        if (IsActive) return true;

        if (_driverDirectory == null)
        {
            Console.WriteLine("[VirtualDisplay] Driver files not found; cannot create a virtual monitor.");
            return false;
        }

        // The registry key can outlive the actual device (stale install), so rely on the command result:
        // if enabling fails, (re)install the driver and try once more.
        Console.WriteLine("[VirtualDisplay] Enabling virtual monitor...");
        bool enabled = IsDriverInstalled() && await RunInstallerAsync("enableidd", "1").ConfigureAwait(false);
        if (!enabled)
        {
            Console.WriteLine("[VirtualDisplay] Installing driver...");
            if (!await RunInstallerAsync("install", $"{DriverInf} {DriverHardwareId}").ConfigureAwait(false))
                return false;
            if (!await RunInstallerAsync("enableidd", "1").ConfigureAwait(false))
                return false;
        }

        IsActive = true;

        // Windows needs a few seconds to enumerate the new monitor; if it only shows up as "disconnected"
        // (not part of the desktop), ask Windows to extend the desktop onto it.
        if (!await WaitForSecondaryAsync(10000).ConfigureAwait(false))
        {
            Console.WriteLine("[VirtualDisplay] Monitor not on the desktop yet, switching to Extend mode...");
            try
            {
                using var p = Process.Start(new ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = false, CreateNoWindow = true });
                if (p != null) await p.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"[VirtualDisplay] DisplaySwitch failed: {ex.Message}"); }

            if (!await WaitForSecondaryAsync(8000).ConfigureAwait(false))
                Console.WriteLine("[VirtualDisplay] Displays seen: " + string.Join(" | ", ScreenCapturer.GetAvailableDisplays()));
        }

        return true;
    }

    public async Task<bool> DisableAsync()
    {
        if (!IsActive) return true;
        if (_driverDirectory == null) return false;

        Console.WriteLine("[VirtualDisplay] Disabling virtual monitor...");
        if (!await RunInstallerAsync("enableidd", "0").ConfigureAwait(false))
            return false;

        IsActive = false;
        return true;
    }

    private static async Task<bool> WaitForSecondaryAsync(int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 500)
        {
            if (FindSecondaryDisplay() != null) return true;
            await Task.Delay(500).ConfigureAwait(false);
        }
        return FindSecondaryDisplay() != null;
    }

    /// <summary>The first non-primary display, i.e. the one the iPad should show.</summary>
    public static DisplayInfo? FindSecondaryDisplay() =>
        ScreenCapturer.GetAvailableDisplays().FirstOrDefault(d => !d.IsPrimary);

    private async Task<bool> RunInstallerAsync(string command, string arguments)
    {
        string exePath = Path.Combine(_driverDirectory!, InstallerExe);
        if (!File.Exists(exePath))
        {
            Console.WriteLine($"[VirtualDisplay] {InstallerExe} not found at {exePath}");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{command} {arguments}",
                WorkingDirectory = _driverDirectory!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process == null) return false;

            using var cts = new CancellationTokenSource(CommandTimeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            Console.WriteLine($"[VirtualDisplay] {command} {arguments} -> exit code {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("[VirtualDisplay] Administrator permission was declined.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VirtualDisplay] {command} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Looks for the driver next to the installed app first, then up the tree for development builds.</summary>
    private static string? FindDriverDirectory()
    {
        string baseDir = AppContext.BaseDirectory;

        string installed = Path.Combine(baseDir, "VirtualDisplayDriver");
        if (File.Exists(Path.Combine(installed, InstallerExe))) return installed;

        string? dir = baseDir;
        for (int i = 0; i < 8 && dir != null; i++, dir = Directory.GetParent(dir)?.FullName)
        {
            string dev = Path.Combine(dir, "scripts", "usbmmidd_v2", "usbmmidd_v2");
            if (File.Exists(Path.Combine(dev, InstallerExe))) return dev;
        }

        return File.Exists(Path.Combine(baseDir, InstallerExe)) ? baseDir : null;
    }
}
