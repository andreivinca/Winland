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
/// A workspace's windows are its tiles, laid out in a scrolling strip (see WorkspaceManager.Tiling.cs).
/// Windows that belong to no workspace are ordinary windows: Winland never tiles, moves or resizes them.
/// Like every window on a monitor, though, they are put away (minimized) when that monitor switches
/// workspace, so the monitor shows only the workspace switched to.
///
/// Win+N: show workspace N on its home monitor — put away everything there that isn't N's and lay out
/// N's strip. This runs on every press, so re-pressing Win+N re-asserts the layout: minimized tiles come
/// back up, tiles dragged elsewhere are pulled home, and every tile snaps back into its column. Other
/// monitors keep their own shown workspace.
/// </summary>
internal sealed partial class WorkspaceManager : IDisposable
{
    private sealed class MonitorState
    {
        public IntPtr Handle;
        public string Device = "";   // stable GDI device name (e.g. \\.\DISPLAY1) — survives handle reissue
        public RECT Work;
        public bool Primary;
        public int Current;
    }

    // window handle -> workspace number (any integer >= 1). The single source of truth for membership.
    private readonly Dictionary<IntPtr, int> _windowWorkspace = new();
    // workspace -> most recently focused window (restores focus after a switch, and keeps it in view).
    private readonly Dictionary<int, IntPtr> _lastActive = new();
    // workspace -> the monitor it is linked to, by stable device name (NOT HMONITOR, which is reissued on
    // sleep/wake/display-config changes). Set the first time the workspace is entered and kept until released.
    private readonly Dictionary<int, string> _workspaceHome = new();
    // device name -> the workspace currently shown on that monitor. Survives the monitor temporarily
    // dropping out of enumeration (sleep/power-off), so a wake restores what it was showing instead of 0.
    private readonly Dictionary<string, int> _shownByDevice = new();
    // HMONITOR -> state. Rebuilt from live enumeration on every RefreshMonitors; handles are never cached
    // across a display-config change.
    private readonly Dictionary<IntPtr, MonitorState> _monitors = new();

    // The scratchpad is a special roaming workspace: it has no home monitor and always shows on the
    // monitor under the mouse. It is a normal membership tag in _windowWorkspace (so windows attach to it
    // via link/move like any workspace), reserved at a value no real workspace will reach.
    public const int ScratchpadWorkspace = int.MaxValue;
    // While the scratchpad is shown, the workspace its monitor was showing beforehand — restored when the
    // scratchpad is toggled off or relocated away. 0 means "the monitor was showing nothing".
    private int _scratchpadReturn;

    private readonly WinEventDelegate _winEventProc;
    private readonly List<IntPtr> _winEventHooks = new();

    /// <summary>
    /// Raised (on the UI thread) with what EVERY live monitor is showing, whenever an operation may
    /// have changed it. It feeds the broadcast event pipe, whose subscribers (the on-screen
    /// indicator) draw one badge per monitor and therefore need all of them.
    /// </summary>
    public event Action<IReadOnlyList<(string Device, int Workspace)>>? WorkspacesChanged;

    /// <summary>
    /// What each live monitor is showing, keyed by stable device name and ordered left-to-right. Raw
    /// values: 0 = nothing shown, <see cref="ScratchpadWorkspace"/> = the scratchpad. A monitor that is
    /// asleep (out of enumeration) is absent — it has nothing on screen to label.
    /// </summary>
    public IReadOnlyList<(string Device, int Workspace)> WorkspaceSnapshot =>
        _monitors.Values
            .OrderBy(m => m.Work.Left)
            .ThenBy(m => m.Work.Top)
            .Select(m => (Device: m.Device, Workspace: m.Current))
            .ToList();

    // Every operation that can change what a monitor shows ends here.
    private void NotifyWorkspaceChanges() => WorkspacesChanged?.Invoke(WorkspaceSnapshot);

    // Window events (foreground/minimize/move) are delivered asynchronously, so events triggered by
    // our OWN minimize/restore during a switch arrive AFTER the switch returns. While the current time
    // is below this tick, ignore re-homing events so our own operations don't corrupt assignments.
    // 64-bit ticks: the 32-bit TickCount wraps negative after ~25 days of uptime.
    private const long EventGuardMs = 1500;
    private long _eventGuardUntilTick;

