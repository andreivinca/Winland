using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Winland.Common;

namespace Winland;

/// <summary>
/// Winland's companion processes, and nothing else: starting them when Winland starts, and ending them
/// when it ends. Winland is the one program the user runs; next to it, in the same folder, run
///
///  * winland-keys       the keyboard hook. Runs with Winland's own (elevated) rights, so it also sees
///                       keys pressed in elevated windows.
///  * winland-indicator  the per-monitor workspace circles. Runs with the user's normal rights: it reads
///                       state off a pipe, draws, and sends one command (moving a workspace) over the
///                       user-accessible control pipe — a window the user drags needs no more.
///
/// Every running companion is in a <see cref="KillOnCloseJob"/> of its own, so it ends with Winland
/// however Winland ends — Exit from the tray, a crash, or Task Manager. One job each, because a process
/// that already belongs to a job can only join an empty one, and a companion may well start in one: the
/// shell-token launch puts the indicator in the logon service's job, and a Winland started by Task
/// Scheduler passes the scheduler's job on to winland-keys.
///
/// A copy left over from an earlier run (an older version, or one started by hand) is stopped first: it
/// isn't in Winland's jobs, and a second keyboard hook would fire every shortcut twice.
/// </summary>
internal sealed class Companions : IDisposable
{
    private enum Rights
    {
        /// <summary>Winland's own token (elevated).</summary>
        Winland,

        /// <summary>The user's normal token, borrowed from the desktop shell.</summary>
        User
    }

    private sealed record Companion(string Name, Rights Rights);

    private static readonly Companion[] All =
    {
        new("winland-keys", Rights.Winland),
        new("winland-indicator", Rights.User)
    };

    private readonly List<KillOnCloseJob> _jobs = new();

    /// <summary>Start every companion found next to winland.exe. A failure is logged and skipped.</summary>
    public void StartAll()
    {
        foreach (Companion companion in All)
        {
            try
            {
                Start(companion);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                Log.Line($"{companion.Name} could not be started: {ex.Message}");
            }
        }
    }

    /// <summary>End every companion (closing their jobs terminates them).</summary>
    public void Dispose()
    {
        foreach (KillOnCloseJob job in _jobs)
        {
            job.Dispose();
        }

        _jobs.Clear();
    }

    private void Start(Companion companion)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, companion.Name + ".exe");
        if (!File.Exists(exe))
        {
            Log.Line($"{companion.Name} not found next to winland.exe; not started");
            return;
        }

        StopLeftovers(companion.Name);

        using Process process = Launch(exe, companion.Rights);
        var job = new KillOnCloseJob();
        try
        {
            job.Add(process);
        }
        catch
        {
            // Never leave a companion running that wouldn't end with Winland.
            job.Dispose();
            process.Kill();
            throw;
        }

        _jobs.Add(job);
        Log.Line($"started {companion.Name} (pid {process.Id})");
    }

    private static Process Launch(string exe, Rights rights)
    {
        // When the shell's token isn't available (no Explorer, UAC off), a User companion falls back to
        // Winland's own rights: it works the same, only elevated.
        if (rights == Rights.User
            && UnelevatedLauncher.Start(exe, string.Empty, AppContext.BaseDirectory, out int processId))
        {
            return Process.GetProcessById(processId);
        }

        return Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })
            ?? throw new InvalidOperationException("no process was started");
    }

    /// <summary>Stop copies of a companion still running in this session from an earlier run.</summary>
    private static void StopLeftovers(string name)
    {
        using Process self = Process.GetCurrentProcess();
        int session = self.SessionId;
        foreach (Process leftover in Process.GetProcessesByName(name))
        {
            using (leftover)
            {
                if (leftover.SessionId != session)
                {
                    continue;
                }

                try
                {
                    leftover.Kill();
                    leftover.WaitForExit(2000);
                    Log.Line($"stopped a leftover {name} (pid {leftover.Id})");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    Log.Line($"could not stop a leftover {name} (pid {leftover.Id}): {ex.Message}");
                }
            }
        }
    }
}
