using System;
using System.Diagnostics;
using System.IO;

namespace Winland.Keys;

/// <summary>
/// Feature: run a configured app shortcut. The action's first token (verb) is resolved next to the exe
/// as: "&lt;verb&gt;.ps1" script → "&lt;verb&gt;.exe" sibling (e.g. winlandctl) → otherwise the whole line
/// is run as a shell command. See HotkeyConfig for the bind grammar.
///
/// Scripts and shell commands are launchers for the user's apps, so they run with the user's NORMAL
/// token (via <see cref="UnelevatedLauncher"/>) — otherwise everything a bind starts would inherit this
/// daemon's elevation. Window management needs no elevation here: it lives in winland-env, reached
/// through the sibling winlandctl.exe (which the pipe accepts from unelevated callers too).
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
                RunScript(script, args, baseDir);
                return;
            }

            // A sibling "<verb>.exe" (e.g. winlandctl) runs directly — no PATH setup needed.
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

            RunShellCommand(verb, args);
        }
        catch
        {
            // ignored — a broken bind must not take the daemon down
        }
    }

    /// <summary>Run a "&lt;verb&gt;.ps1" script, unelevated when possible.</summary>
    private static void RunScript(string script, string args, string baseDir)
    {
        // Invoke via -Command (not -File) so PowerShell parses the arguments: this lets a "--" token
        // in a bind pass the rest through verbatim (incl. dash-prefixed args), which -File can't do.
        // The script path is single-quoted (any ' doubled); double quotes in the args are escaped so
        // they can't cut the outer -Command string short.
        string scriptLiteral = "'" + script.Replace("'", "''") + "'";
        string command = $"& {scriptLiteral} {args.Replace("\"", "\\\"")}".TrimEnd();
        string psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

        if (!UnelevatedLauncher.Start(powershell, psArgs, baseDir))
        {
            Process.Start(new ProcessStartInfo(powershell, psArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir
            });
        }
    }

    /// <summary>Run a plain command (firefox, explorer.exe, a URI, …), unelevated when possible.</summary>
    private static void RunShellCommand(string verb, string args)
    {
        // Quote the verb so App Paths names and paths with spaces survive the cmd/start round trip.
        string commandLine = args.Length == 0 ? $"\"{verb}\"" : $"\"{verb}\" {args}";
        if (!UnelevatedLauncher.StartViaShell(commandLine))
        {
            Process.Start(new ProcessStartInfo(verb, args) { UseShellExecute = true });
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
