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
/// It is a subscriber with one request: when a circle is dropped on another monitor, it asks Winland
/// to move that workspace there ("move-workspace" on the control pipe). The circles still only ever
/// show what Winland's snapshots say, so they change once the move has happened. It runs unelevated —
/// nothing it does needs administrator rights. When Winland is not running, the circles simply keep
/// showing the last state they knew, and a drop does nothing.
/// </summary>
internal sealed class IndicatorApp : ApplicationContext
{
    // Where each monitor's circle is docked, remembered between runs. Next to the exe, like the log:
    // the whole install is one folder you can copy.
    private static readonly string DockStatePath =
        Path.Combine(AppContext.BaseDirectory, "winland-indicator-dock.txt");

    private readonly UiPoster _poster = new();
    private readonly Dictionary<string, IndicatorWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DockState> _docks;
    private readonly EventClient _client;
    // The waits only hold up a worker thread, never the circles: be patient with a busy environment.
    private readonly WinlandClient _winland =
        new(connectTimeout: TimeSpan.FromSeconds(2), replyTimeout: TimeSpan.FromSeconds(10));
    private readonly System.Windows.Forms.Timer _topmostHeartbeat; // a UI-thread timer, on purpose

    // The last snapshot Winland sent, by device name. Monitors missing from it (a fresh
    // display, or no Winland yet) show the "no workspace" glyph.
    private IReadOnlyDictionary<string, int> _workspaces = new Dictionary<string, int>();

    private bool _disposed;

    public IndicatorApp()
    {
        _docks = LoadDockPositions();
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

    /// <summary>Show a fresh snapshot on every circle (UI thread).</summary>
    private void ApplySnapshot(IReadOnlyDictionary<string, int> snapshot)
    {
        _workspaces = snapshot;
        foreach (IndicatorWindow window in _windows.Values)
        {
            window.SetWorkspace(WorkspaceOn(window.Device));
        }
    }

    private int WorkspaceOn(string device) => _workspaces.TryGetValue(device, out int workspace) ? workspace : 0;

    /// <summary>
    /// Reconcile the circles with the live display configuration (UI thread): drop the ones whose
    /// monitor is gone, re-place the ones that stayed (their work area or DPI may have changed), and
    /// open one for each new monitor at its remembered dock position.
    /// </summary>
    private void SyncMonitors()
    {
        List<MonitorInfo> monitors = EnumerateMonitors();

        foreach (string gone in _windows.Keys
                     .Except(monitors.Select(m => m.Device), StringComparer.OrdinalIgnoreCase).ToList())
        {
            _windows[gone].Dispose();
            _windows.Remove(gone);
        }

        foreach (MonitorInfo monitor in monitors)
        {
            if (_windows.TryGetValue(monitor.Device, out IndicatorWindow? window))
            {
                window.ApplyMonitor(monitor);
                continue;
            }

            window = new IndicatorWindow(monitor, DockPositionOf(monitor.Device));
            window.DockPositionChanged += SaveDockPositions;
            window.WorkspaceDropped += MoveWorkspace;
            window.SetWorkspace(WorkspaceOn(monitor.Device));
            window.Show();
            _windows[monitor.Device] = window;
        }
    }

    /// <summary>
    /// A circle carrying <paramref name="workspace"/> was dropped at <paramref name="point"/> on another
    /// monitor: ask Winland to move the workspace to that monitor. The request runs on a worker
    /// thread — the move puts windows away and lays them out again, and the circles must stay responsive
    /// meanwhile. Nothing is redrawn here: the snapshot that follows the move relabels both circles.
    /// </summary>
    private void MoveWorkspace(int workspace, Point point)
    {
        MonitorInfo? target = _windows.Values.Select(w => w.Monitor).FirstOrDefault(m => m.Bounds.Contains(point));
        if (target == null)
        {
            return;
        }

        string command = $"move-workspace {workspace} {target.Device}";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!_winland.TrySend(command))
            {
                Log.Line($"indicator: '{command}' was not carried out (is winland running?)");
            }
        });
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
