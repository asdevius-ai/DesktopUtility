using System.Runtime.InteropServices;

namespace DesktopUtility.Host.Capture;

/// <summary>
/// Desktop Duplication frames do not contain the mouse pointer, so without this the iPad would show
/// no cursor at all (fatal in touchpad mode). The current system cursor is rendered into a BGRA bitmap
/// (alpha recovered by drawing it over black and over white) and blended onto each captured frame.
/// </summary>
internal sealed class CursorOverlay
{
    private IntPtr _cachedHandle;
    private byte[]? _pixels; // premultiplied BGRA
    private int _width, _height, _hotX, _hotY;

    /// <param name="originX">Left edge of the captured display in virtual-desktop pixels.</param>
    /// <param name="originY">Top edge of the captured display in virtual-desktop pixels.</param>
    public void Draw(byte[] frame, int frameW, int frameH, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            return;

        if (info.hCursor != _cachedHandle)
        {
            Rebuild(info.hCursor);
            _cachedHandle = info.hCursor;
        }
        if (_pixels == null) return;

        int left = info.ptScreenPos.X - originX - _hotX;
        int top = info.ptScreenPos.Y - originY - _hotY;

        for (int cy = 0; cy < _height; cy++)
        {
            int fy = top + cy;
            if (fy < 0 || fy >= frameH) continue;

            for (int cx = 0; cx < _width; cx++)
            {
                int fx = left + cx;
                if (fx < 0 || fx >= frameW) continue;

                int s = (cy * _width + cx) * 4;
                int a = _pixels[s + 3];
                if (a == 0) continue;

                int d = (fy * frameW + fx) * 4;
                int inv = 255 - a;
                frame[d] = (byte)Math.Min(255, _pixels[s] + frame[d] * inv / 255);
                frame[d + 1] = (byte)Math.Min(255, _pixels[s + 1] + frame[d + 1] * inv / 255);
                frame[d + 2] = (byte)Math.Min(255, _pixels[s + 2] + frame[d + 2] * inv / 255);
            }
        }
    }

    private void Rebuild(IntPtr cursor)
    {
        _pixels = null;

        if (!GetIconInfo(cursor, out var icon)) return;

        try
        {
            // Monochrome cursors have only a mask bitmap whose height is doubled (AND mask + XOR mask).
            IntPtr sizeSource = icon.hbmColor != IntPtr.Zero ? icon.hbmColor : icon.hbmMask;
            if (GetObject(sizeSource, Marshal.SizeOf<BITMAP>(), out BITMAP bmp) == 0) return;

            int w = bmp.bmWidth;
            int h = icon.hbmColor != IntPtr.Zero ? bmp.bmHeight : bmp.bmHeight / 2;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return;

            byte[]? onBlack = Render(cursor, w, h, 0x00);
            byte[]? onWhite = Render(cursor, w, h, 0xFF);
            if (onBlack == null || onWhite == null) return;

            var result = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                int diff = Math.Max(onWhite[o] - onBlack[o], Math.Max(onWhite[o + 1] - onBlack[o + 1], onWhite[o + 2] - onBlack[o + 2]));
                int alpha = Math.Clamp(255 - diff, 0, 255);

                result[o] = onBlack[o];
                result[o + 1] = onBlack[o + 1];
                result[o + 2] = onBlack[o + 2];
                result[o + 3] = (byte)alpha;
            }

            _pixels = result;
            _width = w;
            _height = h;
            _hotX = icon.xHotspot;
            _hotY = icon.yHotspot;
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        }
    }

    /// <summary>Draws the cursor onto a solid-colour 32bpp DIB and returns its BGRA bytes.</summary>
    private static byte[]? Render(IntPtr cursor, int w, int h, byte fill)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32
            }
        };

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;

        IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(dc);
            return null;
        }

        IntPtr previous = SelectObject(dc, dib);
        try
        {
            var data = new byte[w * h * 4];
            Array.Fill(data, fill);
            Marshal.Copy(data, 0, bits, data.Length);

            DrawIconEx(dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, DI_NORMAL);
            GdiFlush();

            Marshal.Copy(bits, data, 0, data.Length);
            return data;
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    #region Win32 Interop

    private const int CURSOR_SHOWING = 0x1;
    private const int DI_NORMAL = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    #endregion
}
