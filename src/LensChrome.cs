using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Loupe
{
    /// <summary>
    /// The macOS-style frame around a lens of a given size and DPI scale: rounded content area,
    /// soft two-layer shadow and hairline edge. Built once per size, then reused every frame.
    /// The surface is the lens plus <see cref="Margin"/> on each side to fit the shadow.
    /// </summary>
    sealed class LensChrome
    {
        // Geometry in logical (96 DPI) pixels.
        const float CornerRadiusLogical = 12f;
        const float ShadowMargin = 40f;
        const float AmbientBlur = 3f, AmbientOffset = 1f, AmbientOpacity = 0.30f;
        const float DropBlur = 20f, DropOffset = 8f, DropOpacity = 0.40f;

        public readonly int LensWidth, LensHeight;
        public readonly float Scale;
        public readonly int Margin;
        public readonly int Width, Height; // surface size
        public readonly float CornerRadius;

        public readonly byte[] ContentMask; // anti-aliased coverage of the rounded content area
        public readonly uint[] ShadowLayer; // premultiplied BGRA, beneath the content
        public readonly uint[] EdgeLayer;   // premultiplied BGRA, above the content

        public LensChrome(int lensWidth, int lensHeight, float scale)
        {
            LensWidth = lensWidth;
            LensHeight = lensHeight;
            Scale = scale;
            Margin = (int)Math.Ceiling(ShadowMargin * scale);
            Width = lensWidth + 2 * Margin;
            Height = lensHeight + 2 * Margin;
            CornerRadius = CornerRadiusLogical * scale;

            var content = new RectangleF(Margin, Margin, lensWidth, lensHeight);
            ContentMask = RenderAlpha(Width, Height, content, CornerRadius);

            byte[] ambient = Blur(RenderAlpha(Width, Height, Offset(content, AmbientOffset * scale), CornerRadius),
                                  Width, Height, (int)Math.Round(AmbientBlur * scale / 2));
            byte[] drop = Blur(RenderAlpha(Width, Height, Offset(content, DropOffset * scale), CornerRadius),
                               Width, Height, (int)Math.Round(DropBlur * scale / 2));
            ShadowLayer = new uint[Width * Height];
            for (int i = 0; i < ShadowLayer.Length; i++)
            {
                float alpha = 1f - (1f - ambient[i] / 255f * AmbientOpacity) * (1f - drop[i] / 255f * DropOpacity);
                ShadowLayer[i] = (uint)(alpha * 255f + 0.5f) << 24; // black, premultiplied
            }

            EdgeLayer = RenderEdge(Width, Height, content, CornerRadius, scale);
        }

        public bool Matches(int lensWidth, int lensHeight, float scale)
        {
            return lensWidth == LensWidth && lensHeight == LensHeight && scale == Scale;
        }

        // ---------------------------------------------------------------- premultiplied pixel math

        /// <summary>Multiplies every channel of a premultiplied pixel by f/255.</summary>
        public static uint ScalePixel(uint c, uint f)
        {
            uint a = ((c >> 24) * f + 127) / 255;
            uint r = (((c >> 16) & 0xFF) * f + 127) / 255;
            uint g = (((c >> 8) & 0xFF) * f + 127) / 255;
            uint b = ((c & 0xFF) * f + 127) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>Adds two premultiplied pixels, saturating each channel.</summary>
        public static uint AddPixels(uint x, uint y)
        {
            uint a = Math.Min(255u, (x >> 24) + (y >> 24));
            uint r = Math.Min(255u, ((x >> 16) & 0xFF) + ((y >> 16) & 0xFF));
            uint g = Math.Min(255u, ((x >> 8) & 0xFF) + ((y >> 8) & 0xFF));
            uint b = Math.Min(255u, (x & 0xFF) + (y & 0xFF));
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>Premultiplied "source over destination".</summary>
        public static uint Over(uint source, uint destination)
        {
            return source == 0 ? destination : AddPixels(source, ScalePixel(destination, 255 - (source >> 24)));
        }

        // ---------------------------------------------------------------- rendering helpers

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
    }
}
