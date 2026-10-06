using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.RegularExpressions;
using DesktopUtility.Host.Capture;
using DesktopUtility.Host.Core;
using DesktopUtility.Host.Video;
using DesktopUtility.Host.Input;
using DesktopUtility.Host.Network;
using DesktopUtility.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Win32;

namespace DesktopUtility.Host;

/// <summary>What the iPad shows: a second monitor (touch) or a mirror of the main display (touchpad).</summary>
public enum DisplayMode
{
    RemoteControl,
    ExtendedDisplay
}

/// <summary>
/// The whole host backend: screen capture → H.264 → WebRTC, input injection, the embedded web server
/// that serves the PWA and signaling, pairing, and display/mode management.
/// Events may be raised on any thread; UI code must marshal to its dispatcher.
/// </summary>
public sealed partial class HostService : IHostControl, IDisposable
{
    public const int DefaultPort = 9876;

    [GeneratedRegex("^[A-Za-z0-9_-]{6,48}$")]
    private static partial Regex ClientIdPattern();

    private readonly LaunchOptions _options;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _displayLock = new(1, 1);

    private readonly ScreenCapturer _capturer = new();
    private readonly InputInjector _input = new();
    private readonly WebRtcManager _webRtc = new();
    private readonly SessionManager _sessions = new();
    private readonly VirtualDisplayManager _virtualDisplay = new();
    private readonly TrustedDevices _trustedDevices = new();
    private readonly PairingManager _pairing;
    private readonly SignalingServer _signaling;

    private VideoEncoder _encoder;
    private EncoderSettings _encoderSettings;
    private WebApplication? _app;
    private Timer? _maintenanceTimer;
    private DisplayInfo? _activeDisplay;
    private long _lastFrameTimestamp;
    private CancellationTokenSource? _displayRefreshCts;
    private bool _disposed;

    public int Port { get; private set; }
    public string LocalIp { get; private set; } = "";
    public DisplayMode CurrentMode { get; private set; } = DisplayMode.ExtendedDisplay;
    public QualityPreset Quality => _encoderSettings.Quality;
    public int SessionCount => _sessions.ActiveCount;
    public string PairingUrl => _pairing.GetPairingUrl(LocalIp, Port);
    public string PairingPin => _pairing.CurrentPin;

    /// <summary>The web server is listening; pairing info is valid from now on.</summary>
    public event Action? Started;
    /// <summary>The host could not start (e.g. the port is taken). Argument: user-facing reason.</summary>
    public event Action<string>? StartFailed;
    public event Action<DisplayMode>? ModeChanged;
    public event Action<QualityPreset>? QualityChanged;
    public event Action<int>? SessionsChanged;
    /// <summary>New QR token / PIN, or the PC's IP address changed.</summary>
    public event Action? PairingChanged;
    /// <summary>The list of monitors (or the active one) changed.</summary>
    public event Action? DisplaysChanged;
    /// <summary>Human-readable status the UI may show (e.g. the virtual monitor could not be created).</summary>
    public event Action<string>? Notice;

    public HostService(LaunchOptions options)
    {
        _options = options;
        _pairing = new PairingManager(_trustedDevices);
        _signaling = new SignalingServer(_sessions, _input, _pairing, _webRtc, this);

        QualityPreset preset = Enum.TryParse<QualityPreset>(_settings.Quality, true, out var p) ? p : QualityPreset.Medium;
        _encoderSettings = EncoderSettings.FromPreset(preset);
        _encoder = new VideoEncoder(_encoderSettings);
    }

    // ─── Lifecycle ──────────────────────────────

