using System;

namespace Loupe
{
    /// <summary>Process-wide lifetime of the Windows Magnification API.</summary>
    static class Magnification
    {
        static bool initialized;

        public static bool EnsureInitialized()
        {
            if (!initialized) initialized = Native.MagInitialize();
            return initialized;
        }

        /// <summary>
        /// Toggles bitmap smoothing on a magnifier control. The export is undocumented (it's what the
        /// built-in Magnifier uses for "Smooth edges of images and text"), so it's optional.
        /// </summary>
        public static void SetSmoothing(IntPtr magnifier, bool enabled)
        {
            try { Native.MagSetLensUseBitmapSmoothing(magnifier, enabled); }
            catch (EntryPointNotFoundException) { }
        }

        public static void Shutdown()
        {
            if (initialized) Native.MagUninitialize();
            initialized = false;
        }
    }
}
