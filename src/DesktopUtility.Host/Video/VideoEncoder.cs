using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DesktopUtility.Host.Video;

/// <summary>
/// Encodes raw BGRA frames to H.264 (libx264, ultrafast/zerolatency, Constrained Baseline for Safari)
/// through SIPSorceryMedia.FFmpeg. Frames larger than the preset allows are downscaled first.
/// </summary>
public sealed class VideoEncoder : IDisposable
{
    private readonly object _lock = new();
    private EncoderSettings _settings;
    private FFmpegVideoEncoder? _encoder;
    private bool _useOptions = true;
    private bool _initialized;
    private bool _disposed;

    private readonly FrameScaler _scaler = new();
    private byte[] _scaled = [];

    public VideoEncoder(EncoderSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Points FFmpeg.AutoGen at the bundled FFmpeg DLLs. Throws if they cannot be loaded.</summary>
    public void Initialize()
    {
        if (_initialized) return;

        FFmpeg.AutoGen.ffmpeg.RootPath = AppContext.BaseDirectory;
        _initialized = true;
        Console.WriteLine("[VideoEncoder] FFmpeg ready");
    }

    /// <summary>Encodes one frame. Returns the H.264 access unit, or null if encoding failed or produced no output.</summary>
    public byte[]? EncodeFrame(byte[] bgra, int width, int height)
    {
        lock (_lock)
        {
            if (!_initialized || _disposed) return null;

            try
            {
                var (outW, outH) = _settings.GetOutputSize(width, height);

                byte[] pixels = bgra;
                if (outW != width || outH != height)
                {
                    int needed = outW * outH * 4;
                    if (_scaled.Length != needed) _scaled = new byte[needed];
                    _scaler.Scale(bgra, width, height, _scaled, outW, outH);
                    pixels = _scaled;
                }

                return EncodeWithFallback(pixels, outW, outH);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoEncoder] Encode error: {ex.Message}");
                ResetEncoder();
                return null;
            }
        }
    }

    public void ApplySettings(EncoderSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            ResetEncoder();
            Console.WriteLine($"[VideoEncoder] Settings applied: {settings}");
        }
    }

    /// <summary>The next frame starts a new GOP (the encoder is recreated, which always yields an IDR frame).</summary>
    public void ForceKeyframe()
    {
        lock (_lock) ResetEncoder();
    }

    private byte[]? EncodeWithFallback(byte[] pixels, int width, int height)
    {
        try
        {
            _encoder ??= new FFmpegVideoEncoder(_useOptions ? BuildOptions() : null);
            return _encoder.EncodeVideo(width, height, pixels, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);
        }
        catch when (_useOptions)
        {
            // A rejected x264 option would leave the encoder half-initialised; retry once with library defaults.
            Console.WriteLine("[VideoEncoder] Encoder options rejected, falling back to defaults");
            _useOptions = false;
            ResetEncoder();

            _encoder = new FFmpegVideoEncoder();
            return _encoder.EncodeVideo(width, height, pixels, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);
        }
    }

    private Dictionary<string, string> BuildOptions() => new()
    {
        ["preset"] = "ultrafast",
        ["crf"] = _settings.Crf.ToString(),
        ["x264-params"] = $"vbv-maxrate={_settings.MaxBitrateKbps}:vbv-bufsize={_settings.MaxBitrateKbps / 2}"
    };

    private void ResetEncoder()
    {
        _encoder?.Dispose();
        _encoder = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ResetEncoder();
        }
    }
}

/// <summary>Multi-threaded fixed-point bilinear downscaler for BGRA frames.</summary>
internal sealed unsafe class FrameScaler
{
    private int[] _x0 = [], _x1 = [], _xw = [];
    private int _mapSrcW, _mapDstW;

    public void Scale(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH)
    {
        EnsureColumnMap(srcW, dstW);

        int[] x0 = _x0, x1 = _x1, xw = _xw;
        double yRatio = (double)srcH / dstH;
        int srcStride = srcW * 4;
        int dstStride = dstW * 4;

        fixed (byte* srcFixed = src)
        fixed (byte* dstFixed = dst)
        {
            nint srcPtr = (nint)srcFixed, dstPtr = (nint)dstFixed;

            Parallel.For(0, dstH, y =>
            {
                byte* s = (byte*)srcPtr;
                byte* d = (byte*)dstPtr + (long)y * dstStride;

                double sy = Math.Max(0, (y + 0.5) * yRatio - 0.5);
                int y0 = Math.Min((int)sy, srcH - 1);
                int y1 = Math.Min(y0 + 1, srcH - 1);
                int wy = (int)((sy - y0) * 256);
                byte* row0 = s + (long)y0 * srcStride;
                byte* row1 = s + (long)y1 * srcStride;

                for (int x = 0; x < dstW; x++)
                {
                    int wx = xw[x];
                    byte* p00 = row0 + x0[x], p10 = row0 + x1[x];
                    byte* p01 = row1 + x0[x], p11 = row1 + x1[x];

                    for (int c = 0; c < 3; c++)
                    {
                        int top = p00[c] * (256 - wx) + p10[c] * wx;
                        int bottom = p01[c] * (256 - wx) + p11[c] * wx;
                        d[c] = (byte)((top * (256 - wy) + bottom * wy) >> 16);
                    }
                    d[3] = 255;
                    d += 4;
                }
            });
        }
    }

    private void EnsureColumnMap(int srcW, int dstW)
    {
        if (_mapSrcW == srcW && _mapDstW == dstW) return;

        _x0 = new int[dstW];
        _x1 = new int[dstW];
        _xw = new int[dstW];
        double ratio = (double)srcW / dstW;

        for (int x = 0; x < dstW; x++)
        {
            double sx = Math.Max(0, (x + 0.5) * ratio - 0.5);
            int i0 = Math.Min((int)sx, srcW - 1);
            _x0[x] = i0 * 4;
            _x1[x] = Math.Min(i0 + 1, srcW - 1) * 4;
            _xw[x] = (int)((sx - i0) * 256);
        }

        _mapSrcW = srcW;
        _mapDstW = dstW;
    }
}
