using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Winland.Env;

internal enum Direction
{
    Left,
    Up,
    Right,
    Down
}

/// <summary>
/// Feature: directional focus (Win+Arrows moves focus to the nearest window in a direction) and
/// closing the foreground window (Win+W). Self-contained window/geometry interop.
/// </summary>
internal static class WindowNavigator
{
    private const uint GW_HWNDPREV = 3;
    private const int WM_CLOSE = 0x0010;

    public static void CloseForeground()
    {
        IntPtr current = GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            return;
        }

        PostMessage(current, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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
            || string.Equals(name, "WorkerW", StringComparison.Ordinal);
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
            Console.WriteLine($"Focusing: {process.ProcessName}");
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
