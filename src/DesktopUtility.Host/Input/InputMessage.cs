using System.Text.Json.Serialization;

namespace DesktopUtility.Host.Input;

/// <summary>
/// Input event sent by the PWA. Positions are normalized (0..1) relative to the streamed display;
/// a missing x/y means "at the current cursor position".
/// </summary>
public sealed class InputMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }

    /// <summary>Relative pointer movement as a fraction of the streamed display (trackpad mode).</summary>
    [JsonPropertyName("deltaX")] public double DeltaX { get; set; }
    [JsonPropertyName("deltaY")] public double DeltaY { get; set; }

    /// <summary>0 = left, 1 = right, 2 = middle.</summary>
    [JsonPropertyName("button")] public int Button { get; set; }

    /// <summary>Windows virtual-key code.</summary>
    [JsonPropertyName("keyCode")] public int KeyCode { get; set; }

    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Mouse wheel delta in wheel units (120 = one notch); positive scrolls up.</summary>
    [JsonPropertyName("scrollDelta")] public double ScrollDelta { get; set; }

    /// <summary>Hold Ctrl while scrolling (pinch-to-zoom).</summary>
    [JsonPropertyName("ctrl")] public bool Ctrl { get; set; }

    [JsonIgnore] public bool HasPosition => X.HasValue && Y.HasValue;
}

/// <summary>Input event type names shared with the PWA.</summary>
public static class InputTypes
{
    public const string MouseMove = "mousemove";
    public const string MouseMoveAbsolute = "mousemove_abs";
    public const string MouseDown = "mousedown";
    public const string MouseUp = "mouseup";
    public const string Click = "click";
    public const string DoubleClick = "dblclick";
    public const string RightClick = "rightclick";
    public const string MiddleClick = "middleclick";
    public const string Scroll = "scroll";
    public const string KeyDown = "keydown";
    public const string KeyUp = "keyup";
    public const string TextInput = "textinput";

    private static readonly HashSet<string> All =
    [
        MouseMove, MouseMoveAbsolute, MouseDown, MouseUp, Click, DoubleClick,
        RightClick, MiddleClick, Scroll, KeyDown, KeyUp, TextInput
    ];

    public static bool IsInput(string type) => All.Contains(type);
}
