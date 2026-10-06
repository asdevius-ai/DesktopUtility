using System.Runtime.InteropServices;

namespace DesktopUtility.Host.Input;

/// <summary>
/// Injects mouse and keyboard input into Windows with SendInput.
/// Normalized coordinates (0..1) from the PWA are mapped onto the display that is currently streamed.
/// Requires a per-monitor DPI aware process (see app.manifest) so that pixel coordinates match DXGI.
/// </summary>
public sealed class InputInjector
{
    private const int MaxTextLength = 256;

    private readonly object _lock = new();
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly bool[] _heldButtons = new bool[3];

    // Streamed display in virtual-desktop pixels. Zero size means "primary display".
    private int _targetLeft, _targetTop, _targetWidth, _targetHeight;
    private long _lastBlockedLogTicks;

    public bool Enabled { get; set; } = true;

    /// <summary>Sets the display (virtual-desktop pixel rectangle) that normalized coordinates refer to.</summary>
    public void SetTargetDisplay(int left, int top, int width, int height)
    {
        lock (_lock)
        {
            _targetLeft = left;
            _targetTop = top;
            _targetWidth = width;
            _targetHeight = height;
        }
        Console.WriteLine($"[InputInjector] Target display: {width}x{height} at ({left},{top})");
    }

    public void ProcessInput(InputMessage msg)
    {
        if (!Enabled) return;

        lock (_lock)
        {
            switch (msg.Type)
            {
                case InputTypes.MouseMove:
                    MoveRelative(Math.Clamp(msg.DeltaX, -1, 1), Math.Clamp(msg.DeltaY, -1, 1));
                    break;

                case InputTypes.MouseMoveAbsolute:
                    MoveAbsolute(msg.X ?? 0, msg.Y ?? 0);
                    break;

                case InputTypes.MouseDown:
                    if (msg.HasPosition) MoveAbsolute(msg.X!.Value, msg.Y!.Value);
                    SetButton(msg.Button, true);
                    break;

                case InputTypes.MouseUp:
                    if (msg.HasPosition) MoveAbsolute(msg.X!.Value, msg.Y!.Value);
                    SetButton(msg.Button, false);
                    break;

                case InputTypes.Click:
                    Click(msg, msg.Button);
                    break;

                case InputTypes.DoubleClick:
                    Click(msg, 0);
                    Click(null, 0);
                    break;

                case InputTypes.RightClick:
                    Click(msg, 1);
                    break;

                case InputTypes.MiddleClick:
                    Click(msg, 2);
                    break;

                case InputTypes.Scroll:
                    Scroll((int)Math.Clamp(msg.ScrollDelta, -2400, 2400), msg.Ctrl);
                    break;

                case InputTypes.KeyDown:
                    SetKey(msg.KeyCode, true);
                    break;

                case InputTypes.KeyUp:
                    SetKey(msg.KeyCode, false);
                    break;

                case InputTypes.TextInput:
                    TypeText(msg.Text);
                    break;
            }
        }
    }

    /// <summary>Releases every key and mouse button that is still held (client vanished mid-gesture).</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            for (int b = 0; b < _heldButtons.Length; b++)
                if (_heldButtons[b]) SetButton(b, false);

