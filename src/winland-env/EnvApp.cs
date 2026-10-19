using System;
using System.Windows.Forms;

namespace Winland.Env;

/// <summary>
/// Root of the environment service (the "window manager"): owns the workspace engine, the numbered tray
/// icon, the control-channel server, and the event-channel server that broadcasts workspace state to
/// on-screen subscribers. It has no keyboard hook — it is driven entirely by commands arriving over the
/// control pipe (from winlandctl), which the keys daemon triggers.
/// </summary>
internal sealed class EnvApp : ApplicationContext
{
    private readonly WorkspaceManager _workspaces;
    private readonly Dispatcher _dispatcher;
    private readonly UiInvoker _invoker;
    private readonly DispatchServer _server;
    private readonly EventServer _events;
    private readonly TrayIcon _tray;
    private bool _disposed;

    public EnvApp()
    {
        _workspaces = new WorkspaceManager();
        _dispatcher = new Dispatcher(_workspaces);

        // The pipe server runs on a background thread; UiInvoker marshals each command to this UI thread.
        _invoker = new UiInvoker(_dispatcher.Execute);
        _server = new DispatchServer(_invoker);

        // The tray icon is a static badge — the per-monitor workspace numbers live in the on-screen
        // indicator, which subscribes to the event pipe below.
        _tray = new TrayIcon(BuildStatusText, ExitApplication);

        // Subscribers (winland-indicator) get every monitor's workspace, starting with the state as it
        // stands now — a subscriber that connects later is sent the current snapshot on connect.
        _events = new EventServer();
        _workspaces.WorkspacesChanged += _events.Publish;
        _events.Publish(_workspaces.WorkspaceSnapshot);

        _tray.ShowBalloon("Environment running. Control it with: winlandctl workspace 1");
    }

    private string BuildStatusText() =>
        "Winland environment is running.\n\n" +
        "Driven by winlandctl over the control pipe:\n" +
        "  winlandctl workspace <n>          (any whole number >= 1)\n" +
        "  winlandctl movetoworkspace <n>    move the focused window to n (as a tile)\n" +
        "  winlandctl link-here              tile the focused window in the workspace on its monitor\n" +
        "  winlandctl unlink                 stop tiling the focused window (a normal window again)\n" +
        "  winlandctl toggle-float           pop the focused tile out of its row, or put it back\n" +
        "  winlandctl scratchpad             toggle the roaming scratchpad on the mouse's monitor\n" +
        "  winlandctl focus left|right|up|down   (left/right step through and scroll a tile strip)\n" +
        "  winlandctl move-window left|right move the focused tile along its strip / to the next monitor\n" +
        "  winlandctl focus-app <process>    focus that app's window (fails if none, or already focused)\n" +
        "  winlandctl focus-web <url>        same, for a browser --app window of that URL\n" +
        "  winlandctl close\n" +
        "  winlandctl workspace-release\n" +
        "  winlandctl place-next-window     open the next new app window on the mouse's monitor";

    private void ExitApplication() => ExitThread();

    protected override void ExitThreadCore()
    {
        Dispose(true);
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _server.Dispose();
            _events.Dispose();
            _invoker.Dispose();
            _workspaces.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
