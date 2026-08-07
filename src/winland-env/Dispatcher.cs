using System;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// Maps a control command ("workspace 1", "movetoworkspace 1", "link-here", "scratchpad",
/// "workspace-release", "focus left", "focus-app firefox", "close") to the matching
/// in-process operation. The single source of truth for what the environment can do; reached from the
/// IPC server today (and any other front door later). <see cref="Execute"/> runs on the UI thread.
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
                    _workspaces.MoveFocusedWindowToWorkspace(target);
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} movetoworkspace expects a whole number >= 1";

            case "link-here":
                _workspaces.LinkFocusedWindowToCurrentWorkspace();
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
                    WindowNavigator.FocusNearest(dir);
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} focus expects left|right|up|down";

            case "focus-app":
                if (args.Length == 0)
                {
                    return $"{Ipc.ErrPrefix} focus-app expects a process name";
                }
                return WindowNavigator.FocusApp(args)
                    ? Ipc.Ok
                    : $"{Ipc.ErrPrefix} nothing to focus";

            case "close":
                WindowNavigator.CloseForeground();
                return Ipc.Ok;

            default:
                return $"{Ipc.ErrPrefix} unknown verb '{verb}'";
        }
    }

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
