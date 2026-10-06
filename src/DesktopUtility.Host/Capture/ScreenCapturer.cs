using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DesktopUtility.Host.Capture;

/// <summary>
/// Captures one display with the DXGI Desktop Duplication API and publishes frames at a target FPS.
/// The frame buffer handed to <see cref="FrameCaptured"/> is reused, so subscribers must consume it synchronously.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    /// <summary>A static desktop produces no DXGI frames; re-publish the last one this often so viewers never starve.</summary>
    private const int IdleRepeatMs = 500;
    private const int ReinitRetryMs = 500;

    private enum CaptureStatus { NewFrame, NoChange, Lost }

    private readonly object _lock = new();
    private readonly CursorOverlay _cursor = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;

    private byte[] _frame = [];
    private bool _hasFrame;
    private bool _isInitialized;
    private bool _disposed;
    private volatile bool _repeatRequested;

    private int _width, _height;
    private int _originX, _originY;
    private int _adapterIndex, _outputIndex;
    private bool _hasTarget;
    private int _targetFps = 30;
    private long _lastErrorLogTicks;

    public int TargetFps { get => _targetFps; set => _targetFps = Math.Clamp(value, 1, 60); }
    public bool IsInitialized => _isInitialized;

    /// <summary>Whether anyone currently needs frames. While false the capture loop idles instead of copying 8 MB per frame.</summary>
    public Func<bool>? IsConsumerActive { get; set; }

    /// <summary>Raised with the BGRA buffer, width and height. The buffer is only valid during the call.</summary>
    public event Action<byte[], int, int>? FrameCaptured;

    /// <summary>Makes the loop publish the most recent frame again (used when a new viewer connects).</summary>
    public void RequestRepeat() => _repeatRequested = true;

    public void Initialize(int adapterIndex = 0, int outputIndex = 0)
    {
        lock (_lock)
        {
            Cleanup();
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
            _hasTarget = true;

            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (factory.EnumAdapters1((uint)adapterIndex, out var adapter).Failure)
                    throw new InvalidOperationException($"No adapter at index {adapterIndex}");

                using (adapter)
                {
                    D3D11.D3D11CreateDevice(
                        adapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0 },
                        out _device,
                        out _context);

                    if (_device == null || _context == null)
                        throw new InvalidOperationException("Failed to create D3D11 device");

                    if (adapter.EnumOutputs((uint)outputIndex, out var output).Failure)
                        throw new InvalidOperationException($"No output {outputIndex} on adapter {adapterIndex}");

                    using (output)
                    using (var output1 = output.QueryInterface<IDXGIOutput1>())
                    {
                        var coords = output1.Description.DesktopCoordinates;
                        _originX = coords.Left;
                        _originY = coords.Top;
                        _width = coords.Right - coords.Left;
                        _height = coords.Bottom - coords.Top;
                        _duplication = output1.DuplicateOutput(_device);
                    }
                }

                _isInitialized = true;
                _hasFrame = false;
                Console.WriteLine($"[ScreenCapturer] Initialized {_width}x{_height} (adapter {adapterIndex}, output {outputIndex})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapturer] Init failed: {ex.Message}");
                Cleanup();
                throw;
            }
        }
    }

    /// <summary>Runs the capture loop on a background thread until cancelled.</summary>
    public Task StartCaptureLoopAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(
        () => CaptureLoop(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void CaptureLoop(CancellationToken ct)
    {
        Console.WriteLine("[ScreenCapturer] Capture loop started");

        var clock = Stopwatch.StartNew();
        long nextEmitMs = 0;
        long lastEmitMs = 0;
        bool dirty = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_isInitialized || IsConsumerActive?.Invoke() == false)
                {
                    Thread.Sleep(_isInitialized ? 100 : ReinitRetryMs);
                    if (!_isInitialized) TryReinitialize();
                    dirty = false;
                    continue;
                }

                long now = clock.ElapsedMilliseconds;
                int wait = dirty
                    ? (int)Math.Max(0, nextEmitMs - now)
                    : (int)Math.Clamp(IdleRepeatMs - (now - lastEmitMs), 1, 100);

                var status = CaptureFrame(Math.Min(wait, 100));
                if (status == CaptureStatus.Lost)
                {
                    Thread.Sleep(ReinitRetryMs);
                    TryReinitialize();
                    continue;
                }
                if (status == CaptureStatus.NewFrame) dirty = true;

                now = clock.ElapsedMilliseconds;
                bool repeat = _hasFrame && (_repeatRequested || (!dirty && now - lastEmitMs >= IdleRepeatMs));

                if ((dirty && now >= nextEmitMs) || repeat)
                {
                    _repeatRequested = false;
                    dirty = false;
                    lastEmitMs = now;
                    nextEmitMs = now + 1000 / _targetFps;

                    if (_hasFrame) FrameCaptured?.Invoke(_frame, _width, _height);
                }
            }
            catch (Exception ex)
            {
                LogThrottled($"Capture loop error: {ex.Message}");
                Thread.Sleep(100);
            }
        }

        Console.WriteLine("[ScreenCapturer] Capture loop stopped");
    }

    private CaptureStatus CaptureFrame(int timeoutMs)
    {
        lock (_lock)
        {
            if (!_isInitialized || _duplication == null || _context == null || _device == null)
                return CaptureStatus.NoChange;

            var result = _duplication.AcquireNextFrame((uint)timeoutMs, out _, out IDXGIResource? resource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout) return CaptureStatus.NoChange;
            if (result == Vortice.DXGI.ResultCode.AccessLost)
            {
                // Resolution change, display mode switch, secure desktop (UAC / lock screen), driver reset…
                Console.WriteLine("[ScreenCapturer] Access lost, will re-initialize");
                return CaptureStatus.Lost;
            }
            if (result.Failure)
            {
                LogThrottled($"AcquireNextFrame failed: 0x{result.Code:X8}");
                return CaptureStatus.NoChange;
            }

            try
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                var desc = texture.Description;

                EnsureStaging(desc);
                _context.CopyResource(_staging!, texture);

                var mapped = _context.Map(_staging!, 0, MapMode.Read);
                try
                {
                    CopyToFrame(mapped.DataPointer, (int)mapped.RowPitch, (int)desc.Width, (int)desc.Height);
                }
                finally
                {
                    _context.Unmap(_staging!, 0);
                }

                _cursor.Draw(_frame, _width, _height, _originX, _originY);
                _hasFrame = true;
                return CaptureStatus.NewFrame;
            }
            catch (Exception ex)
            {
                LogThrottled($"Frame copy failed: {ex.Message}");
                return CaptureStatus.NoChange;
            }
            finally
            {
                resource?.Dispose();
                try { _duplication.ReleaseFrame(); } catch { }
            }
        }
    }

    private void EnsureStaging(Texture2DDescription source)
    {
        if (_staging != null && _staging.Description.Width == source.Width && _staging.Description.Height == source.Height
            && _staging.Description.Format == source.Format)
            return;

        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });

        if (source.Format != Format.B8G8R8A8_UNorm)
            Console.WriteLine($"[ScreenCapturer] WARNING: unexpected desktop format {source.Format} (HDR enabled?). Colours may look wrong.");

        _width = (int)source.Width;
        _height = (int)source.Height;
        int needed = _width * _height * 4;
        if (_frame.Length != needed) _frame = new byte[needed];
    }

    private unsafe void CopyToFrame(IntPtr source, int sourcePitch, int width, int height)
    {
        int rowBytes = width * 4;
        fixed (byte* dst = _frame)
        {
            byte* src = (byte*)source;
            if (sourcePitch == rowBytes)
            {
                Buffer.MemoryCopy(src, dst, _frame.Length, (long)rowBytes * height);
                return;
            }

            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * sourcePitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
        }
    }

    private void TryReinitialize()
    {
        if (!_hasTarget) return; // no display has been chosen yet

        try
        {
            Initialize(_adapterIndex, _outputIndex);
        }
        catch (Exception ex)
        {
            LogThrottled($"Re-initialization failed (will retry): {ex.Message}");
        }
    }

    private void LogThrottled(string message)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastErrorLogTicks, now) > 5000)
            Console.WriteLine($"[ScreenCapturer] {message}");
    }

    /// <summary>Enumerates every active display output on every adapter.</summary>
    public static List<DisplayInfo> GetAvailableDisplays()
    {
        var displays = new List<DisplayInfo>();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    string adapterName = adapter.Description1.Description;

                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            var r = desc.DesktopCoordinates;
                            displays.Add(new DisplayInfo
                            {
                                AdapterIndex = (int)a,
                                OutputIndex = (int)o,
                                AdapterName = adapterName,
                                DeviceName = desc.DeviceName,
                                Width = r.Right - r.Left,
                                Height = r.Bottom - r.Top,
                                Left = r.Left,
                                Top = r.Top,
                                // The primary display always sits at the virtual-desktop origin.
                                IsPrimary = r.Left == 0 && r.Top == 0
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenCapturer] Error enumerating displays: {ex.Message}");
        }

        return displays;
    }

    private void Cleanup()
    {
        _isInitialized = false;
        _hasFrame = false;
        _staging?.Dispose(); _staging = null;
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }
    }
}

/// <summary>A display output as seen by DXGI (coordinates are virtual-desktop pixels).</summary>
public sealed record DisplayInfo
{
    public int AdapterIndex { get; init; }
    public int OutputIndex { get; init; }
    public string AdapterName { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public bool IsPrimary { get; init; }

    public override string ToString() =>
        $"{DeviceName} ({Width}x{Height}) on {AdapterName}{(IsPrimary ? " [Primary]" : "")}";
}
