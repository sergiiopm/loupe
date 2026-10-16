using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Loupe
{
    /// <summary>Win32 interop used by Loupe.</summary>
    static class Native
    {
        // Hooks and input
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100, WM_MOUSEWHEEL = 0x020A;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;

        // Windows
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                         WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const uint ULW_ALPHA = 0x2;
        public const uint LWA_ALPHA = 0x2;
        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
        public const uint SWP_NOZORDER = 0x4;
        public const int MW_FILTERMODE_EXCLUDE = 0;
        public const byte AC_SRC_OVER = 0x0, AC_SRC_ALPHA = 0x1;

        // DPI
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;

        // GDI
        public const int COLORONCOLOR = 3, HALFTONE = 4;
        public const int SRCCOPY = 0x00CC0020;

        public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAGTRANSFORM
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
            public float[] v;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
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

        /// <summary>DPI scale factor (1.0 = 100%) of the monitor containing the point.</summary>
        public static float GetScaleAt(Point point)
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
            uint dpiX, dpiY;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0) return dpiX / 96f;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1f;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc callback, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDC, ref POINT dstPos, ref SIZE size,
            IntPtr srcDC, ref POINT srcPos, uint colorKey, ref BLENDFUNCTION blend, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("Magnification.dll")] public static extern bool MagInitialize();
        [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] public static extern bool MagSetFullscreenTransform(float level, int xOffset, int yOffset);
        [DllImport("Magnification.dll")] public static extern bool MagSetWindowSource(IntPtr hwnd, RECT source);
        [DllImport("Magnification.dll")] public static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM transform);
        [DllImport("Magnification.dll")]
        public static extern bool MagSetWindowFilterList(IntPtr hwnd, int filterMode, int count, IntPtr[] windows);
        [DllImport("Magnification.dll")] public static extern bool MagSetLensUseBitmapSmoothing(IntPtr hwnd, bool enabled);
    }
}
