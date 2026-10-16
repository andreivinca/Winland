using System;
using System.Runtime.InteropServices;

namespace Winland.Env;

/// <summary>
/// The mouse follows keyboard focus. When a command focuses another window or moves the focused one, the
/// cursor jumps to the middle of that window — unless it is already over it. Otherwise the cursor stays
/// over whatever used to be there, and with "focus follows mouse" (or simply the next click or scroll) the
/// user ends up acting on the wrong window.
///
/// The Dispatcher applies it to the commands that deliberately focus or move a window. Workspace
/// switches don't warp: the mouse's monitor is how the user points a new workspace, the scratchpad, or a
/// release at a monitor, and a switch's focus change can be a side effect (Windows activating whatever
/// is left when the outgoing windows are put away).
/// </summary>
internal static class CursorWarp
{
    /// <summary>The focused window and its visible frame (empty when there is none, or it is minimized).</summary>
    internal readonly record struct FocusState(IntPtr Window, TileRect Frame);

    public static FocusState Capture()
    {
        IntPtr window = GetForegroundWindow();
        return new FocusState(window, FrameOf(window));
    }

    /// <summary>Bring the mouse to the focused window if focus or its position changed since <paramref name="before"/>.</summary>
    public static void Follow(FocusState before)
    {
        FocusState after = Capture();
        if (!GetCursorPos(out POINT cursor))
        {
            return;
        }

        bool over = GetAncestor(WindowFromPoint(cursor), GA_ROOT) == after.Window;
        if (WarpTarget(before, after, over) is (int x, int y))
        {
            SetCursorPos(x, y);
        }
    }

    /// <summary>
    /// Where the cursor should go after a command: the centre of the focused window when focus moved to
    /// it or it moved, and the cursor isn't over it already; otherwise null (leave the mouse alone).
    /// </summary>
    internal static (int X, int Y)? WarpTarget(FocusState before, FocusState after, bool cursorOverWindow)
    {
        if (after.Window == IntPtr.Zero || after.Frame.Width <= 0 || after.Frame.Height <= 0)
        {
            return null;
        }

        if (after == before || cursorOverWindow)
        {
            return null;
        }

        return ((after.Frame.Left + after.Frame.Right) / 2, (after.Frame.Top + after.Frame.Bottom) / 2);
    }

    /// <summary>The window's visible frame on screen (without the invisible resize border).</summary>
    private static TileRect FrameOf(IntPtr window)
    {
        if (window == IntPtr.Zero || IsIconic(window))
        {
            return default;
        }

        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(window, out frame))
        {
            return default;
        }

        return new TileRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    // ----- Win32 -----

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
}
