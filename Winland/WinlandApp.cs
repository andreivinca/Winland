using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace Winland;

/// <summary>
/// The application's root handler: a tray-resident <see cref="ApplicationContext"/> that wires the
/// features together (keyboard hook, workspaces, window focus, app shortcuts, tray UI), owns the
/// combo→action resolver/dispatch table, and manages startup/shutdown.
/// </summary>
internal sealed class WinlandApp : ApplicationContext
{
    // Built-in action ids dispatched from the hook to the UI thread.
    private const int ACTION_LEFT = 1;
    private const int ACTION_UP = 2;
    private const int ACTION_RIGHT = 3;
    private const int ACTION_DOWN = 4;
    private const int ACTION_CLOSE = 7;
    private const int ACTION_RELEASE_WS = 8; // Win+Shift+W: release current workspace from its monitor

    // Workspace switches are encoded as ACTION_WORKSPACE_BASE + n (n = 1..9).
    private const int ACTION_WORKSPACE_BASE = 100;

    // Config binds are encoded as ACTION_CONFIG_BASE + index into _binds.
    private const int ACTION_CONFIG_BASE = 1000;

    private readonly TrayIcon _tray;
    private readonly KeyboardHook _hook;
    private readonly WorkspaceManager _workspaceManager;
    private List<KeyBind> _binds = new();
    private readonly string _configPath;

    public WinlandApp()
    {
        // Disable Windows' own Win+<key> hotkeys so the shell can't act on Win+number (taskbar apps).
        WindowsHotkeyDisabler.EnsureDisabled();

        // The shared "hotkey register": it owns the keyboard hook and asks us to resolve each Win+combo
        // to an action id (0 = not ours), then dispatches the matched id back on the UI thread.
        _hook = new KeyboardHook(ResolveActionId, HandleAction);

        _workspaceManager = new WorkspaceManager();

        _configPath = Config.DefaultPath;
        _binds = HotkeyConfig.Parse(Config.Load(_configPath));

        _tray = new TrayIcon(BuildStatusText, ReloadConfig, OpenConfig, ExitApplication);

        // Tray icon shows the active workspace number, updated on each Win+N.
        _workspaceManager.WorkspaceChanged += OnWorkspaceChanged;
        _tray.SetWorkspace(_workspaceManager.PrimaryWorkspace);

        _tray.ShowBalloon(_hook.Installed
            ? "Running in tray. Super (Win) hotkeys are active."
            : "Running in tray, but the keyboard hook failed to install.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            _workspaceManager.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ReloadConfig()
    {
        _binds = HotkeyConfig.Parse(Config.Load(_configPath));
        _tray.ShowBalloon($"Config reloaded — {_binds.Count} shortcut(s).", 1500);
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private void OnWorkspaceChanged(int workspace) => _tray.SetWorkspace(workspace);

    private string BuildStatusText()
    {
        return _hook.Installed
            ? "Winland is running.\nBuilt-in Super (Win) hotkeys:\n" +
              "Win+Arrows  focus window\n" +
              "Win+W       close window\n" +
              "Win+1..9    switch workspace\n\n" +
              $"App shortcuts: {_binds.Count} from config.\n" +
              "Use the tray menu to open/reload the config."
            : "Winland is running, but the keyboard hook could not be installed.";
    }

    private void ExitApplication()
    {
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        Dispose(true);
        base.ExitThreadCore();
    }

    // Adapter for KeyboardHook: returns the action id for a Win+combo, or 0 if it isn't one of ours.
    private int ResolveActionId(int vk, bool shiftDown, bool altDown, bool ctrlDown)
        => TryResolveActionId(vk, shiftDown, altDown, ctrlDown, out int actionId) ? actionId : 0;

    // Resolve a key combo (with Win held) to an action id: built-ins first, then config binds.
    private bool TryResolveActionId(int vk, bool shiftDown, bool altDown, bool ctrlDown, out int actionId)
    {
        // Built-in window management uses plain Win+<key> (no extra modifiers).
        if (!shiftDown && !altDown && !ctrlDown && TryMapBuiltin(vk, out actionId))
        {
            return true;
        }

        // Built-in: Win+Shift+W releases the current workspace from its monitor.
        if (shiftDown && !altDown && !ctrlDown && vk == (int)Keys.W)
        {
            actionId = ACTION_RELEASE_WS;
            return true;
        }

        var mods = (shiftDown ? BindModifiers.Shift : 0)
            | (altDown ? BindModifiers.Alt : 0)
            | (ctrlDown ? BindModifiers.Ctrl : 0);

        for (int i = 0; i < _binds.Count; i++)
        {
            if (_binds[i].Vk == vk && _binds[i].Modifiers == mods)
            {
                actionId = ACTION_CONFIG_BASE + i;
                return true;
            }
        }

        actionId = 0;
        return false;
    }

    private static bool TryMapBuiltin(int vk, out int actionId)
    {
        // Win+1..9 / Win+NumPad1..9 -> switch the focused monitor's workspace.
        if (TryMapWorkspaceDigit(vk, out int workspace))
        {
            actionId = ACTION_WORKSPACE_BASE + workspace;
            return true;
        }

        switch ((Keys)vk)
        {
            case Keys.Left: actionId = ACTION_LEFT; return true;
            case Keys.Up: actionId = ACTION_UP; return true;
            case Keys.Right: actionId = ACTION_RIGHT; return true;
            case Keys.Down: actionId = ACTION_DOWN; return true;
            case Keys.W: actionId = ACTION_CLOSE; return true;
            default: actionId = 0; return false;
        }
    }

    private static bool TryMapWorkspaceDigit(int vk, out int workspace)
    {
        var key = (Keys)vk;
        if (key >= Keys.D1 && key <= Keys.D9)
        {
            workspace = key - Keys.D1 + 1;
            return true;
        }

        if (key >= Keys.NumPad1 && key <= Keys.NumPad9)
        {
            workspace = key - Keys.NumPad1 + 1;
            return true;
        }

        workspace = 0;
        return false;
    }

    private void HandleAction(int actionId)
    {
        // Runs on the UI thread, immediately when the combo is pressed.
        ResolveAction(actionId)?.Invoke();
    }

    private Action? ResolveAction(int actionId)
    {
        if (actionId > ACTION_WORKSPACE_BASE && actionId <= ACTION_WORKSPACE_BASE + WorkspaceManager.WorkspaceCount)
        {
            int workspace = actionId - ACTION_WORKSPACE_BASE;
            return () => _workspaceManager.SwitchFocusedMonitorTo(workspace);
        }

        if (actionId >= ACTION_CONFIG_BASE && actionId < ACTION_CONFIG_BASE + _binds.Count)
        {
            BindAction action = _binds[actionId - ACTION_CONFIG_BASE].Action;
            return () => AppLauncher.Run(action);
        }

        return actionId switch
        {
            ACTION_LEFT => () => WindowNavigator.FocusNearest(Direction.Left),
            ACTION_UP => () => WindowNavigator.FocusNearest(Direction.Up),
            ACTION_RIGHT => () => WindowNavigator.FocusNearest(Direction.Right),
            ACTION_DOWN => () => WindowNavigator.FocusNearest(Direction.Down),
            ACTION_CLOSE => WindowNavigator.CloseForeground,
            ACTION_RELEASE_WS => _workspaceManager.ReleaseCurrentWorkspace,
            _ => null
        };
    }
}
