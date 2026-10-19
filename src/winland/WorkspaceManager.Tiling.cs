using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Winland.Common;

namespace Winland;

/// <summary>
/// Tiling. A workspace's members are its windows, and each is either a tile or floating:
///
///  * Tiles stand side by side in a scrolling strip on the monitor showing the workspace (geometry in
///    <see cref="StripLayout"/>, order in <see cref="TileOrder"/>). A window becomes a tile when the user
///    tiles it (Win+Space), moves it to a workspace (Win+Shift+N), or opens it from a Winland shortcut.
///  * A tile the user drags or resizes with the mouse leaves the strip and floats: Windows manages it,
///    exactly where the user put it — even on another monitor — but it stays in its workspace, so it is put
///    away and brought back with it. Win+Space tiles it again, back at its place in the strip.
///
/// Windows that belong to no workspace are normal Windows windows: never tiled or resized, and only put
/// away (minimized) when their monitor switches workspace, like everything else on that monitor.
///
/// The strip is laid out whenever its contents or view change: a tile is added, removed, moved or focused
/// past the edge of the view, a workspace is shown, or a tile closes, hides, comes back or floats away.
/// Tiles outside the view are minimized — they never sit off-screen, where they would show up on the
/// neighbouring monitor.
/// </summary>
internal sealed partial class WorkspaceManager
{
    // Event-driven layouts wait this long, so a burst of events (an app closing several windows) costs one.
    private const int ReflowDelayMs = 50;

    // A dragged tile whose frame is still this close to its column wasn't really moved (a click on its title
    // bar starts and ends a move too).
    private const int MovedTolerance = 2;

    // workspace -> its members, left to right: the order of the strip. Ordering metadata only: _windowWorkspace
    // stays the membership authority, and the order is reconciled with it whenever it is read (TilesOf).
    // Floating members keep their place, so tiling one again puts it back where it was.
    private readonly Dictionary<int, List<IntPtr>> _tileOrder = new();
    // Members that are floating rather than tiled (see the class summary). Everything else in _windowWorkspace is a tile.
    private readonly HashSet<IntPtr> _floating = new();
    // workspace -> index of the first tile its view shows: the strip's scroll position.
    private readonly Dictionary<int, int> _scrollFirst = new();
    // workspace -> what its last layout put where (for "is this tile in view" and "was this tile moved").
    private readonly Dictionary<int, Arrangement> _arranged = new();

    // Window events only queue work; this UI-thread timer does it (see OnTileEvent).
    private readonly System.Windows.Forms.Timer _reflowTimer = new() { Interval = ReflowDelayMs };
    private readonly Dictionary<int, IntPtr> _pendingReflows = new();     // workspace -> tile to scroll to, or Zero
    private readonly HashSet<IntPtr> _pendingDrags = new();               // tiles the user dragged or resized
    private readonly Dictionary<IntPtr, string> _pendingLaunches = new(); // window opened by a shortcut -> its monitor

    /// <summary>One layout of one strip: its tiles in order, and where each went.</summary>
    private sealed record Arrangement(IntPtr[] Tiles, StripArrangement Strip)
    {
        public bool Shows(IntPtr hWnd) => ColumnOf(hWnd) != null;

        /// <summary>The column a tile was shown in, or null when it isn't in view (or isn't a tile here).</summary>
        public TileRect? ColumnOf(IntPtr hWnd)
        {
            int index = Array.IndexOf(Tiles, hWnd);
            return index >= 0 && Strip.Slots[index].Visible ? Strip.Slots[index].Rect : null;
        }
    }

    private bool IsTile(IntPtr hWnd) => _windowWorkspace.ContainsKey(hWnd) && !_floating.Contains(hWnd);

    /// <summary>
    /// Tile the focused window (Win+Space): link it to the workspace shown on its monitor and put it into that
    /// workspace's strip — a new window right after the tile that had focus, a floating member back at its
    /// old place; the other tiles make room. On a tile there, it snaps the tile back into its column. A
    /// no-op when the window's monitor shows no workspace.
    /// </summary>
    public void LinkFocusedWindowToCurrentWorkspace()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (!IsManaged(hWnd))
        {
            return;
        }

        RefreshMonitors(); // keep monitor handles live across display-config changes
        if (!TryGetWindowMonitor(hWnd, out MonitorState? monitor) || monitor!.Current < 1)
        {
            return;
        }

        int workspace = monitor.Current;

