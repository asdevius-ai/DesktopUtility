/**
 * app.js — wires connection, input, UI and QR scanner together and owns the screen flow:
 *   onboarding → connection ⇄ pin / qr → connecting → session
 */

(function () {
    'use strict';

    const DEFAULT_PORT = 9876;

    let connection, input, ui, scanner;
    let currentMode = 'extended';
    let target = { host: location.hostname, port: parseInt(location.port, 10) || DEFAULT_PORT };
    let announceNextMode = false;
    let sessionAnnounced = false;

    document.addEventListener('DOMContentLoaded', init);

    function init() {
        I18N.apply();
        removeLegacyServiceWorker();

        connection = new ConnectionManager();
        ui = new UIManager();
        input = new InputManager(connection);
        scanner = new QRScanner();

        input.isReserved = (x, y) => ui.isReservedEdge(y);

        wireConnection();
        wireUi();
        ui.adaptToDisplayMode();
        ui.setMode(currentMode);
        input.setMode(currentMode);

        if (!QRScanner.isSupported()) document.getElementById('btn-scan-qr').classList.add('hidden');

        const pair = new URLSearchParams(location.search).get('pair');
        if (pair) {
            history.replaceState({}, '', '/'); // never leave the one-time token in the address bar
            connectTo(target.host, target.port, pair);
        } else if (isServedByHost() && connection.hasDeviceSecret()) {
            connectTo(target.host, target.port, null); // returning device: no PIN needed
        } else {
            showConnectionScreen(ui.shouldShowOnboarding() ? 'onboarding' : 'connection');
        }
    }

    // ─── Screens ────────────────────────────────

    function showConnectionScreen(name = 'connection') {
        ui.endSession();
        input.active = false;
        ui.showScreen(name);
        if (name === 'connection') loadHostInfo();
    }

    function isServedByHost() {
        return location.protocol === 'http:' || location.protocol === 'https:';
    }

    // ─── Connecting ─────────────────────────────

    async function connectTo(host, port, secret) {
        target = { host, port };
        sessionAnnounced = false;

        ui.setConnecting('connecting.title', secret ? 'connecting.pairing' : 'connecting.sub');
        ui.showScreen('connecting');

        try {
            await connection.connect(host, port, secret);
            ui.setConnecting('connecting.title', 'connecting.stream');
        } catch (err) {
            handleConnectError(err, secret);
        }
    }

    function handleConnectError(err, secret) {
        const code = err.code || 'network';

        switch (code) {
            case 'need-pin':
                ui.showPin();
                break;
            case 'invalid':
                connection.forgetDevice();
                ui.showPin(secret && /^\d{6}$/.test(secret) ? 'pin.invalid' : (secret ? 'pin.expired' : null));
                break;
            case 'locked':
                ui.showPin('pin.locked');
                break;
            case 'timeout':
                ui.showToast(t('toast.timeout'), 'error', 4500);
                showConnectionScreen();
                break;
            case 'webrtc':
                ui.showToast(t('toast.webrtc'), 'error');
                showConnectionScreen();
                break;
            default:
                ui.showToast(t('toast.failed'), 'error');
                showConnectionScreen();
        }
    }

    // ─── Connection events ──────────────────────

    function wireConnection() {
        connection.onVideoStream = (stream) => {
            ui.startSession(stream);
            input.attach(ui.overlay, ui.video);
            input.attachKeyboard(document.getElementById('hidden-keyboard-input'));
            input.active = true;

            if (!sessionAnnounced) {
                sessionAnnounced = true;
                ui.showToast(t('toast.connected'), 'success', 1800);
                ui.flashBadge();
            }
        };

        connection.onStateChange = (state) => {
            if (state === 'connected') ui.setReconnecting(false);
            if (state === 'reconnecting') {
                if (input.active) ui.setReconnecting(true);
                else {
                    ui.setConnecting('connecting.title', 'connecting.reconnect');
                    ui.showScreen('connecting');
                }
            }
        };

        connection.onLatency = (ms) => ui.setLatency(ms);
        connection.onQualityChanged = (preset) => ui.setQuality(preset);

        connection.onModeChanged = (mode) => {
            currentMode = mode;
            ui.setMode(mode, announceNextMode);
            input.setMode(mode);
            if (announceNextMode) {
                ui.showToast(t(`mode.active.${mode}`), 'success', 2200);
                announceNextMode = false;
            }
        };

        connection.onModeFailed = (mode) => {
            announceNextMode = false;
            ui.setMode(mode);
            ui.showToast(t('mode.failed'), 'error', 4000);
        };

        connection.onFatal = (err) => {
            ui.endSession();
            input.active = false;

            if (err.code === 'unauthorized' || err.code === 'need-pin' || err.code === 'invalid') {
                connection.forgetDevice();
                ui.showToast(t('toast.unauthorized'), 'warning', 4500);
                ui.showPin();
            } else {
                ui.showToast(t('toast.lost'), 'error', 4500);
                showConnectionScreen();
            }
        };
    }

    // ─── UI events ──────────────────────────────

    function wireUi() {
        const $ = (id) => document.getElementById(id);

        $('btn-skip-onboarding').addEventListener('click', () => {
            ui.dismissOnboarding();
            showConnectionScreen();
        });

        // Connection screen
        $('btn-scan-qr').addEventListener('click', startQrScan);
        $('btn-manual-connect').addEventListener('click', () => ui.toggleManualConnect());
        $('btn-refresh-hosts').addEventListener('click', loadHostInfo);
        $('hosts-list').addEventListener('click', (e) => {
            const item = e.target.closest('.host-item');
            if (item) connectTo(item.dataset.host, parseInt(item.dataset.port, 10) || DEFAULT_PORT, null);
        });
        $('btn-connect-ip').addEventListener('click', connectManual);
        $('input-ip').addEventListener('keydown', (e) => { if (e.key === 'Enter') connectManual(); });

        // PIN screen
        const pinInput = $('input-pin');
        pinInput.addEventListener('input', () => {
            pinInput.value = pinInput.value.replace(/\D/g, '').slice(0, 6);
            $('pin-error').textContent = '';
            if (pinInput.value.length === 6) submitPin();
        });
        pinInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') submitPin(); });
        $('btn-submit-pin').addEventListener('click', submitPin);
        $('btn-cancel-pin').addEventListener('click', () => showConnectionScreen());

        // QR + connecting screens
        $('btn-close-qr').addEventListener('click', () => { scanner.stop(); showConnectionScreen(); });
        $('btn-cancel-connect').addEventListener('click', () => { connection.disconnect(); showConnectionScreen(); });

        // Session controls
        $('btn-disconnect').addEventListener('click', () => {
            connection.disconnect();
            ui.showToast(t('toast.disconnected'), 'info', 1600);
            showConnectionScreen();
        });

        document.querySelectorAll('[data-mode]').forEach((btn) => btn.addEventListener('click', () => {
            if (btn.dataset.mode === currentMode) return;
            announceNextMode = true;
            ui.showToast(t(`mode.switching.${btn.dataset.mode}`), 'info', 2000);
            connection.changeMode(btn.dataset.mode);
        }));

        document.querySelectorAll('[data-quality]').forEach((btn) => btn.addEventListener('click', () => {
            ui.setQuality(btn.dataset.quality);
            connection.sendQuality(btn.dataset.quality);
        }));

        $('btn-keyboard').addEventListener('click', () => {
            if (input.keyboardOpen) input.blurKeyboard();
            else { input.focusKeyboard(); ui.closeControlBar(); }
        });
    }

    function connectManual() {
        const ip = document.getElementById('input-ip').value.trim();
        const port = parseInt(document.getElementById('input-port').value, 10) || DEFAULT_PORT;

        if (!ip) { ui.showToast(t('toast.enterip'), 'warning'); return; }
        connectTo(ip, port, null);
    }

    function submitPin() {
        const pin = document.getElementById('input-pin').value;
        if (pin.length !== 6) return;
        document.getElementById('input-pin').blur();
        connectTo(target.host, target.port, pin);
    }

    // ─── QR scanning ────────────────────────────

    async function startQrScan() {
        ui.showScreen('qr');

        scanner.onScan = (text) => {
            try {
                const url = new URL(text);
                const token = url.searchParams.get('pair');
                if (!token) throw new Error('no token');
                connectTo(url.hostname, parseInt(url.port, 10) || DEFAULT_PORT, token);
            } catch (e) {
                ui.showToast(t('toast.badqr'), 'error');
                showConnectionScreen();
            }
        };

        try {
            await scanner.start();
        } catch (e) {
            ui.showToast(t('qr.denied'), 'error');
            showConnectionScreen();
        }
    }

    // ─── Host discovery ─────────────────────────

    async function loadHostInfo() {
        if (!isServedByHost()) { ui.updateHostList([]); return; }

        try {
            const response = await fetch('/api/info', { cache: 'no-store' });
            if (!response.ok) throw new Error(response.status);
            const info = await response.json();

            ui.updateHostList([{ name: info.name, address: location.hostname, port: parseInt(location.port, 10) || DEFAULT_PORT }]);
        } catch (e) {
            ui.updateHostList([]);
        }
    }

    // ─── Housekeeping ───────────────────────────

    /** Earlier versions registered a caching service worker that would keep serving a stale UI. */
    function removeLegacyServiceWorker() {
        if (!('serviceWorker' in navigator)) return;
        navigator.serviceWorker.getRegistrations().then((regs) => regs.forEach((r) => r.unregister())).catch(() => {});
        if (window.caches) caches.keys().then((keys) => keys.forEach((k) => caches.delete(k))).catch(() => {});
    }
})();
