using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Winland.Common;

namespace Winland.Keys;

/// <summary>
/// Migration: give Windows its own Win+&lt;key&gt; shortcuts back on machines where an earlier Winland
/// turned them all off with the NoWinKeys policy.
///
/// Winland no longer needs that policy. The keyboard hook swallows every combo Winland binds before the
/// shell sees it, so Windows can't act on those; every other Win combo (Win+V, Win+I, Win+Tab, Win+L…)
/// keeps its normal Windows meaning, and when Winland isn't running all of them work as usual — there
/// is nothing to put back on exit. The policy, by contrast, outlived Winland and only took effect (in
/// either direction) after an Explorer restart.
/// </summary>
internal static class WindowsHotkeyRestorer
{
    private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string PolicyValueName = "NoWinKeys";

    /// <summary>
    /// If the NoWinKeys policy is on for this user, remove it and restart Explorer once so the shell's
    /// shortcuts come back immediately. A no-op when it's already off; logged if the key is locked.
    /// </summary>
    public static void RestoreIfDisabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PolicyKeyPath, writable: true);
            if (key?.GetValue(PolicyValueName) is not int value || value == 0)
            {
                return; // Windows' Win-key shortcuts are already on
            }

            key.DeleteValue(PolicyValueName);
            Log.Line("removed the NoWinKeys policy left by an earlier Winland; restarting Explorer");
            RestartExplorer();
        }
        catch (Exception ex)
        {
            // e.g. the policy key is org-locked even for admins.
            Log.Line($"NoWinKeys policy not removed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restart the shell so it re-reads the policy. Happens at most once, when the policy was
    /// actually removed. Explorer is asked to exit CLEANLY first (the same message as the taskbar's
    /// Ctrl+Shift+right-click "Exit Explorer"), so it saves its state; killing is only the fallback.
    /// Windows itself restarts the exited shell (AutoRestartShell, on by default) with the user's
    /// normal token — we start one ourselves only if that doesn't happen.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                PostMessage(taskbar, WM_USER + 436, IntPtr.Zero, IntPtr.Zero);
            }

            if (!WaitFor(() => CountSessionExplorers() == 0, timeoutMs: 3000))
            {
                foreach (Process p in SessionExplorers())
                {
                    using (p)
                    {
                        try { p.Kill(); } catch { /* already gone */ }
                    }
                }
            }

            if (!WaitFor(() => CountSessionExplorers() > 0, timeoutMs: 3000))
            {
                // Last resort, e.g. AutoRestartShell disabled. Started from this elevated process the
                // shell would inherit elevation, but with no shell running there is no unelevated token
                // left to borrow — an elevated shell beats no shell.
                using Process? shell = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Line($"explorer restart failed: {ex.Message}");
        }
    }

    /// <summary>Explorer processes in THIS logon session — never another signed-in user's shell.</summary>
    private static List<Process> SessionExplorers()
    {
        using Process current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var mine = new List<Process>();
        foreach (Process p in Process.GetProcessesByName("explorer"))
        {
            if (p.SessionId == session)
            {
                mine.Add(p);
            }
            else
            {
                p.Dispose();
            }
        }

        return mine;
    }

    private static int CountSessionExplorers()
    {
        List<Process> explorers = SessionExplorers();
        foreach (Process p in explorers)
        {
            p.Dispose();
        }

        return explorers.Count;
    }

    /// <summary>Poll <paramref name="condition"/> until it holds or the timeout passes.</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        int deadline = Environment.TickCount + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount - deadline >= 0)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    // ----- Win32 -----

    private const int WM_USER = 0x0400;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
