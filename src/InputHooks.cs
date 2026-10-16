using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Loupe
{
    /// <summary>
    /// Low-level mouse and keyboard hooks running on their own thread.
    ///
    /// Windows calls low-level hooks on the thread that installed them, and every mouse and key event
    /// in the system waits for the callback. Keeping them away from the rendering thread means a slow
    /// frame can never make the pointer itself lag.
    /// </summary>
    sealed class InputHooks : IDisposable
    {
        readonly Func<int, bool> onWheel;
        readonly Action onEscape;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread thread;

        // Delegates are kept in fields so they stay alive while the hooks are installed.
        Native.LowLevelHookProc mouseProc, keyboardProc;
        IntPtr mouseHook, keyboardHook;
        uint threadId;

        /// <param name="onWheel">
        /// Called on the hook thread for every wheel notch with its delta; return true to swallow it.
        /// Must be quick: the whole system's input waits for it.
        /// </param>
        /// <param name="onEscape">Called on the hook thread when Esc is pressed. The key is never swallowed.</param>
        public InputHooks(Func<int, bool> onWheel, Action onEscape)
        {
            this.onWheel = onWheel;
            this.onEscape = onEscape;

            thread = new Thread(Run) { Name = "Loupe input hooks", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            thread.Start();
            ready.WaitOne();
        }

        public bool IsInstalled
        {
            get { return mouseHook != IntPtr.Zero; }
        }

        void Run()
        {
            threadId = Native.GetCurrentThreadId();
            mouseProc = MouseCallback;
            keyboardProc = KeyboardCallback;
            IntPtr module = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
            ready.Set();

            // Hooks are delivered through this thread's message loop.
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        }

        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == Native.WM_MOUSEWHEEL)
            {
                var data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                int delta = unchecked((short)(data.mouseData >> 16));
                if (onWheel(delta))
                    return new IntPtr(1); // swallow the scroll so the app underneath doesn't see it
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == Native.WM_KEYDOWN && Marshal.ReadInt32(lParam) == Native.VK_ESCAPE)
                onEscape();
            return Native.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
            ready.Close();
        }
    }
}