        GuardEvents();
        int left = Relink(hWnd, workspace, OrderJoining(workspace, hWnd));
        LayoutWorkspace(workspace, monitor, hWnd);
        ReflowShown(left);
        GuardEvents();

        LogTiles($"link {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>
    /// Unlink the focused window (Win+Shift+Space): it leaves its workspace and becomes a normal window
    /// again, left where it is and in front; if it was a tile, the remaining tiles close the gap.
    /// </summary>
    public void UnlinkFocusedWindow()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (!_windowWorkspace.TryGetValue(hWnd, out int workspace))
        {
            return;
        }

        RefreshMonitors();

        GuardEvents();
        _windowWorkspace.Remove(hWnd);
        _floating.Remove(hWnd);
        ForgetTile(hWnd, workspace);
        ReflowShown(workspace);
        ForceForeground(hWnd); // above the tiles that just moved in under it
        GuardEvents();

        LogTiles($"unlink {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>
    /// Pop the focused window out of tiling, or put it back (Win+T):
    ///  * A tile floats where it is — still in its workspace — and the strip closes the gap, as if the user
    ///    had dragged it out.
    ///  * A floating window goes back into its OWN workspace's strip, at its old place, wherever it was
    ///    floating (unlike Win+Space, which takes the workspace shown on the window's monitor). If that
    ///    workspace isn't on screen, it is shown on its home monitor with the window in view.
    ///  * A window in no workspace is tiled into the workspace shown on its monitor, like Win+Space.
    /// </summary>
    public void ToggleFloating()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (!IsManaged(hWnd))
        {
            return;
        }

        if (!_windowWorkspace.TryGetValue(hWnd, out int workspace))
        {
            LinkFocusedWindowToCurrentWorkspace();
            return;
        }

        RefreshMonitors();

        GuardEvents();
        if (!_floating.Contains(hWnd))
        {
            _floating.Add(hWnd);
            ReflowShown(workspace);
            ForceForeground(hWnd); // above the tiles that just moved in under it
            GuardEvents();

            LogTiles($"float {WsLabel(workspace)}", workspace, hWnd);
            return;
        }

        Relink(hWnd, workspace, OrderJoining(workspace, hWnd)); // its own workspace: back at its old place
        MonitorState? monitor = MonitorShowing(workspace);
        if (monitor != null)
        {
            LayoutWorkspace(workspace, monitor, hWnd);
        }
        GuardEvents();

        LogTiles($"tile {WsLabel(workspace)}", workspace, hWnd);

        if (monitor == null)
        {
            // Tiles show only with their workspace. Show it, with this window in view and focused — or, for
            // the scratchpad (which only its own toggle shows), put the window away with it.
            if (workspace == ScratchpadWorkspace)
            {
                ShowWindow(hWnd, SW_MINIMIZE);
            }
            else
            {
                SwitchFocusedMonitorTo(workspace);
            }
        }
    }

    /// <summary>
    /// Focus the neighbouring tile to the left or right of the focused tile, scrolling the strip when it is
    /// out of view. Returns false — so the caller can fall back to plain directional focus — when the focused
    /// window isn't a tile on screen, the direction isn't left or right, or the strip ends there.
    /// </summary>
    public bool FocusAdjacentTile(Direction direction)
    {
        IntPtr current = GetForegroundWindow();
        if (direction is not (Direction.Left or Direction.Right)
            || !IsTile(current)
            || !_windowWorkspace.TryGetValue(current, out int workspace))
        {
            return false;
        }

        RefreshMonitors();
        MonitorState? monitor = MonitorShowing(workspace);
        if (monitor == null)
        {
            return false;
        }

        List<IntPtr> tiles = TilesOf(workspace);
        int index = tiles.IndexOf(current);
        int next = index + Step(direction);
        if (index < 0 || next < 0 || next >= tiles.Count)
        {
            return false;
        }

        IntPtr target = tiles[next];

        GuardEvents();
        _lastActive[workspace] = target;
        LayoutWorkspace(workspace, monitor, target);
        ForceForeground(target);
        GuardEvents();
        return true;
    }

    /// <summary>
    /// Move the focused window one step left or right (Win+Shift+Left/Right). A tile trades places with its
    /// neighbour; at the end of its strip it crosses to the monitor on that side and joins the workspace
    /// shown there, entering at the near end. Any other window — floating, or in no workspace — hops to
    /// the neighbouring monitor and keeps its membership.
    /// </summary>
    public void MoveFocusedWindow(Direction direction)
    {
        IntPtr hWnd = GetForegroundWindow();
        if (direction is not (Direction.Left or Direction.Right) || !IsManaged(hWnd))
        {
            return;
        }

        RefreshMonitors();

        GuardEvents();
        if (IsTile(hWnd) && MonitorShowing(_windowWorkspace[hWnd]) is MonitorState home)
        {
            MoveTile(hWnd, _windowWorkspace[hWnd], home, direction);
        }
        else if (TryGetWindowMonitor(hWnd, out MonitorState? from) && AdjacentMonitor(from!, direction) is MonitorState to)
        {
            MoveWindowToMonitor(hWnd, to);
            ForceForeground(hWnd);
        }
        GuardEvents();
    }

    private void MoveTile(IntPtr hWnd, int workspace, MonitorState home, Direction direction)
    {
        List<IntPtr> tiles = TilesOf(workspace);
        int index = tiles.IndexOf(hWnd);
        int next = index + Step(direction);
        if (index >= 0 && next >= 0 && next < tiles.Count)
        {
            _tileOrder[workspace] = TileOrder.MoveTo(OrderOf(workspace), hWnd, tiles[next]);
            LayoutWorkspace(workspace, home, hWnd);
            LogTiles($"move tile {direction.ToString().ToLowerInvariant()}", workspace, hWnd);
            return;
        }

        // At the end of the strip: cross to the next monitor, if it shows a workspace to join. Entering
        // through the near end — moving left lands at the right end of the left monitor's strip.
        MonitorState? neighbour = AdjacentMonitor(home, direction);
        if (neighbour == null || neighbour.Current < 1)
        {
            return;
        }

        int entry = direction == Direction.Left ? int.MaxValue : 0;
        MoveTileTo(hWnd, neighbour, TileOrder.InsertAt(OrderOf(neighbour.Current), hWnd, entry));
    }

    /// <summary>Make a tile part of the workspace shown on <paramref name="monitor"/>, in <paramref name="order"/>.</summary>
    private void MoveTileTo(IntPtr hWnd, MonitorState monitor, List<IntPtr> order)
    {
        int workspace = monitor.Current;
        int left = Relink(hWnd, workspace, order);
        LayoutWorkspace(workspace, monitor, hWnd);
        ReflowShown(left);
        ForceForeground(hWnd);

        LogTiles($"move tile to {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>
    /// Make <paramref name="hWnd"/> a tile of <paramref name="workspace"/>, with the strip in
    /// <paramref name="order"/>, and the one to focus there. Returns the workspace it left, or 0.
    /// </summary>
    private int Relink(IntPtr hWnd, int workspace, List<IntPtr> order)
    {
        int left = _windowWorkspace.TryGetValue(hWnd, out int previous) && previous != workspace ? previous : 0;
        if (left != 0)
        {
            ForgetTile(hWnd, left);
        }

        _windowWorkspace[hWnd] = workspace;
        _floating.Remove(hWnd);
        _tileOrder[workspace] = order;
        _lastActive[workspace] = hWnd;
        return left;
    }

    /// <summary>
    /// The strip order of <paramref name="workspace"/> once <paramref name="hWnd"/> is in it: unchanged for
    /// one of its own members (a floating one returns to its old place), otherwise with the window inserted
    /// right after the workspace's focused tile.
    /// </summary>
    private List<IntPtr> OrderJoining(int workspace, IntPtr hWnd)
    {
        bool member = _windowWorkspace.TryGetValue(hWnd, out int current) && current == workspace;
        return member ? OrderOf(workspace) : TileOrder.InsertAfter(OrderOf(workspace), hWnd, FocusTargetOf(workspace));
    }

    private void ForgetTile(IntPtr hWnd, int workspace)
    {
        _tileOrder[workspace] = TileOrder.Remove(OrderOf(workspace), hWnd);
        if (_lastActive.TryGetValue(workspace, out IntPtr last) && last == hWnd)
        {
            _lastActive.Remove(workspace);
        }
    }

    private List<IntPtr> OrderOf(int workspace) =>
        _tileOrder.TryGetValue(workspace, out List<IntPtr>? order) ? order : new List<IntPtr>();

    /// <summary>
    /// A workspace's tiles that take a column, left to right. Reconciles the stored order with membership
    /// first (closed windows are pruned there). Floating members, members their app has hidden (closed to
    /// the tray), and members the shell has cloaked (a closed Store app, a window on another virtual
    /// desktop) keep their place in the order but take no column — nothing of them is on screen.
    /// </summary>
    private List<IntPtr> TilesOf(int workspace)
    {
        List<IntPtr> order = TileOrder.Reconcile(OrderOf(workspace), WindowsOf(workspace));
        _tileOrder[workspace] = order;
        return order.Where(h => !_floating.Contains(h) && IsWindowVisible(h) && !IsWindowCloaked(h)).ToList();
    }

    /// <summary>The tile that should have focus in a workspace: its last-active tile, while it is still one.</summary>
    private IntPtr FocusTargetOf(int workspace) =>
        _lastActive.TryGetValue(workspace, out IntPtr last)
        && IsWindow(last)
        && IsWindowVisible(last)
        && !IsWindowCloaked(last)
        && _windowWorkspace.TryGetValue(last, out int owner)
        && owner == workspace
            ? last
            : IntPtr.Zero;

    private MonitorState? MonitorShowing(int workspace) =>
        workspace < 1 ? null : _monitors.Values.FirstOrDefault(m => m.Current == workspace);

    /// <summary>Lay out a workspace again if a monitor shows it, keeping its focused tile in view.</summary>
    private void ReflowShown(int workspace, IntPtr reveal = default)
    {
        MonitorState? monitor = MonitorShowing(workspace);
        if (monitor != null)
        {
            LayoutWorkspace(workspace, monitor, reveal != IntPtr.Zero ? reveal : FocusTargetOf(workspace));
        }
    }

    /// <summary>
    /// Bring a workspace's windows onto the screen and land focus there: its strip laid out on
    /// <paramref name="monitor"/>, its floating windows back where the user left them — on whichever monitor
    /// that is — and raised above the tiles. (The scratchpad roams, so its floating windows come along to
    /// its monitor instead.) Returns the window it focused, or Zero.
    /// </summary>
    private IntPtr ShowWorkspace(int workspace, MonitorState monitor)
    {
        LayoutWorkspace(workspace, monitor, FocusTargetOf(workspace));

        foreach (IntPtr hWnd in WindowsOf(workspace))
        {
            if (!_floating.Contains(hWnd) || !IsWindowVisible(hWnd) || IsHungAppWindow(hWnd))
            {
                continue;
            }

            if (workspace == ScratchpadWorkspace
                && TryGetWindowMonitor(hWnd, out MonitorState? at) && at!.Device != monitor.Device)
            {
                MoveWindowToMonitor(hWnd, monitor);
            }
            else if (IsIconic(hWnd))
            {
                RestoreInPlace(hWnd);
            }
        }

        // Focus first: focusing raises the focused window, which would cover a floating one again.
        IntPtr focused = FocusWorkspace(workspace);
        RaiseFloating(workspace);
        return focused;
    }

    /// <summary>
    /// Floating windows stand above the tiles. Restored without being activated, a floating window comes
    /// back at its old place in the Z order — often under the tiles of the monitor it sits on, invisible.
    /// Raise the workspace's floating windows on screen to the top, keeping their order among themselves,
    /// without focusing any of them. Windows the user made always-on-top are already above and left alone.
    /// </summary>
    private void RaiseFloating(int workspace)
    {
        var floating = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (_floating.Contains(h) && _windowWorkspace.TryGetValue(h, out int owner) && owner == workspace
                && IsOnScreen(h) && !IsTopmost(h) && !IsHungAppWindow(h))
            {
                floating.Add(h);
            }

            return true;
        }, IntPtr.Zero);

        // EnumWindows runs top to bottom: raise from the bottom up, so the topmost one ends on top again.
        // A plain HWND_TOP from a process that isn't in the foreground is silently ignored (SetWindowPos
        // returns TRUE and the Z order doesn't change). Passing through the topmost band and straight back
        // out is honoured: it leaves the window at the top of the normal band, still not activated.
        for (int i = floating.Count - 1; i >= 0; i--)
        {
            SetWindowPos(floating[i], HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(floating[i], HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Put away all of a workspace's windows, wherever they are — including floating ones the user left on
    /// another monitor — so none of them stays on screen once the workspace isn't shown.
    /// </summary>
    private void PutAwayWorkspace(int workspace)
    {
        foreach (IntPtr hWnd in WindowsOf(workspace))
        {
            if (IsManaged(hWnd) && !IsIconic(hWnd) && !IsHungAppWindow(hWnd))
            {
                ShowWindow(hWnd, SW_MINIMIZE);
            }
        }
    }

    /// <summary>Un-minimize a window into its own last position (maximized again if it was), without focusing it.</summary>
    private static void RestoreInPlace(IntPtr hWnd)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hWnd, ref wp))
        {
            wp.showCmd = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0 ? SW_SHOWMAXIMIZED : SW_SHOWNOACTIVATE;
            SetWindowPlacement(hWnd, ref wp);
        }
    }

    /// <summary>
    /// Arrange a workspace's strip on <paramref name="monitor"/>, scrolled the least needed to show
    /// <paramref name="reveal"/> (Zero: keep the current scroll position). Visible tiles are restored into
    /// their columns; the rest are minimized.
    /// </summary>
    private void LayoutWorkspace(int workspace, MonitorState monitor, IntPtr reveal)
    {
        List<IntPtr> tiles = TilesOf(workspace);
        RECT work = monitor.Work;
        StripArrangement strip = StripLayout.Arrange(
            new TileRect(work.Left, work.Top, work.Right, work.Bottom),
            DpiOf(monitor.Handle),
            tiles.Count,
            _scrollFirst.GetValueOrDefault(workspace),
            tiles.IndexOf(reveal));

        _scrollFirst[workspace] = strip.First;
        _arranged[workspace] = new Arrangement(tiles.ToArray(), strip);

        // Tiles leaving the view go first, so they are never on screen together with the ones entering it.
        for (int i = 0; i < tiles.Count; i++)
        {
            if (!strip.Slots[i].Visible)
            {
                HideTile(tiles[i], strip.Slots[i].Rect);
            }
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            if (strip.Slots[i].Visible)
            {
                ShowTile(tiles[i], strip.Slots[i].Rect);
            }
        }
    }

    private static void ShowTile(IntPtr hWnd, TileRect column)
    {
        // A hung app would block this thread — and with it every shortcut — until it recovers. Skip it.
        if (IsHungAppWindow(hWnd))
        {
            return;
        }

        if (IsIconic(hWnd) || IsZoomed(hWnd))
        {
            // Back to the normal state, straight into the column, without taking focus.
            SetPlacement(hWnd, column, SW_SHOWNOACTIVATE);
        }

        // Windows have an invisible resize border outside their visible frame. Grow the window by it, so the
        // visible edges — not the invisible ones — line up with the column and the gaps come out even.
        RECT border = InvisibleBorder(hWnd);
        SetWindowPos(hWnd, IntPtr.Zero,
            column.Left - border.Left,
            column.Top - border.Top,
            column.Width + border.Left + border.Right,
            column.Height + border.Top + border.Bottom,
            SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Put a tile out of view: minimized, never off-screen (it would land on the neighbouring monitor). Its
    /// restore position is the column it would scroll in through, so restored by hand — taskbar, Alt+Tab —
    /// it reappears on its own monitor, and the foreground hook then scrolls the strip to it.
    /// </summary>
    private static void HideTile(IntPtr hWnd, TileRect column)
    {
        if (!IsHungAppWindow(hWnd))
        {
            SetPlacement(hWnd, column, SW_SHOWMINNOACTIVE);
        }
    }

    private static void SetPlacement(IntPtr hWnd, TileRect rect, uint showCmd)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
        {
            return;
        }

        wp.flags &= ~WPF_RESTORETOMAXIMIZED; // restore to the column, not to maximized
        wp.showCmd = showCmd;
        wp.rcNormalPosition = ToWorkspaceCoordinates(rect);
        SetWindowPlacement(hWnd, ref wp);
    }

    /// <summary>
    /// WINDOWPLACEMENT positions of app windows are in workspace coordinates — relative to the primary
    /// monitor's work area rather than the screen — which differ whenever the taskbar is at the top or left.
    /// </summary>
    private static RECT ToWorkspaceCoordinates(TileRect rect)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        IntPtr primary = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        int dx = 0;
        int dy = 0;
        if (GetMonitorInfo(primary, ref info))
        {
            dx = info.rcWork.Left - info.rcMonitor.Left;
            dy = info.rcWork.Top - info.rcMonitor.Top;
        }

        return new RECT { Left = rect.Left - dx, Top = rect.Top - dy, Right = rect.Right - dx, Bottom = rect.Bottom - dy };
    }

    /// <summary>The invisible resize border on each side: the window rect minus the visible (DWM) frame.</summary>
    private static RECT InvisibleBorder(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out RECT window)
            || DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
        {
            return default;
        }

        return new RECT
        {
            Left = BorderWidth(frame.Left - window.Left),
            Top = BorderWidth(frame.Top - window.Top),
            Right = BorderWidth(window.Right - frame.Right),
            Bottom = BorderWidth(window.Bottom - frame.Bottom)
        };
    }

    // Real borders are a few pixels (more at high DPI); anything else is a transient reading, so ignore it.
    private static int BorderWidth(int pixels) => pixels is >= 0 and <= MaxInvisibleBorder ? pixels : 0;

    private static int DpiOf(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? (int)dpi : 96;

    private static int Step(Direction direction) => direction == Direction.Left ? -1 : 1;

    /// <summary>The nearest monitor in <paramref name="direction"/>, judged by work-area centres.</summary>
    private MonitorState? AdjacentMonitor(MonitorState from, Direction direction)
    {
        (int x, int y) = Centre(from.Work);
        bool horizontal = direction is Direction.Left or Direction.Right;

        return _monitors.Values
            .Where(m => m.Device != from.Device)
            .Select(m => (Monitor: m, Dx: Centre(m.Work).X - x, Dy: Centre(m.Work).Y - y))
            .Where(c => direction switch
            {
                Direction.Left => c.Dx < 0,
                Direction.Right => c.Dx > 0,
                Direction.Up => c.Dy < 0,
                _ => c.Dy > 0
            })
            .OrderBy(c => horizontal ? Math.Abs(c.Dx) : Math.Abs(c.Dy))
            .ThenBy(c => horizontal ? Math.Abs(c.Dy) : Math.Abs(c.Dx))
            .Select(c => c.Monitor)
            .FirstOrDefault();
    }

    private static (int X, int Y) Centre(RECT r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    // ----- Reacting to window events -----

    private void InstallTileHooks()
    {
        _reflowTimer.Tick += OnReflowTick;

        // DESTROY..HIDE (also SHOW, between them): a tile closed, was hidden, or came back; CLOAKED/UNCLOAKED:
        // the shell took it off screen or put it back (a Store app closed or reopened, a virtual-desktop
        // switch). These events only ever trigger a layout: membership itself is still pruned lazily
        // (WindowsOf), so a late event carrying a recycled handle costs at most a needless layout, never a
        // window's membership.
        AddHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, _winEventHooks);
        AddHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, _winEventHooks);
        // The user finished dragging or resizing a window.
        AddHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, _winEventHooks);
    }