    public async Task StartAsync()
    {
        try
        {
            Port = _options.Port ?? DefaultPort;
            LocalIp = NetworkUtil.GetLocalIpAddress();

            _capturer.TargetFps = _encoderSettings.TargetFps;
            _capturer.IsConsumerActive = () => _webRtc.HasConnectedPeers;
            _capturer.FrameCaptured += OnFrameCaptured;

            _webRtc.ConnectionStateChanged += (_, state) =>
            {
                if (state == SIPSorcery.Net.RTCPeerConnectionState.connected)
                {
                    _encoder.ForceKeyframe();
                    _capturer.RequestRepeat();
                }
            };
            _sessions.SessionsChanged += count =>
            {
                Console.WriteLine($"[Host] Active devices: {count}");
                SessionsChanged?.Invoke(count);
            };
            _pairing.CodeChanged += () => PairingChanged?.Invoke();

            try
            {
                _encoder.Initialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Host] FFmpeg libraries could not be loaded: {ex.Message}");
                Notice?.Invoke("FFmpeg libraries are missing; video streaming will not work.");
            }

            await StartWebServerAsync();
            _ = Task.Run(_webRtc.WarmUp);

            _maintenanceTimer = new Timer(_ => _pairing.RotateIfExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            Console.WriteLine($"[Host] Listening on http://{LocalIp}:{Port}");
            Started?.Invoke();

            await ApplyInitialModeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Host] Startup failed: {ex}");
            StartFailed?.Invoke(ex is IOException or System.Net.Sockets.SocketException
                ? $"Port {Port} is already in use. Close the other DesktopUtility instance or pick another port with --port."
                : ex.Message);
        }
    }

    private async Task ApplyInitialModeAsync()
    {
        var wanted = _options.Mode
            ?? (_settings.Mode == "remote" ? DisplayMode.RemoteControl : DisplayMode.ExtendedDisplay);

        // Always have something to capture right away; the extended display may need a UAC prompt first.
        if (wanted == DisplayMode.ExtendedDisplay) UseDisplay(GetPrimaryDisplay());
        _ = _capturer.StartCaptureLoopAsync(_cts.Token);

        // A --mode override applies to this run only and must not overwrite the saved preference.
        bool persist = _options.Mode == null;

        if (!await SetModeCoreAsync(wanted, persist) && wanted == DisplayMode.ExtendedDisplay)
            await SetModeCoreAsync(DisplayMode.RemoteControl, persist);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Console.WriteLine("[Host] Shutting down...");
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        _maintenanceTimer?.Dispose();
        _displayRefreshCts?.Cancel();
        _cts.Cancel();

        try { _app?.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)); } catch { }

        _webRtc.Dispose();
        _input.ReleaseAll();
        _encoder.Dispose();
        _capturer.Dispose();

        // The virtual monitor was created by us, so remove it again (asks for administrator rights).
        if (_virtualDisplay.IsActive)
        {
            try { Task.Run(() => _virtualDisplay.DisableAsync()).Wait(TimeSpan.FromSeconds(30)); } catch { }
        }

        Console.WriteLine("[Host] Goodbye");
    }

    // ─── Frame pipeline ─────────────────────────

    private void OnFrameCaptured(byte[] bgra, int width, int height)
    {
        if (!_webRtc.HasConnectedPeers) return;

        byte[]? encoded = _encoder.EncodeFrame(bgra, width, height);
        if (encoded == null) return;

        // RTP wants the time since the previous frame in 90 kHz units.
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Exchange(ref _lastFrameTimestamp, now);
        uint duration = last == 0
            ? (uint)(90000 / _encoderSettings.TargetFps)
            : (uint)Math.Clamp((now - last) * 90000 / Stopwatch.Frequency, 1, 90000);

        _webRtc.SendVideoFrame(encoded, duration);
    }

    /// <summary>Invalidates the current QR code / PIN and shows a fresh one.</summary>
    public void RotatePairingCode() => _pairing.Rotate();

    // ─── Quality ────────────────────────────────

    public void SetQuality(QualityPreset preset)
    {
        if (preset == _encoderSettings.Quality) return;

        _encoderSettings = EncoderSettings.FromPreset(preset);
        _encoder.ApplySettings(_encoderSettings);
        _capturer.TargetFps = _encoderSettings.TargetFps;
        _capturer.RequestRepeat();

        _settings.Quality = SignalingServer.QualityName(preset);
        _settings.Save();

        Console.WriteLine($"[Host] Quality: {_encoderSettings}");
        QualityChanged?.Invoke(preset);
        _ = _signaling.BroadcastQualityChangedAsync(preset);
    }

    // ─── Displays & modes ───────────────────────

    public List<DisplayInfo> GetDisplays() => ScreenCapturer.GetAvailableDisplays();

    public DisplayInfo? ActiveDisplay => _activeDisplay;

    private static DisplayInfo? GetPrimaryDisplay()
    {
        var displays = ScreenCapturer.GetAvailableDisplays();
        return displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }

    /// <summary>Starts capturing <paramref name="display"/> and aims input injection at it.</summary>
    private void UseDisplay(DisplayInfo? display)
    {
        if (display == null)
        {
            Console.WriteLine("[Host] No display available to capture");
            return;
        }

        try
        {
            _capturer.Initialize(display.AdapterIndex, display.OutputIndex);
            _input.SetTargetDisplay(display.Left, display.Top, display.Width, display.Height);
            _activeDisplay = display;
            _encoder.ForceKeyframe();
            _capturer.RequestRepeat();
            Console.WriteLine($"[Host] Capturing {display}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Host] Could not capture {display.DeviceName}: {ex.Message}");
        }

        DisplaysChanged?.Invoke();
    }

    /// <summary>
    /// Switches between touchpad mode (main display) and second-monitor mode (a second display,
    /// creating a virtual one if the PC only has a single monitor). Returns false if that was not possible.
    /// </summary>
    public Task<bool> SetModeAsync(DisplayMode mode) => SetModeCoreAsync(mode, persist: true);

    private async Task<bool> SetModeCoreAsync(DisplayMode mode, bool persist)
    {
        await _displayLock.WaitAsync();
        try
        {
            DisplayInfo? target;

            if (mode == DisplayMode.ExtendedDisplay)
            {
                target = VirtualDisplayManager.FindSecondaryDisplay();
                if (target == null)
                {
                    Notice?.Invoke("Creating virtual monitor…");
                    if (!await _virtualDisplay.EnableAsync())
                    {
                        Notice?.Invoke("Could not create the virtual monitor. Check the log for the driver installer result.");
                        return false;
                    }

                    target = VirtualDisplayManager.FindSecondaryDisplay();
                    if (target == null)
                    {
                        Notice?.Invoke("The virtual monitor did not appear in Windows.");
                        return false;
                    }
                }
            }
            else
            {
                target = GetPrimaryDisplay();
            }

            UseDisplay(target);
            await ApplyModeAsync(mode, persist);
            return true;
        }
        finally
        {
            _displayLock.Release();
        }
    }

    /// <summary>Captures a specific monitor chosen in the UI; the mode follows (primary = touchpad, other = second monitor).</summary>
    public async Task SelectDisplayAsync(DisplayInfo display)
    {
        await _displayLock.WaitAsync();
        try
        {
            UseDisplay(display);
            await ApplyModeAsync(display.IsPrimary ? DisplayMode.RemoteControl : DisplayMode.ExtendedDisplay);
        }
        finally
        {
            _displayLock.Release();
        }
    }

    private async Task ApplyModeAsync(DisplayMode mode, bool persist = true)
    {
        CurrentMode = mode;
        if (persist)
        {
            _settings.Mode = SignalingServer.ModeName(mode);
            _settings.Save();
        }

        Console.WriteLine($"[Host] Mode: {mode}");
        ModeChanged?.Invoke(mode);
        await _signaling.BroadcastModeChangedAsync(mode);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Windows raises this several times in a row while monitors are (re)configured.
        _displayRefreshCts?.Cancel();
        var cts = _displayRefreshCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, cts.Token);
                await RefreshActiveDisplayAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[Host] Display refresh failed: {ex.Message}"); }
        });
    }

    private async Task RefreshActiveDisplayAsync()
    {
        await _displayLock.WaitAsync();
        try
        {
            var displays = ScreenCapturer.GetAvailableDisplays();
            var current = _activeDisplay == null ? null : displays.FirstOrDefault(d => d.DeviceName == _activeDisplay.DeviceName);

            if (current != null)
            {
                if (current != _activeDisplay) UseDisplay(current); // moved or changed resolution
                else DisplaysChanged?.Invoke();
                return;
            }

            Console.WriteLine("[Host] The captured display disappeared, falling back to the main display");
            UseDisplay(displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault());
            await ApplyModeAsync(DisplayMode.RemoteControl);
        }
        finally
        {
            _displayLock.Release();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        string ip = NetworkUtil.GetLocalIpAddress();
        if (ip == LocalIp) return;

        LocalIp = ip;
        Console.WriteLine($"[Host] IP address changed to {ip}");
        PairingChanged?.Invoke();
    }

    // ─── Web server ─────────────────────────────

    private async Task StartWebServerAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Any, Port);
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        string? wwwroot = FindWebRoot();
        if (wwwroot != null)
        {
            var files = new PhysicalFileProvider(wwwroot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                // Tiny LAN-only assets: always revalidate so an updated host never serves a stale UI.
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
            });
        }
        else
        {
            Console.WriteLine("[Host] WARNING: the web client folder (wwwroot) was not found next to the application");
        }

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map("/ws/signaling", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string? requested = context.Request.Query["clientId"].FirstOrDefault();
            string clientId = requested != null && ClientIdPattern().IsMatch(requested)
                ? requested
                : Guid.NewGuid().ToString("N")[..12];

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await _signaling.HandleAsync(socket, clientId, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _cts.Token);
        });

        // Deliberately minimal and unauthenticated: it only lets the PWA label the host.
        app.MapGet("/api/info", () => Results.Json(new
        {
            name = Environment.MachineName,
            version = AppVersion,
            mode = SignalingServer.ModeName(CurrentMode)
        }));

        _app = app;
        await app.StartAsync(_cts.Token);
    }

    /// <summary>The installed layout keeps wwwroot next to the exe; development builds find it in the project folder.</summary>
    private static string? FindWebRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 7 && dir != null; i++, dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }
        return null;
    }

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
