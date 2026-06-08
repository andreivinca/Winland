using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// Per-monitor workspaces. Each workspace is pinned to a "home" monitor (the monitor it was first
/// opened on) and never moves between monitors. A monitor shows exactly one workspace at a time;
/// the windows of a workspace that is not currently shown are minimized (put away).
///
/// Win+N: if workspace N is already shown on its home monitor, focus it; otherwise open N on its home
/// monitor, minimizing whatever that monitor was showing. No other monitor is ever touched.
/// </summary>
internal sealed class WorkspaceManager : IDisposable
{
    public const int WorkspaceCount = 9;

    private sealed class MonitorState
    {
        public IntPtr Handle;
        public RECT Work;
        public bool Primary;
        public int Current;
    }

    // window handle -> workspace number (1..9). The single source of truth for membership.
    private readonly Dictionary<IntPtr, int> _windowWorkspace = new();
    // workspace -> most recently focused window (used to restore focus after a switch).
    private readonly Dictionary<int, IntPtr> _lastActive = new();
    // workspace -> HMONITOR it is linked to. Set the first time the workspace is entered (to the
    // monitor under the cursor) and kept until the workspace is released.
    private readonly Dictionary<int, IntPtr> _workspaceHome = new();
    // HMONITOR -> state.
    private readonly Dictionary<IntPtr, MonitorState> _monitors = new();

    private readonly WinEventDelegate _winEventProc;
    private readonly List<IntPtr> _winEventHooks = new();

    /// <summary>
    /// Raised (on the UI thread) with the workspace currently shown on the primary monitor, whenever a
    /// Win+N / release operation may have changed it. The tray icon lives on the primary monitor's
    /// taskbar, so it must reflect that monitor's workspace — not whatever workspace a press happened to
    /// switch on some other monitor.
    /// </summary>
    public event Action<int>? PrimaryWorkspaceChanged;

    /// <summary>The workspace currently shown on the primary monitor (for the tray icon).</summary>
    public int PrimaryWorkspace =>
        (_monitors.Values.FirstOrDefault(m => m.Primary) ?? _monitors.Values.FirstOrDefault())?.Current ?? 1;

    private void NotifyPrimaryWorkspace() => PrimaryWorkspaceChanged?.Invoke(PrimaryWorkspace);

    // Window events (foreground/minimize/move) are delivered asynchronously, so events triggered by
    // our OWN minimize/restore during a switch arrive AFTER the switch returns. While the current time
    // is below this tick, ignore re-homing events so our own operations don't corrupt assignments.
    private int _eventGuardUntilTick;

    public WorkspaceManager()
    {
        _winEventProc = WinEventCallback;
        Rebuild();
        InstallWinEventHooks();
    }

    public void Dispose()
    {
        foreach (IntPtr hook in _winEventHooks)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        _winEventHooks.Clear();
    }

    /// <summary>Enumerate physical monitors, left-to-right, as fresh states (Current = 0).</summary>
    private List<MonitorState> EnumerateMonitors()
    {
        var found = new List<MonitorState>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                found.Add(new MonitorState
                {
                    Handle = hMon,
                    Work = info.rcWork,
                    Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                });
            }

