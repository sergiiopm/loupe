using System;
using System.Drawing;
using System.Windows.Forms;

namespace Loupe
{
    /// <summary>
    /// The default lens. A per-pixel-alpha layered window that copies the screen beneath it every
    /// frame. To do that without magnifying itself it is excluded from screen capture, which also
    /// keeps it out of screenshots, recordings and screen sharing (see <see cref="RecordableLensWindow"/>).
    /// </summary>
    sealed class LensWindow : Form, ILens
    {
        readonly LensAnimation animation = new LensAnimation();
        LensChrome chrome;
        DibSurface surface;
        bool visible;

        public LensWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            CreateHandle();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        public bool IsShown
        {
            get { return visible; }
        }

        public void Update(Point cursor, double zoom, Settings settings, bool show)
        {
            float appearance;
            if (!animation.Step(show, out appearance))
            {
                HideLens();
                return;
            }

            float scale = Native.GetScaleAt(cursor);
            int width = (int)Math.Round(settings.LensWidth * scale);
            int height = (int)Math.Round(settings.LensHeight * scale);
            if (chrome == null || !chrome.Matches(width, height, scale))
            {
                chrome = new LensChrome(width, height, scale);
                if (surface != null) surface.Dispose();
                surface = new DibSurface(chrome.Width, chrome.Height);
            }

            // Source area: centred on the pointer, kept inside the desktop.
            zoom = Math.Max(1.0, zoom);
            double sourceWidth = width / zoom, sourceHeight = height / zoom;
            Rectangle desktop = SystemInformation.VirtualScreen;
            double sourceX = Clamp(cursor.X - sourceWidth / 2, desktop.Left, desktop.Right - sourceWidth);
            double sourceY = Clamp(cursor.Y - sourceHeight / 2, desktop.Top, desktop.Bottom - sourceHeight);

            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            try
            {
                Native.SetStretchBltMode(surface.DC, settings.Smooth ? Native.HALFTONE : Native.COLORONCOLOR);
                Native.SetBrushOrgEx(surface.DC, 0, 0, IntPtr.Zero);
                Native.StretchBlt(surface.DC, chrome.Margin, chrome.Margin, width, height, screenDC,
                    (int)Math.Round(sourceX), (int)Math.Round(sourceY),
                    Math.Max(1, (int)Math.Round(sourceWidth)), Math.Max(1, (int)Math.Round(sourceHeight)),
                    Native.SRCCOPY);
                Native.GdiFlush();
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDC);
            }

            Composite();
            surface.Present(Handle, new Point(cursor.X - width / 2 - chrome.Margin, cursor.Y - height / 2 - chrome.Margin),
                            appearance);

            if (!visible)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                visible = true;
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void HideLens()
        {
            animation.Reset();
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
        }

        /// <summary>Combines the freshly magnified pixels with the cached shadow and edge layers.</summary>
        unsafe void Composite()
        {
            uint* pixels = (uint*)surface.Bits;
            byte[] mask = chrome.ContentMask;
            uint[] shadow = chrome.ShadowLayer, edge = chrome.EdgeLayer;

            for (int i = 0; i < mask.Length; i++)
            {
                uint coverage = mask[i];
                uint color;
                if (coverage == 255)
                    color = pixels[i] | 0xFF000000; // GDI leaves alpha at 0; content is opaque
                else if (coverage == 0)
                    color = shadow[i];
                else
                    color = LensChrome.AddPixels(LensChrome.ScalePixel(pixels[i] | 0xFF000000, coverage),
                                                 LensChrome.ScalePixel(shadow[i], 255 - coverage));

                pixels[i] = LensChrome.Over(edge[i], color);
            }
        }

        static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        protected override void Dispose(bool disposing)
        {
            if (surface != null) surface.Dispose();
            surface = null;
            base.Dispose(disposing);
        }
    }
}