            foreach (ushort vk in _heldKeys.ToArray())
                SetKey(vk, false);
        }
    }

    // ─── Mouse ──────────────────────────────────

    private (int Left, int Top, int Width, int Height) Target()
    {
        if (_targetWidth > 0 && _targetHeight > 0)
            return (_targetLeft, _targetTop, _targetWidth, _targetHeight);

        return (0, 0, Math.Max(GetSystemMetrics(SM_CXSCREEN), 1), Math.Max(GetSystemMetrics(SM_CYSCREEN), 1));
    }

    private void MoveAbsolute(double x, double y)
    {
        var t = Target();
        MoveToPixel(
            t.Left + (int)Math.Round(Math.Clamp(x, 0, 1) * (t.Width - 1)),
            t.Top + (int)Math.Round(Math.Clamp(y, 0, 1) * (t.Height - 1)));
    }

    /// <summary>Moves relative to the current cursor, clamped to the streamed display and unaffected by pointer acceleration.</summary>
    private void MoveRelative(double dx, double dy)
    {
        if (!GetCursorPos(out var p)) return;

        var t = Target();
        int x = Math.Clamp(p.X + (int)Math.Round(dx * t.Width), t.Left, t.Left + t.Width - 1);
        int y = Math.Clamp(p.Y + (int)Math.Round(dy * t.Height), t.Top, t.Top + t.Height - 1);
        MoveToPixel(x, y);
    }

    private void MoveToPixel(int pixelX, int pixelY)
    {
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 2);
        int vHeight = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 2);

        var input = new INPUT { type = INPUT_MOUSE };
        input.union.mi = new MOUSEINPUT
        {
            dx = (int)Math.Round((pixelX - vLeft) * 65535.0 / (vWidth - 1)),
            dy = (int)Math.Round((pixelY - vTop) * 65535.0 / (vHeight - 1)),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
        };
        Send(input);
    }

    private void Click(InputMessage? at, int button)
    {
        if (at is { HasPosition: true })
            MoveAbsolute(at.X!.Value, at.Y!.Value);

        SetButton(button, true);
        SetButton(button, false);
    }

    private void SetButton(int button, bool down)
    {
        uint flags = (button, down) switch
        {
            (0, true) => MOUSEEVENTF_LEFTDOWN,
            (0, false) => MOUSEEVENTF_LEFTUP,
            (1, true) => MOUSEEVENTF_RIGHTDOWN,
            (1, false) => MOUSEEVENTF_RIGHTUP,
            (2, true) => MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0
        };
        if (flags == 0) return;

        _heldButtons[button] = down;

        var input = new INPUT { type = INPUT_MOUSE };
        input.union.mi = new MOUSEINPUT { dwFlags = flags };
        Send(input);
    }

    private void Scroll(int delta, bool ctrl)
    {
        if (delta == 0) return;

        if (ctrl) SetKey(VK_CONTROL, true);

        var input = new INPUT { type = INPUT_MOUSE };
        input.union.mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL };
        Send(input);

        if (ctrl) SetKey(VK_CONTROL, false);
    }

    // ─── Keyboard ───────────────────────────────

    private void SetKey(int vkCode, bool down)
    {
        if (vkCode is < 1 or > 254) return;
        ushort vk = (ushort)vkCode;

        // Ignore stray key-ups so we never inject a release for something we did not press.
        if (down) _heldKeys.Add(vk);
        else if (!_heldKeys.Remove(vk)) return;

        Send(KeyInput(vk, down));
    }

    private static INPUT KeyInput(ushort vk, bool down)
    {
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.union.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
            dwFlags = flags
        };
        return input;
    }

    /// <summary>Types text as Unicode key events; Enter and Tab are sent as real keys.</summary>
    private void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r') continue;

            if (c is '\n' or '\t')
            {
                ushort vk = c == '\n' ? VK_RETURN : VK_TAB;
                inputs.Add(KeyInput(vk, true));
                inputs.Add(KeyInput(vk, false));
                continue;
            }

            var down = new INPUT { type = INPUT_KEYBOARD };
            down.union.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE };
            var up = new INPUT { type = INPUT_KEYBOARD };
            up.union.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            inputs.Add(down);
            inputs.Add(up);
        }

        Send(inputs.ToArray());
    }

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp PgDn End Home arrows
        0x2D or 0x2E => true,                                                  // Insert Delete
        0x5B or 0x5C or 0x5D => true,                                          // Win keys, Apps
        0x6F or 0x90 => true,                                                  // Numpad divide, NumLock
        0xA3 or 0xA5 => true,                                                  // Right Ctrl / Right Alt
        _ => false
    };

    // ─── SendInput ──────────────────────────────

    private void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            // Typically ERROR_ACCESS_DENIED: the foreground window is elevated (UIPI) or the secure desktop is active.
            long now = Environment.TickCount64;
            if (now - Interlocked.Exchange(ref _lastBlockedLogTicks, now) > 10_000)
                Console.WriteLine($"[InputInjector] Windows blocked input (error {Marshal.GetLastWin32Error()}). " +
                                  "The active window may be running as administrator.");
        }
    }

    #region Win32 Interop

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MAPVK_VK_TO_VSC = 0;

    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion
}
