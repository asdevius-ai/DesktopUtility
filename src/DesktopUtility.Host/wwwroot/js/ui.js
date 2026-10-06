/**
 * ui.js — screens, toasts, the session control bar and the small interaction effects
 * (cursor spotlight on cards, wake lock).
 */

class UIManager {
    constructor() {
        this.screens = {
            onboarding: document.getElementById('screen-onboarding'),
            connection: document.getElementById('screen-connection'),
            pin: document.getElementById('screen-pin'),
            qr: document.getElementById('screen-qr-scanner'),
            connecting: document.getElementById('screen-connecting'),
            session: document.getElementById('screen-session')
        };

        this.video = document.getElementById('remote-video');
        this.overlay = document.getElementById('touch-overlay');
        this.controlBar = document.getElementById('control-bar');
        this.handle = document.getElementById('bar-handle');
        this.badge = document.getElementById('mode-badge');
        this.reconnectOverlay = document.getElementById('reconnect-overlay');

        this._barTimer = null;
        this._badgeTimer = null;
        this._wakeLock = null;
        this._inSession = false;

        this._initSpotlight();
        this._initWakeLockRecovery();
    }

    // ─── Screens ────────────────────────────────

    showScreen(name) {
        for (const [key, el] of Object.entries(this.screens)) el.classList.toggle('hidden', key !== name);
        document.body.classList.toggle('in-session', name === 'session');
    }

    setConnecting(titleKey, statusKey) {
        document.getElementById('connecting-title').textContent = t(titleKey);
        document.getElementById('connecting-status').textContent = t(statusKey);
    }

    shouldShowOnboarding() {
        const standalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
        return !standalone && !this._storage('du_onboarded');
    }

    dismissOnboarding() {
        this._storage('du_onboarded', '1');
    }

    /** Hides browser-only hints (add-to-home-screen tip) once the app is installed. */
    adaptToDisplayMode() {
        const standalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
        document.getElementById('tip-card').classList.toggle('hidden', standalone);
    }

    // ─── PIN screen ─────────────────────────────

    showPin(errorKey = null) {
        const input = document.getElementById('input-pin');
        input.value = '';
        document.getElementById('pin-error').textContent = errorKey ? t(errorKey) : '';
        this.showScreen('pin');
        setTimeout(() => input.focus(), 350);
    }

    // ─── Session ────────────────────────────────

    startSession(stream) {
        this._inSession = true;
        this.showScreen('session');
        this.setReconnecting(false);

        if (this.video.srcObject !== stream) this.video.srcObject = stream;
        this.video.play().catch(() => { /* autoplay is allowed for muted video; ignore */ });

        this._requestWakeLock();
        this._bindControlBarOnce();
    }

    endSession() {
        this._inSession = false;
        this.video.srcObject = null;
        this.closeControlBar();
        this.setReconnecting(false);
        this._releaseWakeLock();
    }

    setReconnecting(on) {
        this.reconnectOverlay.classList.toggle('hidden', !on);
    }

    setMode(mode, announce = false) {
        const extended = mode === 'extended';
        document.getElementById('seg-extended').classList.toggle('active', extended);
        document.getElementById('seg-remote').classList.toggle('active', !extended);

        document.getElementById('mode-badge-text').textContent = t(extended ? 'mode.extended' : 'mode.remote');
        document.getElementById('mode-badge-icon').firstElementChild.setAttribute('href', '#i-monitor');

        if (announce) this.flashBadge();
    }

    setQuality(preset) {
        this.controlBar.querySelectorAll('[data-quality]').forEach((b) => b.classList.toggle('active', b.dataset.quality === preset));
    }

    setLatency(ms) {
        document.getElementById('latency-value').textContent = `${Math.round(ms)} ms`;
        const dot = document.getElementById('latency-dot');
        dot.classList.toggle('poor', ms > 100);
        dot.classList.toggle('fair', ms > 50 && ms <= 100);
    }

    flashBadge() {
        this.badge.classList.add('show');
        clearTimeout(this._badgeTimer);
        this._badgeTimer = setTimeout(() => this.badge.classList.remove('show'), 2600);
    }

    // ─── Control bar ────────────────────────────

    get controlBarOpen() { return this.controlBar.classList.contains('open'); }

    openControlBar() {
        this.controlBar.classList.add('open');
        this._scheduleBarClose();
    }

    closeControlBar() {
        this.controlBar.classList.remove('open');
        clearTimeout(this._barTimer);
    }

    _scheduleBarClose() {
        clearTimeout(this._barTimer);
        this._barTimer = setTimeout(() => this.closeControlBar(), 7000);
    }

