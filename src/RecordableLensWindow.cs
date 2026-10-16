using System;
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
    /// </summary>
    sealed class RecordableLensWindow : Form, ILens
    {
        readonly LensAnimation animation = new LensAnimation();
        readonly ShadowWindow shadow = new ShadowWindow();
        IntPtr magnifier;
        LensChrome chrome;
        DibSurface shadowSurface;
        Size regionSize;
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
            Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
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

        public void Update(Point cursor, double zoom, Settings settings, bool show)
        {
            float appearance;
            if (!animation.Step(show, out appearance))
            {
                HideLens();
                return;
            }
            if (magnifier == IntPtr.Zero) return;

            float dpiScale = Native.GetScaleAt(cursor);
            int width = (int)Math.Round(settings.LensWidth * dpiScale);
            int height = (int)Math.Round(settings.LensHeight * dpiScale);
            if (chrome == null || !chrome.Matches(width, height, dpiScale))
            {
                chrome = new LensChrome(width, height, dpiScale);
                if (shadowSurface != null) shadowSurface.Dispose();
                shadowSurface = new DibSurface(chrome.Width, chrome.Height);
                PaintShadow();
            }

            if (smoothing != settings.Smooth)
            {
                smoothing = settings.Smooth;
                Magnification.SetSmoothing(magnifier, settings.Smooth);
            }

            // During the animation the whole lens scales as one image, like the default lens.
            float animationScale = LensAnimation.ScaleFor(appearance);
            int lensWidth = Math.Max(1, (int)Math.Round(width * animationScale));
            int lensHeight = Math.Max(1, (int)Math.Round(height * animationScale));
            double magnification = Math.Max(1.0, zoom) * animationScale;

            // Shadow first, then the lens on top of it.
            shadowSurface.Present(shadow.Handle,
                new Point(cursor.X - width / 2 - chrome.Margin, cursor.Y - height / 2 - chrome.Margin), appearance);
            shadow.BringToTop();

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, cursor.X - lensWidth / 2, cursor.Y - lensHeight / 2,
                                lensWidth, lensHeight, Native.SWP_NOACTIVATE);
            if (regionSize.Width != lensWidth || regionSize.Height != lensHeight)
            {
                regionSize = new Size(lensWidth, lensHeight);
                int diameter = (int)Math.Round(chrome.CornerRadius * animationScale * 2);
                Native.SetWindowRgn(Handle, Native.CreateRoundRectRgn(0, 0, lensWidth + 1, lensHeight + 1, diameter, diameter), true);
                Native.SetWindowPos(magnifier, IntPtr.Zero, 0, 0, lensWidth, lensHeight,
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            Native.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(255 * appearance), Native.LWA_ALPHA);

            // Source area: centred on the pointer, kept inside the desktop.
            double sourceWidth = lensWidth / magnification, sourceHeight = lensHeight / magnification;
            Rectangle desktop = SystemInformation.VirtualScreen;
            int sourceX = (int)Math.Round(Clamp(cursor.X - sourceWidth / 2, desktop.Left, desktop.Right - sourceWidth));
            int sourceY = (int)Math.Round(Clamp(cursor.Y - sourceHeight / 2, desktop.Top, desktop.Bottom - sourceHeight));

            var transform = new Native.MAGTRANSFORM
            {
                v = new[] { (float)magnification, 0f, 0f, 0f, (float)magnification, 0f, 0f, 0f, 1f },
            };
            Native.MagSetWindowTransform(magnifier, ref transform);
            Native.MagSetWindowSource(magnifier, new Native.RECT
            {
                Left = sourceX,
                Top = sourceY,
                Right = sourceX + (int)Math.Round(sourceWidth),
                Bottom = sourceY + (int)Math.Round(sourceHeight),
            });
            Native.InvalidateRect(magnifier, IntPtr.Zero, true);

            if (!visible)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                visible = true;
            }
        }

        public void HideLens()
        {
            animation.Reset();
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            shadow.HideNow();
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
            bool visible;

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

            public void BringToTop()
            {
                if (!visible)
                {
                    Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                    visible = true;
                }
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            public void HideNow()
            {
                if (!visible) return;
                Native.ShowWindow(Handle, Native.SW_HIDE);
                visible = false;
            }
        }
    }
}
