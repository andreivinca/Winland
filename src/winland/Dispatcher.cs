using System;
using Winland.Common;

namespace Winland;

/// <summary>
/// Maps a control command ("workspace 1", "movetoworkspace 1", "move-workspace 3 \\.\DISPLAY2", "link-here",
/// "unlink", "toggle-float", "scratchpad",
/// "workspace-release", "focus left", "move-window right", "focus-app firefox",
/// "focus-web https://gemini.google.com", "close", "place-next-window") to the matching
/// in-process operation. The single source of truth for what Winland can do; reached from the
/// IPC server today (and any other front door later). <see cref="Execute"/> runs on the UI thread.
///
/// Commands that deliberately focus or move a window run through <see cref="FollowedByCursor{T}"/>, so
/// the mouse ends up on the window the keyboard is now on.
/// </summary>
internal sealed class Dispatcher
{
    private readonly WorkspaceManager _workspaces;

    public Dispatcher(WorkspaceManager workspaces) => _workspaces = workspaces;

    /// <summary>Run one command line. Returns the protocol response ("OK" or "ERR ...").</summary>
    public string Execute(string commandLine)
    {
        string line = commandLine.Trim();
        int sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        string args = sp < 0 ? string.Empty : line[(sp + 1)..].Trim();

        switch (verb)
        {
            case "workspace":
                if (TryParseWorkspace(args, out int n))
                {
                    _workspaces.SwitchFocusedMonitorTo(n);
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} workspace expects a whole number >= 1";

            case "movetoworkspace":
                if (TryParseWorkspace(args, out int target))
                {
                    FollowedByCursor(() => _workspaces.MoveFocusedWindowToWorkspace(target));
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} movetoworkspace expects a whole number >= 1";

            case "move-workspace":
                // "<workspace> <monitor device>" — sent by the indicator when a circle is dropped on another
                // monitor. Device names never contain a space.
                string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && TryParseWorkspace(parts[0], out int moved))
                {
                    return _workspaces.MoveWorkspaceToMonitor(moved, parts[1])
                        ? Ipc.Ok
                        : $"{Ipc.ErrPrefix} no monitor '{parts[1]}'";
                }
                return $"{Ipc.ErrPrefix} move-workspace expects a whole number >= 1 and a monitor device name";

            case "link-here":
                FollowedByCursor(_workspaces.LinkFocusedWindowToCurrentWorkspace);
                return Ipc.Ok;

            case "unlink":
                _workspaces.UnlinkFocusedWindow();
                return Ipc.Ok;

            case "toggle-float":
                FollowedByCursor(_workspaces.ToggleFloating);
                return Ipc.Ok;

            case "scratchpad":
                _workspaces.ToggleScratchpad();
                return Ipc.Ok;

            case "workspace-release":
                _workspaces.ReleaseCurrentWorkspace();
                return Ipc.Ok;

            case "focus":
                if (TryParseDirection(args, out Direction dir))
                {
                    // Left/right step through a tile strip, scrolling it. Up/down, a window that isn't a
                    // tile, or a step past either end of the strip focus the nearest window on screen.
                    FollowedByCursor(() =>
                    {
                        if (!_workspaces.FocusAdjacentTile(dir))
                        {
                            WindowNavigator.FocusNearest(dir);
                        }
                    });
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} focus expects left|right|up|down";

            case "move-window":
                if (TryParseDirection(args, out Direction moveDir) && moveDir is Direction.Left or Direction.Right)
                {
                    FollowedByCursor(() => _workspaces.MoveFocusedWindow(moveDir));
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} move-window expects left|right";

            case "focus-app":
                if (args.Length == 0)
                {
                    return $"{Ipc.ErrPrefix} focus-app expects a process name";
                }
                return FollowedByCursor(() => WindowNavigator.FocusApp(args))
                    ? Ipc.Ok
                    : $"{Ipc.ErrPrefix} nothing to focus";

            case "focus-web":
                if (!Uri.TryCreate(args, UriKind.Absolute, out Uri? url) || url.Host.Length == 0)
                {
                    return $"{Ipc.ErrPrefix} focus-web expects an absolute URL";
                }
                return FollowedByCursor(() => WindowNavigator.FocusWebApp(url))
                    ? Ipc.Ok
                    : $"{Ipc.ErrPrefix} nothing to focus";

            case "close":
                WindowNavigator.CloseForeground();
                return Ipc.Ok;

            case "place-next-window":
                _workspaces.PlaceNextWindowOnActiveMonitor();
                return Ipc.Ok;

            default:
                return $"{Ipc.ErrPrefix} unknown verb '{verb}'";
        }
    }

    /// <summary>
    /// Run a command that focuses or moves a window, then bring the mouse to the focused window if focus
    /// or its position changed (see <see cref="CursorWarp"/>).
    /// </summary>
    private static T FollowedByCursor<T>(Func<T> command)
    {
        CursorWarp.FocusState before = CursorWarp.Capture();
        T result = command();
        CursorWarp.Follow(before);
        return result;
    }

    private static void FollowedByCursor(Action command) =>
        FollowedByCursor(() =>
        {
            command();
            return true;
        });

    // A user-facing workspace number: any whole number >= 1 except the value reserved for the
    // scratchpad, which is only ever entered through its own toggle.
    private static bool TryParseWorkspace(string s, out int workspace) =>
        int.TryParse(s, out workspace)
        && workspace >= 1
        && workspace != WorkspaceManager.ScratchpadWorkspace;

    private static bool TryParseDirection(string s, out Direction dir)
    {
        switch (s.ToLowerInvariant())
        {
            case "left": dir = Direction.Left; return true;
            case "right": dir = Direction.Right; return true;
            case "up": dir = Direction.Up; return true;
            case "down": dir = Direction.Down; return true;
            default: dir = Direction.Left; return false;
        }
    }
}
