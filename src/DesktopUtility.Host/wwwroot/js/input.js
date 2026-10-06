/**
 * input.js — touch / pen / mouse / keyboard → input events for the Windows host.
 *
 * Two touch models, chosen by the host mode:
 *   extended ("Second monitor"): the tablet is a touchscreen. Touch moves the cursor to the exact spot, a tap clicks there.
 *   remote   ("Touchpad")      : the tablet is a trackpad. Finger movement moves the cursor relatively, a tap clicks where the cursor is.
 *
 * Shared gestures: long-press then drag = press & drag, two-finger drag = scroll (natural direction),
 * pinch = Ctrl+wheel zoom, two-finger tap = right click, three-finger tap = middle click.
 * Apple Pencil and a connected mouse/trackpad always address absolute positions.
 */

class InputManager {
    constructor(connection) {
        this.connection = connection;
        this.overlay = null;
        this.video = null;
        this.trackpad = false;

        // Tunables
        this.pointerSpeed = 1.6;       // trackpad cursor gain
        this.scrollSpeed = 3;          // wheel units per pixel of two-finger travel
        this.tapMaxMs = 280;
        this.longPressMs = 420;
        this.moveSlop = 9;             // px of travel before a touch stops being a tap
        this.pinchStep = 26;           // px of finger-distance change per zoom notch
        this.sendIntervalMs = 1000 / 60;

        this._resetGesture();
        this._lastSend = 0;
        this._attached = false;
        this._keyboardAttached = false;
        this._relX = 0;
        this._relY = 0;

        /** Set by the app while a session is on screen; keyboard events are ignored otherwise. */
        this.active = false;
    }

    setMode(mode) {
        this.trackpad = mode === 'remote';
    }

    // ─── Attachment (idempotent: reconnects must not stack listeners) ───

    attach(overlay, video) {
        this.overlay = overlay;
        this.video = video;
        if (this._attached) return;
        this._attached = true;

        const opts = { passive: false };
        overlay.addEventListener('touchstart', (e) => this._touchStart(e), opts);
        overlay.addEventListener('touchmove', (e) => this._touchMove(e), opts);
        overlay.addEventListener('touchend', (e) => this._touchEnd(e), opts);
        overlay.addEventListener('touchcancel', (e) => this._touchEnd(e), opts);

        overlay.addEventListener('pointerdown', (e) => this._pointerDown(e));
        overlay.addEventListener('pointermove', (e) => this._pointerMove(e));
        overlay.addEventListener('pointerup', (e) => this._pointerUp(e));
        overlay.addEventListener('pointercancel', (e) => this._pointerUp(e));
        overlay.addEventListener('wheel', (e) => this._wheel(e), opts);
        overlay.addEventListener('contextmenu', (e) => e.preventDefault());
    }

    // ─── Touch ──────────────────────────────────

    _resetGesture() {
        clearTimeout(this._longPressTimer);
        this._fingers = new Map();   // touch id → last {x, y}
        this._maxFingers = 0;
        this._startTime = 0;
        this._startPoint = null;
        this._moved = false;
        this._dragging = false;
        this._resetMulti();
    }

    _resetMulti() {
        this._multiMode = null;      // 'scroll' | 'pinch', decided once per multi-finger gesture
        this._centerTravel = 0;
        this._pinchBase = this._fingerDistance();
        this._pinchStart = this._pinchBase;
        this._scrollAcc = 0;
    }

    _isStylus(touch) {
        return touch.touchType === 'stylus'; // handled through pointer events
    }

    _touchStart(e) {
        e.preventDefault();

        // The bottom edge belongs to the swipe-up control bar gesture.
        if (this.isReserved && [...e.changedTouches].some((t) => this.isReserved(t.clientX, t.clientY))) return;

        for (const t of e.changedTouches) {
            if (!this._isStylus(t)) this._fingers.set(t.identifier, { x: t.clientX, y: t.clientY });
        }
        const count = this._fingers.size;
        if (count === 0) return;

        const wasIdle = this._maxFingers === 0;
        this._maxFingers = Math.max(this._maxFingers, count);

        if (wasIdle) {
            const t = e.changedTouches[0];
            this._startTime = Date.now();
            this._startPoint = { x: t.clientX, y: t.clientY };
            this._moved = false;
        }

        if (count === 1) {
            const t = e.changedTouches[0];
            // Touchscreen model: the cursor jumps under the finger right away.
            if (!this.trackpad) this._send({ type: 'mousemove_abs', ...this._coords(t) });
            this._longPressTimer = setTimeout(() => this._beginDrag(t.identifier), this.longPressMs);
        } else {
            // More fingers: this is a scroll / pinch / multi-finger tap, not a drag.
            clearTimeout(this._longPressTimer);
            if (this._dragging) this._endDrag();
            this._resetMulti();
        }
    }

