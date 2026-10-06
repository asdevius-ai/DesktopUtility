namespace DesktopUtility.Host.Video;

public enum QualityPreset
{
    Low,
    Medium,
    High
}

/// <summary>Video encoder configuration, normally created from a <see cref="QualityPreset"/>.</summary>
public sealed class EncoderSettings
{
    /// <summary>Output is scaled down (never up, aspect ratio preserved) to fit this box. 0 = no limit.</summary>
    public int MaxWidth { get; init; }
    public int MaxHeight { get; init; }

    public int TargetFps { get; init; } = 30;

    /// <summary>Upper bound for the video bitrate in kbps.</summary>
    public int MaxBitrateKbps { get; init; } = 3000;

    /// <summary>x264 constant rate factor: lower is sharper and bigger.</summary>
    public int Crf { get; init; } = 25;

    public QualityPreset Quality { get; init; } = QualityPreset.Medium;

    /// <summary>Computes the encoded size for a source frame: even dimensions, aspect ratio kept.</summary>
    public (int Width, int Height) GetOutputSize(int sourceWidth, int sourceHeight)
    {
        double scale = 1.0;
        if (MaxWidth > 0) scale = Math.Min(scale, (double)MaxWidth / sourceWidth);
        if (MaxHeight > 0) scale = Math.Min(scale, (double)MaxHeight / sourceHeight);

        int w = Math.Max(2, (int)(sourceWidth * scale) & ~1);
        int h = Math.Max(2, (int)(sourceHeight * scale) & ~1);
        return (w, h);
    }

    public static EncoderSettings FromPreset(QualityPreset preset) => preset switch
    {
        QualityPreset.Low => new EncoderSettings
        {
            MaxWidth = 960, MaxHeight = 540, TargetFps = 20, MaxBitrateKbps = 1200, Crf = 28, Quality = preset
        },
        QualityPreset.High => new EncoderSettings
        {
            MaxWidth = 1920, MaxHeight = 1080, TargetFps = 30, MaxBitrateKbps = 8000, Crf = 22, Quality = preset
        },
        _ => new EncoderSettings
        {
            MaxWidth = 1280, MaxHeight = 720, TargetFps = 30, MaxBitrateKbps = 3500, Crf = 25, Quality = QualityPreset.Medium
        }
    };

    public override string ToString() =>
        $"{Quality} (≤{(MaxWidth > 0 ? $"{MaxWidth}x{MaxHeight}" : "native")} @ {TargetFps}fps, ≤{MaxBitrateKbps}kbps)";
}
