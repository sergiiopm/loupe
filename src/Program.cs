using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Loupe")]
[assembly: AssemblyDescription("macOS-style hover zoom for Windows")]
[assembly: AssemblyProduct("Loupe")]
[assembly: AssemblyCopyright("Copyright (c) 2026 sergiiopm")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace Loupe
{
    static class Program
    {
        public const string RepoUrl = "https://github.com/sergiiopm/loupe";

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Loupe.SingleInstance", out created))
            {
                if (!created) return;

                // Work in physical pixels on every monitor so captures line up at 125%, 150%, ... scaling.
                try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LoupeApp());
            }
        }
    }
}
