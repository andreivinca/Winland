using System;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// Maps a control command ("workspace 1", "focus left", "close", "workspace-release") to the matching
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
                if (int.TryParse(args, out int n) && n >= 1 && n <= WorkspaceManager.WorkspaceCount)
                {
                    _workspaces.SwitchFocusedMonitorTo(n);
                    return Ipc.Ok;
                }
                return $"{Ipc.ErrPrefix} workspace expects 1..{WorkspaceManager.WorkspaceCount}";

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

            case "close":
                WindowNavigator.CloseForeground();
                return Ipc.Ok;

            default:
                return $"{Ipc.ErrPrefix} unknown verb '{verb}'";
        }
    }

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
