using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using DesktopUtility.Host.Video;
using DesktopUtility.Host.Input;
using DesktopUtility.Host.Security;
using SysEncoding = System.Text.Encoding;

namespace DesktopUtility.Host.Network;

/// <summary>The few host operations a paired client may trigger.</summary>
public interface IHostControl
{
    DisplayMode CurrentMode { get; }
    QualityPreset Quality { get; }
    void SetQuality(QualityPreset preset);
    Task<bool> SetModeAsync(DisplayMode mode);
}

/// <summary>
/// WebSocket endpoint used for pairing, WebRTC signaling (SDP / ICE), input events and a few control messages.
/// Nothing except <c>pair</c> and <c>ping</c> is honoured before the client has authenticated.
/// </summary>
public sealed class SignalingServer
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class ClientConnection(string id, WebSocket socket, string remoteAddress)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Id { get; } = id;
        public WebSocket Socket { get; } = socket;
        public string RemoteAddress { get; } = remoteAddress;
        public bool Authorized { get; set; }

        // WebSocket allows only one outstanding send; ICE callbacks and replies can race.
        public async Task SendAsync(object message)
        {
            if (Socket.State != WebSocketState.Open) return;

            byte[] bytes = SysEncoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Client went away while we were sending.
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly SessionManager _sessions;
    private readonly InputInjector _input;
    private readonly PairingManager _pairing;
    private readonly WebRtcManager _webRtc;
    private readonly IHostControl _host;

    public SignalingServer(SessionManager sessions, InputInjector input, PairingManager pairing,
        WebRtcManager webRtc, IHostControl host)
    {
        _sessions = sessions;
        _input = input;
        _pairing = pairing;
        _webRtc = webRtc;
        _host = host;

        _webRtc.IceCandidateReady += async (clientId, ice) =>
        {
            if (_clients.TryGetValue(clientId, out var client))
                await client.SendAsync(new
                {
                    type = "ice-candidate",
                    candidate = ice.Candidate,
                    sdpMid = ice.SdpMid,
                    sdpMLineIndex = ice.SdpMLineIndex
                });
        };
    }

    public async Task HandleAsync(WebSocket ws, string clientId, string remoteAddress, CancellationToken ct)
    {
        var client = new ClientConnection(clientId, ws, remoteAddress);

        // A reconnecting device replaces its previous (possibly half-dead) connection.
        if (_clients.TryGetValue(clientId, out var previous))
            _ = CloseSocketAsync(previous.Socket);
        _clients[clientId] = client;

        Console.WriteLine($"[Signaling] Client connected: {clientId} ({remoteAddress})");

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? json = await ReceiveTextAsync(ws, ct);
                if (json == null) break;

                await ProcessMessageAsync(client, json);
            }
        }
        catch (OperationCanceledException) { /* shutdown or idle timeout */ }
        catch (WebSocketException) { /* client dropped */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Error for {clientId}: {ex.Message}");
        }
        finally
        {
            // Only tear down state that still belongs to this connection; a newer one may have taken over.
            if (_clients.TryRemove(new KeyValuePair<string, ClientConnection>(clientId, client)))
            {
                _sessions.RemoveSession(clientId);
                _webRtc.CloseConnection(clientId);
                if (_clients.IsEmpty) _input.ReleaseAll();
            }

            Console.WriteLine($"[Signaling] Client disconnected: {clientId}");
            await CloseSocketAsync(ws);
        }
    }

    public async Task BroadcastAsync(object message)
    {
        foreach (var client in _clients.Values.Where(c => c.Authorized))
            await client.SendAsync(message);
    }

    public Task BroadcastModeChangedAsync(DisplayMode mode) =>
        BroadcastAsync(new { type = "mode-changed", mode = ModeName(mode) });

    public Task BroadcastQualityChangedAsync(QualityPreset preset) =>
        BroadcastAsync(new { type = "quality-changed", preset = QualityName(preset) });

    public static string QualityName(QualityPreset preset) => preset.ToString().ToLowerInvariant();

    public static string ModeName(DisplayMode mode) => mode == DisplayMode.ExtendedDisplay ? "extended" : "remote";

    // ─── Message handling ───────────────────────

    private async Task ProcessMessageAsync(ClientConnection client, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type)
            {
                case "ping":
                    await client.SendAsync(new { type = "pong", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    return;

                case "pair":
                    await HandlePairAsync(client, root);
                    return;
            }

            if (!client.Authorized)
            {
                await client.SendAsync(new { type = "error", code = "unauthorized" });
                return;
            }

            switch (type)
            {
                case "offer":
                    await HandleOfferAsync(client, root);
                    break;

                case "ice-candidate":
                    _webRtc.AddIceCandidate(
                        client.Id,
                        root.GetProperty("candidate").GetString() ?? "",
                        root.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                        root.TryGetProperty("sdpMLineIndex", out var idx) && idx.TryGetInt32(out int i) ? i : 0);
                    break;

                case "quality":
                    if (root.TryGetProperty("preset", out var preset))
                    {
                        _host.SetQuality((preset.GetString()?.ToLowerInvariant()) switch
                        {
                            "low" => QualityPreset.Low,
                            "high" => QualityPreset.High,
                            _ => QualityPreset.Medium
                        });
                    }
                    break;

                case "change-mode":
                    if (root.TryGetProperty("mode", out var modeProp))
                    {
                        var mode = modeProp.GetString() == "extended" ? DisplayMode.ExtendedDisplay : DisplayMode.RemoteControl;
                        Console.WriteLine($"[Signaling] Mode change requested by {client.Id}: {ModeName(mode)}");
                        if (!await _host.SetModeAsync(mode))
                            await client.SendAsync(new { type = "mode-failed", mode = ModeName(_host.CurrentMode) });
                    }
                    break;

                default:
                    if (InputTypes.IsInput(type))
                    {
                        var input = JsonSerializer.Deserialize<InputMessage>(json, JsonOptions);
                        if (input != null) _input.ProcessInput(input);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Malformed message from the client: ignore.
        }
        catch (KeyNotFoundException)
        {
            // Missing required field: ignore.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Error processing message from {client.Id}: {ex.Message}");
        }
    }

    private async Task HandlePairAsync(ClientConnection client, JsonElement root)
    {
        string secret = root.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        string deviceSecret = root.TryGetProperty("deviceSecret", out var ds) ? ds.GetString() ?? "" : "";
        string deviceName = SanitizeName(root.TryGetProperty("deviceName", out var d) ? d.GetString() : null);

        string? newDeviceSecret = null;
        bool reconnected = false;
        string? failure = null;

        if (_pairing.IsDeviceTrusted(client.Id, deviceSecret))
        {
            reconnected = true;
        }
        else if (!string.IsNullOrEmpty(secret))
        {
            switch (_pairing.TryPair(secret, client.RemoteAddress, client.Id, deviceName, out newDeviceSecret))
            {
                case PairResult.Success: break;
                case PairResult.LockedOut: failure = "locked"; break;
                default: failure = "invalid"; break;
            }
        }
        else
        {
            failure = "need-pin";
        }

        if (failure != null)
        {
            await client.SendAsync(new { type = "paired", success = false, code = failure });
            return;
        }

        client.Authorized = true;
        _sessions.AddSession(client.Id, deviceName);

        await client.SendAsync(new
        {
            type = "paired",
            success = true,
            reconnected,
            hostName = Environment.MachineName,
            mode = ModeName(_host.CurrentMode),
            quality = QualityName(_host.Quality),
            deviceSecret = newDeviceSecret
        });

        Console.WriteLine($"[Signaling] Device {(reconnected ? "reconnected" : "paired")}: {deviceName} ({client.Id})");
    }

    private async Task HandleOfferAsync(ClientConnection client, JsonElement root)
    {
        string sdp = root.GetProperty("sdp").GetString() ?? "";
        string? answer = await _webRtc.HandleOfferAsync(client.Id, sdp);

        if (answer == null)
        {
            await client.SendAsync(new { type = "error", code = "webrtc-failed" });
            return;
        }

        await client.SendAsync(new { type = "answer", sdp = answer });
        _webRtc.MarkAnswerSent(client.Id);
    }

    // ─── Helpers ────────────────────────────────

    /// <summary>Reads one complete text message, or null when the socket closed or timed out.</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        // The client pings every couple of seconds, so silence means the connection is dead.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleTimeout);

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxMessageBytes) return null;
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text ? SysEncoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length) : "";
    }

    private static async Task CloseSocketAsync(WebSocket ws)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
        catch { /* already gone */ }
    }

    private static string SanitizeName(string? name)
    {
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return "Unknown device";
        return name.Length > 40 ? name[..40] : name;
    }
}
