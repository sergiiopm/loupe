using System;
using System.Diagnostics;

namespace Loupe
{
    /// <summary>
    /// Appear / disappear animation shared by both lenses: the lens grows from <see cref="StartScale"/>
    /// while fading in, and plays the same motion in reverse on the way out.
    /// </summary>
    sealed class LensAnimation
    {
        public const float StartScale = 0.9f;
        const double ShowSeconds = 0.22;
        const double HideSeconds = 0.18;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastFrameTime = -1;
        double progress; // linear: 0 = hidden, 1 = fully shown

        /// <summary>
        /// Advances towards shown or hidden and returns the eased appearance (0..1).
        /// Returns false once the exit animation has finished.
        /// </summary>
        public bool Step(bool show, out float appearance)
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = lastFrameTime < 0 ? 0 : Math.Min(0.05, now - lastFrameTime);
            lastFrameTime = now;

            progress += show ? elapsed / ShowSeconds : -elapsed / HideSeconds;
            progress = Math.Max(0, Math.Min(1, progress));

            // Ease-out cubic: fast start, gentle settle. Run backwards it accelerates away on exit,
            // and reversing mid-animation never jumps because it's a function of progress alone.
            appearance = (float)(1 - Math.Pow(1 - progress, 3));
            return show || progress > 0;
        }

        public void Reset()
        {
            progress = 0;
            lastFrameTime = -1;
        }

        public static float ScaleFor(float appearance)
        {
            return StartScale + (1 - StartScale) * appearance;
        }
    }
}
