namespace Winland.Common;

/// <summary>
/// Shared contract for winland's two named pipes, referenced by every process that talks to it.
///
/// The control channel (<see cref="PipeName"/>) is request/response: the client writes one command
/// line ("workspace 1"); the server replies with a single line — <see cref="Ok"/> on success, or
/// "<see cref="ErrPrefix"/> &lt;message&gt;" on failure. winlandctl is the general client; winland-keys
/// and winland-indicator send their own requests through <see cref="WinlandClient"/>.
///
/// The event channel (<see cref="EventsPipeName"/>) is the opposite direction and carries no
/// commands at all — see its own remarks. winland-indicator is the client.
/// </summary>
public static class Ipc
{
    /// <summary>Pipe Winland listens on (local machine: \\.\pipe\winland).</summary>
    public const string PipeName = "winland";

    /// <summary>
    /// Broadcast-only pipe (\\.\pipe\winland-events) on which winland publishes what every
    /// monitor is showing, and which workspaces live on it. A subscriber connects and only ever reads: the server writes the current
    /// snapshot immediately on connect, then one fresh snapshot line after each workspace change (and
    /// a repeat of the current line after ~30 s of no change), so a
    /// client that starts before or after Winland ends up with the same state either way.
    ///
    /// One message is one line: space-separated "&lt;device&gt;=&lt;shown&gt;:&lt;homed&gt;" entries,
    /// ordered left-to-right, e.g. "\\.\DISPLAY1=4:1,4 \\.\DISPLAY2=0:". The device is the stable GDI
    /// display name (never contains a space); shown is the raw number of the workspace on screen — 0 for
    /// "this monitor shows no workspace" and int.MaxValue for the roaming scratchpad; homed is the
    /// comma-separated, ascending list of workspaces whose home is that monitor (possibly empty).
    /// <see cref="MonitorWorkspaces"/> writes and reads it. Presentation is the client's business.
    /// </summary>
    public const string EventsPipeName = "winland-events";

    /// <summary>
    /// The roaming scratchpad's workspace id, as it appears on the event pipe and in commands. Real
    /// workspaces are whole numbers from 1 and never reach it.
    /// </summary>
    public const int ScratchpadWorkspace = int.MaxValue;

    public const string Ok = "OK";
    public const string ErrPrefix = "ERR";
}
