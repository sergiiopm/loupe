using System;
using System.Drawing;
using System.Windows.Forms;

namespace Loupe
{
    /// <summary>Whole-screen zoom on the primary monitor via the Windows Magnification API.</summary>
    sealed class FullscreenMagnifier : IDisposable
    {
        bool active;

        public void Apply(Point cursor, double zoom)
        {
            if (!Magnification.EnsureInitialized()) return;

            // Keep the pointer at the centre of the magnified view, clamped to the screen edges.
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            double viewWidth = screen.Width / zoom;
            double viewHeight = screen.Height / zoom;
            double x = Math.Max(0, Math.Min(cursor.X - viewWidth / 2, screen.Width - viewWidth));
            double y = Math.Max(0, Math.Min(cursor.Y - viewHeight / 2, screen.Height - viewHeight));

            Native.MagSetFullscreenTransform((float)zoom, (int)x, (int)y);
            active = true;
        }

        public void Reset()
        {
            if (active) Native.MagSetFullscreenTransform(1f, 0, 0);
            active = false;
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