    /// <summary>
    /// React to an event on a member of <paramref name="workspace"/>. Work is only queued here and done a
    /// moment later on the UI thread (<see cref="OnReflowTick"/>): that coalesces bursts, and keeps layout
    /// out of this callback, which can run in the middle of a workspace operation.
    /// </summary>
    private void OnTileEvent(uint eventType, IntPtr hWnd, int workspace)
    {
        if (_floating.Contains(hWnd))
        {
            return; // floating windows take no column, and Windows manages them
        }

        switch (eventType)
        {
            case EVENT_SYSTEM_MOVESIZEEND:
                // Our own layout never drags windows, so this is always the user moving or resizing a tile.
                _pendingDrags.Add(hWnd);
                _reflowTimer.Start();
                break;

            case EVENT_OBJECT_DESTROY:
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_HIDE:
            case EVENT_OBJECT_CLOAKED:
            case EVENT_OBJECT_UNCLOAKED:
                QueueReflow(workspace, IntPtr.Zero); // open or close its column
                break;

            case EVENT_SYSTEM_FOREGROUND:
                // Focused by hand (taskbar, Alt+Tab) while scrolled out of view: scroll to it.
                if (MonitorShowing(workspace) != null
                    && !(_arranged.TryGetValue(workspace, out Arrangement? arranged) && arranged.Shows(hWnd)))
                {
                    QueueReflow(workspace, hWnd);
                }

                break;
        }
    }

