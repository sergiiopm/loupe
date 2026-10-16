using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Loupe
{
    /// <summary>
    /// Tray application: turns modifier + mouse wheel into an eased zoom level and renders it with
    /// either a lens window or full-screen magnification, one frame per display refresh.
    /// </summary>
    class LoupeApp : ApplicationContext
    {
        const double MaxZoom = 20.0;
        const double ZoomStepPerNotch = 1.25;
        const double ResizeStepPerNotch = 1.1;
        const double ZoomEasingSeconds = 0.045; // time constant of the exponential zoom easing
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly Settings settings = Settings.Load();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Control uiThread = new Control(); // marshals hook events onto the UI thread
        readonly FullscreenMagnifier fullscreen = new FullscreenMagnifier();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly InputHooks hooks;
        LensWindow privateLens;
        RecordableLensWindow recordableLens;

        double targetZoom = 1.0;
        double currentZoom = 1.0;
        bool rendering;
        double lastFrameTime;

        public LoupeApp()
        {
            IntPtr createHandle = uiThread.Handle;
            Application.Idle += OnIdle;
            hooks = new InputHooks(OnWheelHook, OnEscapeHook);

            tray.Icon = LoadIcon();
            tray.Text = "Loupe";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowHelp(); };
            BuildMenu();

            if (!hooks.IsInstalled)
            {
                MessageBox.Show(T("Could not install the mouse hook.", "No se pudo instalar el gancho del ratón."),
                                "Loupe", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            tray.ShowBalloonTip(3000, T("Loupe is running", "Loupe está activo"),
                ShortcutLabel + T(" + scroll to zoom. Esc to close.", " + rueda para ampliar. Esc para cerrar."),
                ToolTipIcon.None);
            uiThread.BeginInvoke((Action)PrepareLens);
        }

        /// <summary>Creates the lens and its shadow up front so the first zoom opens without a hitch.</summary>
        void PrepareLens()
        {
            if (settings.Mode != ZoomMode.Lens) return;
            Native.POINT p;
            Native.GetCursorPos(out p);
            Lens.Prepare(new Point(p.X, p.Y), settings);
        }

        static string T(string english, string spanish)
        {
            return Text.T(english, spanish);
        }

        /// <summary>The lens variant selected in the menu, created on first use.</summary>
        ILens Lens
        {
            get
            {
                if (settings.ShowInRecordings)
                    return recordableLens ?? (recordableLens = new RecordableLensWindow());
                return privateLens ?? (privateLens = new LensWindow());
            }
        }

        string ShortcutLabel
        {
            get { return settings.Shortcut == Shortcut.Ctrl ? "Ctrl" : "Ctrl + Alt"; }
        }

        // ---------------------------------------------------------------- input

        static bool IsKeyDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        bool IsShortcutHeld()
        {
            if (!IsKeyDown(Native.VK_CONTROL)) return false;
            return settings.Shortcut == Shortcut.Ctrl || IsKeyDown(Native.VK_MENU);
        }

        // Hook thread: decide right away whether to swallow the wheel, handle it on the UI thread.
        bool OnWheelHook(int delta)
        {
            if (!IsShortcutHeld()) return false;
            bool resize = IsKeyDown(Native.VK_SHIFT);
            uiThread.BeginInvoke((Action)(() => OnWheel(delta, resize)));
            return true;
        }

        // Hook thread: Esc closes the zoom. A hook (rather than polling) sees even a quick tap.
        void OnEscapeHook()
        {
            uiThread.BeginInvoke((Action)(() => { if (targetZoom > 1.0) targetZoom = 1.0; }));
        }

        void OnWheel(int delta, bool resize)
        {
            // Delta isn't always a multiple of 120 (precision touchpads), so work in fractional notches.
            double notches = delta / 120.0;

            if (resize && settings.Mode == ZoomMode.Lens)
            {
                int width = (int)Math.Round(settings.LensWidth * Math.Pow(ResizeStepPerNotch, notches));
                settings.LensWidth = Math.Max(Settings.MinLensWidth, Math.Min(Settings.MaxLensWidth, width));
                settings.LensHeight = settings.LensWidth * 9 / 16;
                if (targetZoom <= 1.0) targetZoom = 2.0;
            }
            else
            {
                targetZoom *= Math.Pow(ZoomStepPerNotch, notches);
                if (targetZoom < 1.03) targetZoom = 1.0;
                if (targetZoom > MaxZoom) targetZoom = MaxZoom;
            }

            if (!rendering)
            {
                rendering = true;
                lastFrameTime = clock.Elapsed.TotalSeconds;
            }
        }

        // ---------------------------------------------------------------- rendering

        // Render loop: while zooming, draw frames back to back whenever the UI thread has no messages
        // waiting, so the tray menu and window painting stay responsive. Each frame is paced by the
        // compositor, giving one frame per display refresh.
        void OnIdle(object sender, EventArgs e)
        {
            Native.MSG msg;
            while (rendering && !Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE))
                RenderFrame();
        }

        void RenderFrame()
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Min(0.1, now - lastFrameTime);
            lastFrameTime = now;

            // Exponential easing towards the target, independent of the frame rate.
            currentZoom += (targetZoom - currentZoom) * (1 - Math.Exp(-elapsed / ZoomEasingSeconds));
            if (Math.Abs(targetZoom - currentZoom) < 0.004) currentZoom = targetZoom;

            Native.POINT p;
            Native.GetCursorPos(out p);
            var cursor = new Point(p.X, p.Y);

            bool zoomed = currentZoom > 1.001;
            bool waitedForCompositor = false;
            if (settings.Mode == ZoomMode.Lens)
            {
                // The lens starts its exit animation as soon as the target drops to 1×,
                // while the magnification keeps easing out underneath it.
                waitedForCompositor = Lens.Update(cursor, currentZoom, settings, targetZoom > 1.0);
            }
            else
            {
                if (zoomed) fullscreen.Apply(cursor, currentZoom);
                else fullscreen.Reset();
            }

            if (!zoomed && !Lens.IsShown)
            {
                rendering = false;
                return;
            }

            // Wait for the next composition unless the frame already did, so we never draw faster
            // than the display can show.
            if (!waitedForCompositor && Native.DwmFlush() != 0)
                System.Threading.Thread.Sleep(8);
        }

        void ResetZoom()
        {
            targetZoom = currentZoom = 1.0;
            rendering = false;
            if (privateLens != null) privateLens.HideLens();
            if (recordableLens != null) recordableLens.HideLens();
            fullscreen.Reset();
        }

        // ---------------------------------------------------------------- tray menu

        void BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var header = new ToolStripMenuItem("Loupe  ·  " + ShortcutLabel + T(" + scroll", " + rueda"));
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            var mode = new ToolStripMenuItem(T("Mode", "Modo"));
            mode.DropDownItems.Add(Item(T("Lens", "Lupa"), settings.Mode == ZoomMode.Lens,
                delegate { settings.Mode = ZoomMode.Lens; OnSettingsChanged(); }));
            mode.DropDownItems.Add(Item(T("Full screen (primary monitor)", "Pantalla completa (monitor principal)"),
                settings.Mode == ZoomMode.Fullscreen,
                delegate { settings.Mode = ZoomMode.Fullscreen; OnSettingsChanged(); }));
            menu.Items.Add(mode);

            var shortcut = new ToolStripMenuItem(T("Shortcut", "Atajo"));
            shortcut.DropDownItems.Add(Item(T("Ctrl + Alt + scroll", "Ctrl + Alt + rueda"),
                settings.Shortcut == Shortcut.CtrlAlt,
                delegate { settings.Shortcut = Shortcut.CtrlAlt; OnSettingsChanged(); }));
            shortcut.DropDownItems.Add(Item(T("Ctrl + scroll", "Ctrl + rueda"),
                settings.Shortcut == Shortcut.Ctrl,
                delegate { settings.Shortcut = Shortcut.Ctrl; OnSettingsChanged(); }));
            menu.Items.Add(shortcut);

            var size = new ToolStripMenuItem(T("Lens size", "Tamaño de la lupa"));
            AddSizeItem(size, T("Small", "Pequeña"), 320, 180);
            AddSizeItem(size, T("Medium", "Mediana"), 480, 270);
            AddSizeItem(size, T("Large", "Grande"), 640, 360);
            AddSizeItem(size, T("Extra large", "Muy grande"), 800, 450);
            menu.Items.Add(size);

            menu.Items.Add(Item(T("Smooth scaling", "Suavizar imagen"), settings.Smooth,
                delegate { settings.Smooth = !settings.Smooth; OnSettingsChanged(); }));
            var recordable = Item(T("Show lens in recordings", "Mostrar lupa en grabaciones"), settings.ShowInRecordings,
                delegate { settings.ShowInRecordings = !settings.ShowInRecordings; OnSettingsChanged(); });
            recordable.ToolTipText = T(
                "Visible to screenshots, screen recorders and screen sharing. Corners are slightly less smooth.",
                "Visible en capturas, grabaciones y al compartir pantalla. Las esquinas quedan algo menos suaves.");
            menu.Items.Add(recordable);
            menu.Items.Add(Item(T("Start with Windows", "Iniciar con Windows"), IsAutostartEnabled(),
                delegate { SetAutostart(!IsAutostartEnabled()); BuildMenu(); }));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(T("How to use", "Cómo se usa"), null, delegate { ShowHelp(); });
            menu.Items.Add(T("About Loupe", "Acerca de Loupe"), null, delegate { Process.Start(Program.RepoUrl); });
            menu.Items.Add(T("Quit", "Salir"), null, delegate { Quit(); });

            var previous = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (previous != null) previous.Dispose();
        }

        void AddSizeItem(ToolStripMenuItem parent, string name, int width, int height)
        {
            bool selected = settings.LensWidth == width && settings.LensHeight == height;
            parent.DropDownItems.Add(Item(name + "  (" + width + " × " + height + ")", selected,
                delegate { settings.LensWidth = width; settings.LensHeight = height; OnSettingsChanged(); }));
        }

        static ToolStripMenuItem Item(string text, bool isChecked, EventHandler onClick)
        {
            return new ToolStripMenuItem(text, null, onClick) { Checked = isChecked };
        }

        void OnSettingsChanged()
        {
            ResetZoom();
            settings.Save();
            BuildMenu();
            PrepareLens();
        }

        void ShowHelp()
        {
            string keys = ShortcutLabel;
            MessageBox.Show(
                T("How to use Loupe:", "Cómo se usa Loupe:") + "\n\n" +
                "•  " + keys + T(" + scroll up  →  zoom in", " + rueda adelante  →  ampliar") + "\n" +
                "•  " + keys + T(" + scroll down  →  zoom out (until it closes)", " + rueda atrás  →  reducir (hasta cerrar)") + "\n" +
                "•  " + keys + T(" + Shift + scroll  →  resize the lens", " + Shift + rueda  →  cambiar el tamaño de la lupa") + "\n" +
                "•  Esc  →  " + T("close the zoom", "cerrar el zoom") + "\n\n" +
                T("The lens follows the pointer and clicks pass straight through it.\n" +
                  "Right-click the tray icon to change mode, size and shortcut.",
                  "La lupa sigue al ratón y los clics la atraviesan.\n" +
                  "Haz clic derecho en el icono de la bandeja para cambiar modo, tamaño y atajo."),
                "Loupe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static bool IsAutostartEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue("Loupe") != null;
        }

        static void SetAutostart(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue("Loupe", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("Loupe", false);
            }
        }

        static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Loupe.loupe.ico"))
                return stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application;
        }

        void Quit()
        {
            ResetZoom();
            settings.Save();
            hooks.Dispose();
            Application.Idle -= OnIdle;
            fullscreen.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (privateLens != null) privateLens.Dispose();
            if (recordableLens != null) recordableLens.Dispose();
            Magnification.Shutdown();
            uiThread.Dispose();
            ExitThread();
        }
    }
}
