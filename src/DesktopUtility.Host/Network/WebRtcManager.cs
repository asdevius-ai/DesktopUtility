using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace DesktopUtility.Host.Network;

/// <summary>
/// Owns one WebRTC peer connection per paired client and pushes the encoded H.264
/// screen stream to all connected peers. Input travels over the signaling WebSocket,
/// so no data channel is negotiated.
/// </summary>
public sealed class WebRtcManager : IDisposable
{
    private const int DefaultH264PayloadType = 96;

    private sealed class Peer(RTCPeerConnection connection)
    {
        public RTCPeerConnection Connection { get; } = connection;
        public List<IceCandidateInfo> Pending { get; } = new();
        public bool AnswerSent { get; set; }
        public int PayloadType { get; set; } = DefaultH264PayloadType;
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private long _lastSendErrorTicks;

    /// <summary>A local ICE candidate that should be forwarded to the client (only raised after the answer was sent).</summary>
    public event Func<string, IceCandidateInfo, Task>? IceCandidateReady;

    public event Action<string, RTCPeerConnectionState>? ConnectionStateChanged;

    public bool HasConnectedPeers =>
        _peers.Values.Any(p => p.Connection.connectionState == RTCPeerConnectionState.connected);

    /// <summary>
    /// Creates and discards a peer connection so the first real client does not wait for
    /// JIT compilation and DTLS certificate generation (several seconds on a cold start).
    /// </summary>
    public void WarmUp()
    {
        try
        {
            using var pc = new RTCPeerConnection(new RTCConfiguration());
            pc.close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC] Warm-up skipped: {ex.Message}");
        }
    }

    /// <summary>Handles an SDP offer from a client and returns the SDP answer, or null on failure.</summary>
    public async Task<string?> HandleOfferAsync(string clientId, string sdpOffer)
    {
        CloseConnection(clientId);

        try
        {
            // LAN only: host candidates are enough, and skipping STUN avoids slow ICE gathering offline.
            var pc = new RTCPeerConnection(new RTCConfiguration());
            var peer = new Peer(pc);
            _peers[clientId] = peer;

            pc.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new(SDPMediaTypesEnum.video, DefaultH264PayloadType, "H264/90000",
                        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f")
                },
                MediaStreamStatusEnum.SendOnly));

            pc.onicecandidate += candidate =>
            {
                if (candidate == null) return;

                var info = new IceCandidateInfo(candidate.ToString(), candidate.sdpMid, candidate.sdpMLineIndex);
                lock (peer.Gate)
                {
                    if (!peer.AnswerSent)
                    {
                        peer.Pending.Add(info);
                        return;
                    }
                }
                _ = RaiseIceCandidateAsync(clientId, info);
            };

            pc.onconnectionstatechange += state =>
            {
                Console.WriteLine($"[WebRTC] {clientId}: {state}");
                ConnectionStateChanged?.Invoke(clientId, state);

                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                    Remove(clientId, peer);
            };

            var offer = SDP.ParseSDPDescription(sdpOffer);
            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer.ToString()
            });

            if (result != SetDescriptionResultEnum.OK)
            {
                Console.WriteLine($"[WebRTC] Failed to set remote description: {result}");
                Remove(clientId, peer);
                return null;
            }

            // Use the payload type the browser actually negotiated for H.264.
            int negotiated = pc.GetSendingFormat(SDPMediaTypesEnum.video).ID;
            peer.PayloadType = negotiated > 0 ? negotiated : DefaultH264PayloadType;

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            Console.WriteLine($"[WebRTC] Created answer for {clientId} (H.264 payload type {peer.PayloadType})");
            return answer.sdp.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC] Error handling offer from {clientId}: {ex.Message}");
            CloseConnection(clientId);
            return null;
        }
    }

    /// <summary>Called once the answer went out: flushes buffered candidates and enables trickle ICE.</summary>
    public void MarkAnswerSent(string clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;

        List<IceCandidateInfo> flush;
        lock (peer.Gate)
        {
            peer.AnswerSent = true;
            flush = new List<IceCandidateInfo>(peer.Pending);
            peer.Pending.Clear();
        }

        foreach (var info in flush)
            _ = RaiseIceCandidateAsync(clientId, info);
    }

    public void AddIceCandidate(string clientId, string candidate, string? sdpMid, int sdpMLineIndex)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;

        try
        {
            peer.Connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid ?? "0",
                sdpMLineIndex = (ushort)Math.Clamp(sdpMLineIndex, 0, ushort.MaxValue)
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC] Bad ICE candidate from {clientId}: {ex.Message}");
        }
    }

    /// <summary>Sends one encoded access unit to every connected peer.</summary>
    /// <param name="durationRtpUnits">Time since the previous frame in 90 kHz RTP units.</param>
    public void SendVideoFrame(byte[] encodedFrame, uint durationRtpUnits)
    {
        foreach (var (clientId, peer) in _peers)
        {
            if (peer.Connection.connectionState != RTCPeerConnectionState.connected) continue;

            try
            {
                peer.Connection.SendH264Frame(durationRtpUnits, peer.PayloadType, encodedFrame);
            }
            catch (Exception ex)
            {
                // Don't flood the log at 30 messages per second.
                long now = Environment.TickCount64;
                if (now - Interlocked.Exchange(ref _lastSendErrorTicks, now) > 5000)
                    Console.WriteLine($"[WebRTC] Error sending frame to {clientId}: {ex.Message}");
            }
        }
    }

    public void CloseConnection(string clientId)
    {
        if (_peers.TryGetValue(clientId, out var peer))
            Remove(clientId, peer);
    }

    public void Dispose()
    {
        foreach (var (clientId, peer) in _peers)
            Remove(clientId, peer);
    }

    /// <summary>Removes the peer only if it is still the registered one (a newer offer may have replaced it).</summary>
    private void Remove(string clientId, Peer peer)
    {
        if (_peers.TryRemove(new KeyValuePair<string, Peer>(clientId, peer)))
            Console.WriteLine($"[WebRTC] Closed connection: {clientId}");

        try { peer.Connection.close(); } catch { }
    }

    private async Task RaiseIceCandidateAsync(string clientId, IceCandidateInfo info)
    {
        var handler = IceCandidateReady;
        if (handler == null) return;

        try { await handler(clientId, info); }
        catch (Exception ex) { Console.WriteLine($"[WebRTC] Failed to forward ICE candidate: {ex.Message}"); }
    }
}

/// <summary>ICE candidate in the shape the signaling channel needs.</summary>
public sealed record IceCandidateInfo(string Candidate, string? SdpMid, int SdpMLineIndex);
