/**
 * connection.js — signaling WebSocket + WebRTC peer connection to the Windows host.
 *
 * One WebSocket carries pairing, SDP/ICE, ping/pong, control messages and all input events.
 * Video arrives over a receive-only WebRTC track. A paired device stores a secret in localStorage
 * and uses it to reconnect without a PIN.
 */

class ConnectionError extends Error {
    /** @param {string} code  'need-pin' | 'invalid' | 'locked' | 'timeout' | 'network' | 'webrtc' | 'unauthorized' */
    constructor(code, message) {
        super(message || code);
        this.code = code;
    }
}

class ConnectionManager {
    constructor() {
        this.clientId = this._loadClientId();
        this.host = '';
        this.port = 9876;
        this.state = 'disconnected'; // disconnected | connecting | connected | reconnecting

        // Callbacks (assigned by app.js)
        this.onStateChange = null;
        this.onVideoStream = null;
        this.onLatency = null;
        this.onModeChanged = null;
        this.onModeFailed = null;
        this.onQualityChanged = null;
        this.onFatal = null; // reconnecting gave up, or the host no longer trusts this device

        this._session = null; // { ws, pc, pendingCandidates, remoteSet }
        this._pairWaiter = null;
        this._pingTimer = null;
        this._lastPong = 0;
        this._reconnectTimer = null;
        this._reconnectAttempts = 0;
        this._maxReconnectAttempts = 12;
        this._dropTimer = null;
    }

    // ─── Public API ─────────────────────────────

    /**
     * Connects and pairs. Resolves once the offer was sent (video arrives later through onVideoStream).
     * @param {string|null} secret  QR token or PIN; omit to reconnect as an already paired device.
     * @throws {ConnectionError}
     */
    async connect(host, port, secret = null) {
        this._clearReconnect();
        this.host = host;
        this.port = port;
        this._setState('connecting');

        try {
            await this._open(secret);
            this._reconnectAttempts = 0;
        } catch (err) {
            this._teardown();
            this._setState('disconnected');
            throw err;
        }
    }

    /** User-initiated disconnect: no reconnection. */
    disconnect() {
        this._clearReconnect();
        this._teardown();
        this._setState('disconnected');
    }

    /** True when this device has previously paired (so a PIN is not needed). */
    hasDeviceSecret() {
        return !!localStorage.getItem('du_deviceSecret');
    }

    forgetDevice() {
        localStorage.removeItem('du_deviceSecret');
    }

    sendInput(message) {
        const ws = this._session && this._session.ws;
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
    }

    sendQuality(preset) { this._send({ type: 'quality', preset }); }
    changeMode(mode) { this._send({ type: 'change-mode', mode }); }

    // ─── Connection setup ───────────────────────

    async _open(secret) {
        const session = { ws: null, pc: null, pendingCandidates: [], remoteSet: false, live: false };
        this._session = session;

        await this._openSocket(session);
        const paired = await this._pair(session, secret);

        if (paired.deviceSecret) localStorage.setItem('du_deviceSecret', paired.deviceSecret);
        if (paired.mode && this.onModeChanged) this.onModeChanged(paired.mode);
        if (paired.quality && this.onQualityChanged) this.onQualityChanged(paired.quality);

        await this._startWebRtc(session);
        this._startPingMonitor(session);
        session.live = true; // from here on a socket or ICE failure triggers reconnection instead of rejecting connect()
    }

