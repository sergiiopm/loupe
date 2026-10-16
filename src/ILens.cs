using System;
using System.Drawing;

namespace Loupe
{
    /// <summary>A floating magnifier that follows the pointer.</summary>
    interface ILens : IDisposable
    {
        /// <summary>True while the lens is on screen, including during its exit animation.</summary>
        bool IsShown { get; }

        /// <summary>
        /// Draws one frame. <paramref name="show"/> is where the lens is heading: it animates in or out
        /// towards that state, and hides itself once the exit animation finishes.
        /// </summary>
        void Update(Point cursor, double zoom, Settings settings, bool show);

        /// <summary>Hides the lens immediately, without animating.</summary>
        void HideLens();
    }
}
