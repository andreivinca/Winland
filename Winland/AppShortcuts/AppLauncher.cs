using System;
using System.Diagnostics;
using System.IO;

namespace Winland;

/// <summary>
/// Feature: run a configured app shortcut. If the action's first token names a "&lt;verb&gt;.ps1"
/// script next to the exe, it runs that script with the remaining arguments; otherwise the whole
/// line is executed as a command. See HotkeyConfig for the bind grammar.
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

            string script = Path.Combine(AppContext.BaseDirectory, verb + ".ps1");
            if (File.Exists(script))
            {
                string psArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}".TrimEnd();
                Process.Start(new ProcessStartInfo("powershell.exe", psArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(verb, args) { UseShellExecute = true });
            }
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