            return true;
        }, IntPtr.Zero);

        // Order left-to-right (leftmost monitor = workspace 1).
        return found.OrderBy(m => m.Work.Left).ThenBy(m => m.Work.Top).ToList();
    }

    /// <summary>Scan monitors and (non-minimized) windows, assigning each to its monitor's workspace.</summary>
    private void Rebuild()
    {
        _monitors.Clear();
        _windowWorkspace.Clear();
        _lastActive.Clear();
        _workspaceHome.Clear();

        List<MonitorState> ordered = EnumerateMonitors();
        for (int i = 0; i < ordered.Count; i++)
        {
            MonitorState m = ordered[i];
            m.Current = Math.Min(i + 1, WorkspaceCount);
            _monitors[m.Handle] = m;
            _workspaceHome[m.Current] = m.Handle;
        }

        EnumWindows((hWnd, _) =>
        {
            // Link only currently-visible (non-minimized) windows at startup.
            if (IsManaged(hWnd) && !IsIconic(hWnd) && TryGetWindowMonitor(hWnd, out MonitorState? monitor))
            {
                _windowWorkspace[hWnd] = monitor!.Current;
            }

            return true;
        }, IntPtr.Zero);

        LogWorkspaces("startup");
    }

    /// <summary>
    /// Reconcile the cached monitor map with the current display configuration. HMONITOR handles are
    /// only valid until the display setup changes (resolution, sleep/wake, dock/undock, driver reset);
    /// afterwards the cached handles go stale and every "is this window on this monitor" test fails —
    /// so a Win+N switch updates <see cref="MonitorState.Current"/> but minimizes/restores nothing. We
    /// re-enumerate and carry each monitor's workspace (and any workspace homes) across by position, so
    /// the handles we compare against are always live. Called before each switch/release.
    /// </summary>
    private void RefreshMonitors()
    {
        List<MonitorState> current = EnumerateMonitors();
        if (current.Count == 0)
        {
            return; // transient empty enumeration — keep what we had rather than wipe state
        }

        var remap = new Dictionary<IntPtr, IntPtr>(); // stale handle -> current handle

        foreach (MonitorState m in current)
        {
            // Match a previous monitor by position (the work area's top-left is stable across a handle
            // reissue) so its current workspace carries over to the live handle.
            MonitorState? prev = _monitors.Values.FirstOrDefault(
                p => p.Work.Left == m.Work.Left && p.Work.Top == m.Work.Top);
            if (prev != null)
            {
                m.Current = prev.Current;
                if (prev.Handle != m.Handle)
                {
                    remap[prev.Handle] = m.Handle;
                }
            }
        }

        _monitors.Clear();
        foreach (MonitorState m in current)
        {
            _monitors[m.Handle] = m;
        }

        // Repoint workspace homes whose monitor handle was reissued.
        if (remap.Count > 0)
        {
            foreach (int ws in _workspaceHome.Keys.ToList())
            {
                if (remap.TryGetValue(_workspaceHome[ws], out IntPtr live))
                {
                    _workspaceHome[ws] = live;
                }
            }
        }
    }

    /// <summary>
    /// Handle Win+<paramref name="k"/> (1..9). Each workspace is linked to a home monitor — set the
    /// first time it's entered, to the monitor under the mouse cursor. Win+k always acts on k's home
    /// monitor: if k is already shown there, focus it; otherwise put away that monitor's apps and show
    /// k (empty the first time). Other monitors are untouched.
    /// </summary>
    public void SwitchFocusedMonitorTo(int k)
    {
        if (k < 1 || k > WorkspaceCount)
        {
            return;
        }

        RefreshMonitors(); // keep monitor handles live across display-config changes
        if (_monitors.Count == 0)
        {
            return;
        }

        MonitorState? home = ResolveHome(k);
        if (home == null)
        {
            return;
        }

        if (home.Current == k)
        {
            FocusWorkspace(k);
            LogWorkspaces($"focus WS{k}");
            NotifyPrimaryWorkspace();
            return;
        }

        GuardEvents();
        MinimizeMonitorWindows(home, home.Current, keep: k); // put away everything on k's home monitor
        ShowWorkspaceOnMonitor(k, home);
        home.Current = k;
        FocusWorkspace(k);
        GuardEvents();

        LogWorkspaces($"switch WS{k}");
        NotifyPrimaryWorkspace();
    }

    /// <summary>k's linked home monitor; on first entry it's pinned to the monitor under the cursor.</summary>
    private MonitorState? ResolveHome(int k)
    {
        if (_workspaceHome.TryGetValue(k, out IntPtr handle) && _monitors.TryGetValue(handle, out MonitorState? home))
        {
            return home;
        }

        MonitorState? active = GetActiveMonitor();
        if (active != null)
        {
            _workspaceHome[k] = active.Handle;
        }

        return active;
    }

    /// <summary>
    /// Release the workspace currently shown on the active (cursor) monitor: put its windows away
    /// (minimized, but still members of the workspace) and unpin its home, so it can be re-summoned
    /// on any monitor — `Win+N` on another monitor will re-home it there and restore its windows.
    /// The active monitor is left showing no workspace.
    /// </summary>
    public void ReleaseCurrentWorkspace()
    {
        RefreshMonitors(); // keep monitor handles live across display-config changes
        MonitorState? m = GetActiveMonitor();
        if (m == null || m.Current < 1)
        {
            return;
        }

        int w = m.Current;

        GuardEvents();
        MinimizeMonitorWindows(m, w, keep: -1); // put away everything on this monitor as part of w
        _workspaceHome.Remove(w);               // unlink: Win+w re-homes to the cursor monitor next time
        m.Current = 0;                          // monitor now shows no workspace
        GuardEvents();

        LogWorkspaces($"release WS{w}");
        NotifyPrimaryWorkspace();
    }

    private void GuardEvents() => _eventGuardUntilTick = Environment.TickCount + 1500;

    private bool EventsGuarded => unchecked(Environment.TickCount - _eventGuardUntilTick) < 0;

    // The active monitor is the one under the mouse cursor.
    private MonitorState? GetActiveMonitor()
    {
        if (GetCursorPos(out POINT pt))
        {
            IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (_monitors.TryGetValue(hMon, out MonitorState? m))
            {
                return m;
            }
        }

        return _monitors.Values.FirstOrDefault(s => s.Primary) ?? _monitors.Values.FirstOrDefault();
    }

    private void ShowWorkspaceOnMonitor(int workspace, MonitorState monitor)
    {
        foreach (IntPtr h in WindowsOf(workspace))
        {
            MoveWindowToMonitor(h, monitor);
        }
    }

    // Minimize every visible managed window physically on the monitor, except those of workspace
    // <paramref name="keep"/>. Each minimized window is (re)assigned to <paramref name="assignTo"/>
    // (when >= 1) so it is remembered as part of the outgoing workspace and restored when it returns.
    private void MinimizeMonitorWindows(MonitorState monitor, int assignTo, int keep)
    {
        // Redefine the outgoing workspace as exactly the windows we're about to put away here — the
        // ones still visible on the monitor. Any window the user minimized himself before leaving is
        // no longer part of it, so forget those stale members first. This is safe precisely because
        // the workspace's own windows are still visible at this point (we haven't minimized them yet),
        // so the only iconic members of assignTo are user-minimized ones.
        if (assignTo >= 1)
        {
            ForgetMinimizedMembers(assignTo);
        }

        EnumWindows((h, _) =>
        {
            if (!IsManaged(h) || IsIconic(h))
            {
                return true;
            }

            if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != monitor.Handle)
            {
                return true; // not on this monitor
            }

            if (_windowWorkspace.TryGetValue(h, out int ws) && ws == keep)
            {
                return true; // belongs to the workspace we're showing
            }

            if (assignTo >= 1)
            {
                _windowWorkspace[h] = assignTo;
            }

            ShowWindow(h, SW_MINIMIZE);
            return true;
        }, IntPtr.Zero);
    }

    // Drop every window currently assigned to <paramref name="workspace"/> that is minimized (or gone).
    // Called when leaving a workspace: a minimized member at that moment is one the user put away
    // himself, so it should no longer return when the workspace is next shown.
    private void ForgetMinimizedMembers(int workspace)
    {
        var stale = new List<IntPtr>();
        foreach (KeyValuePair<IntPtr, int> kv in _windowWorkspace)
        {
            if (kv.Value == workspace && (!IsWindow(kv.Key) || IsIconic(kv.Key)))
            {
                stale.Add(kv.Key);
            }
        }

        foreach (IntPtr h in stale)
        {
            _windowWorkspace.Remove(h);
        }
    }

    private List<IntPtr> WindowsOf(int workspace)
    {
        var result = new List<IntPtr>();
        foreach (KeyValuePair<IntPtr, int> kv in _windowWorkspace)
        {
            if (kv.Value == workspace)
            {
                result.Add(kv.Key);
            }
        }

        // Drop windows that have since closed.
        result.RemoveAll(h =>
        {
            if (IsWindow(h))
            {
                return false;
            }

            _windowWorkspace.Remove(h);
            return true;
        });

        return result;
    }

    /// <summary>
    /// Move a window onto the given monitor, preserving maximized state and relative position, and
    /// un-minimizing it. Stateless: derives source from the window's own restored position.
    /// </summary>
    private void MoveWindowToMonitor(IntPtr hWnd, MonitorState monitor)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
        {
            return;
        }

        bool maximized = wp.showCmd == SW_SHOWMAXIMIZED || (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;

        RECT norm = wp.rcNormalPosition;
        IntPtr srcMon = MonitorFromRect(ref norm, MONITOR_DEFAULTTONEAREST);
        RECT srcWork = GetWorkArea(srcMon);
        RECT dstWork = monitor.Work;

        OffsetRect(ref norm, dstWork.Left - srcWork.Left, dstWork.Top - srcWork.Top);
        ClampToWork(ref norm, dstWork);

        wp.rcNormalPosition = norm;
        wp.showCmd = maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        SetWindowPlacement(hWnd, ref wp);
    }

    private static void OffsetRect(ref RECT r, int dx, int dy)
    {
        r.Left += dx;
        r.Right += dx;
        r.Top += dy;
        r.Bottom += dy;
    }

    private static void ClampToWork(ref RECT r, RECT work)
    {
        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;

        if (r.Right > work.Right) { r.Left = work.Right - width; r.Right = work.Right; }
        if (r.Bottom > work.Bottom) { r.Top = work.Bottom - height; r.Bottom = work.Bottom; }
        if (r.Left < work.Left) { r.Left = work.Left; r.Right = work.Left + width; }
        if (r.Top < work.Top) { r.Top = work.Top; r.Bottom = work.Top + height; }
    }

    private RECT GetWorkArea(IntPtr hMon)
    {
        if (_monitors.TryGetValue(hMon, out MonitorState? m))
        {
            return m.Work;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(hMon, ref info) ? info.rcWork : new RECT();
    }

    private void FocusWorkspace(int workspace)
    {
        IntPtr target = IntPtr.Zero;
        if (_lastActive.TryGetValue(workspace, out IntPtr last) && IsWindow(last) && !IsIconic(last)
            && _windowWorkspace.TryGetValue(last, out int ws) && ws == workspace)
        {
            target = last;
        }
        else
        {
            target = WindowsOf(workspace).FirstOrDefault(h => !IsIconic(h));
        }

        if (target != IntPtr.Zero)
        {
            ForceForeground(target);
            _lastActive[workspace] = target;
        }
    }

    // Bring a window to the foreground. We avoid AttachThreadInput (it can corrupt keyboard input
    // state when attaching to the shell thread); briefly clearing the foreground-lock timeout is safe.
    private void ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        uint original = 0;
        bool got = SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref original, 0);
        if (got)
        {
            SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, UIntPtr.Zero, 0);
        }

        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);

        if (got)
        {
            SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (UIntPtr)original, 0);
        }
    }

    private bool TryGetWindowMonitor(IntPtr hWnd, out MonitorState? monitor)
    {
        // Use the restored (normal) position so minimized windows resolve to the right monitor.
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        IntPtr hMon;
        if (GetWindowPlacement(hWnd, ref wp))
        {
            RECT norm = wp.rcNormalPosition;
            hMon = MonitorFromRect(ref norm, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        }

        return _monitors.TryGetValue(hMon, out monitor);
    }

    private bool IsManaged(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        // Minimized windows still report visible (WS_VISIBLE); only truly hidden windows are excluded.
        if (!IsWindowVisible(hWnd) && !IsIconic(hWnd))
        {
            return false;
        }

        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        if (GetWindowTextLength(hWnd) == 0)
        {
            return false;
        }

        long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        return !IsWindowCloaked(hWnd) && !IsDesktopWindow(hWnd);
    }

    private static bool IsDesktopWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(256);
        int length = GetClassName(hWnd, className, className.Capacity);
        if (length <= 0)
        {
            return false;
        }

        string name = className.ToString(0, length);
        return string.Equals(name, "Progman", StringComparison.Ordinal)
            || string.Equals(name, "WorkerW", StringComparison.Ordinal);
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        const int DWMWA_CLOAKED = 14;
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, Marshal.SizeOf<int>()) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private void InstallWinEventHooks()
    {
        AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        AddHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND);
        AddHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        AddHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY);
    }

    private void AddHook(uint eventMin, uint eventMax)
    {
        IntPtr hook = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
        {
            _winEventHooks.Add(hook);
        }
    }

    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hWnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType == EVENT_OBJECT_DESTROY)
        {
            _windowWorkspace.Remove(hWnd);
            return;
        }

        // Ignore events caused by our own switch operations, and non-app windows.
        if (EventsGuarded || !IsManaged(hWnd))
        {
            return;
        }

        if (!TryGetWindowMonitor(hWnd, out MonitorState? monitor))
        {
            return;
        }

        // Whenever a window is activated, restored, maximized, or dragged, it joins the workspace
        // currently shown on its monitor — unlinking it from any workspace it was on before. (Only
        // when that monitor actually shows a workspace; Current == 0 means "released / nothing shown".)
        if (monitor!.Current >= 1
            && (eventType == EVENT_SYSTEM_FOREGROUND
                || eventType == EVENT_SYSTEM_MINIMIZEEND
                || eventType == EVENT_SYSTEM_MOVESIZEEND))
        {
            _windowWorkspace[hWnd] = monitor.Current;
            _lastActive[monitor.Current] = hWnd;
        }
    }

    private void LogWorkspaces(string action)
    {
        var parts = _monitors.Values
            .OrderBy(m => m.Work.Left)
            .Select((m, i) => $"mon{i + 1}=WS{m.Current}");
        string line = $"[{action}] {string.Join("  ", parts)}";
        Log.Line(line);
    }

    // ----- Win32 -----

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const uint SW_SHOWNORMAL = 1;
    private const uint SW_SHOWMAXIMIZED = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WPF_RESTORETOMAXIMIZED = 0x0002;

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, UIntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
