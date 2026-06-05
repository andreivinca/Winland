using System;
using System.Diagnostics;
using System.IO;

namespace Winland.Keys;

/// <summary>
/// Feature: run a configured app shortcut. The action's first token (verb) is resolved next to the exe
/// as: "&lt;verb&gt;.ps1" script → "&lt;verb&gt;.exe" sibling (e.g. winlandctl) → otherwise the whole line
/// is run as a shell command. See HotkeyConfig for the bind grammar.
/// </summary>
internal static class AppLauncher
{
    public static void Run(BindAction action)
    {
        try
        {
            (string verb, string args) = SplitCommand(action.Command);
            if (verb.Length == 0)
            {
                return;
            }

            string baseDir = AppContext.BaseDirectory;

            string script = Path.Combine(baseDir, verb + ".ps1");
            if (File.Exists(script))
            {
                // Invoke via -Command (not -File) so PowerShell parses the arguments: this lets a "--"
                // token in a bind pass the rest through verbatim (incl. dash-prefixed args), which -File
                // can't do. The script path is single-quoted (and any ' doubled) so spaces are safe.
                string scriptLiteral = "'" + script.Replace("'", "''") + "'";
                string command = $"& {scriptLiteral} {args}".TrimEnd();
                string psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";
                Process.Start(new ProcessStartInfo("powershell.exe", psArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return;
            }

            // A sibling "<verb>.exe" (e.g. winlandctl) runs directly — no PATH setup needed, and as a
            // child of this (elevated) daemon it inherits elevation, so it can reach winland-env's pipe.
            string siblingExe = Path.Combine(baseDir, verb + ".exe");
            if (File.Exists(siblingExe))
            {
                Process.Start(new ProcessStartInfo(siblingExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return;
            }

            // Otherwise run the whole line as a shell command (firefox, explorer.exe, a URI, …).
            Process.Start(new ProcessStartInfo(verb, args) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    // Split a command line into the executable/app (a leading quoted path or first token) and the rest.
    private static (string exe, string args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end > 0)
            {
                return (command.Substring(1, end - 1), command[(end + 1)..].Trim());
            }
        }

        int sp = command.IndexOf(' ');
        return sp < 0 ? (command, string.Empty) : (command[..sp], command[(sp + 1)..].Trim());
    }
}
