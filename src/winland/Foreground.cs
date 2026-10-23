using System;
using System.Runtime.InteropServices;
using Winland.Common;

namespace Winland;

/// <summary>
/// Activates another process's window from <c>winland</c>, which runs in the background.
///
/// Windows only lets a process take the foreground in a few cases — chiefly when it received the last
/// input event. A hotkey's keystroke goes to <c>winland-keys</c> (whose hook swallows it), then
/// <c>winlandctl</c> forwards the command here over the pipe, so <c>winland</c> never received it. Then
/// <c>SetForegroundWindow</c> is refused unless some other exception happens to apply (e.g. nothing is in
/// the foreground, as after a workspace's windows are put away) — and when it is refused the target only
/// flashes on the taskbar. That is why focusing used to fail only sometimes.
///
/// So before activating, <c>winland</c> injects a no-op mouse input: it doesn't move the cursor or click,
/// but makes <c>winland</c> the process that received the last input, which grants the foreground right.
/// (Clearing the foreground-lock timeout, the earlier approach, doesn't help: Windows only lets a process
/// that may already set the foreground change that setting.)
/// </summary>
internal static class Foreground
{
    /// <summary>Restore <paramref name="hWnd"/> if minimized, activate it and raise it; true when it is now the foreground window.</summary>
    public static bool Activate(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        SendNoOpInput();
        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);

        bool activated = GetForegroundWindow() == hWnd;
        if (!activated)
        {
            Log.Line($"foreground refused for window 0x{hWnd.ToInt64():X}");
        }

        return activated;
    }

    // A relative mouse "move" by (0, 0) with no flags: no motion, no button — only an input event.
    private static void SendNoOpInput()
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE } };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
        {
            Log.Line($"SendInput failed (error {Marshal.GetLastWin32Error()})");
        }
    }

    // ----- Win32 -----

    private const int SW_RESTORE = 9;
    private const uint INPUT_MOUSE = 0;

    // MOUSEINPUT is the largest member of INPUT's union, so this layout has the native size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