    _openSocket(session) {
        return new Promise((resolve, reject) => {
            const url = `ws://${this.host}:${this.port}/ws/signaling?clientId=${encodeURIComponent(this.clientId)}`;
            let settled = false;
            let ws;

            try {
                ws = new WebSocket(url);
            } catch (e) {
                reject(new ConnectionError('network', e.message));
                return;
            }
            session.ws = ws;

            const timer = setTimeout(() => {
                if (settled) return;
                settled = true;
                try { ws.close(); } catch (e) { /* ignore */ }
                reject(new ConnectionError('timeout'));
            }, 8000);

            ws.onopen = () => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                resolve();
            };

            ws.onerror = () => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                reject(new ConnectionError('network'));
            };

            ws.onclose = () => {
                if (this._session === session && session.live) this._handleDrop();
            };

            ws.onmessage = (event) => {
                if (this._session === session) this._onSignaling(session, event.data);
            };
        });
    }

    _pair(session, secret) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this._pairWaiter = null;
                reject(new ConnectionError('timeout'));
            }, 10000);

            this._pairWaiter = (msg) => {
                clearTimeout(timer);
                if (msg.success) resolve(msg);
                else reject(new ConnectionError(msg.code || 'invalid'));
            };

            this._send({
                type: 'pair',
                token: secret || '',
                deviceSecret: localStorage.getItem('du_deviceSecret') || '',
                deviceName: this._deviceName()
            });
        });
    }

    async _startWebRtc(session) {
        // LAN only: host candidates are enough, no STUN needed.
        const pc = new RTCPeerConnection({ iceServers: [] });
        session.pc = pc;

        pc.ontrack = (event) => {
            if (event.track.kind !== 'video') return;
            try { event.receiver.playoutDelayHint = 0; } catch (e) { /* not supported */ }
            try { event.receiver.jitterBufferTarget = 0; } catch (e) { /* not supported */ }
            if (this.onVideoStream && event.streams[0]) this.onVideoStream(event.streams[0]);
        };

        pc.onicecandidate = (event) => {
            if (event.candidate && this._session === session) {
                this._send({
                    type: 'ice-candidate',
                    candidate: event.candidate.candidate,
                    sdpMid: event.candidate.sdpMid,
                    sdpMLineIndex: event.candidate.sdpMLineIndex
                });
            }
        };

        pc.onconnectionstatechange = () => {
            if (this._session !== session) return;

            switch (pc.connectionState) {
                case 'connected':
                    clearTimeout(this._dropTimer);
                    this._reconnectAttempts = 0;
                    this._setState('connected');
                    break;
                case 'disconnected':
                    // Often transient on Wi-Fi: give it a moment before reconnecting.
                    clearTimeout(this._dropTimer);
                    this._dropTimer = setTimeout(() => {
                        if (this._session === session && pc.connectionState !== 'connected') this._handleDrop();
                    }, 3000);
                    break;
                case 'failed':
                    this._handleDrop();
                    break;
            }
        };

        const transceiver = pc.addTransceiver('video', { direction: 'recvonly' });
        try {
            // The host only sends H.264; offering just that keeps negotiation unambiguous.
            const h264 = RTCRtpReceiver.getCapabilities('video').codecs.filter((c) => c.mimeType === 'video/H264');
            if (h264.length) transceiver.setCodecPreferences(h264);
        } catch (e) { /* older browsers: let negotiation sort it out */ }

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        this._send({ type: 'offer', sdp: offer.sdp });
    }

    // ─── Signaling messages ─────────────────────

    async _onSignaling(session, data) {
        let msg;
        try { msg = JSON.parse(data); } catch (e) { return; }

        switch (msg.type) {
            case 'paired':
                if (this._pairWaiter) {
                    const waiter = this._pairWaiter;
                    this._pairWaiter = null;
                    waiter(msg);
                }
                break;

            case 'answer':
                try {
                    await session.pc.setRemoteDescription({ type: 'answer', sdp: msg.sdp });
                    session.remoteSet = true;
                    for (const c of session.pendingCandidates) await this._addCandidate(session, c);
                    session.pendingCandidates = [];
                } catch (e) {
                    console.error('[Connection] Bad answer:', e);
                }
                break;

            case 'ice-candidate':
                if (!msg.candidate) break;
                if (session.remoteSet) await this._addCandidate(session, msg);
                else session.pendingCandidates.push(msg);
                break;

            case 'pong':
                this._lastPong = Date.now();
                if (this._lastPingSent && this.onLatency) this.onLatency(this._lastPong - this._lastPingSent);
                break;

            case 'mode-changed':
                if (this.onModeChanged) this.onModeChanged(msg.mode);
                break;

            case 'quality-changed':
                if (this.onQualityChanged) this.onQualityChanged(msg.preset);
                break;

            case 'mode-failed':
                if (this.onModeFailed) this.onModeFailed(msg.mode);
                break;

            case 'error':
                if (msg.code === 'unauthorized') this._fatal(new ConnectionError('unauthorized'));
                else if (msg.code === 'webrtc-failed') this._fatal(new ConnectionError('webrtc'));
                break;
        }
    }

    async _addCandidate(session, c) {
        try {
            await session.pc.addIceCandidate({ candidate: c.candidate, sdpMid: c.sdpMid, sdpMLineIndex: c.sdpMLineIndex });
        } catch (e) { /* a stray candidate is not fatal */ }
    }

    // ─── Liveness & reconnection ────────────────

    _startPingMonitor(session) {
        this._stopPingMonitor();
        this._lastPong = Date.now();

        this._pingTimer = setInterval(() => {
            if (this._session !== session) return;

            // The host answers every ping; silence means a half-open connection.
            if (Date.now() - this._lastPong > 9000) {
                this._handleDrop();
                return;
            }
            this._lastPingSent = Date.now();
            this._send({ type: 'ping' });
        }, 2000);
    }

    _stopPingMonitor() {
        clearInterval(this._pingTimer);
        this._pingTimer = null;
    }

    _handleDrop() {
        if (this.state === 'disconnected') return;

        this._teardown();
        this._setState('reconnecting');
        this._scheduleReconnect();
    }

    _scheduleReconnect() {
        clearTimeout(this._reconnectTimer);
        if (this._reconnectAttempts >= this._maxReconnectAttempts) {
            this._fatal(new ConnectionError('network', 'gave up'));
            return;
        }

        const attempt = ++this._reconnectAttempts;
        const delay = Math.min(800 * Math.pow(1.5, attempt - 1), 8000);

        this._reconnectTimer = setTimeout(async () => {
            if (this.state !== 'reconnecting') return;
            try {
                await this._open(null);
                // State flips to 'connected' once WebRTC is up.
            } catch (err) {
                this._teardown();
                if (err.code === 'need-pin' || err.code === 'invalid' || err.code === 'locked') this._fatal(err);
                else if (this.state === 'reconnecting') this._scheduleReconnect();
            }
        }, delay);
    }

    _fatal(error) {
        this._clearReconnect();
        this._teardown();
        this._setState('disconnected');
        if (this.onFatal) this.onFatal(error);
    }

    _clearReconnect() {
        clearTimeout(this._reconnectTimer);
        clearTimeout(this._dropTimer);
        this._reconnectTimer = null;
    }

    _teardown() {
        this._stopPingMonitor();
        const s = this._session;
        this._session = null;
        this._pairWaiter = null;
        if (!s) return;

        if (s.pc) {
            s.pc.onconnectionstatechange = null;
            s.pc.ontrack = null;
            s.pc.onicecandidate = null;
            try { s.pc.close(); } catch (e) { /* ignore */ }
        }
        if (s.ws) {
            s.ws.onclose = null;
            s.ws.onmessage = null;
            try { s.ws.close(); } catch (e) { /* ignore */ }
        }
    }

    // ─── Helpers ────────────────────────────────

    _send(data) {
        const ws = this._session && this._session.ws;
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(data));
    }

    _setState(next) {
        if (this.state === next) return;
        const prev = this.state;
        this.state = next;
        if (this.onStateChange) this.onStateChange(next, prev);
    }

    _loadClientId() {
        let id = localStorage.getItem('du_clientId');
        if (!id) {
            id = 'web_' + Array.from(crypto.getRandomValues(new Uint8Array(8)))
                .map((b) => b.toString(16).padStart(2, '0')).join('');
            localStorage.setItem('du_clientId', id);
        }
        return id;
    }

    _deviceName() {
        const ua = navigator.userAgent;
        // iPadOS 13+ reports itself as a Mac with touch support.
        if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
        if (/iPhone/.test(ua)) return 'iPhone';
        if (/Android/.test(ua)) return 'Android';
        if (/Mac/.test(ua)) return 'Mac';
        return 'Browser';
    }
}

window.ConnectionManager = ConnectionManager;
window.ConnectionError = ConnectionError;
