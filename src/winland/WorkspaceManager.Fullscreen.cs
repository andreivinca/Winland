using System;
using System.Collections.Generic;

namespace Winland;

/// <summary>
/// Fullscreen tiles (Win+Alt+F, "toggle-fullscreen"). A tile can fill its monitor's whole work area,
/// inside the usual gap, without leaving its strip: it stays a tile of its workspace and keeps its column,
/// reserved behind it (<see cref="StripLayout.WithFullscreen"/>), so toggling again puts it straight back
/// there. It is not real fullscreen: the taskbar stays visible and the window never covers it.
///
/// Fullscreen is part of the tile's layout, so it lasts across layouts: the workspace put away and shown
/// again, moved to another monitor, the strip scrolling past it and back. It ends when the tile is toggled
/// back, another tile of the same workspace goes fullscreen (one per workspace), or the window stops being
/// a tile of that workspace — dragged out to float, unlinked, moved to another workspace, or closed.
/// </summary>
internal sealed partial class WorkspaceManager
{
    // workspace -> its fullscreen tile. At most one per workspace.
    private readonly Dictionary<int, IntPtr> _fullscreen = new();

    /// <summary>
    /// Toggle the focused tile between its column and its monitor's whole work area (Win+Alt+F). Going
    /// fullscreen sends the workspace's previous fullscreen tile, if any, back to its column. A no-op on a
    /// window that isn't a tile of a workspace on screen — floating, or in no workspace.
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
        }

        _lastActive[workspace] = hWnd;
        LayoutWorkspace(workspace, monitor, hWnd);
        Foreground.Activate(hWnd); // above the other tiles it now overlaps
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
        if (hWnd != IntPtr.Zero && _fullscreen.TryGetValue(workspace, out IntPtr current) && current == hWnd)
        {
            _fullscreen.Remove(workspace);
        }
    }
}
