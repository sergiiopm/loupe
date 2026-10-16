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

        IntPtr memDC, dib, oldBitmap, bits;
        int surfaceWidth, surfaceHeight;

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

        public void Render(Point cursor, double zoom, Settings settings)
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

                var position = new Native.POINT { X = cursor.X - width / 2 - margin, Y = cursor.Y - height / 2 - margin };
                var size = new Native.SIZE { cx = surfaceWidth, cy = surfaceHeight };
                var origin = new Native.POINT();
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };
                Native.UpdateLayeredWindow(Handle, screenDC, ref position, ref size, memDC, ref origin, 0,
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

        public void HideLens()
        {
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
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

            var info = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };

            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screenDC);
            dib = Native.CreateDIBSection(screenDC, ref info, 0, out bits, IntPtr.Zero, 0);
            oldBitmap = Native.SelectObject(memDC, dib);
            Native.ReleaseDC(IntPtr.Zero, screenDC);

            surfaceWidth = w;
            surfaceHeight = h;
        }

        void FreeSurface()
        {
            if (memDC == IntPtr.Zero) return;
            Native.SelectObject(memDC, oldBitmap);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDC);
            memDC = dib = oldBitmap = bits = IntPtr.Zero;
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