    private void QueueReflow(int workspace, IntPtr reveal)
    {
        // A specific tile to scroll to wins over a plain reflow of the same workspace.
        if (reveal != IntPtr.Zero || !_pendingReflows.ContainsKey(workspace))
        {
            _pendingReflows[workspace] = reveal;
        }

        _reflowTimer.Start();
    }

    /// <summary>A window opened from a Winland shortcut, on <paramref name="device"/>'s monitor: tile it there.</summary>
    private void QueueLaunchTile(IntPtr hWnd, string device)
    {
        _pendingLaunches[hWnd] = device;
        _reflowTimer.Start();
    }

    private void OnReflowTick(object? sender, EventArgs e)
    {
        _reflowTimer.Stop();

        List<KeyValuePair<IntPtr, string>> launches = _pendingLaunches.ToList();
        List<IntPtr> drags = _pendingDrags.ToList();
        List<KeyValuePair<int, IntPtr>> reflows = _pendingReflows.ToList();
        _pendingLaunches.Clear();
        _pendingDrags.Clear();
        _pendingReflows.Clear();

        RefreshMonitors();
        foreach ((IntPtr hWnd, string device) in launches)
        {
            TileLaunchedWindow(hWnd, device);
        }

        foreach (IntPtr hWnd in drags)
        {
            FloatIfMoved(hWnd);
        }

        foreach ((int workspace, IntPtr reveal) in reflows)
        {
            ReflowShown(workspace, reveal);
        }
    }

