using System;
using System.Collections.Generic;
using System.Linq;
using Winland.Common;

namespace Winland;

/// <summary>
/// Launch placement: an app opened from a shortcut appears on the monitor under the mouse, as a tile of the
/// workspace shown there. The launcher arms it ("place-next-window") just before starting the app; the
/// first app window that then appears — one that was not on screen when armed — is moved to that monitor
/// and tiled. Fixed-size windows (dialogs such as Run) are placed but not tiled. One window per arm, and an
/// arm that catches nothing expires.
/// </summary>
internal sealed partial class WorkspaceManager
{
    // Long enough for a cold start of a heavy app (VS Code, Spotify).
    private static readonly TimeSpan LaunchPlacementTimeout = TimeSpan.FromSeconds(10);

    // Hooked only while armed: a new window can show untitled or cloaked and only qualify later.
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;

    private readonly List<IntPtr> _launchHooks = new();
    private HashSet<IntPtr> _launchExisting = new();
    private string? _launchDevice;
    private long _launchUntilTick;

    /// <summary>Place the next newly opened app window on the monitor under the mouse cursor.</summary>
    public void PlaceNextWindowOnActiveMonitor()
    {
        RefreshMonitors();
        MonitorState? target = GetActiveMonitor();
        if (target == null)
        {
            return;
        }

        var visible = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                visible.Add(h);
            }

            return true;
        }, IntPtr.Zero);

        _launchExisting = visible;
        _launchDevice = target.Device;
        _launchUntilTick = Environment.TickCount64 + (long)LaunchPlacementTimeout.TotalMilliseconds;

        if (_launchHooks.Count == 0)
        {
            AddHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, _launchHooks);
            AddHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, _launchHooks);
            AddHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED, _launchHooks);
        }
    }

    private void TryPlaceLaunchedWindow(IntPtr hWnd)
    {
        if (_launchDevice == null)
        {
            return;
        }

        if (Environment.TickCount64 >= _launchUntilTick)
        {
            DisarmLaunchPlacement();
            return;
        }

        // Topmost windows are overlays (e.g. the screen-clip UI), not app windows to relocate.
        if (_launchExisting.Contains(hWnd) || !IsManaged(hWnd) || IsIconic(hWnd) || IsTopmost(hWnd))
        {
            return;
        }

        string device = _launchDevice;
        DisarmLaunchPlacement();

        // A fresh enumeration rather than RefreshMonitors: this event can arrive in the middle of a
        // workspace operation that is still holding the current monitor states.
        MonitorState? target = EnumerateMonitors().FirstOrDefault(m => m.Device == device);
        if (target == null)
        {
            return;
        }

        if (MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST) != target.Handle)
        {
            MoveWindowToMonitor(hWnd, target);
            Log.Line($"placed launched window on {device}");
        }

        // Tiled a moment later, outside this callback (see OnReflowTick).
        if (IsResizable(hWnd))
        {
            QueueLaunchTile(hWnd, device);
        }
    }

    private void DisarmLaunchPlacement()
    {
        _launchDevice = null;
        _launchExisting = new HashSet<IntPtr>();

        foreach (IntPtr hook in _launchHooks)
        {
            UnhookWinEvent(hook);
        }

        _launchHooks.Clear();
    }

    private static bool IsTopmost(IntPtr hWnd)
    {
        return (GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
    }

    // A resizable frame: an app window to tile, rather than a fixed-size dialog.
    private static bool IsResizable(IntPtr hWnd)
    {
        return (GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64() & WS_THICKFRAME) != 0;
    }
}
