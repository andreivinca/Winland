using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Winland.Common;

namespace Winland.Env;

internal enum Direction
{
    Left,
    Up,
    Right,
    Down
}

/// <summary>
/// Feature: window actions that don't involve workspaces — directional focus (Win+Arrows moves focus
/// to the nearest window in a direction), closing the foreground window (Win+W), and focusing an app
/// by process name (the launch-or-focus binds). Self-contained window/geometry interop.
/// </summary>
internal static class WindowNavigator
{
    private const uint GW_HWNDPREV = 3;
    private const uint GW_OWNER = 4;
    private const int WM_CLOSE = 0x0010;
    private const int SW_RESTORE = 9;
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    public static void CloseForeground()
    {
        IntPtr current = GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            return;
        }

        PostMessage(current, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Focus the best window of the given process ("focus-app", used by launch-or-focus binds):
    /// the topmost non-minimized window, or a minimized one (restored) when there is nothing else.
    /// Returns false when the process has no suitable window or is already in the foreground — in
    /// both cases the caller launches a new instance instead.
    /// </summary>
    public static bool FocusApp(string processName)
    {
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var pids = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                pids.Add((uint)p.Id);
            }
        }

        if (pids.Count == 0)
        {
            return false;
        }

        // Already in the foreground: report "nothing to focus" so the caller opens a fresh instance.
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero
            && GetWindowThreadProcessId(foreground, out uint foregroundPid) != 0
            && pids.Contains(foregroundPid))
        {
            return false;
        }

        // EnumWindows yields top-to-bottom, so the first non-minimized hit is the app's frontmost
        // window; the first minimized one is kept only as a fallback.
        IntPtr best = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h))
            {
                return true;
            }

            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid))
            {
                return true;
            }

            if (!IsIconic(h))
            {
                best = h;
                return false;
            }

            if (best == IntPtr.Zero)
            {
                best = h;
            }

            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero)
        {
            return false;
        }

        Log.Line($"focus-app {name}");
        ForceForeground(best);
        return true;
    }

    public static void FocusNearest(Direction direction)
    {
        IntPtr current = GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(current, out RECT currentRect))
        {
            return;
        }

        IntPtr bestHandle = IntPtr.Zero;
        long bestPrimary = long.MaxValue;
        long bestSecondary = long.MaxValue;

        EnumWindows((hWnd, _) =>
        {
            if (!IsCandidateWindow(hWnd, current))
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out RECT rect))
            {
                return true;
            }

            if (!TryGetDirectionalDistance(direction, currentRect, rect, out long primary, out long secondary))
            {
                return true;
            }

            if (primary < bestPrimary || (primary == bestPrimary && secondary < bestSecondary))
            {
                bestPrimary = primary;
                bestSecondary = secondary;
                bestHandle = hWnd;
            }

            return true;
        }, IntPtr.Zero);

        if (bestHandle != IntPtr.Zero)
        {
            LogFocusedProcess(bestHandle);
            SetForegroundWindow(bestHandle);
        }
    }

    private static bool IsCandidateWindow(IntPtr hWnd, IntPtr current)
    {
        if (hWnd == current || !IsWindowVisible(hWnd) || IsIconic(hWnd))
        {
            return false;
        }

        if (IsDesktopWindow(hWnd))
        {
            return false;
        }

        if (GetWindowTextLength(hWnd) == 0)
        {
            return false;
        }

        if (IsWindowCloaked(hWnd))
        {
            return false;
        }

        if (IsWindowOccluded(hWnd))
        {
            return false;
        }

        WINDOWPLACEMENT placement = new WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>()
        };

        if (GetWindowPlacement(hWnd, ref placement)
            && (placement.showCmd == ShowWindowCommand.ShowMinimized
                || placement.showCmd == ShowWindowCommand.Minimize))
        {
            return false;
        }

        return true;
    }

    // A top-level window that represents an app: visible, unowned, titled, not the shell, not cloaked.
    private static bool IsAppWindow(IntPtr hWnd)
    {
        return IsWindowVisible(hWnd)
            && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero
            && GetWindowTextLength(hWnd) > 0
            && !IsDesktopWindow(hWnd)
            && !IsWindowCloaked(hWnd);
    }

    private static bool IsDesktopWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(256);
        int length = GetClassName(hWnd, className, className.Capacity);
        if (length <= 0)
        {
            return false;
        }

        string name = className.ToString(0, length);
        return string.Equals(name, "Progman", StringComparison.Ordinal)
            || string.Equals(name, "WorkerW", StringComparison.Ordinal)
            || string.Equals(name, "Shell_TrayWnd", StringComparison.Ordinal); // the taskbar
    }

    // Bring a window to the foreground, restoring it first if minimized. Briefly clears the
    // foreground-lock timeout, the same technique WorkspaceManager uses (AttachThreadInput is avoided
    // — it can corrupt keyboard input state when attaching to the shell thread).
    private static void ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        uint original = 0;
        bool got = SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref original, 0);
        if (got)
        {
            SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, UIntPtr.Zero, 0);
        }

        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);

        if (got)
        {
            SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (UIntPtr)original, 0);
        }
    }

    private static bool IsWindowOccluded(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out RECT targetRect))
        {
            return false;
        }

        if (IsRectEmpty(targetRect))
        {
            return false;
        }

        IntPtr current = GetWindow(hWnd, GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (IsWindowVisible(current) && !IsIconic(current) && GetWindowRect(current, out RECT rect))
            {
                if (RectContains(rect, targetRect))
                {
                    return true;
                }
            }

            current = GetWindow(current, GW_HWNDPREV);
        }

        return false;
    }

    private static bool RectContains(RECT container, RECT target) =>
        container.Left <= target.Left
        && container.Top <= target.Top
        && container.Right >= target.Right
        && container.Bottom >= target.Bottom;

    private static bool IsRectEmpty(RECT rect) => rect.Right <= rect.Left || rect.Bottom <= rect.Top;

    private static void LogFocusedProcess(IntPtr hWnd)
    {
        if (GetWindowThreadProcessId(hWnd, out uint processId) == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            Log.Line($"focus -> {process.ProcessName}");
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryGetDirectionalDistance(Direction direction, RECT currentRect, RECT rect, out long primary, out long secondary)
    {
        primary = 0;
        secondary = 0;

        switch (direction)
        {
            case Direction.Left:
                if (rect.Left >= currentRect.Left)
                {
                    return false;
                }

                primary = currentRect.Left - rect.Left;
                secondary = RangeDistance(currentRect.Top, currentRect.Bottom, rect.Top, rect.Bottom);
                return true;

            case Direction.Up:
                if (rect.Top >= currentRect.Top)
                {
                    return false;
                }

                primary = currentRect.Top - rect.Top;
                secondary = RangeDistance(currentRect.Left, currentRect.Right, rect.Left, rect.Right);
                return true;

            case Direction.Right:
                if (rect.Left <= currentRect.Left)
                {
                    return false;
                }

                primary = rect.Left - currentRect.Left;
                secondary = RangeDistance(currentRect.Top, currentRect.Bottom, rect.Top, rect.Bottom);
                return true;

            case Direction.Down:
                if (rect.Top <= currentRect.Top)
                {
                    return false;
                }

                primary = rect.Top - currentRect.Top;
                secondary = RangeDistance(currentRect.Left, currentRect.Right, rect.Left, rect.Right);
                return true;

            default:
                return false;
        }
    }

    private static long RangeDistance(int start1, int end1, int start2, int end2)
    {
        if (end1 < start2)
        {
            return start2 - end1;
        }

        if (end2 < start1)
        {
            return start1 - end2;
        }

        return 0;
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        const int DWMWA_CLOAKED = 14;
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, Marshal.SizeOf<int>()) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public ShowWindowCommand showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private enum ShowWindowCommand : uint
    {
        Hide = 0,
        Normal = 1,
        ShowMinimized = 2,
        Maximize = 3,
        ShowNoActivate = 4,
        Show = 5,
        Minimize = 6
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, UIntPtr pvParam, uint fWinIni);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
