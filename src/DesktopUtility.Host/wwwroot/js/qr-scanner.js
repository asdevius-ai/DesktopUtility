/**
 * qr-scanner.js — camera QR scanner (jsQR is loaded on demand).
 *
 * Browsers expose the camera only on secure origins, and the host serves plain HTTP on the LAN,
 * so this works on localhost / HTTPS only. On an iPad the QR code is normally scanned with the
 * system Camera app, which opens the pairing link directly.
 */

class QRScanner {
    constructor() {
        this.video = document.getElementById('qr-video');
        this.canvas = document.getElementById('qr-canvas');
        this.ctx = this.canvas.getContext('2d', { willReadFrequently: true });
        this.stream = null;
        this.scanning = false;
        this._raf = 0;
        this.onScan = null;
    }

    static isSupported() {
        return !!(window.isSecureContext && navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
    }

    async start() {
        if (this.scanning) return;

        await this._loadLibrary();
        this.stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'environment', width: { ideal: 960 }, height: { ideal: 720 } }
        });

        this.video.srcObject = this.stream;
        await this.video.play();

        this.scanning = true;
        this._loop();
    }

    stop() {
        this.scanning = false;
        cancelAnimationFrame(this._raf);

        if (this.stream) {
            this.stream.getTracks().forEach((track) => track.stop());
            this.stream = null;
        }
        this.video.srcObject = null;
    }

    _loadLibrary() {
        if (window.jsQR) return Promise.resolve();

        return new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = '/lib/jsqr.min.js';
            script.onload = resolve;
            script.onerror = () => reject(new Error('jsQR failed to load'));
            document.head.appendChild(script);
        });
    }

    _loop() {
        if (!this.scanning) return;

        if (this.video.readyState === this.video.HAVE_ENOUGH_DATA) {
            const w = this.video.videoWidth;
            const h = this.video.videoHeight;
            this.canvas.width = w;
            this.canvas.height = h;
            this.ctx.drawImage(this.video, 0, 0, w, h);

            const image = this.ctx.getImageData(0, 0, w, h);
            const code = window.jsQR(image.data, w, h, { inversionAttempts: 'dontInvert' });

            if (code && code.data) {
                if (navigator.vibrate) navigator.vibrate(60);
                this.stop();
                if (this.onScan) this.onScan(code.data);
                return;
            }
        }

        this._raf = requestAnimationFrame(() => this._loop());
    }
}

window.QRScanner = QRScanner;
