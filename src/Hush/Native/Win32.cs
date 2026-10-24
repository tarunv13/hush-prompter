using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace Hush.Native
{
    public static class Win32
    {
        public const int WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
        public const int WM_HOTKEY = 0x0312, WM_DEVICECHANGE = 0x0219;

        /// <summary>
        /// Removes a window from screenshots, screen recording and screen sharing (Windows 10 2004
        /// and later). Returns false on older systems, where nothing is hidden.
        /// </summary>
        public static bool ExcludeFromCapture(Window w, bool exclude)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return false;
            return SetWindowDisplayAffinity(h, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }

        public static int GetDisplayAffinity(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            return h != IntPtr.Zero && GetWindowDisplayAffinity(h, out int a) ? a : -1;
        }

        /// <summary>No taskbar button and no focus theft: clicking the prompter keeps your call app active.</summary>
        public static void MakeToolWindow(Window w, bool noActivate)
        {
            var h = new WindowInteropHelper(w).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TOOLWINDOW;
            ex &= ~WS_EX_APPWINDOW;
            if (noActivate) ex |= WS_EX_NOACTIVATE; else ex &= ~WS_EX_NOACTIVATE;
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
        }

        public struct MonitorArea { public Rect Bounds; public Rect Work; }

        /// <summary>Bounds of the monitor holding a window, in WPF device-independent units.</summary>
        public static MonitorArea MonitorOf(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            IntPtr mon = MonitorFromWindow(h, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            var src = PresentationSource.FromVisual(w);
            double sx = 1, sy = 1;
            if (src?.CompositionTarget != null) { sx = src.CompositionTarget.TransformFromDevice.M11; sy = src.CompositionTarget.TransformFromDevice.M22; }
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                return new MonitorArea
                {
                    Bounds = new Rect(mi.rcMonitor.Left * sx, mi.rcMonitor.Top * sy, (mi.rcMonitor.Right - mi.rcMonitor.Left) * sx, (mi.rcMonitor.Bottom - mi.rcMonitor.Top) * sy),
                    Work = new Rect(mi.rcWork.Left * sx, mi.rcWork.Top * sy, (mi.rcWork.Right - mi.rcWork.Left) * sx, (mi.rcWork.Bottom - mi.rcWork.Top) * sy)
                };
            }
            return new MonitorArea { Bounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), Work = SystemParameters.WorkArea };
        }

        public static void SetRoundedCorners(Window w, bool round)
        {
            var h = new WindowInteropHelper(w).Handle;
            int pref = round ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
            try { DwmSetWindowAttribute(h, 33, ref pref, sizeof(int)); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, int affinity);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out int affinity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    }

    /// <summary>System-wide shortcuts, registered only while the prompter is running.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        readonly HwndSource _src;
        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _next = 0xB000;

        public HotkeyManager(Window owner)
        {
            _src = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle());
            _src.AddHook(Hook);
        }

        /// <summary>Registers Ctrl+Alt+key. Returns false if another app already owns it.</summary>
        public bool RegisterCtrlAlt(Key key, Action action, bool repeat = false)
        {
            int id = _next++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint mods = MOD_CONTROL | MOD_ALT | (repeat ? 0 : MOD_NOREPEAT);
            if (!Win32.RegisterHotKey(_src.Handle, id, mods, vk)) return false;
            _actions[id] = action;
            return true;
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a)) { handled = true; a(); }
            return IntPtr.Zero;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_src.Handle, id);
            _actions.Clear();
        }

        public void Dispose() { UnregisterAll(); _src.RemoveHook(Hook); }
    }
}
