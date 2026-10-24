using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace NvHud
{
    internal static class Native
    {
        // 窗口扩展样式
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);
        public static readonly int WM_TASKBAR_CREATED = RegisterWindowMessage("TaskbarCreated");
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;

        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const int TME_LEAVE = 0x00000002;

        public const int WM_SETTINGCHANGE = 0x001A;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx, cy;
            public SIZE(int w, int h) { cx = w; cy = h; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndTrack;
            public int dwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi,
            uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        public static void EnableDpiAware()
        {
            // -4 = PER_MONITOR_AWARE_V2
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-3))) return; // PER_MONITOR_AWARE
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static float GetScale(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96f;
            }
            catch { }
            return 1f;
        }
    }

    /// <summary>带逐像素 alpha 的位图表面，用于 UpdateLayeredWindow。</summary>
    internal sealed class LayeredSurface : IDisposable
    {
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _old;
        private IntPtr _bits;
        private int _w, _h;

        public int Width { get { return _w; } }
        public int Height { get { return _h; } }

        public void Ensure(int w, int h)
        {
            if (w == _w && h == _h && _memDc != IntPtr.Zero) return;
            Dispose();

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(screen);

            var bmi = new Native.BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _old = Native.SelectObject(_memDc, _dib);
            Native.ReleaseDC(IntPtr.Zero, screen);

            _w = w; _h = h;
        }

        /// <summary>把 GDI+ 位图（直通 alpha）预乘后拷入 DIB，附加整体不透明度。</summary>
        public void CopyFrom(Bitmap src, float opacity)
        {
            Ensure(src.Width, src.Height);

            var rect = new Rectangle(0, 0, src.Width, src.Height);
            System.Drawing.Imaging.BitmapData data = src.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int w = src.Width, h = src.Height;
                unsafe
                {
                    byte* srow = (byte*)data.Scan0;
                    byte* drow = (byte*)_bits;
                    for (int y = 0; y < h; y++)
                    {
                        byte* sp = srow + y * stride;
                        byte* dp = drow + y * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            byte b = sp[0], g = sp[1], r = sp[2], a = sp[3];
                            int na = (int)(a * opacity);
                            if (na < 0) na = 0;
                            if (na > 255) na = 255;
                            // 预乘
                            dp[0] = (byte)(b * na / 255);
                            dp[1] = (byte)(g * na / 255);
                            dp[2] = (byte)(r * na / 255);
                            dp[3] = (byte)na;
                            sp += 4; dp += 4;
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(data);
            }
        }

        public void Push(IntPtr hwnd, int x, int y)
        {
            if (_memDc == IntPtr.Zero) return;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT(x, y);
            var size = new Native.SIZE(_w, _h);
            var srcPt = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ref srcPt, 0, ref blend, Native.ULW_ALPHA);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        public void Dispose()
        {
            if (_memDc != IntPtr.Zero)
            {
                if (_old != IntPtr.Zero) Native.SelectObject(_memDc, _old);
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero)
            {
                Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
            _bits = IntPtr.Zero;
            _w = 0; _h = 0;
        }
    }
}