    _bindControlBarOnce() {
        if (this._barBound) return;
        this._barBound = true;

        this.handle.addEventListener('click', () => (this.controlBarOpen ? this.closeControlBar() : this.openControlBar()));
        this.controlBar.addEventListener('pointerdown', () => { if (this.controlBarOpen) this._scheduleBarClose(); });

        // Swipe up from the bottom edge, or tap the video, to toggle the bar.
        let edgeStart = null;
        this.overlay.addEventListener('touchstart', (e) => {
            const t0 = e.touches[0];
            edgeStart = this.isReservedEdge(t0.clientY) ? { y: t0.clientY } : null;
        }, { passive: true });
        this.overlay.addEventListener('touchmove', (e) => {
            if (edgeStart && edgeStart.y - e.touches[0].clientY > 28) {
                edgeStart = null;
                this.openControlBar();
            }
        }, { passive: true });
        this.overlay.addEventListener('touchstart', () => { if (this.controlBarOpen) this.closeControlBar(); }, { passive: true });
    }

    /** The bottom strip is reserved for the swipe-up gesture; input ignores touches that start there. */
    isReservedEdge(y) {
        return y > window.innerHeight - 34;
    }

    // ─── Toasts ─────────────────────────────────

    showToast(message, type = 'info', durationMs = 3000) {
        const container = document.getElementById('toast-container');
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        toast.textContent = message;
        container.appendChild(toast);

        setTimeout(() => {
            toast.classList.add('toast-exit');
            setTimeout(() => toast.remove(), 220);
        }, durationMs);
    }

    // ─── Host list ──────────────────────────────

    updateHostList(hosts) {
        const list = document.getElementById('hosts-list');
        list.replaceChildren();

        if (hosts.length === 0) {
            const p = document.createElement('div');
            p.className = 'host-placeholder';
            p.textContent = t('conn.none');
            list.appendChild(p);
            return;
        }

        for (const host of hosts) {
            const item = document.createElement('button');
            item.className = 'host-item';
            item.dataset.host = host.address;
            item.dataset.port = host.port;
            item.innerHTML = `
                <span class="host-icon"><svg width="22" height="22"><use href="#i-monitor"/></svg></span>
                <span class="host-info"><span class="host-name"></span><span class="host-address"></span></span>
                <span class="host-chevron"><svg width="18" height="18"><use href="#i-chevron"/></svg></span>`;
            item.querySelector('.host-name').textContent = host.name || 'Windows PC';
            item.querySelector('.host-address').textContent = `${host.address}:${host.port}`;
            list.appendChild(item);
        }
    }

    toggleManualConnect() {
        const panel = document.getElementById('manual-connect-panel');
        panel.classList.toggle('hidden');
        if (!panel.classList.contains('hidden')) document.getElementById('input-ip').focus();
    }

    // ─── Effects ────────────────────────────────

    /** Cards glow where the pointer is (touch: where the finger last pressed). */
    _initSpotlight() {
        const update = (e) => {
            const card = e.target.closest && e.target.closest('.card');
            if (!card) return;
            const r = card.getBoundingClientRect();
            card.style.setProperty('--mx', `${e.clientX - r.left}px`);
            card.style.setProperty('--my', `${e.clientY - r.top}px`);
        };

        document.addEventListener('pointermove', update, { passive: true });
        document.addEventListener('pointerdown', (e) => {
            update(e);
            if (e.pointerType !== 'touch') return;
            const card = e.target.closest && e.target.closest('.card');
            if (!card) return;
            card.classList.add('is-spot');
            setTimeout(() => card.classList.remove('is-spot'), 700);
        }, { passive: true });
    }

    // ─── Wake lock ──────────────────────────────

    async _requestWakeLock() {
        if (!('wakeLock' in navigator) || this._wakeLock) return;
        try {
            this._wakeLock = await navigator.wakeLock.request('screen');
            this._wakeLock.addEventListener('release', () => { this._wakeLock = null; });
        } catch (e) { /* denied or unsupported: the screen may dim, which is acceptable */ }
    }

    _releaseWakeLock() {
        if (this._wakeLock) { this._wakeLock.release().catch(() => {}); this._wakeLock = null; }
    }

    _initWakeLockRecovery() {
        // The system drops the lock whenever the page is hidden; take it again when we return.
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible' && this._inSession) this._requestWakeLock();
        });
    }

    _storage(key, value) {
        try {
            if (value === undefined) return localStorage.getItem(key);
            localStorage.setItem(key, value);
        } catch (e) { return null; }
    }
}

window.UIManager = UIManager;