    _touchMove(e) {
        e.preventDefault();

        let sumDy = 0;
        let moved = 0;
        for (const t of e.changedTouches) {
            const f = this._fingers.get(t.identifier);
            if (!f) continue;

            const dx = t.clientX - f.x;
            const dy = t.clientY - f.y;
            f.x = t.clientX;
            f.y = t.clientY;

            if (this._fingers.size === 1) this._moveSingle(t, dx, dy);
            else { sumDy += dy; moved++; }
        }

        if (this._fingers.size >= 2 && moved) this._moveMulti(sumDy / moved);
    }

    _moveSingle(touch, dx, dy) {
        if (!this._moved && this._startPoint) {
            const travel = Math.hypot(touch.clientX - this._startPoint.x, touch.clientY - this._startPoint.y);
            if (travel > this.moveSlop) {
                this._moved = true;
                if (!this._dragging) clearTimeout(this._longPressTimer);
            }
        }
        if (!this._moved && !this._dragging) return;

        if (this.trackpad) {
            // Relative motion must not be dropped by throttling, so deltas are summed and flushed at ~60 Hz.
            const area = this._area();
            this._relX += (dx * this.pointerSpeed) / area.width;
            this._relY += (dy * this.pointerSpeed) / area.height;
            if (performance.now() - this._lastSend >= this.sendIntervalMs) this._flushRelative();
        } else {
            this._sendThrottled({ type: 'mousemove_abs', ...this._coords(touch) });
        }
    }

    _moveMulti(avgDy) {
        this._moved = true;
        clearTimeout(this._longPressTimer);

        const dist = this._fingerDistance();

        if (!this._multiMode) {
            this._centerTravel += avgDy;
            const pinch = Math.abs(dist - this._pinchStart);
            if (pinch > 14 && pinch > Math.abs(this._centerTravel)) this._multiMode = 'pinch';
            else if (Math.abs(this._centerTravel) > 14) this._multiMode = 'scroll';
            else return;
        }

        if (this._multiMode === 'pinch') {
            // One Ctrl+wheel notch per `pinchStep` pixels of finger-distance change.
            const delta = dist - this._pinchBase;
            if (Math.abs(delta) >= this.pinchStep) {
                this._send({ type: 'scroll', scrollDelta: delta > 0 ? 120 : -120, ctrl: true });
                this._pinchBase = dist;
            }
            return;
        }

        // Natural scrolling: the page follows the fingers (fingers up → wheel down).
        this._scrollAcc += avgDy * this.scrollSpeed;
        if (Math.abs(this._scrollAcc) >= 8) {
            this._send({ type: 'scroll', scrollDelta: Math.round(this._scrollAcc) });
            this._scrollAcc = 0;
        }
    }

    _flushRelative() {
        if (!this._relX && !this._relY) return;
        this._lastSend = performance.now();
        this._send({ type: 'mousemove', deltaX: this._relX, deltaY: this._relY });
        this._relX = 0;
        this._relY = 0;
    }

    _touchEnd(e) {
        e.preventDefault();
        this._flushRelative();

        for (const t of e.changedTouches) this._fingers.delete(t.identifier);
        if (this._fingers.size > 0) return; // wait for the last finger

        const duration = Date.now() - this._startTime;
        const lastTouch = e.changedTouches[e.changedTouches.length - 1];

        if (this._dragging) {
            this._endDrag(lastTouch);
        } else if (e.type === 'touchend' && !this._moved && duration < this.tapMaxMs && this._maxFingers > 0) {
            this._tap(this._maxFingers, lastTouch);
        }

        this._resetGesture();
    }

    _tap(fingers, touch) {
        // Touchscreen: click where the finger is. Trackpad: click at the cursor (no position).
        const at = this.trackpad ? {} : this._coords(touch);

        if (fingers === 1) this._send({ type: 'click', button: 0, ...at });
        else if (fingers === 2) this._send({ type: 'rightclick', button: 1, ...at });
        else if (fingers >= 3) this._send({ type: 'middleclick', button: 2, ...at });
    }

    _beginDrag(touchId) {
        if (this._fingers.size !== 1 || this._moved || !this._fingers.has(touchId)) return;

        const f = this._fingers.get(touchId);
        this._dragging = true;
        if (navigator.vibrate) navigator.vibrate(30);

        const at = this.trackpad ? {} : this._coords(f);
        this._send({ type: 'mousedown', button: 0, ...at });
    }

    _endDrag(touch) {
        this._dragging = false;
        const at = !this.trackpad && touch ? this._coords(touch) : {};
        this._send({ type: 'mouseup', button: 0, ...at });
    }

    // ─── Pen & mouse (absolute) ─────────────────

    _pointerDown(e) {
        if (e.pointerType === 'touch') return;
        e.preventDefault();
        try { this.overlay.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }

        this._send({ type: 'mousedown', button: this._button(e), ...this._coords(e) });
    }

    _pointerMove(e) {
        if (e.pointerType === 'touch') return;
        this._sendThrottled({ type: 'mousemove_abs', ...this._coords(e) });
    }

