using System.Security.Cryptography;
using System.Text;

namespace DesktopUtility.Host.Security;

public enum PairResult
{
    Success,
    Invalid,
    LockedOut
}

/// <summary>
/// Issues the QR token / 6-digit PIN shown on the PC and validates pairing attempts.
/// A code is valid for a limited time and is invalidated after the first successful pairing
/// or after too many wrong guesses, which makes brute-forcing the PIN impractical.
/// </summary>
public sealed class PairingManager
{
    private const int MaxFailuresPerClient = 5;
    private const int MaxFailuresPerCode = 8;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    private readonly TrustedDevices _trustedDevices;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _clientFailures = new();

    private string _pin = "";
    private string _token = "";
    private DateTimeOffset _expiry;
    private int _failuresSinceRotate;

    /// <summary>Raised whenever a new PIN/token pair is generated.</summary>
    public event Action? CodeChanged;

    public string CurrentPin { get { lock (_lock) return _pin; } }
    public string CurrentToken { get { lock (_lock) return _token; } }

    public PairingManager(TrustedDevices trustedDevices)
    {
        _trustedDevices = trustedDevices;
        Rotate();
    }

    public string GetPairingUrl(string host, int port)
    {
        lock (_lock) return $"http://{host}:{port}/?pair={_token}";
    }

    /// <summary>Generates a fresh PIN + token and invalidates the previous ones.</summary>
    public void Rotate()
    {
        lock (_lock)
        {
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _expiry = DateTimeOffset.UtcNow + CodeLifetime;
            _failuresSinceRotate = 0;
        }

        Console.WriteLine("[Pairing] New pairing code generated");
        CodeChanged?.Invoke();
    }

    /// <summary>Rotates the code if it has expired. Called periodically by the host.</summary>
    public void RotateIfExpired()
    {
        bool expired;
        lock (_lock) expired = DateTimeOffset.UtcNow >= _expiry;
        if (expired) Rotate();
    }

    /// <summary>
    /// Validates a QR token or PIN. On success the code is consumed and the device
    /// receives a secret it must present on every future connection.
    /// </summary>
    public PairResult TryPair(string? secret, string clientAddress, string deviceId, string deviceName, out string? deviceSecret)
    {
        deviceSecret = null;
        bool rotate = false;
        PairResult result;

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (_clientFailures.TryGetValue(clientAddress, out var f) && f.Count >= MaxFailuresPerClient && now < f.Until)
                return PairResult.LockedOut;

            bool valid = now < _expiry && !string.IsNullOrEmpty(secret)
                && (Matches(secret, _token) || Matches(secret, _pin));

            if (valid)
            {
                _clientFailures.Remove(clientAddress);
                result = PairResult.Success;
                rotate = true;
            }
            else
            {
                int count = (_clientFailures.TryGetValue(clientAddress, out var prev) && now < prev.Until ? prev.Count : 0) + 1;
                _clientFailures[clientAddress] = (count, now + LockoutDuration);
                result = PairResult.Invalid;
                Console.WriteLine($"[Pairing] Failed attempt from {clientAddress}");

                // Too many wrong guesses against the same code: burn it.
                if (++_failuresSinceRotate >= MaxFailuresPerCode) rotate = true;
            }
        }

        if (result == PairResult.Success)
            deviceSecret = _trustedDevices.AddDevice(deviceId, deviceName);
        if (rotate)
            Rotate();

        return result;
    }

    /// <summary>True when the device id + secret pair belongs to a previously paired device.</summary>
    public bool IsDeviceTrusted(string deviceId, string? deviceSecret) =>
        _trustedDevices.Verify(deviceId, deviceSecret);

    public void RevokeDevice(string deviceId) => _trustedDevices.RemoveDevice(deviceId);

    private static bool Matches(string candidate, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
}
