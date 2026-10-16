using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Loupe
{
    /// <summary>A top-down 32-bit premultiplied-alpha bitmap selected into a memory DC.</summary>
    sealed class DibSurface : IDisposable
    {
        public readonly int Width;
        public readonly int Height;
        public IntPtr DC { get; private set; }
        public IntPtr Bits { get; private set; }

        IntPtr bitmap, previousBitmap;
        DibSurface scratch; // receives scaled frames during the appear / disappear animation

        public DibSurface(int width, int height)
        {
            Width = width;
            Height = height;

            var info = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };

            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            DC = Native.CreateCompatibleDC(screenDC);
            IntPtr bits;
            bitmap = Native.CreateDIBSection(screenDC, ref info, 0, out bits, IntPtr.Zero, 0);
            Bits = bits;
            previousBitmap = Native.SelectObject(DC, bitmap);
            Native.ReleaseDC(IntPtr.Zero, screenDC);
        }

        /// <summary>
        /// Shows this surface in a per-pixel-alpha layered window at <paramref name="position"/>,
        /// scaled around its centre and faded by <paramref name="opacity"/>.
        /// </summary>
        public void Present(IntPtr hwnd, Point position, float opacity, float scale)
        {
            IntPtr source = DC;
            if (scale < 0.999f)
            {
                if (scratch == null) scratch = new DibSurface(Width, Height);
                DrawScaled(scratch, scale);
                source = scratch.DC;
            }

            var dst = new Native.POINT { X = position.X, Y = position.Y };
            var size = new Native.SIZE { cx = Width, cy = Height };
            var origin = new Native.POINT();
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0f, Math.Min(1f, opacity))),
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, source, ref origin, 0,
                                       ref blend, Native.ULW_ALPHA);
        }

        void DrawScaled(DibSurface target, float scale)
        {
            using (var src = Wrap())
            using (var dst = target.Wrap())
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                float w = Width * scale, h = Height * scale;
                g.DrawImage(src, new RectangleF((Width - w) / 2, (Height - h) / 2, w, h));
            }
        }

        Bitmap Wrap()
        {
            return new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, Bits);
        }

        public void Dispose()
        {
            if (scratch != null) scratch.Dispose();
            scratch = null;
            if (DC == IntPtr.Zero) return;
            Native.SelectObject(DC, previousBitmap);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(DC);
            DC = Bits = bitmap = previousBitmap = IntPtr.Zero;
        }
    }
}
