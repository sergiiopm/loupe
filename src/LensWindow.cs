using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Loupe
{
    /// <summary>
    /// The floating magnifier. A per-pixel-alpha layered window that follows the pointer, ignores
    /// clicks and is excluded from screen capture, so it can copy the screen underneath it without
    /// seeing itself. Styled after macOS windows: rounded corners, soft shadow and a hairline edge.
    /// </summary>
    sealed class LensWindow : Form
    {
        // Chrome geometry in logical (96 DPI) pixels.
        const float CornerRadius = 12f;
        const float ShadowMargin = 40f;
        const float AmbientBlur = 3f, AmbientOffset = 1f, AmbientOpacity = 0.30f;
        const float DropBlur = 20f, DropOffset = 8f, DropOpacity = 0.40f;

        // Appear / disappear animation: grow from StartScale while fading in, the reverse on the way out.
        const double ShowSeconds = 0.22;
        const double HideSeconds = 0.18;
        const float StartScale = 0.9f;

        // Two 32-bit surfaces: the composited lens, and a scratch copy for scaled animation frames.
        IntPtr memDC, dib, oldBitmap, bits;
        IntPtr animDC, animDib, animOldBitmap, animBits;
        int surfaceWidth, surfaceHeight;

        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastFrameTime = -1;
        double presence; // linear animation progress: 0 = hidden, 1 = fully shown

        // Chrome cached for the current lens size and DPI scale.
        int lensWidth, lensHeight, margin;
        float chromeScale;
        byte[] contentMask;  // anti-aliased coverage of the rounded content area
        uint[] shadowLayer;  // premultiplied BGRA, composited beneath the content
        uint[] edgeLayer;    // premultiplied BGRA, composited above the content

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

        /// <summary>True while the lens is on screen, including during its exit animation.</summary>
        public bool IsShown
        {
            get { return visible; }
        }

        /// <summary>
        /// Draws one frame. <paramref name="show"/> is where the lens is heading: it animates in or out
        /// towards that state, and hides itself once the exit animation finishes.
        /// </summary>
        public void Update(Point cursor, double zoom, Settings settings, bool show)
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = lastFrameTime < 0 ? 0 : Math.Min(0.05, now - lastFrameTime);
            lastFrameTime = now;

            presence += show ? elapsed / ShowSeconds : -elapsed / HideSeconds;
            presence = Math.Max(0, Math.Min(1, presence));

            if (!show && presence <= 0)
            {
                HideLens();
                return;
            }

            // Ease-out cubic: fast start, gentle settle. Run backwards it accelerates away on exit,
            // and reversing mid-animation never jumps because it's a function of progress alone.
            double eased = 1 - Math.Pow(1 - presence, 3);
            Render(cursor, Math.Max(1.0, zoom), settings, (float)eased);
        }

        void Render(Point cursor, double zoom, Settings settings, float appearance)
        {
            float scale = Native.GetScaleAt(cursor);
            int width = (int)Math.Round(settings.LensWidth * scale);
            int height = (int)Math.Round(settings.LensHeight * scale);
            EnsureChrome(width, height, scale);

            // Source area: centred on the pointer, kept inside the desktop.
            double sourceWidth = width / zoom, sourceHeight = height / zoom;
            Rectangle desktop = SystemInformation.VirtualScreen;
            double sourceX = Clamp(cursor.X - sourceWidth / 2, desktop.Left, desktop.Right - sourceWidth);
            double sourceY = Clamp(cursor.Y - sourceHeight / 2, desktop.Top, desktop.Bottom - sourceHeight);

            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            try
            {
                Native.SetStretchBltMode(memDC, settings.Smooth ? Native.HALFTONE : Native.COLORONCOLOR);
                Native.SetBrushOrgEx(memDC, 0, 0, IntPtr.Zero);
                Native.StretchBlt(memDC, margin, margin, width, height, screenDC,
                    (int)Math.Round(sourceX), (int)Math.Round(sourceY),
                    Math.Max(1, (int)Math.Round(sourceWidth)), Math.Max(1, (int)Math.Round(sourceHeight)),
                    Native.SRCCOPY);
                Native.GdiFlush();

                Composite();

                IntPtr frameDC = memDC;
                if (appearance < 0.999f)
                {
                    DrawScaledFrame(StartScale + (1 - StartScale) * appearance);
                    frameDC = animDC;
                }

                var position = new Native.POINT { X = cursor.X - width / 2 - margin, Y = cursor.Y - height / 2 - margin };
                var size = new Native.SIZE { cx = surfaceWidth, cy = surfaceHeight };
                var origin = new Native.POINT();
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0f, Math.Min(1f, appearance))),
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };
                Native.UpdateLayeredWindow(Handle, screenDC, ref position, ref size, frameDC, ref origin, 0,
                                           ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDC);
            }

            if (!visible)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                visible = true;
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Hides the lens immediately, without animating.</summary>
        public void HideLens()
        {
            presence = 0;
            lastFrameTime = -1;
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
        }

        /// <summary>Copies the composited lens into the scratch surface, scaled around its centre.</summary>
        void DrawScaledFrame(float scale)
        {
            int stride = surfaceWidth * 4;
            using (var source = new Bitmap(surfaceWidth, surfaceHeight, stride, PixelFormat.Format32bppPArgb, bits))
            using (var target = new Bitmap(surfaceWidth, surfaceHeight, stride, PixelFormat.Format32bppPArgb, animBits))
            using (var g = Graphics.FromImage(target))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                float w = surfaceWidth * scale, h = surfaceHeight * scale;
                g.DrawImage(source, new RectangleF((surfaceWidth - w) / 2, (surfaceHeight - h) / 2, w, h));
            }
        }

        // ---------------------------------------------------------------- compositing

        /// <summary>Combines the freshly magnified pixels with the cached shadow and edge layers.</summary>
        unsafe void Composite()
        {
            uint* pixels = (uint*)bits;
            int count = surfaceWidth * surfaceHeight;

            for (int i = 0; i < count; i++)
            {
                uint coverage = contentMask[i];
                uint color;
                if (coverage == 255)
                    color = pixels[i] | 0xFF000000; // GDI leaves alpha at 0; content is opaque
                else if (coverage == 0)
                    color = shadowLayer[i];
                else
                    color = Add(Scale(pixels[i] | 0xFF000000, coverage), Scale(shadowLayer[i], 255 - coverage));

                uint edge = edgeLayer[i];
                if (edge != 0)
                    color = Add(edge, Scale(color, 255 - (edge >> 24)));

                pixels[i] = color;
            }
        }

        static uint Scale(uint c, uint f)
        {
            uint a = ((c >> 24) * f + 127) / 255;
            uint r = (((c >> 16) & 0xFF) * f + 127) / 255;
            uint g = (((c >> 8) & 0xFF) * f + 127) / 255;
            uint b = ((c & 0xFF) * f + 127) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        static uint Add(uint x, uint y)
        {
            uint a = Math.Min(255u, (x >> 24) + (y >> 24));
            uint r = Math.Min(255u, ((x >> 16) & 0xFF) + ((y >> 16) & 0xFF));
            uint g = Math.Min(255u, ((x >> 8) & 0xFF) + ((y >> 8) & 0xFF));
            uint b = Math.Min(255u, (x & 0xFF) + (y & 0xFF));
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        // ---------------------------------------------------------------- chrome

        void EnsureChrome(int width, int height, float scale)
        {
            if (contentMask != null && width == lensWidth && height == lensHeight && scale == chromeScale) return;
            lensWidth = width;
            lensHeight = height;
            chromeScale = scale;

            margin = (int)Math.Ceiling(ShadowMargin * scale);
            int w = width + 2 * margin, h = height + 2 * margin;
            EnsureSurface(w, h);

            float radius = CornerRadius * scale;
            var content = new RectangleF(margin, margin, width, height);

            contentMask = RenderAlpha(w, h, content, radius);

            byte[] ambient = Blur(RenderAlpha(w, h, Offset(content, AmbientOffset * scale), radius), w, h,
                                  (int)Math.Round(AmbientBlur * scale / 2));
            byte[] drop = Blur(RenderAlpha(w, h, Offset(content, DropOffset * scale), radius), w, h,
                               (int)Math.Round(DropBlur * scale / 2));
            shadowLayer = new uint[w * h];
            for (int i = 0; i < shadowLayer.Length; i++)
            {
                float alpha = 1f - (1f - ambient[i] / 255f * AmbientOpacity) * (1f - drop[i] / 255f * DropOpacity);
                shadowLayer[i] = (uint)(alpha * 255f + 0.5f) << 24; // black, premultiplied
            }

            edgeLayer = RenderEdge(w, h, content, radius, scale);
        }

        static RectangleF Offset(RectangleF r, float dy)
        {
            r.Offset(0, dy);
            return r;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static byte[] RenderAlpha(int w, int h, RectangleF rect, float radius)
        {
            using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var path = RoundedRect(rect, radius))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.FillPath(Brushes.White, path);
                }

                int[] argb = ReadPixels(bitmap, PixelFormat.Format32bppArgb);
                var alpha = new byte[argb.Length];
                for (int i = 0; i < argb.Length; i++) alpha[i] = (byte)((uint)argb[i] >> 24);
                return alpha;
            }
        }

        static uint[] RenderEdge(int w, int h, RectangleF content, float radius, float scale)
        {
            using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    float hairline = Math.Max(1f, scale);

                    // Dark outer hairline that separates the lens from whatever is behind it.
                    using (var path = RoundedRect(content, radius))
                    using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0), hairline))
                        g.DrawPath(pen, path);

                    // Faint inner highlight, like the top edge of a macOS window.
                    var inner = RectangleF.Inflate(content, -hairline, -hairline);
                    using (var path = RoundedRect(inner, Math.Max(0f, radius - hairline)))
                    using (var pen = new Pen(Color.FromArgb(45, 255, 255, 255), hairline))
                        g.DrawPath(pen, path);
                }

                int[] pixels = ReadPixels(bitmap, PixelFormat.Format32bppPArgb);
                var layer = new uint[pixels.Length];
                Buffer.BlockCopy(pixels, 0, layer, 0, pixels.Length * 4);
                return layer;
            }
        }

        static int[] ReadPixels(Bitmap bitmap, PixelFormat format)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, format);
            try
            {
                var pixels = new int[bitmap.Width * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width, bitmap.Width);
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>Three box-blur passes per axis, a cheap approximation of a Gaussian blur.</summary>
        static byte[] Blur(byte[] source, int w, int h, int radius)
        {
            if (radius < 1) return source;

            var a = new int[source.Length];
            var b = new int[source.Length];
            for (int i = 0; i < source.Length; i++) a[i] = source[i];

            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(a, b, w, h, radius, 1, w);  // horizontal
                BoxBlur(b, a, h, w, radius, w, 1);  // vertical
            }

            var result = new byte[source.Length];
            for (int i = 0; i < result.Length; i++) result[i] = (byte)Math.Min(255, a[i]);
            return result;
        }

        /// <summary>Running-sum box blur along lines of <paramref name="length"/> samples.</summary>
        static void BoxBlur(int[] src, int[] dst, int length, int lines, int radius, int step, int lineStride)
        {
            int window = 2 * radius + 1;
            for (int line = 0; line < lines; line++)
            {
                int start = line * lineStride;
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                    if (k >= 0 && k < length) sum += src[start + k * step];

                for (int k = 0; k < length; k++)
                {
                    dst[start + k * step] = sum / window;
                    int incoming = k + radius + 1, outgoing = k - radius;
                    if (incoming < length) sum += src[start + incoming * step];
                    if (outgoing >= 0) sum -= src[start + outgoing * step];
                }
            }
        }

        // ---------------------------------------------------------------- surface

        void EnsureSurface(int w, int h)
        {
            if (memDC != IntPtr.Zero && w == surfaceWidth && h == surfaceHeight) return;
            FreeSurface();

            CreateDib(w, h, out memDC, out dib, out oldBitmap, out bits);
            CreateDib(w, h, out animDC, out animDib, out animOldBitmap, out animBits);
            surfaceWidth = w;
            surfaceHeight = h;
        }

        static void CreateDib(int w, int h, out IntPtr dc, out IntPtr bitmap, out IntPtr previous, out IntPtr pixels)
        {
            var info = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };

            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            dc = Native.CreateCompatibleDC(screenDC);
            bitmap = Native.CreateDIBSection(screenDC, ref info, 0, out pixels, IntPtr.Zero, 0);
            previous = Native.SelectObject(dc, bitmap);
            Native.ReleaseDC(IntPtr.Zero, screenDC);
        }

        static void FreeDib(ref IntPtr dc, ref IntPtr bitmap, ref IntPtr previous, ref IntPtr pixels)
        {
            if (dc == IntPtr.Zero) return;
            Native.SelectObject(dc, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
            dc = bitmap = previous = pixels = IntPtr.Zero;
        }

        void FreeSurface()
        {
            FreeDib(ref memDC, ref dib, ref oldBitmap, ref bits);
            FreeDib(ref animDC, ref animDib, ref animOldBitmap, ref animBits);
        }

        static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        protected override void Dispose(bool disposing)
        {
            FreeSurface();
            base.Dispose(disposing);
        }
    }
}
