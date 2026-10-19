using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// One live monitor: its stable GDI device name, its whole area on the desktop, its work area (without the
/// taskbar), and its own DPI.
/// </summary>
internal sealed record MonitorInfo(string Device, Rectangle Bounds, Rectangle Work, int Dpi);

/// <summary>
/// Root of the indicator: one <see cref="IndicatorWindow"/> per monitor, kept in step with the display
/// configuration, fed by winland's broadcast event pipe, and remembering where the user parked each
/// circle.
///
/// It is a subscriber with one request: dragging a circle that shows a workspace onto another monitor
/// moves that workspace there the moment the cursor enters it ("move-workspace" on the control pipe; the
/// rules are in <see cref="WorkspaceDrag"/>). Requests leave in order on a worker thread, newest wins
/// (<see cref="LatestRequestSender"/>). It runs unelevated — nothing it does needs administrator rights.
/// When Winland is not running, the circles simply keep showing the last state they knew, and a drag
/// moves nothing.
/// </summary>
internal sealed class IndicatorApp : ApplicationContext
{
    // Where each monitor's circle is docked, remembered between runs. Next to the exe, like the log:
    // the whole install is one folder you can copy.
    private static readonly string DockStatePath =
        Path.Combine(AppContext.BaseDirectory, "winland-indicator-dock.txt");

    // How long a released drag waits for a snapshot showing its workspace where it left it, before the
    // circles go back to plain snapshots (Winland isn't running, or refused the move).
    private const int SettleTimeoutMs = 3000;

    private readonly UiPoster _poster = new();
    private readonly Dictionary<string, IndicatorWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DockState> _docks;
    private readonly EventClient _client;
    // The waits only hold up the sender's worker thread, never the circles: be patient with a busy Winland.
    private readonly LatestRequestSender _moves = new(
        new WinlandClient(connectTimeout: TimeSpan.FromSeconds(2), replyTimeout: TimeSpan.FromSeconds(10)).TrySend);
    private readonly System.Windows.Forms.Timer _settleTimeout = new() { Interval = SettleTimeoutMs };
    private readonly System.Windows.Forms.Timer _topmostHeartbeat; // a UI-thread timer, on purpose

    // The last snapshot Winland sent, by device name. Monitors missing from it (a fresh
    // display, or no Winland yet) show the "no workspace" glyph.
    private IReadOnlyDictionary<string, int> _workspaces = new Dictionary<string, int>();

    // The drag in effect: from the press until Winland shows where it left its workspace. Null otherwise.
    private WorkspaceDrag? _drag;

    private bool _disposed;

