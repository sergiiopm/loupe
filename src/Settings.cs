using System;
using System.IO;

namespace Loupe
{
    enum ZoomMode { Lens, Fullscreen }

    enum Shortcut { CtrlAlt, Ctrl }

    /// <summary>User preferences, persisted as key=value lines in %APPDATA%\Loupe\config.ini.</summary>
    class Settings
    {
        public const int MinLensWidth = 160;
        public const int MaxLensWidth = 2000;

        public ZoomMode Mode = ZoomMode.Lens;
        public Shortcut Shortcut = Shortcut.CtrlAlt;

        // Lens size in logical (96 DPI) pixels; scaled to the monitor's DPI when drawn.
        public int LensWidth = 480;
        public int LensHeight = 270;

        public bool Smooth = true;

        static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loupe", "config.ini");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;

            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "Mode": Enum.TryParse(value, out s.Mode); break;
                        case "Shortcut": Enum.TryParse(value, out s.Shortcut); break;
                        case "LensWidth": int.TryParse(value, out s.LensWidth); break;
                        case "LensHeight": int.TryParse(value, out s.LensHeight); break;
                        case "Smooth": bool.TryParse(value, out s.Smooth); break;
                    }
                }
            }
            catch (IOException) { }

            s.LensWidth = Math.Max(MinLensWidth, Math.Min(MaxLensWidth, s.LensWidth));
            s.LensHeight = Math.Max(MinLensWidth * 9 / 16, Math.Min(MaxLensWidth, s.LensHeight));
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "Mode=" + Mode,
                    "Shortcut=" + Shortcut,
                    "LensWidth=" + LensWidth,
                    "LensHeight=" + LensHeight,
                    "Smooth=" + Smooth,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