    /// <summary>
    /// Opened from a Winland shortcut: the window joins the workspace shown on the monitor it was placed on,
    /// as a tile right after the focused one. (Windows opened any other way stay unlinked until Win+Space.)
    /// </summary>
    private void TileLaunchedWindow(IntPtr hWnd, string device)
    {
        MonitorState? monitor = _monitors.Values.FirstOrDefault(m => m.Device == device);
        if (monitor == null || monitor.Current < 1 || !IsManaged(hWnd) || _windowWorkspace.ContainsKey(hWnd))
        {
            return;
        }

        int workspace = monitor.Current;
        Relink(hWnd, workspace, OrderJoining(workspace, hWnd));
        LayoutWorkspace(workspace, monitor, hWnd);

        LogTiles($"tile launched {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>
    /// The user dragged or resized a tile with the mouse: it leaves the strip and floats where they put it —
    /// still a member of its workspace, even when dropped on another monitor — and the strip closes the
    /// gap. A tile still in its column wasn't really moved (a click on the title bar) and stays a tile, and so
    /// does a maximized or minimized one: those are states of the tile (a double-click on the title bar
    /// maximizes through the same move loop), and its next layout puts it back in its column.
    /// </summary>
    private void FloatIfMoved(IntPtr hWnd)
    {
        if (!IsWindow(hWnd) || !IsTile(hWnd) || IsZoomed(hWnd) || VisibleFrame(hWnd) is not TileRect frame)
        {
            return;
        }

        int workspace = _windowWorkspace[hWnd];
        if (_arranged.TryGetValue(workspace, out Arrangement? arranged)
            && arranged.ColumnOf(hWnd) is TileRect column
            && frame.IsNear(column, MovedTolerance))
        {
            return;
        }

        _floating.Add(hWnd);
        ReflowShown(workspace);

        LogTiles($"float {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>The window's visible frame on screen (without the invisible resize border), or null if minimized.</summary>
    private static TileRect? VisibleFrame(IntPtr hWnd)
    {
        if (IsIconic(hWnd)
            || DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
        {
            return null;
        }

        return new TileRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    private void LogTiles(string action, int workspace, IntPtr window)
    {
        int count = _arranged.TryGetValue(workspace, out Arrangement? arranged) ? arranged.Tiles.Length : 0;
        Log.Line($"[{action}] {WsLabel(workspace)}: {ProcessNameOf(window)}; {count} tile(s), view from tile {_scrollFirst.GetValueOrDefault(workspace) + 1}");
    }

    // Which app a window belongs to, for the log — so a surprising tile can be traced to its window.
    private static string ProcessNameOf(IntPtr hWnd)
    {
        if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == 0)
        {
            return "(gone)";
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "(gone)";
        }
    }

    // ----- Win32 -----

    private const uint SW_SHOWNOACTIVATE = 4;
    private const uint SW_SHOWMINNOACTIVE = 7;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int MaxInvisibleBorder = 64;

    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_HIDE = 0x8003; // EVENT_OBJECT_SHOW (0x8002) is shared with launch placement
    private const uint EVENT_OBJECT_CLOAKED = 0x8017; // EVENT_OBJECT_UNCLOAKED (0x8018) is shared with launch placement

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
