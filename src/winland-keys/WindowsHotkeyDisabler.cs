using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Winland.Keys;

/// <summary>
/// Feature: turn off Windows' own Win+&lt;key&gt; hotkeys so the shell can't act on combos Winland owns
/// (notably Win+number launching taskbar apps). Sets the NoWinKeys policy; requires elevation.
/// </summary>
internal static class WindowsHotkeyDisabler
{
    /// <summary>
    /// Ensure the NoWinKeys policy is set. If it had to be changed, restart Explorer so it takes
    /// effect immediately (otherwise it's a no-op). No-ops silently if the key is locked (e.g. by org).
    /// </summary>
    public static void EnsureDisabled()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");

            if (key.GetValue("NoWinKeys") is int v && v == 1)
            {
                return; // already disabled
            }

            key.SetValue("NoWinKeys", 1, RegistryValueKind.DWord);
            RestartExplorer();
        }
        catch
        {
            // ignored (e.g. the policy key is org-locked even for admins)
        }
    }

    private static void RestartExplorer()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { /* ignored */ }
            }

            System.Threading.Thread.Sleep(1200);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
        }
        catch
        {
            // ignored
        }
    }
}