    public WorkspaceManager()
    {
        _winEventProc = WinEventCallback;
        Rebuild();
        InstallWinEventHooks();
    }

    public void Dispose()
    {
        DisarmLaunchPlacement();
        _reflowTimer.Dispose();

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
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(hMon, ref info))
            {
                found.Add(new MonitorState
                {
                    Handle = hMon,
                    Device = info.szDevice ?? "",
                    Work = info.rcWork,
                    Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                });
            }

            return true;
        }, IntPtr.Zero);

        // Order left-to-right (leftmost monitor = workspace 1).
        return found.OrderBy(m => m.Work.Left).ThenBy(m => m.Work.Top).ToList();
    }

    /// <summary>Home a workspace to each monitor (leftmost = 1, then 2, …). No window is assigned to any
    /// workspace at startup — membership is set only later, by moving (Win+Shift+N) or linking
    /// (Win+Space) a window. So open apps start unlinked until you claim them.</summary>
    private void Rebuild()
    {
        _monitors.Clear();
        _windowWorkspace.Clear();
        _lastActive.Clear();
        _workspaceHome.Clear();
        _shownByDevice.Clear();

        List<MonitorState> ordered = EnumerateMonitors();
        for (int i = 0; i < ordered.Count; i++)
        {
            MonitorState m = ordered[i];
            m.Current = i + 1; // leftmost monitor = workspace 1, next = 2, ...
            _monitors[m.Handle] = m;
            _workspaceHome[m.Current] = m.Device;
            _shownByDevice[m.Device] = m.Current;
        }

        LogWorkspaces("startup");
    }

    /// <summary>
    /// Reconcile the cached monitor map with the current display configuration. HMONITOR handles are
    /// only valid until the display setup changes (resolution, sleep/wake, dock/undock, driver reset);
    /// afterwards the cached handles go stale and every "is this window on this monitor" test fails — so a
    /// Win+N switch updates <see cref="MonitorState.Current"/> but minimizes/restores nothing.
    ///
    /// We key all persistent state (workspace homes, what each monitor is showing) on the stable GDI
    /// device name (\\.\DISPLAYn) rather than the volatile HMONITOR. A monitor that sleeps simply stops
    /// enumerating; we keep its shown-workspace in <see cref="_shownByDevice"/> and restore it by device
    /// name when it wakes — so a sleep/wake no longer resets the monitor to "no workspace" (WS0) or
    /// orphans its homed workspaces. Called before each switch/release.
    /// </summary>
    private void RefreshMonitors()
    {
        List<MonitorState> current = EnumerateMonitors();
        if (current.Count == 0)
        {
            return; // transient empty enumeration — keep what we had rather than wipe state
        }

        // Restore each live monitor's shown workspace from the stable per-device record. A monitor that
        // was asleep keeps the workspace it had; a brand-new monitor starts at 0 (shows nothing yet).
        foreach (MonitorState m in current)
        {
            if (_shownByDevice.TryGetValue(m.Device, out int shown))
            {
                m.Current = shown;
            }
        }

        _monitors.Clear();
        foreach (MonitorState m in current)
        {
            _monitors[m.Handle] = m;
        }
    }

    /// <summary>
    /// Handle a switch to workspace <paramref name="k"/> (any integer >= 1). Each workspace is linked
    /// to a home monitor — set the first time it's entered, to the monitor under the mouse cursor. Win+k
    /// always acts on k's home monitor: put away everything there that isn't k's — unlinked windows
    /// included — and lay out k's strip (an empty monitor the first time). Other monitors are untouched.
    /// </summary>
    public void SwitchFocusedMonitorTo(int k)
    {
        if (k < 1 || k == ScratchpadWorkspace)
        {
            return; // the scratchpad is only ever entered through its own toggle
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

        // Reconcile k's home monitor on every press (even when it already shows k), so a tile the user
        // dragged away or minimized comes back and every tile is in its column again.
        bool already = home.Current == k;
        int outgoing = home.Current;

        GuardEvents();

        // Before putting the outgoing workspace away, remember its focused tile so focus returns there.
        // (Skip on a re-press of the same workspace — nothing is changing.)
        if (!already && outgoing >= 1)
        {
            RememberFrontmost(outgoing, home);
        }

        // Put away everything on k's home monitor that isn't k's — unlinked windows too, so the monitor
        // shows only k — and the outgoing workspace's windows that float on other monitors. Membership is
        // left alone — switching never links or unlinks a window; only move (Win+Shift+N), tile (Win+Space),
        // unlink (Win+Shift+Space), or closing does.
        MinimizeMonitorWindows(home, keep: k);
        if (!already && outgoing >= 1)
        {
            PutAwayWorkspace(outgoing);
        }

        SetShown(home, k); // remember across sleep; ensure no other monitor still claims k

        // Lay out k's strip on its home monitor, scrolled to its focused tile, and bring back its floating
        // windows where they were left.
        ShowWorkspace(k, home);
        FocusWorkspace(k);
        GuardEvents();

        LogWorkspaces(already ? $"reclaim WS{k}" : $"switch WS{k}");
        NotifyWorkspaceChanges();
    }

    /// <summary>k's linked home monitor; on first entry it's pinned to the monitor under the cursor.</summary>
    private MonitorState? ResolveHome(int k)
    {
        // The workspace's home monitor, remembered by stable device name (the "last monitor known"). If
        // that monitor is present right now (awake/connected) use it, even if its HMONITOR was reissued
        // since the home was set.
        bool remembered = _workspaceHome.TryGetValue(k, out string? device) && !string.IsNullOrEmpty(device);
        if (remembered)
        {
            MonitorState? home = _monitors.Values.FirstOrDefault(m => m.Device == device);
            if (home != null)
            {
                return home;
            }
            // Last-known monitor isn't here right now (asleep/disconnected): show the workspace on the
            // mouse monitor this time, but KEEP the remembered home so the workspace returns to it once
            // that monitor is back. We do NOT overwrite the home here.
        }

        MonitorState? active = GetActiveMonitor();
        if (active != null && !remembered)
        {
            // First-ever use of this workspace: pin its home to the monitor under the mouse.
            _workspaceHome[k] = active.Device;
        }

        return active;
    }

    /// <summary>
    /// Record that <paramref name="monitor"/> now shows <paramref name="workspace"/>, persisting it by
    /// device name so it survives the monitor sleeping/dropping out of enumeration. A workspace shows on
    /// exactly one monitor at a time, so any other monitor that still claims this workspace is cleared —
    /// this matters when a workspace was displaced onto another monitor while its home slept and the home
    /// then wakes (without this, both would think they show it).
    /// </summary>
    private void SetShown(MonitorState monitor, int workspace)
    {
        if (workspace >= 1)
        {
            foreach (string dev in _shownByDevice
                         .Where(kv => kv.Value == workspace && kv.Key != monitor.Device)
                         .Select(kv => kv.Key).ToList())
            {
                _shownByDevice[dev] = 0;
                MonitorState? other = _monitors.Values.FirstOrDefault(m => m.Device == dev);
                if (other != null && other.Current == workspace)
                {
                    other.Current = 0;
                }
            }
        }

        monitor.Current = workspace;
        _shownByDevice[monitor.Device] = workspace;
    }

    /// <summary>
    /// Release the workspace currently shown on the active (cursor) monitor: put away every window on
    /// that monitor (minimized) and unpin the workspace's home, so it can be re-summoned on any monitor —
    /// `Win+N` on another monitor re-homes it there and lays its (still-linked) tiles out again.
    /// Membership is untouched: unlinked windows are just minimized and stay unlinked. The active monitor
    /// is left showing no workspace.
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
        RememberFrontmost(w, m);                // so focus returns to the same tile
        MinimizeMonitorWindows(m, keep: -1);    // put away every window on this monitor
        PutAwayWorkspace(w);                    // and the workspace's windows floating on other monitors
        if (w == ScratchpadWorkspace)
        {
            _scratchpadReturn = 0;              // released, not toggled off — nothing to return to
        }
        else
        {
            _workspaceHome.Remove(w);           // unpin: Win+w re-homes to the cursor monitor next time
        }
        m.Current = 0;                          // monitor now shows no workspace
        _shownByDevice[m.Device] = 0;
        GuardEvents();

        LogWorkspaces($"release {WsLabel(w)}");
        NotifyWorkspaceChanges();
    }

    /// <summary>
    /// Move the focused window to workspace <paramref name="n"/> (Win+Shift+N) and follow it there: the
    /// window leaves its old workspace's strip (which closes the gap), becomes a tile of n right after n's
    /// focused tile, and n is made the active (shown) workspace on its home monitor — so the moved window
    /// ends up in view and focused, on n's monitor.
    /// </summary>
    public void MoveFocusedWindowToWorkspace(int n)
    {
        if (n < 1 || n == ScratchpadWorkspace)
        {
            return; // windows join the scratchpad via link (Win+Space) while it is shown
        }

        IntPtr hWnd = GetForegroundWindow();
        if (!IsManaged(hWnd))
        {
            return;
        }

        RefreshMonitors(); // keep monitor handles live across display-config changes
        if (_monitors.Count == 0)
        {
            return;
        }

        // Explicit membership: unlink the focused window from its old workspace and link it to n, as n's
        // focused tile — so the layout below scrolls n's strip to it.
        GuardEvents();
        int left = Relink(hWnd, n, OrderJoining(n, hWnd));

        // Follow the window: make n the active workspace on its home monitor (laying out n's strip there,
        // the moved window included, and putting away the outgoing tiles), then land focus on it.
        SwitchFocusedMonitorTo(n);
        ForceForeground(hWnd);
        ReflowShown(left); // the workspace it came from, if still on screen, closes the gap
        GuardEvents();

        LogWorkspaces($"move-window WS{n}");
    }

    /// <summary>
    /// Toggle the scratchpad (Win+S). The scratchpad has no home monitor — it always appears on the
    /// monitor under the mouse, carrying its attached windows there:
    ///  * Mouse on the monitor already showing the scratchpad → hide it and restore that monitor's
    ///    previous workspace.
    ///  * Otherwise → bring the scratchpad (and its windows) onto the mouse monitor. If it was already up
    ///    on another monitor, that monitor is sent back to its previous workspace first.
    /// Windows attach to the scratchpad like any workspace: focus one while it's shown and press Win+Space
    /// (tile it there). Its tiles are laid out on whichever monitor it appears on.
    /// </summary>
    public void ToggleScratchpad()
    {
        RefreshMonitors();
        if (_monitors.Count == 0)
        {
            return;
        }

        MonitorState? target = GetActiveMonitor();
        if (target == null)
        {
            return;
        }

        // The live monitor currently showing the scratchpad, if any.
        MonitorState? showing = _monitors.Values.FirstOrDefault(m => m.Current == ScratchpadWorkspace);

        GuardEvents();

        if (showing != null && showing.Device == target.Device)
        {
            // Toggle off: the mouse is on the scratchpad's monitor — restore its previous workspace.
            RenderWorkspaceOn(showing, _scratchpadReturn);
            _scratchpadReturn = 0;

            GuardEvents();
            LogWorkspaces("scratchpad off");
            NotifyWorkspaceChanges();
            return;
        }

        // Opening or relocating onto the mouse monitor. If the scratchpad is up on another monitor, send
        // that monitor back to the workspace it was showing before the scratchpad arrived.
        if (showing != null)
        {
            RenderWorkspaceOn(showing, _scratchpadReturn);
        }

        // Remember what the mouse monitor is showing now (to return to on toggle-off), then bring the
        // scratchpad and its windows here.
        _scratchpadReturn = target.Current >= 1 ? target.Current : 0;
        RenderWorkspaceOn(target, ScratchpadWorkspace);

        GuardEvents();
        LogWorkspaces("scratchpad on");
        NotifyWorkspaceChanges();
    }

    /// <summary>
    /// Show <paramref name="workspace"/> on a specific monitor (no home resolution): put away everything there
    /// that isn't its (and the outgoing workspace's windows elsewhere), record it as shown, bring its windows
    /// onto the screen, and land focus. <paramref name="workspace"/> &lt; 1 means "show nothing".
    /// </summary>
    private void RenderWorkspaceOn(MonitorState monitor, int workspace)
    {
        int outgoing = monitor.Current;
        bool changing = outgoing >= 1 && outgoing != workspace;
        if (changing)
        {
            RememberFrontmost(outgoing, monitor);
        }

        MinimizeMonitorWindows(monitor, keep: workspace);
        if (changing)
        {
            PutAwayWorkspace(outgoing);
        }

        SetShown(monitor, workspace);

        if (workspace >= 1)
        {
            ShowWorkspace(workspace, monitor);
            FocusWorkspace(workspace);
        }
    }

    private void GuardEvents()
    {
        _eventGuardUntilTick = Environment.TickCount64 + EventGuardMs;
    }

    private bool EventsGuarded
    {
        get { return Environment.TickCount64 < _eventGuardUntilTick; }
    }

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

    // Remember a workspace's frontmost tile on this monitor as the one to focus when it comes back.
    // EnumWindows yields windows in Z order, topmost first, so the first match is the frontmost. Trust it
    // over _lastActive, which is fed by the foreground event hook and can miss a click made during the
    // event guard window (e.g. clicking a window then immediately toggling the scratchpad).
    private void RememberFrontmost(int workspace, MonitorState monitor)
    {
        if (workspace < 1)
        {
            return;
        }

        EnumWindows((h, _) =>
        {
            if (!_windowWorkspace.TryGetValue(h, out int ws) || ws != workspace || !IsManaged(h) || IsIconic(h)
                || MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != monitor.Handle)
            {
                return true;
            }

            _lastActive[workspace] = h;
            return false;
        }, IntPtr.Zero);
    }

    // Minimize every visible managed window physically on the monitor, except the windows of workspace
    // <paramref name="keep"/> (pass -1 to keep none) — unlinked windows and other workspaces' floating
    // windows included, so the monitor shows only the workspace being switched to (an empty workspace
    // means an empty monitor). Membership is NOT touched: a put-away member returns with its workspace, and
    // an unlinked window stays unlinked. Windows are linked only by an explicit command, never by switching.
    private void MinimizeMonitorWindows(MonitorState monitor, int keep)
    {
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
                return true; // a window of the workspace we're showing
            }

            ShowWindow(h, SW_MINIMIZE);
            return true;
        }, IntPtr.Zero);
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
            _floating.Remove(h);
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
        if (_lastActive.TryGetValue(workspace, out IntPtr last) && IsWindow(last) && IsOnScreen(last)
            && _windowWorkspace.TryGetValue(last, out int ws) && ws == workspace)
        {
            target = last;
        }
        else
        {
            target = WindowsOf(workspace).FirstOrDefault(IsOnScreen);
        }

        if (target != IntPtr.Zero)
        {
            ForceForeground(target);
            _lastActive[workspace] = target;
        }
    }

    // Shown and not minimized: not hidden by its app (closed to the tray), and not cloaked by the shell —
    // which keeps WS_VISIBLE on a closed Store app, a closed shell panel, or a window on another virtual
    // desktop while nothing of it is on screen.
    private static bool IsOnScreen(IntPtr hWnd) => IsWindowVisible(hWnd) && !IsIconic(hWnd) && !IsWindowCloaked(hWnd);

    // Bring a window to the foreground. We avoid AttachThreadInput (it can corrupt keyboard input
    // state when attaching to the shell thread); briefly clearing the foreground-lock timeout is safe.
    private void ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        WithForegroundLockLifted(() =>
        {
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
        });
    }

    // Run an action with the system foreground-lock timeout at 0, which lets this background process
    // activate and raise other processes' windows; the user's setting is always put back.
    private static void WithForegroundLockLifted(Action action)
    {
        uint original = 0;
        bool got = SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref original, 0);
        if (got)
        {
            SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, UIntPtr.Zero, 0);
        }

        try
        {
            action();
        }
        finally
        {
            if (got)
            {
                SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (UIntPtr)original, 0);
            }
        }
    }

    private bool TryGetWindowMonitor(IntPtr hWnd, out MonitorState? monitor)
    {
        // Use the DISPLAYED monitor: a maximized window moved with Win+Shift+Arrow keeps a restored rect
        // on its old monitor. Only a minimized (off-screen) window resolves by its restored rect.
        IntPtr hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (IsIconic(hWnd) && GetWindowPlacement(hWnd, ref wp))
        {
            RECT norm = wp.rcNormalPosition;
            hMon = MonitorFromRect(ref norm, MONITOR_DEFAULTTONEAREST);
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

        return !IsWindowCloaked(hWnd) && !IsShellWindow(hWnd);
    }

    // The desktop, and the shell's own panels: Start, Search, notifications and the touch keyboard are all
    // top-level CoreWindows. Open, they look like app windows (titled, unowned, not tool windows); closed,
    // the shell cloaks them instead of hiding them — so a linked one would hold an invisible column.
    // (A Store app's tileable window is its ApplicationFrameWindow, not a top-level CoreWindow.)
    private static bool IsShellWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(256);
        int length = GetClassName(hWnd, className, className.Capacity);
        if (length <= 0)
        {
            return false;
        }

        string name = className.ToString(0, length);
        return string.Equals(name, "Progman", StringComparison.Ordinal)
            || string.Equals(name, "WorkerW", StringComparison.Ordinal)
            || string.Equals(name, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal);
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
        // FOREGROUND: track each workspace's last-focused window (for focus restore on switch), and scroll a
        // strip to a tile focused by hand. Tiling adds DESTROY/SHOW/HIDE and MOVESIZEEND (InstallTileHooks).
        //
        // No hook mutates membership. Membership is changed only by Win+Shift+N (move), Win+Space (tile) and
        // Win+Shift+Space (unlink); closed windows are pruned lazily and race-free in WindowsOf (see below).
        // EVENT_OBJECT_DESTROY is delivered asynchronously on this (sometimes stalled) thread, so during a
        // sleep/wake window-churn a backlogged DESTROY can carry a recycled HWND that now belongs to a
        // still-open window — acting on its handle would silently unlink that window. Tiling therefore only
        // re-lays out on it, which re-checks every member.
        AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, _winEventHooks);
        InstallTileHooks();
    }

    private void AddHook(uint eventMin, uint eventMax, List<IntPtr> hooks)
    {
        IntPtr hook = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
        {
            hooks.Add(hook);
        }
    }

    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hWnd == IntPtr.Zero)
        {
            return;
        }

        // Before the guard: an app launched right after a workspace switch must still be placed.
        TryPlaceLaunchedWindow(hWnd);

        // Everything below is about tiles; a window that belongs to no workspace is not ours to manage.
        if (!_windowWorkspace.TryGetValue(hWnd, out int workspace))
        {
            return;
        }

        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            // Ignore focus changes caused by our own switch operations, and non-app windows.
            if (EventsGuarded || !IsManaged(hWnd))
            {
                return;
            }

            // Window membership is NOT changed by interaction: activating, restoring, maximizing or dragging
            // a window never pulls it into the workspace shown on its monitor. Here we just remember the
            // last-focused tile of its workspace, so focus can be restored when that workspace is shown again.
            _lastActive[workspace] = hWnd;
        }

        OnTileEvent(eventType, hWnd, workspace);
    }

    private void LogWorkspaces(string action)
    {
        var parts = _monitors.Values
            .OrderBy(m => m.Work.Left)
            .Select((m, i) => $"mon{i + 1}={WsLabel(m.Current)}");
        string line = $"[{action}] {string.Join("  ", parts)}";
        Log.Line(line);
    }

    private static string WsLabel(int workspace) =>
        workspace == ScratchpadWorkspace ? "SCRATCH" : $"WS{workspace}";

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
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    private const uint SWP_NOACTIVATE = 0x0010;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
