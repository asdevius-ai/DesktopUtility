using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopUtility.Host.Core;

namespace DesktopUtility.Host.Security;

/// <summary>
/// Persistent list of paired devices. Each device gets a random secret at pairing time;
/// only its SHA-256 hash is stored, so a leaked device id alone cannot be used to reconnect.
/// </summary>
public sealed class TrustedDevices
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, TrustedDevice> _devices = new();
    private readonly string _storagePath;
    private readonly object _saveLock = new();

    public TrustedDevices(string? storagePath = null)
    {
        _storagePath = storagePath ?? AppPaths.TrustedDevicesFile;
        Load();
    }

    /// <summary>Registers (or re-registers) a device and returns its new plaintext secret.</summary>
    public string AddDevice(string deviceId, string deviceName)
    {
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        _devices[deviceId] = new TrustedDevice
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            SecretHash = Hash(secret),
            PairedAt = now,
            LastSeen = now
        };

        Save();
        Console.WriteLine($"[TrustedDevices] Added: {deviceName} ({deviceId})");
        return secret;
    }

    public bool Verify(string deviceId, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !_devices.TryGetValue(deviceId, out var device)) return false;

        bool ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(secret)), Encoding.UTF8.GetBytes(device.SecretHash));
        if (ok) device.LastSeen = DateTimeOffset.UtcNow;
        return ok;
    }

    public void RemoveDevice(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            Save();
            Console.WriteLine($"[TrustedDevices] Removed: {device.DeviceName} ({deviceId})");
        }
    }

    private static string Hash(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            var devices = JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(_storagePath));
            // Entries without a secret hash come from an older version and must re-pair.
            foreach (var d in devices?.Where(d => !string.IsNullOrEmpty(d.SecretHash)) ?? [])
                _devices[d.DeviceId] = d;

            Console.WriteLine($"[TrustedDevices] Loaded {_devices.Count} device(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrustedDevices] Error loading: {ex.Message}");
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                string tmp = _storagePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_devices.Values.ToList(), JsonOptions));
                File.Move(tmp, _storagePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrustedDevices] Error saving: {ex.Message}");
            }
        }
    }
}

public sealed class TrustedDevice
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
