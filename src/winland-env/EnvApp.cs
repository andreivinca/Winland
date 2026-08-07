using System;
using System.Windows.Forms;

namespace Winland.Env;

/// <summary>
/// Root of the environment service (the "window manager"): owns the workspace engine, the numbered tray
/// icon, and the control-channel server. It has no keyboard hook — it is driven entirely by commands
/// arriving over the pipe (from winlandctl), which the keys daemon triggers.
/// </summary>
internal sealed class EnvApp : ApplicationContext
{
    private readonly WorkspaceManager _workspaces;
    private readonly Dispatcher _dispatcher;
    private readonly UiInvoker _invoker;
    private readonly DispatchServer _server;
    private readonly TrayIcon _tray;
    private bool _disposed;

    public EnvApp()
    {
        _workspaces = new WorkspaceManager();
        _dispatcher = new Dispatcher(_workspaces);

        // The pipe server runs on a background thread; UiInvoker marshals each command to this UI thread.
        _invoker = new UiInvoker(_dispatcher.Execute);
        _server = new DispatchServer(_invoker);

        _tray = new TrayIcon(BuildStatusText, onReloadConfig: null, onOpenConfig: null, ExitApplication);

        // The tray icon lives on the primary monitor's taskbar, so it tracks that monitor's workspace.
        _workspaces.PrimaryWorkspaceChanged += OnPrimaryWorkspaceChanged;
        _tray.SetWorkspace(_workspaces.PrimaryWorkspace);

        _tray.ShowBalloon("Environment running. Control it with: winlandctl workspace 1");
    }

    private void OnPrimaryWorkspaceChanged(int workspace) => _tray.SetWorkspace(workspace);

    private string BuildStatusText() =>
        "Winland environment is running.\n\n" +
        "Driven by winlandctl over the control pipe:\n" +
        "  winlandctl workspace <n>          (any whole number >= 1)\n" +
        "  winlandctl movetoworkspace <n>    move the focused window to n\n" +
        "  winlandctl link-here              link the focused window to the current workspace\n" +
        "  winlandctl scratchpad             toggle the roaming scratchpad on the mouse's monitor\n" +
        "  winlandctl focus left|right|up|down\n" +
        "  winlandctl focus-app <process>    focus that app's window (fails if none, or already focused)\n" +
        "  winlandctl close\n" +
        "  winlandctl workspace-release";

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
            _invoker.Dispose();
            _workspaces.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
