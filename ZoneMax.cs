// =====================================================================================
//  ZoneMax v3  --  real Windows maximize inside a region of one physical monitor.
//
//  THE ENGINE (proven in spike/true-maximize-spike.ps1):
//     1. temporarily shrink the monitor's WORK AREA down to the target rect
//     2. issue a genuine ShowWindow(SW_MAXIMIZE)  -> Windows maximizes to the work area,
//        so the window fills only that rect and is TRULY maximized (IsZoomed == true)
//     3. restore the work area WITHOUT broadcasting, so nothing re-layouts and the window
//        keeps its real maximized state.
//  Because the window is genuinely maximized, Windows deletes its resize border and lets it
//  overhang the edge -- so slamming the mouse at the top of a zone hits a Chrome tab, not a
//  resize handle. That is the whole point of this program.
//
//  MODEL -- zones are not a static config you maintain. Each MONITOR has a mode, and you drive it:
//     Win+Up          -> that monitor collapses to ONE zone; maximize the window across it
//     Win+Left/Right  -> that monitor splits into TWO zones (activating the split if needed);
//                        send the window to that zone and truly maximize it there
//     Win+Down        -> restore, then minimize
//     Win+Shift+L/R   -> throw the window onto the neighbouring monitor
//     drag to an OUTER screen edge -> truly maximize in the zone you dropped it on
//  A monitor in Single mode behaves EXACTLY like plain Windows (its one zone is the whole
//  work area), so the laptop panel is native until you deliberately split it.
//
//  v5 -- the audit batch. Races (watchdog vs engine, watchdog vs drop), SPI results checked,
//  per-monitor-V2 DPI, elevated windows fall through to native snap, hooks reduced to
//  microsecond dispatchers (all real work happens on the pump), background logger, zone/monitor
//  caches, device-name monitor identity, hwnd-recycling guards, honest startup toggle,
//  Snap-style continuation onto the next monitor.
//
//  v4 -- DRAGGING. Three things, all learned by breaking them:
//     * Windows Snap must stay ON. "Drag a maximized window to un-maximize it" IS Aero Snap; with
//       Snap off, every truly-maximized window (i.e. every window we touch) becomes undraggable.
//     * A title-bar press must NOT leave the work area shrunk for the length of a drag -- it is a
//       GLOBAL per-monitor setting, and other windows re-maximize into it. Disarm on the first move.
//     * The disarm watchdog cannot be a WinForms timer: the mouse hook starves WM_TIMER during a
//       drag (measured: a 900ms disarm arriving 2s late).
//
//  Build: build.ps1   (in-box csc.exe -> C# 5, .NET Framework, no SDK required)
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZoneMax
{
    // ---------------------------------------------------------------------------- native
    internal static class N
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const int SW_RESTORE = 9;
        public const int SW_MAXIMIZE = 3;
        public const int SW_MINIMIZE = 6;

        public const int SPI_SETWORKAREA = 0x002F;
        public const int SPI_GETWINARRANGING = 0x0082;
        public const int SPI_SETWINARRANGING = 0x0083;
        public const int SPIF_UPDATEINIFILE = 0x0001;
        public const int SPIF_SENDCHANGE = 0x0002;

        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const int SM_CXSIZEFRAME = 32;
        public const int SM_CXPADDEDBORDER = 92;

        public const int MONITOR_DEFAULTTONULL = 0;      // "is there actually a screen here?"
        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const int MONITORINFOF_PRIMARY = 1;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;

        public const int WH_MOUSE_LL = 14;
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_NCHITTEST = 0x0084;
        public const int HTCAPTION = 2;
        public const int HTMAXBUTTON = 9;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint LLKHF_INJECTED = 0x00000010;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_Z = 0x5A;
        public const int VK_LBUTTON = 0x01;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public const int VK_SNAPSHOT = 0x2C;   // PrintScreen
        public const int VK_S = 0x53;
        public const int VK_TAB = 0x09;
        public const int VK_NOOP = 0xE8;   // unassigned VK -- injected purely to stop the Start menu opening

        public const int GA_ROOT = 2;
        public const uint ABM_GETSTATE = 0x00000004;
        public const int ABS_AUTOHIDE = 0x00000001;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TOKEN_ELEVATION = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public long Area { get { return (long)Width * (long)Height; } }
            public static RECT FromXYWH(int x, int y, int w, int h)
            {
                RECT r = new RECT(); r.Left = x; r.Top = y; r.Right = x + w; r.Bottom = y + h; return r;
            }
            public RECT Inflate(int n)
            {
                RECT r = new RECT(); r.Left = Left - n; r.Top = Top - n; r.Right = Right + n; r.Bottom = Bottom + n; return r;
            }
            public static RECT Intersect(RECT a, RECT b)
            {
                RECT r = new RECT();
                r.Left = Math.Max(a.Left, b.Left); r.Top = Math.Max(a.Top, b.Top);
                r.Right = Math.Min(a.Right, b.Right); r.Bottom = Math.Min(a.Bottom, b.Bottom);
                if (r.Right < r.Left) r.Right = r.Left;
                if (r.Bottom < r.Top) r.Bottom = r.Top;
                return r;
            }
            public bool NearlyEquals(RECT o, int tol)
            {
                return Math.Abs(Left - o.Left) <= tol && Math.Abs(Top - o.Top) <= tol
                    && Math.Abs(Right - o.Right) <= tol && Math.Abs(Bottom - o.Bottom) <= tol;
            }
            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture, "({0},{1}) {2}x{3}", Left, Top, Width, Height);
            }
        }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public int cbSize; public uint dwTime; }

        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        public static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(none)";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, 64);
            return sb.ToString();
        }
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT r, int flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, int flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(int a, int u, ref RECT r, int w);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] public static extern bool SpiPtr(int a, int u, IntPtr p, int w);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] public static extern bool SpiInt(int a, int u, ref int p, int w);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll")] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] public static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr mod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr res);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);

        public static int Border() { return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER); }

        public static string TitleOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(160);
            GetWindowText(h, sb, 160);
            string s = sb.ToString();
            return s.Length > 44 ? s.Substring(0, 44) : s;
        }

        public static bool WinDown()
        {
            return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        // Windows opens the Start menu when the Win key goes up having "done nothing". If we swallow
        // the arrow key, that is exactly what it thinks happened -- so inject an unassigned key.
        public static void SwallowStartMenu()
        {
            keybd_event((byte)VK_NOOP, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VK_NOOP, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // ---- elevated windows are untouchable (UIPI) --------------------------------------------
        // A non-elevated process cannot SetWindowPos/ShowWindow an elevated window, and its
        // WM_NCHITTEST replies never arrive. If we swallow Win+Arrow for one, NOTHING happens --
        // not even native Snap. So: detect elevation and keep our hands (and hooks) off.
        // Only ever called from the UI thread, so the cache needs no lock.
        static readonly Dictionary<uint, bool> ElevCache = new Dictionary<uint, bool>();
        public static bool IsWindowElevated(IntPtr hwnd)
        {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;
            bool elev;
            if (ElevCache.TryGetValue(pid, out elev)) return elev;

            elev = true;                       // can't even open it -> definitely can't manage it
            IntPtr proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc != IntPtr.Zero)
            {
                IntPtr tok;
                if (OpenProcessToken(proc, TOKEN_QUERY, out tok))
                {
                    int info, len;
                    if (GetTokenInformation(tok, TOKEN_ELEVATION, out info, 4, out len)) elev = info != 0;
                    CloseHandle(tok);
                }
                CloseHandle(proc);
            }
            if (ElevCache.Count > 200) ElevCache.Clear();   // PIDs recycle; keep it small and honest
            ElevCache[pid] = elev;
            return elev;
        }

        // ---- Windows Snap must stay ON, and ZoneMax depends on it ------------------------------
        // "Drag a maximized window's title bar to un-maximize it and move it" is part of Aero Snap.
        // Switch Snap off and Windows silently ignores that drag -- and since making windows TRULY
        // maximized is this program's entire reason to exist, that makes every window undraggable.
        // (Learned by shipping it. 2026-07-12.)
        //
        // NB: the boolean goes in uiParam. MSDN documents it as pvParam. MSDN is wrong -- called that
        // way the function returns TRUE and does absolutely nothing.
        public static bool WinArrangingOn()
        {
            int v = 0;
            SpiInt(SPI_GETWINARRANGING, 0, ref v, 0);
            return v != 0;
        }

        public static bool EnsureWinArranging()
        {
            if (WinArrangingOn()) return true;
            SpiPtr(SPI_SETWINARRANGING, 1, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            return WinArrangingOn();
        }

        public static bool TaskbarAutoHides()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            return (SHAppBarMessage(ABM_GETSTATE, ref d).ToInt64() & ABS_AUTOHIDE) != 0;
        }

        public static List<IntPtr> TaskbarWindows()
        {
            List<IntPtr> bars = new List<IntPtr>();
            IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (h != IntPtr.Zero) bars.Add(h);
            IntPtr s = IntPtr.Zero;
            while ((s = FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) bars.Add(s);
            return bars;
        }
    }

    // ---------------------------------------------------------------------------- model
    internal enum Mode { Single, Split }

    internal class Zone
    {
        public string Name;      // "Full" | "Left" | "Right"
        public string MonKey;    // which monitor it belongs to
        public N.RECT Rect;      // already clipped to that monitor's real work area
    }

    internal class Config
    {
        public bool AutoFix = true;    // safety net: pull a stray maximize back into its zone
        public bool Seamless = true;   // arm the work area on the click, so there is no flicker
        public bool SnapKeys = true;   // Win+Arrow belongs to us
        public bool DragZones = true;  // drop a window on a screen edge -> truly maximize it in that zone
        public bool Verbose = true;    // log every action and its result
        public bool Startup = true;    // registered in HKCU Run; false must SURVIVE restarts (it didn't)
        public double SplitFraction = 0.5;                       // where a split monitor divides
        public Dictionary<string, Mode> Modes = new Dictionary<string, Mode>();

        public static string Path { get { return System.IO.Path.Combine(App.Dir, "zonemax.ini"); } }

        public static void Save()
        {
            Config c = Engine.Cfg;
            StringBuilder sb = new StringBuilder();
            sb.Append("; =====================================================================\r\n");
            sb.Append("; ZoneMax\r\n");
            sb.Append("; =====================================================================\r\n");
            sb.Append(";   Win+Up          this monitor becomes ONE zone; maximize across it\r\n");
            sb.Append(";   Win+Left/Right  this monitor splits in TWO; send the window there, maximized\r\n");
            sb.Append(";   Win+Down        restore, then minimize\r\n");
            sb.Append(";   Win+Shift+L/R   throw the window onto the next monitor\r\n");
            sb.Append(";   Shift+maximize  ignore zones, fill the whole monitor\r\n");
            sb.Append(";   drag to an OUTER screen edge -> truly maximize in the zone you dropped it on\r\n");
            sb.Append(";   (the edge between two screens is not an edge -- you can drag across it freely)\r\n");
            sb.Append(";\r\n");
            sb.Append("; A monitor in 'single' mode behaves EXACTLY like plain Windows.\r\n");
            sb.Append("\r\n[options]\r\n");
            sb.Append("autofix = " + (c.AutoFix ? "true" : "false") + "\r\n");
            sb.Append("seamless = " + (c.Seamless ? "true" : "false") + "\r\n");
            sb.Append("snapkeys = " + (c.SnapKeys ? "true" : "false") + "\r\n");
            sb.Append("dragzones = " + (c.DragZones ? "true" : "false") + "\r\n");
            sb.Append("verbose = " + (c.Verbose ? "true" : "false") + "\r\n");
            sb.Append("startup = " + (c.Startup ? "true" : "false") + "\r\n");
            sb.Append("; where a split monitor divides, as a fraction of its width (0.5 = down the middle)\r\n");
            sb.Append("splitfraction = " + c.SplitFraction.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("\r\n[modes]\r\n");
            sb.Append("; per-monitor, remembered between sessions.  X,Y,W,H = single | split\r\n");
            foreach (KeyValuePair<string, Mode> kv in c.Modes)
                sb.Append(kv.Key + " = " + (kv.Value == Mode.Split ? "split" : "single") + "\r\n");
            try { File.WriteAllText(Path, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { App.Log("config save failed: " + ex.Message); }
        }

        public static Config Load()
        {
            Config c = new Config();
            if (!File.Exists(Path)) return c;

            string section = "";
            foreach (string raw in File.ReadAllLines(Path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (section == "options")
                {
                    bool b = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
                    if (key.Equals("autofix", StringComparison.OrdinalIgnoreCase)) c.AutoFix = b;
                    else if (key.Equals("seamless", StringComparison.OrdinalIgnoreCase)) c.Seamless = b;
                    else if (key.Equals("snapkeys", StringComparison.OrdinalIgnoreCase)) c.SnapKeys = b;
                    else if (key.Equals("dragzones", StringComparison.OrdinalIgnoreCase)) c.DragZones = b;
                    else if (key.Equals("verbose", StringComparison.OrdinalIgnoreCase)) c.Verbose = b;
                    else if (key.Equals("startup", StringComparison.OrdinalIgnoreCase)) c.Startup = b;
                    else if (key.Equals("splitfraction", StringComparison.OrdinalIgnoreCase))
                    {
                        double d;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0.1 && d < 0.9)
                            c.SplitFraction = d;
                    }
                }
                else if (section == "modes")
                {
                    c.Modes[key] = val.Equals("split", StringComparison.OrdinalIgnoreCase) ? Mode.Split : Mode.Single;
                }
            }
            return c;
        }
    }

    // ---------------------------------------------------------------------------- engine
    internal static class Engine
    {
        static readonly Dictionary<long, N.RECT> TrueWork = new Dictionary<long, N.RECT>();
        static readonly Dictionary<long, int> Suppress = new Dictionary<long, int>();
        static readonly Dictionary<long, string> Homes = new Dictionary<long, string>();   // hwnd -> "pid|monKey|zoneName"

        // TWO locks, deliberately. Gate serializes engine placements and work-area arming -- and is
        // held for up to ~150ms across MaximizeIntoRect's waits. CacheGate protects the lookup
        // dictionaries and is only ever held for microseconds, so the MOUSE HOOK may take it. The
        // hook must NEVER take Gate unbounded (a blocked hook is a dead hook). Lock order where
        // both are needed: Gate first, CacheGate second. CacheGate holders never take Gate.
        static readonly object Gate = new object();
        static readonly object CacheGate = new object();
        static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

        // Derived data that used to be recomputed per event (monitor enumeration, monitor keys,
        // zone lists). It only actually changes on mode/fraction changes and display changes, so
        // it is cached and invalidated there -- the LOCATIONCHANGE flood hits dictionary lookups.
        static List<IntPtr> monCache;
        static readonly Dictionary<long, string> KeyCache = new Dictionary<long, string>();
        static readonly Dictionary<long, List<Zone>> ZoneCache = new Dictionary<long, List<Zone>>();

        public static void InvalidateCaches()
        {
            lock (CacheGate) { monCache = null; KeyCache.Clear(); ZoneCache.Clear(); }
        }

        // ---- the engine worker ------------------------------------------------------------
        // Every slow action (MaximizeIntoRect's waits, Config.Save, work-area recapture) runs on
        // THIS thread, never on the UI thread. The UI thread is the one that pumps the low-level
        // hooks: block it for ~300ms once (LowLevelHooksTimeout) and Windows silently uninstalls
        // both hooks -- which is exactly how Win+Arrow died and native 50/50 Snap took over.
        // One thread + one queue also preserves action ordering.
        static readonly Queue<Action> Work = new Queue<Action>();
        static readonly object WorkGate = new object();
        static Thread workThread;

        public static void Post(Action a)
        {
            lock (WorkGate)
            {
                if (workThread == null)
                {
                    workThread = new Thread(WorkPump);
                    workThread.IsBackground = true;
                    workThread.Name = "ZoneMax engine";
                    workThread.Start();
                }
                Work.Enqueue(a);
                Monitor.Pulse(WorkGate);
            }
        }

        static void WorkPump()
        {
            while (true)
            {
                Action a;
                lock (WorkGate)
                {
                    while (Work.Count == 0) Monitor.Wait(WorkGate);
                    a = Work.Dequeue();
                }
                try { a(); }
                catch (Exception ex) { App.Log("engine error: " + ex.Message); }
            }
        }

        static IntPtr armedMonitor = IntPtr.Zero;
        static N.RECT armedRect;                   // what the work area is currently armed TO (Gate)
        static int armedUntil = 0;

        // TRUE from the moment a title-bar press turns into an actual drag, until the button comes up.
        // While it is true we keep our hands off the window completely -- no arming, no autofix.
        public static volatile bool Dragging = false;

        public static Config Cfg = new Config();

        // #1A2B3C after every title: two Chrome windows showing the same page are otherwise
        // indistinguishable in the log, which made a wrong-zone incident unattributable.
        public static string Tag(IntPtr h) { return "#" + h.ToInt64().ToString("X"); }

        public static string Describe(IntPtr h)
        {
            N.RECT r; N.GetWindowRect(h, out r);
            return "\"" + N.TitleOf(h) + "\"" + Tag(h) + " rect=" + r + " zoomed=" + N.IsZoomed(h);
        }

        // ---- monitors ------------------------------------------------------------
        public static List<IntPtr> Monitors()
        {
            lock (CacheGate) { if (monCache != null) return monCache; }

            List<KeyValuePair<IntPtr, N.RECT>> mons = new List<KeyValuePair<IntPtr, N.RECT>>();
            N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref N.RECT r, IntPtr d)
            {
                mons.Add(new KeyValuePair<IntPtr, N.RECT>(h, MonitorRect(h))); return true;
            }, IntPtr.Zero);
            mons.Sort(delegate(KeyValuePair<IntPtr, N.RECT> a, KeyValuePair<IntPtr, N.RECT> b)
            {
                int c = a.Value.Left.CompareTo(b.Value.Left);
                return c != 0 ? c : a.Value.Top.CompareTo(b.Value.Top);
            });
            List<IntPtr> list = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, N.RECT> kv in mons) list.Add(kv.Key);

            lock (CacheGate) { monCache = list; }     // published lists are never mutated afterwards
            return list;
        }

        // NB: an HMONITOR can be stale (monitor unplugged while we held it). GetMonitorInfo then
        // fails and this returns zeroed rects -- callers guard on Area > 0 before acting on them.
        public static N.MONITORINFO Info(IntPtr hMon)
        {
            N.MONITORINFO mi = new N.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFO));
            N.GetMonitorInfo(hMon, ref mi);
            return mi;
        }

        public static N.RECT MonitorRect(IntPtr hMon) { return Info(hMon).rcMonitor; }
        public static bool IsPrimary(IntPtr hMon) { return (Info(hMon).dwFlags & N.MONITORINFOF_PRIMARY) != 0; }

        // Monitor identity for config/homes. The device name (\\.\DISPLAY1) survives re-arranging
        // and resolution changes; the old "X,Y,W,H" geometry key silently orphaned saved modes the
        // moment anything about the layout moved.
        public static string MonKey(IntPtr hMon)
        {
            lock (CacheGate)
            {
                string cached;
                if (KeyCache.TryGetValue(hMon.ToInt64(), out cached)) return cached;
            }
            N.MONITORINFOEX mi = new N.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFOEX));
            string key;
            if (N.GetMonitorInfo(hMon, ref mi) && !string.IsNullOrEmpty(mi.szDevice))
                key = mi.szDevice;
            else
            {
                N.RECT m = MonitorRect(hMon);   // stale handle fallback: geometry is better than nothing
                key = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", m.Left, m.Top, m.Width, m.Height);
            }
            lock (CacheGate) { KeyCache[hMon.ToInt64()] = key; }
            return key;
        }

        // ---- true work area (self-healing) ---------------------------------------
        // Never read the work area back from Windows and call it "the truth" -- WE mutate it, so a
        // force-kill while armed leaves a shrunken zone behind, and the next startup would enshrine
        // it. (SPI_SETWORKAREA with a NULL rect does NOT undo it; that call is a no-op.) Derive it:
        // monitor rect, minus whatever the taskbar is actually reserving.
        public static N.RECT ComputeTrueWork(N.RECT mon)
        {
            N.RECT work = mon;
            if (N.TaskbarAutoHides()) return work;         // auto-hidden bars reserve nothing

            foreach (IntPtr tb in N.TaskbarWindows())
            {
                if (!N.IsWindowVisible(tb)) continue;
                N.RECT tr;
                if (!N.GetWindowRect(tb, out tr)) continue;
                N.RECT hit = N.RECT.Intersect(mon, tr);
                if (hit.Area <= 0) continue;

                if (hit.Width >= hit.Height)               // horizontal bar
                {
                    if (hit.Top <= mon.Top + 2) work.Top = Math.Max(work.Top, hit.Bottom);
                    else work.Bottom = Math.Min(work.Bottom, hit.Top);
                }
                else                                       // vertical bar
                {
                    if (hit.Left <= mon.Left + 2) work.Left = Math.Max(work.Left, hit.Right);
                    else work.Right = Math.Min(work.Right, hit.Left);
                }
            }
            return work;
        }

        public static void CaptureTrueWorkAreas()
        {
            InvalidateCaches();
            List<KeyValuePair<long, N.RECT>> found = new List<KeyValuePair<long, N.RECT>>();

            foreach (IntPtr hMon in Monitors())
            {
                N.MONITORINFO mi = Info(hMon);
                if (mi.rcMonitor.Area <= 0) continue;              // stale handle mid-topology-change
                N.RECT truth = ComputeTrueWork(mi.rcMonitor);

                // Respect OTHER appbars (docked launchers etc): a tighter TOP/BOTTOM in Windows'
                // rcWork cannot be our leftover -- our zones always span the work area's full
                // height -- so adopt it. A narrower WIDTH is our signature; that heals to derived.
                if (mi.rcWork.Top > truth.Top && mi.rcWork.Top < mi.rcMonitor.Bottom) truth.Top = mi.rcWork.Top;
                if (mi.rcWork.Bottom < truth.Bottom && mi.rcWork.Bottom > mi.rcMonitor.Top) truth.Bottom = mi.rcWork.Bottom;

                found.Add(new KeyValuePair<long, N.RECT>(hMon.ToInt64(), truth));
                App.Log("monitor " + mi.rcMonitor + (IsPrimary(hMon) ? " [primary]" : "")
                        + "  true workarea " + truth
                        + (mi.rcWork.NearlyEquals(truth, 2) ? "" : "   [!! Windows said " + mi.rcWork + " -- HEALING]"));
            }

            lock (CacheGate)
            {
                TrueWork.Clear();
                foreach (KeyValuePair<long, N.RECT> kv in found) TrueWork[kv.Key] = kv.Value;
            }
            lock (Gate)
            {
                armedMonitor = IntPtr.Zero;
                foreach (KeyValuePair<long, N.RECT> kv in found)
                {
                    N.RECT w = kv.Value;
                    if (w.Area > 0 && !N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref w, N.SPIF_SENDCHANGE))
                        App.Log("!! SPI_SETWORKAREA failed for " + w + " err=" + Marshal.GetLastWin32Error());
                }
            }
            Thread.Sleep(120);
        }

        // CacheGate only -- safe to call from anywhere, including while Gate is held.
        public static N.RECT TrueWorkOf(IntPtr hMon)
        {
            lock (CacheGate)
            {
                N.RECT r;
                if (TrueWork.TryGetValue(hMon.ToInt64(), out r)) return r;
            }
            return ComputeTrueWork(MonitorRect(hMon));
        }

        public static void ResetWorkAreasToDefault()
        {
            List<N.RECT> works = new List<N.RECT>();
            lock (CacheGate) { foreach (KeyValuePair<long, N.RECT> kv in TrueWork) works.Add(kv.Value); }
            lock (Gate)
            {
                armedMonitor = IntPtr.Zero;
                foreach (N.RECT w0 in works)
                {
                    N.RECT w = w0;
                    if (w.Area > 0) N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref w, N.SPIF_SENDCHANGE);
                }
            }
        }

        // ---- zones are DERIVED from (monitor, mode, split fraction) ---------------
        public static Mode ModeOf(IntPtr hMon)
        {
            string k = MonKey(hMon);
            Mode m;
            if (Cfg.Modes.TryGetValue(k, out m)) return m;
            return IsPrimary(hMon) ? Mode.Split : Mode.Single;   // the big screen splits, everything else is native
        }

        public static void SetMode(IntPtr hMon, Mode m)
        {
            string k = MonKey(hMon);
            Mode cur;
            bool changed = !Cfg.Modes.TryGetValue(k, out cur) || cur != m;
            Cfg.Modes[k] = m;
            if (!changed) return;

            InvalidateCaches();
            RemapHomes(k, m);
            App.Log("MODE monitor " + k + "  ->  " + m.ToString().ToUpperInvariant());
            Config.Save();
            // NO overlay here. ZoneMax is invisible: the user must never see it do anything unless
            // they explicitly ask (Win+Alt+Z / tray). An automatic flash on Win+Up was shipped once
            // and immediately, correctly, hated.
        }

        // A mode flip changes which zone names exist on that monitor. Anything homed there must be
        // translated, not orphaned -- a stale "Left" home on a now-single monitor made autofix
        // useless for that window, and (worse) a stale "Full" made it fight the new split.
        static void RemapHomes(string monKey, Mode m)
        {
            lock (CacheGate)
            {
                List<long> keys = new List<long>(Homes.Keys);
                foreach (long h in keys)
                {
                    string v = Homes[h];
                    int p1 = v.IndexOf('|'); if (p1 <= 0) continue;
                    int p2 = v.IndexOf('|', p1 + 1); if (p2 <= p1) continue;
                    if (v.Substring(p1 + 1, p2 - p1 - 1) != monKey) continue;
                    if (m == Mode.Single) Homes[h] = v.Substring(0, p2 + 1) + "Full";
                    else Homes.Remove(h);      // Full -> split: let the window's rect pick its half
                }
            }
        }

        public static List<Zone> ZonesOf(IntPtr hMon)
        {
            long id = hMon.ToInt64();
            lock (CacheGate)
            {
                List<Zone> cached;
                if (ZoneCache.TryGetValue(id, out cached)) return cached;
            }

            N.RECT work = TrueWorkOf(hMon);
            string k = MonKey(hMon);
            List<Zone> list = new List<Zone>();

            if (ModeOf(hMon) == Mode.Single || work.Width < 700)
            {
                list.Add(NewZone("Full", work, k));
            }
            else
            {
                int w = (int)Math.Round(work.Width * Cfg.SplitFraction);
                if (w < 300) w = 300;
                if (w > work.Width - 300) w = work.Width - 300;
                list.Add(NewZone("Left", N.RECT.FromXYWH(work.Left, work.Top, w, work.Height), k));
                list.Add(NewZone("Right", N.RECT.FromXYWH(work.Left + w, work.Top, work.Width - w, work.Height), k));
            }
            lock (CacheGate) { if (!ZoneCache.ContainsKey(id)) ZoneCache[id] = list; }
            return list;
        }

        static Zone NewZone(string name, N.RECT r, string monKey)
        {
            Zone z = new Zone(); z.Name = name; z.Rect = r; z.MonKey = monKey; return z;
        }

        public static List<Zone> AllZones()
        {
            List<Zone> all = new List<Zone>();
            foreach (IntPtr h in Monitors()) all.AddRange(ZonesOf(h));
            return all;
        }

        // ---- which zone does a window belong to ----------------------------------
        static Zone Pick(List<Zone> zs, N.RECT r)
        {
            Zone best = null; long bestA = 0;
            foreach (Zone z in zs)
            {
                long a = N.RECT.Intersect(z.Rect, r).Area;
                if (a > bestA) { bestA = a; best = z; }
            }
            if (best == null) return null;
            // SLIVER GUARD: a maximized window overhangs its screen by 8px, so one maximized on the
            // laptop pokes 7px into the ultrawide. An overlap has to actually mean something.
            if (r.Area > 0 && bestA * 5 < r.Area) return null;
            return best;
        }

        // NB: a null zone must CLEAR the memory, not be skipped -- otherwise a window dragged onto
        // another screen keeps a stale "home" and gets hauled back the moment you maximize it.
        // The owning PID is stored too: Windows RECYCLES hwnd values, and without it a brand-new
        // window could inherit a dead window's home and get yanked into a zone it never chose.
        public static void RememberHome(IntPtr h, Zone z)
        {
            string val = null;
            if (z != null)
            {
                uint pid; N.GetWindowThreadProcessId(h, out pid);
                val = pid.ToString(CultureInfo.InvariantCulture) + "|" + z.MonKey + "|" + z.Name;
            }
            string old;
            lock (CacheGate)
            {
                Homes.TryGetValue(h.ToInt64(), out old);
                if (val == null) Homes.Remove(h.ToInt64()); else Homes[h.ToInt64()] = val;
            }
            // Every home REWRITE gets a log line -- the one transition the log couldn't show
            // when autofix hauled a window into a zone the user never chose. Same-value writes
            // (the overwhelming majority) stay silent. Logged outside the lock: TitleOf can
            // block on the target window's thread.
            if (Cfg.Verbose && old != val && (old != null || val != null))
                App.Log("HOME \"" + N.TitleOf(h) + "\"" + Tag(h) + "  ["
                        + HomeZoneName(old) + "] -> [" + HomeZoneName(val) + "]");
        }

        static string HomeZoneName(string v)
        {
            if (v == null) return "none";
            int p = v.LastIndexOf('|');
            return p < 0 ? v : v.Substring(p + 1);
        }

        static Zone HomeOf(IntPtr h, IntPtr hMon, List<Zone> zs)
        {
            string v;
            lock (CacheGate) { if (!Homes.TryGetValue(h.ToInt64(), out v)) return null; }
            int p1 = v.IndexOf('|'); if (p1 <= 0) return null;
            int p2 = v.IndexOf('|', p1 + 1); if (p2 <= p1) return null;

            uint owner;
            if (!uint.TryParse(v.Substring(0, p1), out owner)) return null;
            uint pid; N.GetWindowThreadProcessId(h, out pid);
            if (pid != owner)                                        // recycled hwnd -- not the same window
            {
                lock (CacheGate) { Homes.Remove(h.ToInt64()); }
                return null;
            }
            if (v.Substring(p1 + 1, p2 - p1 - 1) != MonKey(hMon)) return null;   // changed monitors: stale
            string zn = v.Substring(p2 + 1);
            foreach (Zone z in zs) if (z.Name == zn) return z;       // mode changed? then "Left" no longer exists
            return null;
        }

        // Windows recycles hwnds, so dead entries aren't just garbage -- they're landmines.
        // Called from the watchdog every ~30s.
        public static void PruneDeadWindows()
        {
            lock (CacheGate)
            {
                List<long> dead = null;
                foreach (long k in Homes.Keys)
                    if (!N.IsWindow((IntPtr)k)) { if (dead == null) dead = new List<long>(); dead.Add(k); }
                if (dead != null) foreach (long k in dead) Homes.Remove(k);

                dead = null;
                foreach (long k in Suppress.Keys)
                    if (!N.IsWindow((IntPtr)k)) { if (dead == null) dead = new List<long>(); dead.Add(k); }
                if (dead != null) foreach (long k in dead) Suppress.Remove(k);
            }
        }

        // NOTE: we never ask GetWindowPlacement().rcNormalPosition where a maximized window came
        // from. That field is reported in WORKSPACE coordinates -- relative to the primary monitor's
        // work-area ORIGIN -- and ZoneMax moves that origin. Arm the right zone and the origin becomes
        // (1720,0), so a window truly at x=2000 reports back as x=280, i.e. the LEFT zone. That single
        // footnote is why right-hand windows used to maximize on the left.
        public static Zone ZoneOf(IntPtr hwnd)
        {
            IntPtr hMon = N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST);
            List<Zone> zs = ZonesOf(hMon);
            N.RECT r;
            if (!N.GetWindowRect(hwnd, out r)) return null;

            if (!N.IsZoomed(hwnd))
            {
                Zone z = Pick(zs, r);          // screen coords -- always trustworthy
                // ...but the STATE may be transient: a window mid-explosion sits at its RESTORE
                // bounds for a few ms (Chrome: restore -> re-maximize; Slack: restore, full stop),
                // and writing home from that rect teleports it to whatever zone the restore bounds
                // happen to overlap -- autofix then "corrects" it into a zone the user never chose.
                // Passive observation only rewrites home once the window has been unzoomed a while;
                // deliberate placements (keys, drops) write home explicitly and skip this gate.
                if (RecentlyZoomed(hwnd, 2500))
                {
                    if (Cfg.Verbose)
                        App.Log("HOME skip (unzoomed <2.5s ago) \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd)
                                + " at " + r + " saw [" + (z == null ? "none" : z.Name) + "]");
                }
                else RememberHome(hwnd, z);
                return z;
            }
            NoteZoomed(hwnd);
            Zone home = HomeOf(hwnd, hMon, zs);
            if (home != null) return home;
            Zone guess = Pick(zs, r);
            // Restart/mode-change amnesia: Homes is in-memory, and a monitor mode change kills
            // the old zone names -- either way a zoomed window can be home-less. If it is sitting
            // EXACTLY in a zone, that IS its home: learn it. Without this, the window's first
            // explosion is "corrected" into whichever zone overlaps the whole-monitor rect most
            // (always the bigger one) -- observed as a window teleporting [Left] -> [Right]
            // after a screenshot.
            if (guess != null && r.NearlyEquals(guess.Rect.Inflate(N.Border()), 12))
                RememberHome(hwnd, guess);
            return guess;
        }

        // Homes die with the process. Windows already truly maximized inside a zone when we start
        // are re-adopted here (ZoneOf's learn-on-sight does the work), so their first explosion
        // gets corrected into the zone they were actually in -- not a whole-monitor-overlap guess.
        public static void AdoptExistingWindows()
        {
            int adopted = 0;
            N.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (N.IsZoomed(h) && Manageable(h) && ZoneOf(h) != null) adopted++;
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            App.Log("adopted " + adopted + " pre-existing zoomed window(s)");
        }

        public static bool Manageable(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !N.IsWindow(hwnd) || !N.IsWindowVisible(hwnd)) return false;
            int style = N.GetWindowLong(hwnd, N.GWL_STYLE);
            if ((style & N.WS_CHILD) != 0) return false;
            if ((style & N.WS_THICKFRAME) == 0) return false;
            if ((N.GetWindowLong(hwnd, N.GWL_EXSTYLE) & N.WS_EX_TOOLWINDOW) != 0) return false;
            if (N.GetWindowTextLength(hwnd) == 0) return false;   // cheaper than fetching the text
            uint pid; N.GetWindowThreadProcessId(hwnd, out pid);
            return pid != OwnPid;
        }

        static void Hold(IntPtr h, int ms) { lock (CacheGate) { Suppress[h.ToInt64()] = Environment.TickCount + ms; } }
        static bool Held(IntPtr h)
        {
            lock (CacheGate)
            {
                int until;
                // subtraction, not comparison: TickCount wraps negative at ~25 days of uptime
                return Suppress.TryGetValue(h.ToInt64(), out until) && (until - Environment.TickCount) > 0;
            }
        }

        // Poll a condition instead of sleeping blind: typical case exits in 5-15ms where the old
        // fixed sleeps burned the full budget every time -- on the thread that services the hooks.
        static void WaitUntil(Func<bool> cond, int capMs)
        {
            int deadline = Environment.TickCount + capMs;
            while ((deadline - Environment.TickCount) > 0)
            {
                if (cond()) return;
                Thread.Sleep(5);
            }
        }

        // ================= THE ENGINE =================
        // Runs entirely under Gate: the watchdog's Disarm used to be able to restore the work area
        // BETWEEN our shrink and the SW_MAXIMIZE (it fired mid-sleep), maximizing the window across
        // the whole monitor -- with Hold() then suppressing the very autofix that would repair it.
        public static void MaximizeIntoRect(IntPtr hwnd, N.RECT target, string why)
        {
            if (target.Area <= 0) { App.Log("!! refusing to maximize into empty rect " + target); return; }
            IntPtr hMon = N.MonitorFromRect(ref target, N.MONITOR_DEFAULTTONEAREST);

            for (int attempt = 1; ; attempt++)
            {
            Hold(hwnd, 900);
            bool shrunkOk = false;
            lock (Gate)
            {
                armedMonitor = IntPtr.Zero;          // supersede any pending arm; Disarm now no-ops
                N.RECT trueWork = TrueWorkOf(hMon);
                try
                {
                    N.RECT shrunk = target;
                    shrunkOk = N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref shrunk, 0);   // 0 = no broadcast
                    if (!shrunkOk)
                    {
                        App.Log("!! SPI_SETWORKAREA shrink to " + target + " FAILED err="
                                + Marshal.GetLastWin32Error() + " -- not maximizing");
                    }
                    else
                    {
                        if (N.IsZoomed(hwnd) || N.IsIconic(hwnd))
                        {
                            N.ShowWindow(hwnd, N.SW_RESTORE);
                            WaitUntil(delegate { return !N.IsZoomed(hwnd) && !N.IsIconic(hwnd); }, 60);
                        }
                        if (!N.SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                                            N.SWP_NOZORDER | N.SWP_NOACTIVATE))
                            App.Log("!! SetWindowPos failed err=" + Marshal.GetLastWin32Error() + " (elevated window?)");
                        Thread.Sleep(10);
                        N.ShowWindow(hwnd, N.SW_MAXIMIZE);
                        WaitUntil(delegate { return N.IsZoomed(hwnd); }, 80);
                        Thread.Sleep(15);            // let the window finish reading the shrunken work area
                    }
                }
                finally
                {
                    if (shrunkOk && trueWork.Area > 0)
                    {
                        N.RECT back = trueWork;
                        if (!N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref back, 0))
                        {
                            App.Log("!! work-area RESTORE FAILED err=" + Marshal.GetLastWin32Error() + " -- retrying");
                            Thread.Sleep(30);
                            N.RECT back2 = trueWork;
                            if (!N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref back2, 0))
                                App.Log("!!!! WORK AREA STUCK at " + target + " -- tray > 'Reset work area' will fix it");
                        }
                    }
                }
            }
            N.RECT got; N.GetWindowRect(hwnd, out got);
            App.Log("ACT  " + why + "   \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd)
                    + (attempt > 1 ? "  (attempt " + attempt + ")" : ""));
            App.Log("       target=" + target + "  ->  RESULT " + got + " zoomed=" + N.IsZoomed(hwnd));

            // A queued re-evaluation inside the target app can fire in the gap between our
            // work-area restore and the window settling, undoing the fix we JUST made (snip
            // overlay storms leave several queued: observed AUTOFIX target 1376px -> RESULT
            // 3456px). One retry consumes it -- otherwise every mass explosion healed in two
            // or three visible bounces instead of one.
            if (got.NearlyEquals(target.Inflate(N.Border()), 12)) return;   // landed where asked
            if (attempt >= 2) return;                                       // twice is enough; autofix owns the rest
            if (Dragging || !N.IsZoomed(hwnd) || !Manageable(hwnd)) return; // restored itself/gone: the healer's job
            Thread.Sleep(40);       // let the app's queued re-evaluation land before countering it
            }
        }

        public static void RestoreWindow(IntPtr hwnd)
        {
            // 4s, not 600ms: this restore is DELIBERATE (Win+Down), and the stray-restore healer
            // watches a 4s window -- a shorter hold would let it re-maximize what the user just
            // explicitly un-maximized.
            Hold(hwnd, 4000);
            N.ShowWindow(hwnd, N.SW_RESTORE);
            App.Log("ACT  restore   \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd));
        }

        // ---- arming: momentary, only around a click ------------------------------
        // Persistent (focus-based) arming was an architecture mistake: the work area is a GLOBAL
        // per-monitor setting, so while the left zone was armed, ANY right-zone window that
        // re-evaluated its maximized bounds (Chrome does this on activation) re-maximized itself
        // into the LEFT zone. Now the arm lives for ~900ms around your click and no longer.
        public static void ArmFor(IntPtr hwnd, Zone z) { ArmFor(hwnd, z, 900); }

        // Best effort BY DESIGN: called from the mouse hook, so it must never wait on the engine.
        // If Gate is busy the engine is mid-placement anyway and autofix will clean up after it.
        public static void ArmFor(IntPtr hwnd, Zone z, int ttlMs) { ArmFor(hwnd, z, ttlMs, false); }

        public static void ArmFor(IntPtr hwnd, Zone z, int ttlMs, bool ignoreShift)
        {
            if (!Cfg.Seamless || Dragging || z == null) return;
            // SHIFT = give me the whole monitor -- except when Shift is part of the combo that got
            // us here (Win+Shift+S), where it means nothing of the sort.
            if (!ignoreShift && (N.GetAsyncKeyState(N.VK_SHIFT) & 0x8000) != 0) return;
            if (z.Rect.Area <= 0) return;

            N.RECT zr = z.Rect;
            IntPtr mon = N.MonitorFromRect(ref zr, N.MONITOR_DEFAULTTONEAREST);
            if (z.Rect.NearlyEquals(TrueWorkOf(mon), 2)) return;           // zone IS the work area: nothing to arm

            if (!Monitor.TryEnter(Gate, 25))
            {
                // The one silent failure that turns into a user-visible explosion: the click goes
                // through unarmed and Chrome re-maximizes to the full monitor. Log it -- no titles
                // here (this can run on the hook thread; GetWindowText is a cross-process call).
                App.Log("     ARM MISSED [" + z.Name + "] -- engine busy; autofix will catch it");
                return;
            }
            try
            {
                // Rolling-arm fast path: already armed to exactly this zone -> just extend the TTL.
                // This is what makes per-KEYSTROKE arming affordable: one SPI call per typing
                // burst, not one per key, and no log spam.
                if (armedMonitor == mon && armedRect.NearlyEquals(z.Rect, 0))
                {
                    armedUntil = Environment.TickCount + ttlMs;
                    return;
                }
                if (armedMonitor != IntPtr.Zero && armedMonitor != mon) DisarmLocked();
                N.RECT eff = z.Rect;
                N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref eff, 0);
                armedMonitor = mon;
                armedRect = z.Rect;
                armedUntil = Environment.TickCount + ttlMs;
                if (Cfg.Verbose) App.Log("     ARM  workarea -> [" + z.Name + "] " + eff + " (" + ttlMs + "ms)");
            }
            finally { Monitor.Exit(Gate); }
        }

        // Expiry check and restore live in ONE lock acquisition. Split across two, the watchdog
        // could confirm expiry, lose the CPU to a fresh click's ArmFor, then blindly disarm the
        // brand-new arm it never saw.
        public static void DisarmIfDue()
        {
            lock (Gate)
            {
                if (armedMonitor == IntPtr.Zero) return;
                if ((armedUntil - Environment.TickCount) > 0) return;
                DisarmLocked();
            }
        }

        public static void Disarm()
        {
            lock (Gate) { DisarmLocked(); }
        }

        static void DisarmLocked()   // Gate must be held
        {
            if (armedMonitor == IntPtr.Zero) return;
            N.RECT back = TrueWorkOf(armedMonitor);
            if (back.Area > 0) N.SystemParametersInfo(N.SPI_SETWORKAREA, 0, ref back, 0);
            armedMonitor = IntPtr.Zero;
            if (Cfg.Verbose) App.Log("     DISARM workarea -> " + back);
        }

        // ---- mid-drag native-Snap suppression --------------------------------------
        // Snap must be ON globally ("drag a maximized window to un-maximize it" IS Snap), but once
        // a drag is under way and the window has already restored, native edge-snap is pure
        // competition: drop at the left screen edge and Windows half-snaps the window -- preview
        // overlay and all -- an instant before HandleDrop maximizes it into the zone. A visible
        // fight on every edge drop. So: drag running + window restored -> Snap OFF for the rest of
        // the drag; drop (or drag end seen by the watchdog) -> Snap back ON. Startup and the
        // watchdog both re-enable it, so a crash mid-drag cannot leave Snap off.
        static volatile bool snapSuppressed = false;
        public static bool SnapSuppressed { get { return snapSuppressed; } }

        public static void SuppressSnapForDrag(IntPtr hwnd)
        {
            if (snapSuppressed || !Dragging) return;
            if (N.IsZoomed(hwnd)) return;    // restore hasn't happened yet -- Snap still has a job to do
            // fWinIni = 0, NOT SPIF_SENDCHANGE: the broadcast makes every zoned Chrome re-evaluate
            // its maximized bounds against the (full) work area -- i.e. every drag would visibly
            // explode the OTHER maximized windows. The toggle takes effect fine without it (verified
            // with SPI_GETWINARRANGING).
            N.SpiPtr(N.SPI_SETWINARRANGING, 0, IntPtr.Zero, 0);
            snapSuppressed = true;
            if (Cfg.Verbose) App.Log("     SNAP off (mid-drag)");
        }

        public static void RestoreSnap()
        {
            if (!snapSuppressed) return;
            snapSuppressed = false;
            N.SpiPtr(N.SPI_SETWINARRANGING, 1, IntPtr.Zero, 0);   // silent: see SuppressSnapForDrag
            if (!N.WinArrangingOn()) N.EnsureWinArranging();      // didn't stick -- do it loud
            if (Cfg.Verbose) App.Log("     SNAP back on");
        }

        // ---- drag & drop into a zone ---------------------------------------------
        // Is (x,y) off the end of the desktop -- i.e. is this a real OUTER edge of the screen?
        // The border between the ultrawide and the laptop is NOT an edge: you must be able to drag
        // straight across it without the window snapping into a zone on the way past.
        static bool OuterEdge(int x, int y)
        {
            N.POINT p; p.X = x; p.Y = y;
            return N.MonitorFromPoint(p, N.MONITOR_DEFAULTTONULL) == IntPtr.Zero;
        }

        const int EDGE_BAND = 18;   // how close to the edge you have to let go

        public static void HandleDrop(IntPtr hwnd, N.POINT cursor, N.RECT startRect)
        {
            if (!Cfg.DragZones || !Manageable(hwnd)) return;
            if ((N.GetAsyncKeyState(N.VK_SHIFT) & 0x8000) != 0) return;    // SHIFT = leave it exactly where I dropped it

            // A drop only counts if the WINDOW moved. The cursor drifting 7px during an ordinary
            // click on a title bar near the screen edge is not a drag, and acting on it maximizes
            // windows the user never dragged.
            N.RECT now;
            if (N.GetWindowRect(hwnd, out now) && now.NearlyEquals(startRect, 4))
            {
                if (Cfg.Verbose) App.Log("DROP ignored -- window never moved (jitter click)");
                return;
            }

            IntPtr hMon = N.MonitorFromPoint(cursor, N.MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return;
            N.RECT mon = MonitorRect(hMon);
            List<Zone> zs = ZonesOf(hMon);
            if (zs.Count == 0) return;

            Zone target = null;
            string why = null;

            if (cursor.Y <= mon.Top + EDGE_BAND && OuterEdge(cursor.X, mon.Top - 8))
            {
                foreach (Zone z in zs)                                   // the zone you dropped it ON
                    if (cursor.X >= z.Rect.Left && cursor.X < z.Rect.Right) target = z;
                if (target == null) target = zs[0];
                why = "DROP top edge";
            }
            else if (cursor.X <= mon.Left + EDGE_BAND && OuterEdge(mon.Left - 8, cursor.Y))
            {
                target = zs[0];
                why = "DROP left edge";
            }
            else if (cursor.X >= mon.Right - EDGE_BAND && OuterEdge(mon.Right + 8, cursor.Y))
            {
                target = zs[zs.Count - 1];
                why = "DROP right edge";
            }

            if (target == null)
            {
                Zone z = ZoneOf(hwnd);                                    // free move: just note where it lives now
                RememberHome(hwnd, z);
                if (Cfg.Verbose) App.Log("DROP free move -> [" + (z == null ? "none" : z.Name) + "]  " + Describe(hwnd));
                return;
            }

            RememberHome(hwnd, target);
            MaximizeIntoRect(hwnd, target.Rect, why + " -> [" + target.Name + "]");
        }

        // ---- safety net -----------------------------------------------------------
        // Deliberately narrow. It steps in for exactly two unambiguously-wrong states and nothing
        // else, so it can never fight a placement somebody made on purpose:
        //    (a) Windows maximized it across the WHOLE monitor, but its zone is smaller
        //    (b) it landed in a DIFFERENT zone of that monitor than its home
        // Set by the mouse hook on ANY button-down. Lets keyboard-time checks distinguish "the user
        // is mousing" from "the user is typing" -- Shift means something during a Shift+click, and
        // nothing at all during a Shift+Enter.
        public static volatile int LastClickTick = -1000000;

        // UI-thread only (OnLocationChange): when a managed window we KNOW was zoomed un-zooms
        // itself with no click, no drag and no Win+Down, that was the APP's doing (Slack/Electron
        // answers a bounds mismatch by RESTORING instead of re-maximizing like Chrome) -- and it
        // must be healed, because autofix only ever corrects zoomed windows. State transition, not
        // a time window: an idle zoomed window fires no events, so any freshness timer expires.
        static readonly Dictionary<IntPtr, bool> WasZoomed = new Dictionary<IntPtr, bool>();
        static readonly Dictionary<IntPtr, int> LastHealAt = new Dictionary<IntPtr, int>();

        // When was this window last SEEN zoomed. Time-based on purpose (unlike WasZoomed, where a
        // freshness timer was a bug): here a stale entry is the correct answer -- a window that has
        // been unzoomed for a while SHOULD get passive home updates again. Guards ZoneOf against
        // recording a mid-explosion restore transient as the window's chosen home.
        static readonly Dictionary<long, int> LastZoomedTick = new Dictionary<long, int>();

        static void NoteZoomed(IntPtr h)
        {
            lock (CacheGate)
            {
                if (LastZoomedTick.Count > 512) LastZoomedTick.Clear();
                LastZoomedTick[h.ToInt64()] = Environment.TickCount;
            }
        }

        static bool RecentlyZoomed(IntPtr h, int ms)
        {
            int t;
            lock (CacheGate) { if (!LastZoomedTick.TryGetValue(h.ToInt64(), out t)) return false; }
            return (Environment.TickCount - t) < ms;   // subtraction: TickCount wraps at ~25 days
        }

        public static void CorrectIfNeeded(IntPtr hwnd)
        {
            if (!Cfg.AutoFix) return;
            if (Dragging) return;                          // hands off: the window is under the cursor right now
            if (!N.IsZoomed(hwnd)) { MaybeHealStrayRestore(hwnd); return; }

            // Record BEFORE the Held bail: the only events an idle window ever fires happen while
            // our own placement hold is active -- recorded after it, WasZoomed stays false forever
            // and the stray-restore healer never triggers. Recording state is not correcting.
            if (WasZoomed.Count > 512) WasZoomed.Clear();           // crude leak cap; resets heal history only
            WasZoomed[hwnd] = true;
            NoteZoomed(hwnd);

            if (Held(hwnd)) return;
            if (!Manageable(hwnd)) return;

            // SHIFT = "leave it where I put it" -- but only when it accompanies a MOUSE action
            // (Shift+maximize, Shift+drop). Checked globally, it also fired while TYPING: every
            // Shift+Enter in Slack was an autofix outage.
            if ((N.GetAsyncKeyState(N.VK_SHIFT) & 0x8000) != 0
                && (Environment.TickCount - LastClickTick) < 2000) return;

            IntPtr hMon = N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST);
            List<Zone> zs = ZonesOf(hMon);
            Zone home = ZoneOf(hwnd);
            if (home == null) return;

            N.RECT cur;
            if (!N.GetWindowRect(hwnd, out cur)) return;
            int b = N.Border();

            // These two exits are the hot path (every LOCATIONCHANGE of a correctly-placed zoomed
            // window) -- the strings must not even be BUILT unless verbose is on.
            if (cur.NearlyEquals(home.Rect.Inflate(b), 12))
            {
                if (Cfg.Verbose) App.Log("MAX  correct: [" + home.Name + "] " + cur + " " + Tag(hwnd));
                return;
            }

            bool fullMonitor = cur.NearlyEquals(TrueWorkOf(hMon).Inflate(b), 16);
            bool wrongZone = false;
            foreach (Zone z in zs)
                if (z.Name != home.Name && cur.NearlyEquals(z.Rect.Inflate(b), 12)) wrongZone = true;

            if (!fullMonitor && !wrongZone)
            {
                if (Cfg.Verbose) App.Log("MAX  \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd) + " at " + cur + " -- deliberate, leaving it");
                return;
            }

            // Attribution: these explosions fire without any click (screenshot overlays, Alt+Tab,
            // taskbar activation...). Knowing what was foreground at that instant is the only way
            // to tell WHICH trigger from the log.
            IntPtr fgNow = N.GetForegroundWindow();
            App.Log("MAX  \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd) + " landed at " + cur
                    + (fullMonitor ? " (whole monitor)" : " (wrong zone)") + " -- home is [" + home.Name + "]"
                    + "  fg=\"" + N.TitleOf(fgNow) + "\"" + Tag(fgNow) + " (" + N.ClassOf(fgNow) + ")");
            // Hold BEFORE posting: LOCATIONCHANGE keeps firing while the fix sits in the queue,
            // and without the hold every one of those events would enqueue a duplicate fix.
            Hold(hwnd, 900);
            N.RECT fixRect = home.Rect;
            string fixWhy = "AUTOFIX -> [" + home.Name + "]";
            Post(delegate { MaximizeIntoRect(hwnd, fixRect, fixWhy); });
        }

        // The Electron counterpart of the whole-monitor explosion. Every guard below exists to
        // avoid fighting a restore the USER meant: recent click (restore button, taskbar,
        // double-click caption, drag), Win+Down (RestoreWindow holds the window), minimize
        // (Win+M/Win+D leaves it iconic), and F11/video fullscreen (rect == whole monitor).
        static void MaybeHealStrayRestore(IntPtr hwnd)
        {
            int now = Environment.TickCount;
            bool wasZoomed;
            if (!WasZoomed.TryGetValue(hwnd, out wasZoomed) || !wasZoomed) return;
            WasZoomed[hwnd] = false;                              // one shot per zoomed episode
            if (Dragging || Held(hwnd)) return;
            if ((now - LastClickTick) < 1500) return;             // a click was involved -- probably deliberate
            if (N.IsIconic(hwnd)) return;                         // minimized, not restored
            if (!Manageable(hwnd)) return;

            IntPtr hMon = N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST);
            Zone home = HomeOf(hwnd, hMon, ZonesOf(hMon));        // remembered home ONLY, no geometry guessing
            if (home == null)
            {
                if (Cfg.Verbose) App.Log("STRAY \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd) + " un-maximized itself but has no remembered home -- leaving it");
                return;
            }

            N.RECT cur;
            if (!N.GetWindowRect(hwnd, out cur)) return;
            if (cur.NearlyEquals(MonitorRect(hMon), 4)) return;   // borderless fullscreen (F11), not a stray

            int ht;
            if (LastHealAt.TryGetValue(hwnd, out ht) && (now - ht) < 10000)
            {
                App.Log("STRAY \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd) + " un-maximized itself AGAIN within 10s -- it wants this, leaving it");
                return;
            }
            if (LastHealAt.Count > 512) LastHealAt.Clear();
            LastHealAt[hwnd] = now;

            App.Log("STRAY \"" + N.TitleOf(hwnd) + "\"" + Tag(hwnd) + " un-maximized itself (no click/drag) at " + cur
                    + " -- healing into [" + home.Name + "]");
            N.RECT r = home.Rect;
            string why = "HEAL -> [" + home.Name + "]";
            Post(delegate { MaximizeIntoRect(hwnd, r, why); });
        }

        // ---- the keys -------------------------------------------------------------
        public static void SnapKey(IntPtr hwnd, int vk)
        {
            if (!Manageable(hwnd)) return;
            IntPtr hMon = N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST);

            string key = vk == N.VK_LEFT ? "Win+Left" : vk == N.VK_RIGHT ? "Win+Right"
                       : vk == N.VK_UP ? "Win+Up" : "Win+Down";
            if (Cfg.Verbose) App.Log("KEY  " + key + "  " + Describe(hwnd) + "  monitor=" + MonKey(hMon) + " mode=" + ModeOf(hMon));

            if (vk == N.VK_DOWN)
            {
                if (N.IsZoomed(hwnd)) RestoreWindow(hwnd);
                else { Hold(hwnd, 600); N.ShowWindow(hwnd, N.SW_MINIMIZE); }
                return;
            }

            if (vk == N.VK_UP)
            {
                // collapse this monitor to a single zone, and fill it
                SetMode(hMon, Mode.Single);
                Zone z = ZonesOf(hMon)[0];
                RememberHome(hwnd, z);
                MaximizeIntoRect(hwnd, z.Rect, key + " -> single zone");
                return;
            }

            // Left / Right: split the monitor in two (activating the split if it wasn't), then
            // send the window to that zone and truly maximize it there.
            SetMode(hMon, Mode.Split);
            List<Zone> zs = ZonesOf(hMon);
            Zone target = (vk == N.VK_LEFT) ? zs[0] : zs[zs.Count - 1];

            // Already truly filling that zone? Continue onto the neighbouring monitor, entering
            // from the side you're travelling -- exactly what native Snap does.
            N.RECT curRect;
            if (N.IsZoomed(hwnd) && N.GetWindowRect(hwnd, out curRect)
                && curRect.NearlyEquals(target.Rect.Inflate(N.Border()), 12))
            {
                List<IntPtr> mons = Monitors();
                int i = mons.IndexOf(hMon);
                int next = i + (vk == N.VK_LEFT ? -1 : 1);
                if (next < 0 || next >= mons.Count) return;        // edge of the desktop: nowhere to go
                List<Zone> nz = ZonesOf(mons[next]);
                Zone t2 = (vk == N.VK_LEFT) ? nz[nz.Count - 1] : nz[0];
                RememberHome(hwnd, t2);
                MaximizeIntoRect(hwnd, t2.Rect, key + " -> continue to [" + t2.Name + "] on " + MonKey(mons[next]));
                return;
            }

            RememberHome(hwnd, target);
            MaximizeIntoRect(hwnd, target.Rect, key + " -> [" + target.Name + "]");
        }

        public static void MoveToAdjacentMonitor(IntPtr hwnd, int dir)
        {
            if (!Manageable(hwnd)) return;
            List<IntPtr> mons = Monitors();
            if (mons.Count < 2) return;

            IntPtr cur = N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST);
            int i = 0;
            for (int n = 0; n < mons.Count; n++) if (mons[n] == cur) i = n;
            i += dir;
            if (i < 0) i = mons.Count - 1;
            if (i >= mons.Count) i = 0;

            // enter from the side you're travelling: moving right lands in the leftmost zone
            List<Zone> zs = ZonesOf(mons[i]);
            Zone z = (dir > 0) ? zs[0] : zs[zs.Count - 1];
            RememberHome(hwnd, z);
            MaximizeIntoRect(hwnd, z.Rect, "move to monitor " + MonKey(mons[i]) + " -> [" + z.Name + "]");
        }
    }

    // ---------------------------------------------------------------------------- overlay
    internal class ZoneOverlay : Form
    {
        readonly string caption;

        public ZoneOverlay(Zone z, string modeLabel)
        {
            caption = z.Name + "\n" + z.Rect.Width + " x " + z.Rect.Height + "\n" + modeLabel;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(z.Rect.Left, z.Rect.Top, z.Rect.Width, z.Rect.Height);
            BackColor = Color.White;
            Opacity = 0.55;
            TopMost = true;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;  // LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 244, 248, 255)))
                g.FillRectangle(fill, 0, 0, Width, Height);
            using (Pen p = new Pen(Color.FromArgb(255, 37, 99, 235), 10))
                g.DrawRectangle(p, 5, 5, Width - 10, Height - 10);
            using (Font f = new Font("Segoe UI", 30f, FontStyle.Bold))
            using (SolidBrush t = new SolidBrush(Color.FromArgb(255, 17, 24, 39)))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(caption, f, t, new RectangleF(0, 0, Width, Height), sf);
            }
        }

        public static void Flash(int ms)
        {
            List<ZoneOverlay> shown = new List<ZoneOverlay>();
            foreach (IntPtr hMon in Engine.Monitors()) AddOverlays(hMon, shown);
            CloseAfter(shown, ms);
        }

        static void AddOverlays(IntPtr hMon, List<ZoneOverlay> shown)
        {
            string label = Engine.ModeOf(hMon) == Mode.Single ? "single zone" : "split";
            foreach (Zone z in Engine.ZonesOf(hMon))
            {
                ZoneOverlay o = new ZoneOverlay(z, label);
                o.Show();
                shown.Add(o);
            }
        }

        static void CloseAfter(List<ZoneOverlay> shown, int ms)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = ms;
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                foreach (ZoneOverlay o in shown) { try { o.Close(); o.Dispose(); } catch { } }
            };
            t.Start();
        }
    }

    // ---------------------------------------------------------------------------- tray
    internal class TrayForm : Form
    {
        NotifyIcon tray;
        ToolStripMenuItem miAutoFix, miSeamless, miSnapKeys, miDragZones, miVerbose, miStartup, miSplitRoot;
        IntPtr hookLoc = IntPtr.Zero, hookFg = IntPtr.Zero, hookMouse = IntPtr.Zero, hookKey = IntPtr.Zero;
        N.WinEventProc procLoc, procFg;      // MUST stay referenced or the GC eats them
        N.HookProc procMouse, procKey;
        System.Threading.Timer watchdog;
        volatile int lastHookEvent = Environment.TickCount;   // heartbeat: any LL hook callback

        // Windows UNINSTALLS a low-level hook silently if its thread ever takes longer than
        // LowLevelHooksTimeout (~300ms) to respond -- no error, no event, Win+Arrow just becomes
        // native Snap again (observed in the wild: hooks alive at startup, dead two hours later).
        // The install lives in a method so the watchdog can put them back.
        void InstallLLHooks(bool reinstall)
        {
            if (reinstall)
            {
                try { if (hookMouse != IntPtr.Zero) N.UnhookWindowsHookEx(hookMouse); } catch { }
                try { if (hookKey != IntPtr.Zero) N.UnhookWindowsHookEx(hookKey); } catch { }
            }
            procMouse = OnMouse;
            hookMouse = N.SetWindowsHookEx(N.WH_MOUSE_LL, procMouse, N.GetModuleHandle(null), 0);

            // The shell will not surrender Win+Arrow to RegisterHotKey (error 1409), so we take it
            // one level lower -- a low-level keyboard hook sees the key before the shell does.
            procKey = OnKey;
            hookKey = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, procKey, N.GetModuleHandle(null), 0);

            lastHookEvent = Environment.TickCount;
            if (reinstall)
                App.Log("!! low-level hooks were DEAD (Windows timed one out) -- reinstalled: mouse="
                        + (hookMouse != IntPtr.Zero) + " keyboard=" + (hookKey != IntPtr.Zero));
            else
                App.Log(hookKey != IntPtr.Zero ? "keyboard hook installed -- Win+Arrow is ours" : "KEYBOARD HOOK FAILED");
        }

        public TrayForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(1, 1);

            BuildTray();
            LoadEverything();
            Engine.AdoptExistingWindows();

            procLoc = OnLocationChange;
            hookLoc = N.SetWinEventHook(N.EVENT_OBJECT_LOCATIONCHANGE, N.EVENT_OBJECT_LOCATIONCHANGE,
                                        IntPtr.Zero, procLoc, 0, 0, N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
            procFg = OnForeground;
            hookFg = N.SetWinEventHook(N.EVENT_SYSTEM_FOREGROUND, N.EVENT_SYSTEM_FOREGROUND,
                                       IntPtr.Zero, procFg, 0, 0, N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
            InstallLLHooks(false);

            // Deliberately NOT a WinForms timer. WM_TIMER is the lowest-priority message there is, and
            // during a drag the mouse hook floods this very thread -- the tick was landing 2 SECONDS late,
            // so the work area stayed shrunk for the whole drag. A thread-pool timer cannot be starved.
            int dragUpTicks = 0, tick = 0;
            watchdog = new System.Threading.Timer(delegate
            {
                try
                {
                    // If the button came up somewhere we never saw it (Esc, Alt+Tab), do not stay
                    // stuck -- but require TWO consecutive ticks. The physical release is visible to
                    // GetAsyncKeyState instantly, while the hook's WM_LBUTTONUP may still be queued
                    // behind a busy pump; clearing Dragging on the first tick stole that race and
                    // silently ate the drop.
                    if (Engine.Dragging && (N.GetAsyncKeyState(N.VK_LBUTTON) & 0x8000) == 0)
                    {
                        dragUpTicks++;
                        if (dragUpTicks >= 2) { Engine.Dragging = false; dragUpTicks = 0; }
                    }
                    else dragUpTicks = 0;

                    // Backstop for drags that end without a WM_LBUTTONUP we saw (Esc, Alt+Tab):
                    // never leave native Snap suppressed once no drag is running.
                    if (!Engine.Dragging) Engine.RestoreSnap();

                    Engine.DisarmIfDue();
                    if (++tick % 150 == 0) Engine.PruneDeadWindows();   // every ~30s

                    // Dead-hook detector: the system saw input in the last 2s, but no hook callback
                    // has fired for 8s -> Windows killed our hooks. Put them back (from the UI
                    // thread -- LL hooks are dispatched via their installer's message pump).
                    if (tick % 10 == 0)
                    {
                        N.LASTINPUTINFO li = new N.LASTINPUTINFO();
                        li.cbSize = Marshal.SizeOf(typeof(N.LASTINPUTINFO));
                        int now = Environment.TickCount;
                        if (N.GetLastInputInfo(ref li)
                            && (now - (int)li.dwTime) < 2000
                            && (now - lastHookEvent) > 8000)
                        {
                            lastHookEvent = now;    // don't queue a storm of reinstalls
                            try { BeginInvoke(new Action(delegate { InstallLLHooks(true); })); } catch { }
                        }
                    }
                }
                catch { }
            }, null, 200, 200);

            SystemEvents.DisplaySettingsChanged += delegate
            {
                App.Log("display settings changed -- recapturing");
                // On the worker: the recapture broadcasts WM_SETTINGCHANGE to every window and
                // sleeps -- running that on the pump thread is a textbook way to get the LL hooks
                // timed out, and display changes happen a lot on this machine.
                Engine.Post(delegate
                {
                    Engine.Disarm();
                    Engine.CaptureTrueWorkAreas();
                    try { BeginInvoke(new Action(RefreshTray)); } catch { }
                });
            };
            Application.ApplicationExit += delegate { Cleanup(); };
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        // Diagnostic tap: WM_SETTINGCHANGE / WM_DISPLAYCHANGE broadcasts are what make every
        // maximized Chrome re-evaluate its bounds at once (the "both windows exploded in the same
        // second" signature). This window receives the same broadcasts, so the log can name the
        // culprit -- including ZoneMax itself, whose CaptureTrueWorkAreas broadcasts at startup.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x001A)        // WM_SETTINGCHANGE
            {
                string area = null;
                try { if (m.LParam != IntPtr.Zero) area = Marshal.PtrToStringUni(m.LParam); } catch { }
                App.Log("BCAST WM_SETTINGCHANGE wParam=0x" + m.WParam.ToInt64().ToString("X")
                        + (string.IsNullOrEmpty(area) ? "" : " \"" + area + "\""));
            }
            else if (m.Msg == 0x007E)   // WM_DISPLAYCHANGE
            {
                App.Log("BCAST WM_DISPLAYCHANGE");
            }
            base.WndProc(ref m);
        }

        void LoadEverything()
        {
            Engine.Cfg = Config.Load();
            Engine.CaptureTrueWorkAreas();
            Config.Save();          // always leave a readable config on disk

            // Sync the Run key to the CONFIG, not the other way round -- blindly re-enabling made
            // the "Start with Windows" checkbox a placebo you could never actually turn off.
            if (Engine.Cfg.Startup != App.IsStartupEnabled()) App.SetStartup(Engine.Cfg.Startup);

            RefreshTray();

            App.Log(N.EnsureWinArranging()
                    ? "windows snap is ON -- maximized windows can be dragged off their zone"
                    : "!! WINDOWS SNAP IS OFF -- maximized windows CANNOT be dragged. Could not turn it back on.");

            App.Log("options: autofix=" + Engine.Cfg.AutoFix + " seamless=" + Engine.Cfg.Seamless
                    + " snapkeys=" + Engine.Cfg.SnapKeys + " dragzones=" + Engine.Cfg.DragZones
                    + " split=" + Engine.Cfg.SplitFraction.ToString("0.##", CultureInfo.InvariantCulture));
            foreach (IntPtr hMon in Engine.Monitors())
            {
                App.Log("  monitor " + Engine.MonKey(hMon) + "  mode=" + Engine.ModeOf(hMon).ToString().ToUpperInvariant());
                foreach (Zone z in Engine.ZonesOf(hMon)) App.Log("      zone [" + z.Name + "] " + z.Rect);
            }
        }

        void RefreshTray()
        {
            miAutoFix.Checked = Engine.Cfg.AutoFix;
            miSeamless.Checked = Engine.Cfg.Seamless;
            miSnapKeys.Checked = Engine.Cfg.SnapKeys;
            miDragZones.Checked = Engine.Cfg.DragZones;
            miVerbose.Checked = Engine.Cfg.Verbose;
            miStartup.Checked = App.IsStartupEnabled();

            StringBuilder tip = new StringBuilder("ZoneMax");
            foreach (IntPtr hMon in Engine.Monitors())
            {
                N.RECT m = Engine.MonitorRect(hMon);
                tip.Append("  |  " + m.Width + "px: " + (Engine.ModeOf(hMon) == Mode.Split ? "split" : "single"));
            }
            string s = tip.ToString();
            tray.Text = s.Length > 62 ? s.Substring(0, 62) : s;
            RebuildSplitMenu();
        }

        // ---- tray ----
        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            miSplitRoot = new ToolStripMenuItem("Split ratio");
            menu.Items.Add(miSplitRoot);

            menu.Items.Add(new ToolStripSeparator());

            miSnapKeys = new ToolStripMenuItem("Win+Arrow drives the zones");
            miSnapKeys.CheckOnClick = true;
            miSnapKeys.Click += delegate { Engine.Cfg.SnapKeys = miSnapKeys.Checked; Config.Save(); };
            menu.Items.Add(miSnapKeys);

            miDragZones = new ToolStripMenuItem("Drag to a screen edge to fill a zone");
            miDragZones.CheckOnClick = true;
            miDragZones.Click += delegate { Engine.Cfg.DragZones = miDragZones.Checked; Config.Save(); };
            menu.Items.Add(miDragZones);

            miSeamless = new ToolStripMenuItem("Seamless maximize (no flicker)");
            miSeamless.CheckOnClick = true;
            miSeamless.Click += delegate
            {
                Engine.Cfg.Seamless = miSeamless.Checked;
                if (!miSeamless.Checked) Engine.Disarm();
                Config.Save();
            };
            menu.Items.Add(miSeamless);

            miAutoFix = new ToolStripMenuItem("Keep windows inside their zone");
            miAutoFix.CheckOnClick = true;
            miAutoFix.Click += delegate { Engine.Cfg.AutoFix = miAutoFix.Checked; Config.Save(); };
            menu.Items.Add(miAutoFix);

            miVerbose = new ToolStripMenuItem("Log every action (debug)");
            miVerbose.CheckOnClick = true;
            miVerbose.Click += delegate { Engine.Cfg.Verbose = miVerbose.Checked; Config.Save(); };
            menu.Items.Add(miVerbose);

            miStartup = new ToolStripMenuItem("Start with Windows");
            miStartup.CheckOnClick = true;
            miStartup.Click += delegate
            {
                Engine.Cfg.Startup = miStartup.Checked;   // persisted, so unchecking survives restarts
                App.SetStartup(miStartup.Checked);
                Config.Save();
            };
            menu.Items.Add(miStartup);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miEdit = new ToolStripMenuItem("Edit config...");
            miEdit.Click += delegate { Process.Start("notepad.exe", Config.Path); };
            menu.Items.Add(miEdit);

            ToolStripMenuItem miReload = new ToolStripMenuItem("Reload config");
            miReload.Click += delegate { Engine.Disarm(); LoadEverything(); };
            menu.Items.Add(miReload);

            ToolStripMenuItem miPanic = new ToolStripMenuItem("Reset work area (panic button)");
            miPanic.Click += delegate
            {
                Engine.Disarm(); Engine.CaptureTrueWorkAreas();
                tray.ShowBalloonTip(2000, "ZoneMax", "Work areas reset to the Windows default.", ToolTipIcon.Info);
            };
            menu.Items.Add(miPanic);

            ToolStripMenuItem miLog = new ToolStripMenuItem("Open log");
            miLog.Click += delegate { Process.Start("notepad.exe", App.LogPath); };
            menu.Items.Add(miLog);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit ZoneMax");
            miExit.Click += delegate { Application.Exit(); };
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "ZoneMax";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
        }

        void RebuildSplitMenu()
        {
            miSplitRoot.DropDownItems.Clear();
            double[] fracs = new double[] { 0.5, 0.4, 0.35, 0.6, 0.65 };
            N.RECT pm = Engine.MonitorRect(Engine.Monitors()[0]);
            foreach (IntPtr h in Engine.Monitors()) if (Engine.IsPrimary(h)) pm = Engine.MonitorRect(h);

            for (int i = 0; i < fracs.Length; i++)
            {
                double f = fracs[i];
                int lw = (int)Math.Round(pm.Width * f);
                ToolStripMenuItem mi = new ToolStripMenuItem(
                    string.Format(CultureInfo.InvariantCulture, "{0} / {1}    ({2}px | {3}px)",
                        (int)(f * 100), 100 - (int)(f * 100), lw, pm.Width - lw));
                mi.Checked = Math.Abs(Engine.Cfg.SplitFraction - f) < 0.005;
                mi.Click += delegate
                {
                    Engine.Cfg.SplitFraction = f;
                    Engine.InvalidateCaches();     // zones are cached now; the fraction changes them
                    Config.Save();
                    App.Log("split fraction -> " + f.ToString("0.##", CultureInfo.InvariantCulture));
                    RefreshTray();
                    ZoneOverlay.Flash(2000);
                };
                miSplitRoot.DropDownItems.Add(mi);
            }
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 45, 50, 60))) g.FillRectangle(b, 1, 5, 30, 22);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 96, 165, 250))) g.FillRectangle(b, 3, 7, 12, 18);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 240, 244, 250))) g.FillRectangle(b, 17, 7, 12, 18);
                }
                // Icon.FromHandle does NOT own the HICON -- clone, then free both native and managed
                IntPtr hIcon = bmp.GetHicon();
                using (Icon tmp = Icon.FromHandle(hIcon))
                {
                    Icon result = (Icon)tmp.Clone();
                    N.DestroyIcon(hIcon);
                    return result;
                }
            }
        }

        // ---- hooks ----
        void OnLocationChange(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint th, uint t)
        {
            if (idObject != N.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            try { Engine.CorrectIfNeeded(hwnd); }
            catch (Exception ex) { App.Log("locchange error: " + ex.Message); }
        }

        void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint th, uint t)
        {
            if (idObject != N.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            try
            {
                // Deliberately does NOT arm. Focus-based arming is what made maximized windows jump
                // between zones. All this does is keep each window's home zone up to date.
                if (Engine.Manageable(hwnd))
                {
                    Zone z = Engine.ZoneOf(hwnd);   // side effect: refreshes the home of unzoomed windows
                    if (Engine.Cfg.Verbose)
                        App.Log("FOCUS " + Engine.Describe(hwnd) + " -> [" + (z == null ? "none" : z.Name) + "]");

                    // Chrome/Electron re-compute their maximized bounds against the CURRENT work
                    // area on every activation. A window truly maximized inside a sub-zone would
                    // visibly explode to the full monitor and get hauled back by autofix ("clicking
                    // anywhere maximizes it"). Arm its own zone briefly so the re-maximize lands
                    // exactly where the window already is -- nothing moves, nothing flickers.
                    // ignoreShift: this arms the window's OWN zone on focus arrival -- "Shift =
                    // whole monitor" is click semantics and means nothing here (and the user is
                    // frequently mid-Shift while typing).
                    if (z != null && N.IsZoomed(hwnd)) Engine.ArmFor(hwnd, z, 500, true);
                }
            }
            catch (Exception ex) { App.Log("foreground error: " + ex.Message); }
        }

        // A title-bar press is ambiguous: it might become a double-click-to-maximize (we want to arm
        // the work area for that) or it might become a DRAG (we must get out of the way instantly).
        // We can only tell them apart by waiting to see if the mouse moves.
        //
        // The hook callback itself must return in MICROSECONDS: everything it does here blocks
        // global mouse input, and Windows silently uninstalls a hook that exceeds
        // LowLevelHooksTimeout (~300ms). So the hook only reads the coordinates and posts the real
        // work (hit-test, zone math, arming) to the message pump via Classify(). Hook callbacks
        // and BeginInvoke targets both run on this one UI thread, so pressed/pressedAt need no lock.
        IntPtr pressed = IntPtr.Zero;
        N.POINT pressedAt;
        N.RECT pressedRect;      // where the window WAS at press -- a drop only counts if it moved
        int lastSnapTry;         // throttles mid-drag Snap-suppression retries

        IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            lastHookEvent = Environment.TickCount;
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();

                    if (msg == N.WM_LBUTTONDOWN || msg == N.WM_MBUTTONDOWN
                        || msg == N.WM_RBUTTONDOWN || msg == N.WM_XBUTTONDOWN)
                    {
                        Engine.LastClickTick = Environment.TickCount;
                        N.POINT pt;                                   // field reads, not PtrToStructure:
                        pt.X = Marshal.ReadInt32(lParam);             // zero allocation on the hook thread
                        pt.Y = Marshal.ReadInt32(lParam, 4);

                        // SYNCHRONOUS pre-arm, and it must stay synchronous: this hook runs BEFORE
                        // the app receives the click, and Chrome re-computes its maximized bounds
                        // against the CURRENT work area on every mouse activation. Arm the window's
                        // zone now and the re-maximize lands where the window already is. Armed
                        // async (tried once), Chrome wins the race on every click and each click
                        // visibly bounces the window off the full monitor. EVERY button, not just
                        // left: a middle-click activates the window exactly the same way, and
                        // arming only for left-clicks left middle-clicks flickering. Cost here is
                        // a handful of local user32 calls + cached lookups -- no cross-process
                        // messages.
                        IntPtr root = N.GetAncestor(N.WindowFromPoint(pt), N.GA_ROOT);
                        if (root != IntPtr.Zero && N.IsZoomed(root) && Engine.Manageable(root))
                        {
                            Zone z = Engine.ZoneOf(root);
                            if (z != null) Engine.ArmFor(root, z, 900);
                        }

                        if (msg == N.WM_LBUTTONDOWN)                  // only left starts drags/maximizes
                        {
                            pressed = IntPtr.Zero;
                            N.POINT ptCopy = pt;
                            BeginInvoke(new Action(delegate { Classify(ptCopy); }));
                        }
                    }
                    else if (msg == N.WM_MOUSEMOVE && Engine.Dragging && !Engine.SnapSuppressed
                             && pressed != IntPtr.Zero)
                    {
                        // The drag-start attempt may have run before Windows restored the window
                        // (Snap OFF while it's still zoomed would break the restore itself), so
                        // keep retrying -- throttled -- until the window is restored and Snap is off.
                        int t = Environment.TickCount;
                        if (t - lastSnapTry > 60)
                        {
                            lastSnapTry = t;
                            IntPtr dh2 = pressed;
                            Engine.Post(delegate { Engine.SuppressSnapForDrag(dh2); });
                        }
                    }
                    else if (msg == N.WM_MOUSEMOVE && pressed != IntPtr.Zero && !Engine.Dragging)
                    {
                        // Stale press guard: if the button-up was never seen by this hook (Esc'd
                        // drag, hook reinstall mid-gesture), `pressed` survives and every later
                        // cursor move would fake a drag. The physical button state is the truth.
                        if ((N.GetAsyncKeyState(N.VK_LBUTTON) & 0x8000) == 0)
                        {
                            pressed = IntPtr.Zero;
                            return N.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                        }
                        int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);
                        if (Math.Abs(x - pressedAt.X) > 6 || Math.Abs(y - pressedAt.Y) > 6)
                        {
                            // It is a DRAG. Hand the work area straight back -- it is a GLOBAL setting,
                            // and leaving it shrunk for the length of a drag is what made other windows
                            // explode to the full monitor and then get hauled back by autofix.
                            Engine.Dragging = true;
                            IntPtr dh = pressed;
                            Engine.Post(delegate
                            {
                                Engine.Disarm();
                                Engine.SuppressSnapForDrag(dh);   // native edge-snap out of the way
                                if (Engine.Cfg.Verbose) App.Log("DRAG start \"" + N.TitleOf(dh) + "\"");
                            });
                        }
                    }
                    else if (msg == N.WM_LBUTTONUP)
                    {
                        IntPtr dh = pressed;
                        bool wasDrag = Engine.Dragging;
                        pressed = IntPtr.Zero;
                        Engine.Dragging = false;

                        if (wasDrag && dh != IntPtr.Zero)
                        {
                            N.POINT drop;
                            drop.X = Marshal.ReadInt32(lParam);
                            drop.Y = Marshal.ReadInt32(lParam, 4);
                            N.RECT startRect = pressedRect;
                            Engine.Post(delegate
                            {
                                // Place first, THEN hand Snap back: re-enabling before the window's
                                // move loop has fully wound down could let native snap claim the drop.
                                Engine.HandleDrop(dh, drop, startRect);
                                Engine.RestoreSnap();
                            });
                        }
                        else if (wasDrag) Engine.Post(delegate { Engine.RestoreSnap(); });
                    }
                }
                catch { /* a hook must never throw */ }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // The expensive half of a button-press, running on the pump a few ms after the hook
        // returned. The arm lives 900ms and a human double-click takes 100ms+, so this latency
        // is invisible -- and the first click of a double-click arms for the second anyway.
        void Classify(N.POINT pt)
        {
            try
            {
                IntPtr root = N.GetAncestor(N.WindowFromPoint(pt), N.GA_ROOT);
                if (!Engine.Manageable(root)) return;

                IntPtr res;
                IntPtr lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
                N.SendMessageTimeout(root, N.WM_NCHITTEST, IntPtr.Zero, lp, N.SMTO_ABORTIFHUNG, 40, out res);
                int ht = res.ToInt32();
                if (ht != N.HTMAXBUTTON && ht != N.HTCAPTION) return;

                // Only track the press while the button is still down. If it was a fast click that
                // already released, setting `pressed` now would make the next unrelated mouse-move
                // look like a drag.
                if ((N.GetAsyncKeyState(N.VK_LBUTTON) & 0x8000) != 0)
                {
                    pressed = root;
                    pressedAt = pt;
                    N.GetWindowRect(root, out pressedRect);
                }

                Zone z = Engine.ZoneOf(root);          // refreshes the window's home as a side effect
                Engine.RememberHome(root, z);
                if (Engine.Cfg.Verbose)
                    App.Log("CLICK " + (ht == N.HTMAXBUTTON ? "maximize-button" : "title-bar")
                            + " " + Engine.Describe(root) + " -> [" + (z == null ? "none" : z.Name) + "]");
                if (!N.IsZoomed(root)) Engine.ArmFor(root, z);   // in case the click becomes a maximize
            }
            catch (Exception ex) { App.Log("classify error: " + ex.Message); }
        }

        IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            lastHookEvent = Environment.TickCount;
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    if (msg == N.WM_KEYDOWN || msg == N.WM_SYSKEYDOWN)
                    {
                        // field reads, not PtrToStructure -- this path runs on EVERY keydown system-wide
                        int vk = Marshal.ReadInt32(lParam);
                        uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                        bool injected = (flags & N.LLKHF_INJECTED) != 0;
                        bool isArrow = vk == N.VK_LEFT || vk == N.VK_RIGHT || vk == N.VK_UP || vk == N.VK_DOWN;

                        // Rolling typing arm, on EVERY keydown: apps re-compute their maximized
                        // bounds at their own leisure while focused -- Slack does it when a sent
                        // message updates its taskbar badge, i.e. every time you hit Enter. No
                        // input event "causes" that from our point of view, so the only winning
                        // move is to keep the work area armed to the focused window's zone for
                        // the whole typing session. The ArmFor fast path makes this one SPI call
                        // per burst, not per key. Screenshot combos (Win+Shift+S, PrintScreen)
                        // get a long TTL instead: the snip steals the foreground and hands it
                        // back without a click, sometimes many seconds later.
                        if (!injected)
                        {
                            // Snip / Task View overlays used to get a LONG arm (10s/4s) here. An
                            // instrumented run killed that idea with two facts: (a) the focused
                            // window explodes to the full monitor even while its own zone is
                            // armed -- Chrome's overlay-time re-evaluation ignores the live work
                            // area for the active window; (b) the SIBLING zoned window
                            // re-maximizes INTO the armed zone, i.e. the arm teleports innocent
                            // windows to the wrong zone. A long arm protects nothing and poisons
                            // everything, so overlay storms belong to autofix (verified
                            // single-bounce with correct homes). The plain rolling arm stays: its
                            // job is the focused window's own badge-update re-evaluations
                            // (Slack on every Enter), which don't touch siblings.
                            bool snip = vk == N.VK_SNAPSHOT
                                        || (vk == N.VK_S && N.WinDown()
                                            && (N.GetAsyncKeyState(N.VK_SHIFT) & 0x8000) != 0);
                            bool taskView = vk == N.VK_TAB && N.WinDown();
                            IntPtr fgT = N.GetForegroundWindow();
                            if (fgT != IntPtr.Zero && N.IsZoomed(fgT) && Engine.Manageable(fgT))
                            {
                                Zone zT = Engine.ZoneOf(fgT);
                                if (zT != null) Engine.ArmFor(fgT, zT, 1200, true);
                            }
                            if (snip || taskView)
                                App.Log((snip ? "SNIP" : "TASKVIEW") + " combo seen (vk=" + vk
                                        + ") -- overlay storm expected; autofix sweeps it");
                        }

                        if (Engine.Cfg.SnapKeys && !injected && isArrow && N.WinDown()
                            && (N.GetAsyncKeyState(N.VK_CONTROL) & 0x8000) == 0
                            && (N.GetAsyncKeyState(N.VK_MENU) & 0x8000) == 0)
                        {
                            bool shift = (N.GetAsyncKeyState(N.VK_SHIFT) & 0x8000) != 0;
                            IntPtr fg = N.GetForegroundWindow();
                            // Elevated windows fall through to NATIVE snap: UIPI blocks our
                            // SetWindowPos, so swallowing the key here would make Win+Arrow do
                            // nothing at all on admin windows.
                            if (Engine.Manageable(fg) && !N.IsWindowElevated(fg))
                            {
                                N.SwallowStartMenu();
                                int vkCopy = vk; bool shiftCopy = shift; IntPtr fgCopy = fg;
                                // Engine worker, NOT BeginInvoke: the action sleeps, and sleeping on
                                // this thread is what got the hooks silently uninstalled.
                                Engine.Post(delegate
                                {
                                    if (shiftCopy && (vkCopy == N.VK_LEFT || vkCopy == N.VK_RIGHT))
                                        Engine.MoveToAdjacentMonitor(fgCopy, vkCopy == N.VK_LEFT ? -1 : 1);
                                    else if (!shiftCopy)
                                        Engine.SnapKey(fgCopy, vkCopy);
                                    try { BeginInvoke(new Action(RefreshTray)); } catch { }
                                });
                                return (IntPtr)1;   // swallowed -- the shell never sees it
                            }
                        }
                    }
                }
                catch { /* a hook must never throw */ }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        void Cleanup()
        {
            try { if (watchdog != null) watchdog.Dispose(); } catch { }
            try { Engine.Disarm(); } catch { }
            try { Engine.ResetWorkAreasToDefault(); } catch { }
            try { if (hookLoc != IntPtr.Zero) N.UnhookWinEvent(hookLoc); } catch { }
            try { if (hookFg != IntPtr.Zero) N.UnhookWinEvent(hookFg); } catch { }
            try { if (hookMouse != IntPtr.Zero) N.UnhookWindowsHookEx(hookMouse); } catch { }
            try { if (hookKey != IntPtr.Zero) N.UnhookWindowsHookEx(hookKey); } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            App.Log("---- exit ----");
            App.FlushLog(400);
        }
    }

    // ---------------------------------------------------------------------------- entry
    internal static class App
    {
        public static string Dir { get { return System.IO.Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string LogPath { get { return System.IO.Path.Combine(Dir, "zonemax.log"); } }

        // Log() must be near-free: it gets called from hook callbacks and the LOCATIONCHANGE flood,
        // where a synchronous file write (open/stat/write/close, plus whatever the AV adds) was
        // blocking global input and counting toward the LowLevelHooksTimeout. Callers only enqueue;
        // one background thread drains and writes.
        static readonly object LogGate = new object();
        static readonly Queue<string> LogQueue = new Queue<string>();
        static Thread logThread;

        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg;
            lock (LogGate)
            {
                if (logThread == null)
                {
                    logThread = new Thread(LogPump);
                    logThread.IsBackground = true;
                    logThread.Name = "ZoneMax log";
                    logThread.Start();
                }
                LogQueue.Enqueue(line);
                Monitor.Pulse(LogGate);
            }
        }

        static void LogPump()
        {
            StringBuilder batch = new StringBuilder();
            int writes = 0;
            while (true)
            {
                batch.Length = 0;
                lock (LogGate)
                {
                    while (LogQueue.Count == 0) Monitor.Wait(LogGate);
                    while (LogQueue.Count > 0) batch.Append(LogQueue.Dequeue()).Append("\r\n");
                }
                try
                {
                    if (writes++ % 100 == 0)
                    {
                        FileInfo fi = new FileInfo(LogPath);
                        if (fi.Exists && fi.Length > 400000)
                        {
                            // rotate, don't delete -- the interesting line is always the one you just lost
                            string old = LogPath + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(LogPath, old);
                        }
                    }
                    File.AppendAllText(LogPath, batch.ToString());
                }
                catch { }
            }
        }

        // Best effort: give the pump a moment to drain before the process dies.
        public static void FlushLog(int ms)
        {
            int deadline = Environment.TickCount + ms;
            while ((deadline - Environment.TickCount) > 0)
            {
                lock (LogGate) { if (LogQueue.Count == 0) return; }
                Thread.Sleep(20);
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ZoneMax";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        public static void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                Log("start with windows = " + on);
            }
            catch (Exception ex) { Log("startup error: " + ex.Message); }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mtx = new Mutex(true, "ZoneMax_SingleInstance_9f2c", out created))
            {
                if (!created) return;

                // Per-monitor-V2 DPI awareness: physical pixels EVERYWHERE, which is what all the
                // math already assumes. System-aware only worked because the primary happens to run
                // 100% -- one scale-slider change or an undock and every rect on the other monitor
                // was off by the scale factor while the mouse hook kept reporting physical pixels.
                try
                {
                    if (!N.SetProcessDpiAwarenessContext((IntPtr)(-4)))   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                        N.SetProcessDPIAware();
                }
                catch (EntryPointNotFoundException) { N.SetProcessDPIAware(); }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log("FATAL: " + e.ExceptionObject);
                    try { Engine.Disarm(); Engine.ResetWorkAreasToDefault(); } catch { }
                    FlushLog(500);   // the FATAL line is the one line that must reach disk
                };
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log("UI error: " + e.Exception.Message);
                };

                Log("---- ZoneMax v5 starting ----");
                // startup registration is synced from config in LoadEverything -- forcing it here
                // made the tray checkbox impossible to turn off

                Application.Run(new TrayForm());
            }
        }
    }
}