    public IndicatorApp()
    {
        _docks = LoadDockPositions();
        _settleTimeout.Tick += (_, _) => FinishDrag();
        SyncMonitors();
        Log.Line($"indicator: showing {_windows.Count} monitor(s)");

        // Monitors come and go (undock, resolution change, a screen waking up).
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Windows can silently demote a topmost overlay (display reset, DWM restart, sleep/wake).
        // Redraws reclaim the topmost band on their own; this covers the hours where nothing changes.
        _topmostHeartbeat = new System.Windows.Forms.Timer { Interval = 5000 };
        _topmostHeartbeat.Tick += (_, _) =>
        {
            foreach (IndicatorWindow window in _windows.Values)
            {
                window.EnsureOnTop();
            }
        };
        _topmostHeartbeat.Start();

        _client = new EventClient(snapshot => _poster.Post(() => ApplySnapshot(snapshot)));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _poster.Post(SyncMonitors);

    /// <summary>A fresh snapshot (UI thread): it may be the one a released drag waits for.</summary>
    private void ApplySnapshot(IReadOnlyDictionary<string, int> snapshot)
    {
        _workspaces = snapshot;
        if (_drag != null && _drag.IsSettled(WorkspaceOn(_drag.Carrier)))
        {
            FinishDrag();
            return;
        }

        ShowCircles();
    }

    private int WorkspaceOn(string device) => _workspaces.TryGetValue(device, out int workspace) ? workspace : 0;

    /// <summary>
    /// Give every circle its place and glyph: the workspace the latest snapshot says its monitor shows, at
    /// its dock — adjusted by the drag in effect (<see cref="WorkspaceDrag"/>), whose carrier docks for the
    /// <paramref name="cursor"/> when there is one. Circles that disappear are drawn first, so a workspace
    /// never shows on two circles, not even for a frame.
    /// </summary>
    private void ShowCircles(Point? cursor = null)
    {
        var badges = _windows.Values.Select(window => BadgeOf(window, cursor)).ToList();
        foreach ((IndicatorWindow window, DockState dock, int workspace, bool visible) in badges.OrderBy(b => b.Visible))
        {
            window.ShowBadge(dock, workspace, visible);
        }
    }

    private (IndicatorWindow Window, DockState Dock, int Workspace, bool Visible) BadgeOf(IndicatorWindow window, Point? cursor)
    {
        int shown = WorkspaceOn(window.Device);
        if (_drag == null)
        {
            return (window, window.DockPosition, shown, true);
        }

        (int workspace, bool visible) = _drag.Badge(window.Device, shown);
        DockState dock = !_drag.IsCarrier(window.Device) ? _drag.DockAtPress(window.Device, window.DockPosition)
            : cursor is Point at ? window.DockFor(at)
            : window.DockPosition;
        return (window, dock, workspace, visible);
    }

    /// <summary>A circle was pressed: a drag of what it shows begins.</summary>
    private void StartDrag(IndicatorWindow pressed)
    {
        _settleTimeout.Stop(); // a newer drag replaces one still settling
        _drag = new WorkspaceDrag(pressed.Device, pressed.Workspace,
            _windows.Values.ToDictionary(w => w.Device, w => w.DockPosition));
    }

    /// <summary>
    /// The cursor moved during the drag. Entering another monitor with a workspace in hand moves the
    /// workspace there at once; either way the carrier docks for the cursor.
    /// </summary>
    private void MoveDrag(Point cursor)
    {
        if (_drag is not { Released: false } drag)
        {
            return;
        }

        string? under = _windows.Values.FirstOrDefault(w => w.Monitor.Bounds.Contains(cursor))?.Device;
        if (drag.Follow(under))
        {
            _moves.Post($"move-workspace {drag.Workspace} {drag.Carrier}");
        }

        ShowCircles(cursor);
    }

    /// <summary>
    /// The button is up. Every circle is where it will stay, so remember the docks; the drag stays in
    /// effect until a snapshot shows its workspace where it was left (or the settle timeout passes).
    /// </summary>
    private void ReleaseDrag()
    {
        if (_drag is not { Released: false } drag)
        {
            return;
        }

        drag.Release();
        SaveDockPositions();
        if (drag.IsSettled(WorkspaceOn(drag.Carrier)))
        {
            FinishDrag();
            return;
        }

        _settleTimeout.Start();
    }

    private void FinishDrag()
    {
        _settleTimeout.Stop();
        _drag = null;
        ShowCircles();
    }

    /// <summary>
    /// Reconcile the circles with the live display configuration (UI thread): drop the ones whose
    /// monitor is gone, re-place the ones that stayed (their work area or DPI may have changed), and
    /// open one for each new monitor at its remembered dock position.
    /// </summary>
    private void SyncMonitors()
    {
        List<MonitorInfo> monitors = EnumerateMonitors();

        List<string> gone = _windows.Keys
            .Except(monitors.Select(m => m.Device), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string device in gone)
        {
            _windows[device].Dispose();
            _windows.Remove(device);
        }

        var opened = new List<IndicatorWindow>();
        foreach (MonitorInfo monitor in monitors)
        {
            if (_windows.TryGetValue(monitor.Device, out IndicatorWindow? existing))
            {
                existing.ApplyMonitor(monitor);
                continue;
            }

            var window = new IndicatorWindow(monitor, DockPositionOf(monitor.Device));
            window.DragStarted += () => StartDrag(window);
            window.DragMoved += MoveDrag;
            window.DragEnded += ReleaseDrag;
            _windows[monitor.Device] = window;
            opened.Add(window);
        }

        // A drag across monitors that came or went no longer describes the screens: drop it.
        if (_drag != null && (gone.Count > 0 || opened.Count > 0))
        {
            _settleTimeout.Stop();
            _drag = null;
        }

        ShowCircles(); // new circles get their glyph before their first frame
        foreach (IndicatorWindow window in opened)
        {
            window.Show();
        }
    }

    private DockState DockPositionOf(string device) =>
        _docks.TryGetValue(device, out DockState dock) ? dock : DockState.Default;

    private static Dictionary<string, DockState> LoadDockPositions()
    {
        var docks = new Dictionary<string, DockState>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string raw in File.ReadAllLines(DockStatePath))
            {
                // "<device> <edge> <offset>" — the device name never contains a space.
                string line = raw.Trim();
                int split = line.IndexOf(' ');
                if (line.Length == 0 || line[0] == '#' || split <= 0)
                {
                    continue;
                }

                if (DockState.TryParse(line[(split + 1)..], out DockState dock))
                {
                    docks[line[..split]] = dock;
                }
            }
        }
        catch
        {
            // No state file yet (first run), or it is unreadable — every circle starts at its default.
        }

        return docks;
    }

    /// <summary>
    /// Write the dock positions after a drag. Monitors that are not attached right now keep their
    /// remembered entry, so unplugging a screen never forgets where its circle lived.
    /// </summary>
    private void SaveDockPositions()
    {
        foreach (IndicatorWindow window in _windows.Values)
        {
            _docks[window.Device] = window.DockPosition;
        }

        try
        {
            IEnumerable<string> lines = new[] { "# <device> <edge> <offset along the edge, 0..1>" }
                .Concat(_docks.Select(d => $"{d.Key} {d.Value.Format()}"));
            File.WriteAllLines(DockStatePath, lines);
        }
        catch
        {
            // A read-only install folder costs the user only the memory of where they dragged it.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _topmostHeartbeat.Dispose();
            _settleTimeout.Dispose();
            _client.Dispose();

            foreach (IndicatorWindow window in _windows.Values)
            {
                window.Dispose();
            }

            _windows.Clear();
            _poster.Dispose();
        }

        base.Dispose(disposing);
    }

    // ----- Win32 -----

    /// <summary>
    /// Enumerate live monitors left-to-right. We ask Win32 rather than WinForms' Screen so the device
    /// names are exactly the ones winland keys its workspace state on (\\.\DISPLAYn), and we take
    /// the WORK area so a circle never lands under the taskbar.
    /// </summary>
    private static List<MonitorInfo> EnumerateMonitors()
    {
        var found = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(hMon, ref info))
            {
                RECT bounds = info.rcMonitor;
                RECT work = info.rcWork;
                found.Add(new MonitorInfo(
                    info.szDevice ?? "",
                    Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    Rectangle.FromLTRB(work.Left, work.Top, work.Right, work.Bottom),
                    DpiOf(hMon)));
            }

            return true;
        }, IntPtr.Zero);

        return found.OrderBy(m => m.Work.Left).ThenBy(m => m.Work.Top).ToList();
    }

    /// <summary>A monitor's effective DPI, or the 96 DPI baseline if the shell cannot tell us.</summary>
    private static int DpiOf(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint _) == 0 ? (int)dpiX : 96;

    private const int MDT_EFFECTIVE_DPI = 0;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
