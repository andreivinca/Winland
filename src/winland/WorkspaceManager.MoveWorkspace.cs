using System;
using System.Linq;

namespace Winland;

/// <summary>
/// Moving a workspace to another monitor — what dropping a monitor's indicator circle onto another monitor
/// asks for ("move-workspace"). The workspace is re-homed there and shown there with everything in it, and
/// the monitor it left goes back to the workspace it showed before. Membership is untouched: the same
/// windows belong to the same workspaces; only where the workspace lives changes.
/// </summary>
internal sealed partial class WorkspaceManager
{
    /// <summary>
    /// Move <paramref name="workspace"/> to the monitor <paramref name="device"/> and show it there:
    ///  * it is re-homed there, so Win+N shows it there from now on;
    ///  * that monitor puts away everything it showed (its workspace stays homed on it) and lays out the
    ///    workspace's tiles; the workspace's floating windows on the monitor it leaves come along — ones the
    ///    user put on any other monitor stay where they are;
    ///  * the monitor it leaves goes back to the workspace it showed before (<see cref="PreviousWorkspace"/>),
    ///    or shows nothing when it has no other;
    ///  * focus — and the mouse — end on the moved workspace.
    /// A workspace no monitor shows is simply re-homed and shown there. Returns false when the workspace is
    /// not a numbered one or no monitor <paramref name="device"/> is attached.
    /// </summary>
    public bool MoveWorkspaceToMonitor(int workspace, string device)
    {
        if (workspace < 1 || workspace == ScratchpadWorkspace)
        {
            return false; // the scratchpad has no home to move: it roams with its own toggle
        }

        RefreshMonitors(); // keep monitor handles live across display-config changes
        MonitorState? target = _monitors.Values.FirstOrDefault(
            m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return false;
        }

        MonitorState? source = MonitorShowing(workspace);
        if (source?.Device == target.Device)
        {
            return true; // it is there already
        }

        // Decided before anything changes: what the monitor it leaves showed before it.
        int previous = source == null ? 0 : PreviousWorkspaceOn(source);

        GuardEvents();
        _workspaceHome[workspace] = target.Device;
        if (source != null)
        {
            RememberFrontmost(workspace, source); // focus lands on the tile that had it
            CarryFloating(workspace, source, target);
        }

        // The target puts away what it showed and lays the workspace's tiles out — which takes them off the
        // monitor it leaves, so that one can then go back to its previous workspace without touching them.
        IntPtr focused = RenderWorkspaceOn(target, workspace);
        if (source != null)
        {
            RenderWorkspaceOn(source, previous);
            if (DpiOf(source.Handle) != DpiOf(target.Handle))
            {
                // A window arriving on a monitor of another DPI rescales itself to Windows' suggestion,
                // overriding its column. Now that it is there, put it back into place.
                LayoutWorkspace(workspace, target, FocusTargetOf(workspace));
            }

            focused = FocusAndRaise(workspace); // showing the previous workspace took focus; hand it back
        }

        BringCursorTo(focused, target);
        GuardEvents();

        LogWorkspaces($"move WS{workspace} to {target.Device}");
        NotifyWorkspaceChanges();
        return true;
    }

    /// <summary>
    /// Bring a workspace's floating windows that sit on <paramref name="from"/> over to <paramref name="to"/>, at
    /// the same place relative to the monitor (maximized ones maximized there). Hidden ones (closed to the
    /// tray) stay hidden, and ones on any other monitor stay where the user put them.
    /// </summary>
    private void CarryFloating(int workspace, MonitorState from, MonitorState to)
    {
        foreach (IntPtr hWnd in WindowsOf(workspace))
        {
            if (_floating.Contains(hWnd) && IsWindowVisible(hWnd) && !IsHungAppWindow(hWnd)
                && TryGetWindowMonitor(hWnd, out MonitorState? at) && at!.Device == from.Device)
            {
                MoveWindowToMonitor(hWnd, to);
            }
        }
    }

    private int PreviousWorkspaceOn(MonitorState monitor) =>
        PreviousWorkspace.For(monitor.Device, _workspaceHome, _lastShown, _shownByDevice.Values.ToHashSet());
}