    _pointerUp(e) {
        if (e.pointerType === 'touch') return;
        this._send({ type: 'mouseup', button: this._button(e), ...this._coords(e) });
    }

    _button(e) {
        if (e.pointerType === 'pen') return e.button === 2 ? 1 : 0; // barrel button = right click
        return e.button === 2 ? 1 : e.button === 1 ? 2 : 0;
    }

    _wheel(e) {
        e.preventDefault();
        const unit = e.deltaMode === 1 ? 40 : 1; // lines vs pixels
        this._send({ type: 'scroll', scrollDelta: Math.round(-e.deltaY * unit * 3), ctrl: e.ctrlKey });
    }

    // ─── Keyboard ───────────────────────────────

    /**
     * Virtual keyboard: the hidden <input> receives composed text.
     * Hardware keyboard: raw key events, listened for on the whole document while a session is open.
     */
    attachKeyboard(input) {
        this.keyboardInput = input;
        if (this._keyboardAttached) return;
        this._keyboardAttached = true;

        input.addEventListener('input', (e) => {
            const text = e.target.value;
            if (text) this._send({ type: 'textinput', text });
            e.target.value = '';
        });

        document.addEventListener('keydown', (e) => this._key(e, true));
        document.addEventListener('keyup', (e) => this._key(e, false));
    }

    focusKeyboard() { if (this.keyboardInput) this.keyboardInput.focus(); }
    blurKeyboard() { if (this.keyboardInput) this.keyboardInput.blur(); }
    get keyboardOpen() { return !!this.keyboardInput && document.activeElement === this.keyboardInput; }

    _key(e, down) {
        if (!this.active || !e.isTrusted) return;
        if (e.target && e.target.tagName === 'INPUT' && e.target !== this.keyboardInput) return; // typing in a form field

        const printable = e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey;

        if (printable) {
            // Characters go through textinput so the host's keyboard layout never mangles them.
            // From the hidden input they arrive via the 'input' event instead.
            if (down && e.target !== this.keyboardInput) {
                e.preventDefault();
                this._send({ type: 'textinput', text: e.key });
            }
            return;
        }

        const vk = this._virtualKey(e);
        if (!vk) return;

        e.preventDefault();
        this._send({ type: down ? 'keydown' : 'keyup', keyCode: vk });
    }

    _virtualKey(e) {
        const byKey = {
            Backspace: 8, Tab: 9, Enter: 13, Escape: 27, ' ': 32,
            PageUp: 33, PageDown: 34, End: 35, Home: 36,
            ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40,
            Insert: 45, Delete: 46,
            Shift: 16, Control: 17, Alt: 18, Meta: 91, CapsLock: 20,
            ContextMenu: 93
        };
        if (byKey[e.key] !== undefined) return byKey[e.key];

        const c = e.code || '';
        if (/^F([1-9]|1[0-2])$/.test(e.key)) return 111 + parseInt(e.key.slice(1), 10);
        if (c.startsWith('Key')) return c.charCodeAt(3);          // A–Z
        if (c.startsWith('Digit')) return c.charCodeAt(5);        // 0–9
        const punctuation = {
            Minus: 0xBD, Equal: 0xBB, BracketLeft: 0xDB, BracketRight: 0xDD, Semicolon: 0xBA,
            Quote: 0xDE, Comma: 0xBC, Period: 0xBE, Slash: 0xBF, Backslash: 0xDC, Backquote: 0xC0
        };
        return punctuation[c] || 0;
    }

    // ─── Geometry ───────────────────────────────

    /** The rectangle the remote display occupies inside the (letterboxed) video element. */
    _area() {
        const v = this.video;
        const rect = v.getBoundingClientRect();
        if (!v.videoWidth || !v.videoHeight || rect.width <= 0 || rect.height <= 0) {
            return { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight };
        }

        const videoRatio = v.videoWidth / v.videoHeight;
        const boxRatio = rect.width / rect.height;
        let width = rect.width;
        let height = rect.height;
        if (boxRatio > videoRatio) width = height * videoRatio;
        else height = width / videoRatio;

        return { left: rect.left + (rect.width - width) / 2, top: rect.top + (rect.height - height) / 2, width, height };
    }

    /** Normalized 0..1 position on the remote display. */
    _coords(point) {
        const a = this._area();
        return {
            x: Math.min(1, Math.max(0, (point.clientX - a.left) / a.width)),
            y: Math.min(1, Math.max(0, (point.clientY - a.top) / a.height))
        };
    }

    _fingerDistance() {
        const pts = [...this._fingers.values()];
        return pts.length < 2 ? 0 : Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
    }

    // ─── Sending ────────────────────────────────

    _send(message) {
        this.connection.sendInput(message);
    }

    /** Coalesces high-frequency absolute move events to ~60 Hz (clicks and keys must never go through this). */
    _sendThrottled(message) {
        const now = performance.now();
        if (now - this._lastSend < this.sendIntervalMs) return;
        this._lastSend = now;
        this._send(message);
    }
}

window.InputManager = InputManager;
