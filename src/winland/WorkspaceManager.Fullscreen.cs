using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Winland.Common;

namespace Winland;

/// <summary>
/// Fullscreen tiles (Win+Alt+F, "toggle-fullscreen"). A tile can cover its whole monitor, taskbar
/// included, without leaving its strip: it stays a tile of its workspace and keeps its column, reserved
/// behind it (<see cref="StripLayout.WithFullscreen"/>), so toggling again puts it straight back there.
///
/// Fullscreen is part of the tile's layout, so it lasts across layouts: the workspace put away and shown
/// again, moved to another monitor, the strip scrolling past it and back. It ends when the tile is toggled
/// back, another tile of the same workspace goes fullscreen (one per workspace), or the window stops being
/// a tile of that workspace — dragged out to float, unlinked, moved to another workspace, or closed.
///
/// The shell is told the window is fullscreen (<c>ITaskbarList2::MarkFullscreenWindow</c>), so the
/// taskbar steps behind it while it is the active window instead of covering its bottom edge.
/// </summary>
internal sealed partial class WorkspaceManager
{
    // workspace -> its fullscreen tile. At most one per workspace.
    private readonly Dictionary<int, IntPtr> _fullscreen = new();

    /// <summary>
    /// Toggle the focused tile between its column and its whole monitor (Win+Alt+F). Going fullscreen
    /// sends the workspace's previous fullscreen tile, if any, back to its column. A no-op on a window
    /// that isn't a tile of a workspace on screen — floating, or in no workspace.
    /// </summary>
    public void ToggleFullscreen()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (!IsTile(hWnd))
        {
            return;
        }

        int workspace = _windowWorkspace[hWnd];
        RefreshMonitors(); // keep monitor handles live across display-config changes
        MonitorState? monitor = MonitorShowing(workspace);
        if (monitor == null)
        {
            return;
        }

        IntPtr previous = FullscreenTileOf(workspace);
        bool entering = previous != hWnd;

        GuardEvents();
        EndFullscreen(previous, workspace);
        if (entering)
        {
            _fullscreen[workspace] = hWnd;
            MarkFullscreen(hWnd, true); // before it is resized, so the taskbar steps back as it grows
        }

        _lastActive[workspace] = hWnd;
        LayoutWorkspace(workspace, monitor, hWnd);
        Foreground.Activate(hWnd); // above the other tiles, and active: the taskbar yields to the active window
        GuardEvents();

        LogTiles($"fullscreen {(entering ? "on" : "off")} {WsLabel(workspace)}", workspace, hWnd);
    }

    /// <summary>The workspace's fullscreen tile, or Zero — also once that window is no longer a tile there.</summary>
    private IntPtr FullscreenTileOf(int workspace) =>
        _fullscreen.TryGetValue(workspace, out IntPtr hWnd)
        && IsTile(hWnd)
        && _windowWorkspace[hWnd] == workspace
            ? hWnd
            : IntPtr.Zero;

    /// <summary>
    /// <paramref name="hWnd"/> stops being <paramref name="workspace"/>'s fullscreen tile (a no-op when it
    /// isn't). Its next layout puts it back in its column, if it still has one.
    /// </summary>
    private void EndFullscreen(IntPtr hWnd, int workspace)
    {
        if (hWnd == IntPtr.Zero || !_fullscreen.TryGetValue(workspace, out IntPtr current) || current != hWnd)
        {
            return;
        }

        _fullscreen.Remove(workspace);
        if (IsWindow(hWnd))
        {
            MarkFullscreen(hWnd, false);
        }
    }

    /// <summary>
    /// On exit: the shell stops treating the remaining fullscreen windows as fullscreen. They are left where
    /// they are, like every window Winland leaves behind.
    /// </summary>
    private void EndAllFullscreen()
    {
        foreach ((int workspace, IntPtr hWnd) in _fullscreen.ToList())
        {
            EndFullscreen(hWnd, workspace);
        }
    }

    /// <summary>
    /// Tell the shell whether <paramref name="hWnd"/> is fullscreen: while a marked window is active, the
    /// taskbar moves to the bottom of the Z order. Without it, a window covering the monitor still has
    /// the (always-on-top) taskbar over its bottom edge.
    /// </summary>
    private static void MarkFullscreen(IntPtr hWnd, bool fullscreen)
    {
        ITaskbarList2? taskbar = null;
        try
        {
            taskbar = (ITaskbarList2)new TaskbarList();
            taskbar.HrInit();
            taskbar.MarkFullscreenWindow(hWnd, fullscreen);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The window still covers the monitor; only the taskbar stays in front of it.
            Log.Line($"taskbar fullscreen mark ({fullscreen}) failed for window 0x{hWnd.ToInt64():X}: {ex.Message}");
        }
        finally
        {
            if (taskbar != null)
            {
                Marshal.ReleaseComObject(taskbar);
            }
        }
    }

    // ----- COM: the shell's taskbar list -----

    [ComImport]
    [Guid("602D4995-B13A-429b-A66E-1935E44F4317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }
}
