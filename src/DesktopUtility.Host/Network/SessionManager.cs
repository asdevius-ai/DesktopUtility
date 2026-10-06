using System.Collections.Concurrent;

namespace DesktopUtility.Host.Network;

/// <summary>Tracks the currently connected, authorized devices (shown in the UI).</summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();

    /// <summary>Raised with the new session count when a session is added or removed.</summary>
    public event Action<int>? SessionsChanged;

    public int ActiveCount => _sessions.Count;
    public IReadOnlyCollection<ClientSession> ActiveSessions => _sessions.Values.ToList();

    public void AddSession(string clientId, string deviceName)
    {
        _sessions[clientId] = new ClientSession(clientId, deviceName, DateTimeOffset.UtcNow);
        Console.WriteLine($"[Session] Added: {deviceName} ({clientId})");
        SessionsChanged?.Invoke(_sessions.Count);
    }

    public void RemoveSession(string clientId)
    {
        if (_sessions.TryRemove(clientId, out var session))
        {
            Console.WriteLine($"[Session] Removed: {session.DeviceName} ({clientId})");
            SessionsChanged?.Invoke(_sessions.Count);
        }
    }
}

public sealed record ClientSession(string ClientId, string DeviceName, DateTimeOffset ConnectedAt);
