# DesktopUtility

Use an iPad or iPhone as a **second monitor** or a **trackpad** for a Windows PC, over your local Wi-Fi.
A Windows host app streams the screen (H.264 over WebRTC); the iPad runs a PWA served by the host — no App Store, no client install.

## Features

- **Second monitor** — the iPad shows an extra display (a virtual one is created if the PC has a single monitor); touch controls the cursor directly.
- **Touchpad** — the iPad mirrors the main display and acts as a trackpad.
- Touch gestures, Apple Pencil, connected mouse/trackpad and hardware or on-screen keyboard.
- Pairing with a QR code or a 6-digit PIN; paired devices reconnect without a PIN.
- Three quality presets (540p / 720p / 1080p), switchable live from the iPad or the PC.
- Mouse cursor is drawn into the stream; the stream keeps running on a static desktop and recovers after resolution changes or the lock screen.
- Ukrainian and English UI (follows the system language).

## Run without installing

You need Windows 10/11 (x64) and, only for building from source, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

**Option A — straight from source**

```powershell
cd src\DesktopUtility.Host
dotnet run
```

**Option B — portable build** (no SDK needed on the machine that runs it)

```powershell
.\build_release.ps1 -PortableOnly      # creates dist\DesktopUtility\ and dist\DesktopUtility-portable.zip
.\dist\DesktopUtility\DesktopUtility.exe
```

Then:

1. Allow the app through Windows Firewall when asked (**Private networks**).
2. On the iPad (same Wi-Fi) scan the QR code from the app window with the **Camera** app and open the link.
3. Tap Share → **Add to Home Screen** for the full-screen app. The next launches reconnect automatically.
4. No iPad at hand? Open `http://localhost:9876` in a browser on the PC and enter the PIN from the window.

Command-line options: `--port 9877`, `--mode remote|extended`, `--minimized`, `--quit` (asks a running instance to exit).

Notes

- *Second monitor* with only one physical display creates a virtual monitor with the bundled usbmmidd driver; Windows asks for administrator permission (UAC) when it is added and again when it is removed on exit. Use `--mode remote` to skip this.
- The PWA is served over plain HTTP on the LAN. Browsers only allow camera access on secure origins, so scanning the QR code is done with the system Camera app, not inside the PWA.
- Logs: `%LOCALAPPDATA%\DesktopUtility\logs`. Settings and paired devices live in `%LOCALAPPDATA%\DesktopUtility`.

## Architecture

```
 Windows host (WPF + Kestrel)                          iPad / iPhone PWA
┌──────────────────────────────┐   WebSocket (JSON)   ┌─────────────────────────┐
│ DXGI Desktop Duplication     │◄────────────────────►│ pairing, SDP/ICE, ping  │
│  └ cursor overlay            │  input, mode, quality│ touch/pen/keyboard input│
│ BGRA → (scale) → x264 H.264  │                      │                         │
│ SIPSorcery WebRTC ───────────┼─── SRTP video ──────►│ <video> (receive-only)  │
│ SendInput injection          │                      │                         │
└──────────────────────────────┘                      └─────────────────────────┘
```

```
src/DesktopUtility.Host/
├── App.xaml(.cs), MainWindow.xaml(.cs)   WPF UI, tray icon, single-instance handling
├── HostService.cs                        capture → encode → WebRTC pipeline, modes, displays, web server
├── Capture/        ScreenCapturer (DXGI), CursorOverlay, VirtualDisplayManager
├── Video/          VideoEncoder (FFmpeg/x264) + fast scaler, quality presets
├── Input/          InputInjector (SendInput), InputMessage
├── Network/        SignalingServer (WebSocket), WebRtcManager, SessionManager
├── Security/       PairingManager (QR/PIN, rate limiting), TrustedDevices, QR generator
├── Core/           logging, settings, localization, network helpers
├── Controls/       SpotlightBorder (card with cursor-tracking glow)
└── wwwroot/        the PWA: index.html, css/app.css, js/*.js
third_party/ffmpeg/ FFmpeg 6.1 shared DLLs (GPL build)
scripts/            virtual display driver (usbmmidd_v2)
design/             source artwork
```

## Security model

- The QR token / PIN is single-use, expires after 10 minutes, and is burned after too many wrong guesses; each IP is locked out for a minute after 5 failures.
- A paired device receives a random secret (only its hash is stored on the PC) and must present it to reconnect.
- Nothing except pairing and ping is accepted from an unauthenticated WebSocket; there are no unauthenticated control endpoints (only `GET /api/info`, which returns the PC name).
- Traffic is not encrypted on the Wi-Fi (HTTP/WebSocket); the WebRTC video itself is DTLS-SRTP. Use a network you trust.

## Building the installer

`.\build_release.ps1` (without `-PortableOnly`) also compiles `installer.iss` with Inno Setup 6, producing `Output\DesktopUtility_Setup.exe`.

## Licenses

DesktopUtility is MIT-licensed. The bundled FFmpeg build in `third_party/ffmpeg` is GPL v3 (see `third_party/ffmpeg/LICENSE.txt`);
usbmmidd_v2 comes with its own license in `scripts/usbmmidd_v2`.
