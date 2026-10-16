using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Loupe
{
    /// <summary>
    /// A lens that shows up in screenshots, recordings and screen sharing.
    ///
    /// Instead of copying the screen itself (which forces <see cref="LensWindow"/> to hide from
    /// capture), it hosts the Magnification API's magnifier control. The control renders the desktop
    /// while skipping the lens's own windows, so the lens can stay visible to capture tools.
    /// The rounded corners come from a window region, so they aren't anti-aliased; the shadow and
    /// hairline edge live in a separate per-pixel-alpha window just beneath the lens.
    ///
    /// Every magnifier call waits for the next composition, so this lens only calls it when something
    /// actually changed, and fades in and out instead of growing (resizing costs several frames).
    /// </summary>
    sealed class RecordableLensWindow : Form, ILens
    {
        readonly LensAnimation animation = new LensAnimation();
        readonly ShadowWindow shadow = new ShadowWindow();
        IntPtr magnifier;
        LensChrome chrome;
        DibSurface shadowSurface;
        Size lensSize;
        float magnification;
        int lensAlpha = -1, shadowAlpha = -1;
        bool? smoothing;
        bool visible;

        public RecordableLensWindow()
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
            Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
            if (!Magnification.EnsureInitialized()) return;

            magnifier = Native.CreateWindowEx(0, "Magnifier", null, Native.WS_CHILD | Native.WS_VISIBLE,
                0, 0, 1, 1, Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

            // Never magnify the lens or its shadow, otherwise the lens would show itself.
            Native.MagSetWindowFilterList(magnifier, Native.MW_FILTERMODE_EXCLUDE, 2,
                                          new[] { Handle, shadow.Handle });
        }

        public bool IsShown
        {
            get { return visible; }
        }

        public bool Update(Point cursor, double zoom, Settings settings, bool show)
        {
            float appearance;
            if (!animation.Step(show, out appearance))
            {
                HideLens();
                return false;
            }
            if (magnifier == IntPtr.Zero) return false;

            bool chromeChanged = EnsureChrome(cursor, settings);
            int width = chrome.LensWidth, height = chrome.LensHeight;

            if (smoothing != settings.Smooth)
            {
                smoothing = settings.Smooth;
                Magnification.SetSmoothing(magnifier, settings.Smooth);
            }

            var lensPosition = new Point(cursor.X - width / 2, cursor.Y - height / 2);
            var shadowPosition = new Point(lensPosition.X - chrome.Margin, lensPosition.Y - chrome.Margin);
            int alpha = (int)Math.Round(255 * appearance);

            // The shadow's pixels only need re-sending while it fades or after a resize.
            bool shadowRepainted = chromeChanged || alpha != shadowAlpha;
            if (shadowRepainted)
            {
                shadowSurface.Present(shadow.Handle, shadowPosition, appearance, 1f);
                shadowAlpha = alpha;
            }

            if (lensSize.Width != width || lensSize.Height != height)
            {
                lensSize = new Size(width, height);
                int diameter = (int)Math.Round(chrome.CornerRadius * 2);
                Native.SetWindowRgn(Handle, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter), true);
                Native.SetWindowPos(magnifier, IntPtr.Zero, 0, 0, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }

            // Move the lens and its shadow together so they land in the same composed frame, keeping
            // the shadow directly beneath the lens. (A repainted shadow was already moved above.)
            uint shadowFlags = Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | (shadowRepainted ? Native.SWP_NOMOVE : 0);
            IntPtr batch = Native.BeginDeferWindowPos(2);
            batch = Native.DeferWindowPos(batch, Handle, Native.HWND_TOPMOST, lensPosition.X, lensPosition.Y,
                                          width, height, Native.SWP_NOACTIVATE);
            batch = Native.DeferWindowPos(batch, shadow.Handle, Handle, shadowPosition.X, shadowPosition.Y, 0, 0,
                                          shadowFlags);
            Native.EndDeferWindowPos(batch);

            if (alpha != lensAlpha)
            {
                Native.SetLayeredWindowAttributes(Handle, 0, (byte)alpha, Native.LWA_ALPHA);
                lensAlpha = alpha;
            }

            if (!visible)
            {
                Native.ShowWindow(shadow.Handle, Native.SW_SHOWNOACTIVATE);
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                visible = true;
            }

            long started = Stopwatch.GetTimestamp();
            UpdateMagnifier(cursor, (float)Math.Max(1.0, zoom), width, height);
            double waitedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            return waitedMs > 2.0;
        }

        void UpdateMagnifier(Point cursor, float zoom, int width, int height)
        {
            if (zoom != magnification)
            {
                magnification = zoom;
                var transform = new Native.MAGTRANSFORM { v = new[] { zoom, 0f, 0f, 0f, zoom, 0f, 0f, 0f, 1f } };
                Native.MagSetWindowTransform(magnifier, ref transform);
            }

            // Source area: centred on the pointer, kept inside the desktop. Set every frame, even when
            // unchanged, so moving content underneath (videos, typing) stays live. Setting the source
            // repaints the control by itself; an extra InvalidateRect would cost another frame.
            double sourceWidth = width / zoom, sourceHeight = height / zoom;
            Rectangle desktop = SystemInformation.VirtualScreen;
            int x = (int)Math.Round(Clamp(cursor.X - sourceWidth / 2, desktop.Left, desktop.Right - sourceWidth));
            int y = (int)Math.Round(Clamp(cursor.Y - sourceHeight / 2, desktop.Top, desktop.Bottom - sourceHeight));
            Native.MagSetWindowSource(magnifier, new Native.RECT
            {
                Left = x,
                Top = y,
                Right = x + (int)Math.Round(sourceWidth),
                Bottom = y + (int)Math.Round(sourceHeight),
            });
        }

        public void Prepare(Point cursor, Settings settings)
        {
            EnsureChrome(cursor, settings);
        }

        /// <summary>Builds the shadow for the current size and DPI if needed; returns true if it did.</summary>
        bool EnsureChrome(Point cursor, Settings settings)
        {
            float dpiScale = Native.GetScaleAt(cursor);
            int width = (int)Math.Round(settings.LensWidth * dpiScale);
            int height = (int)Math.Round(settings.LensHeight * dpiScale);
            if (chrome != null && chrome.Matches(width, height, dpiScale)) return false;

            chrome = new LensChrome(width, height, dpiScale);
            if (shadowSurface != null) shadowSurface.Dispose();
            shadowSurface = new DibSurface(chrome.Width, chrome.Height);
            PaintShadow();
            return true;
        }

        public void HideLens()
        {
            animation.Reset();
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            Native.ShowWindow(shadow.Handle, Native.SW_HIDE);
            Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
            lensAlpha = shadowAlpha = -1;
            visible = false;
        }

        /// <summary>Shadow and hairline edge with the content area left transparent for the lens.</summary>
        unsafe void PaintShadow()
        {
            uint* pixels = (uint*)shadowSurface.Bits;
            byte[] mask = chrome.ContentMask;
            uint[] shadowLayer = chrome.ShadowLayer, edge = chrome.EdgeLayer;

            for (int i = 0; i < mask.Length; i++)
            {
                uint outside = LensChrome.ScalePixel(shadowLayer[i], 255u - mask[i]);
                pixels[i] = LensChrome.Over(edge[i], outside);
            }
        }

        static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                shadow.Dispose();
                if (shadowSurface != null) shadowSurface.Dispose();
                shadowSurface = null;
            }
            base.Dispose(disposing);
        }

        /// <summary>Click-through per-pixel-alpha window that carries the lens's shadow.</summary>
        sealed class ShadowWindow : Form
        {
            public ShadowWindow()
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
        }
    }
}
