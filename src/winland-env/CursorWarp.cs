using System;
using System.Runtime.InteropServices;

namespace Winland.Env;

/// <summary>
/// The mouse follows keyboard focus. When a command focuses another window or moves the focused one, the
/// cursor jumps to the middle of that window — unless it is already over it. Otherwise the cursor stays
/// over whatever used to be there, and with "focus follows mouse" (or simply the next click or scroll) the
/// user ends up acting on the wrong window.
///
/// The Dispatcher applies <see cref="Follow"/> to the commands that deliberately focus or move a window.
/// Showing a workspace uses <see cref="ToWindow"/> / <see cref="ToArea"/> instead, with the window it
/// focused (or its monitor, when it has none): its focus change can't be read off the foreground window,
/// which may be whatever Windows activated while the outgoing windows were put away.
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
        if (GetCursorPos(out POINT cursor) && WarpTarget(before, after, IsOver(after.Window, cursor)) is (int x, int y))
        {
            SetCursorPos(x, y);
        }
    }

    /// <summary>Put the mouse on <paramref name="window"/>: the centre of its frame, unless it is over it already.</summary>
    public static void ToWindow(IntPtr window)
    {
        if (GetCursorPos(out POINT cursor) && WindowTarget(FrameOf(window), IsOver(window, cursor)) is (int x, int y))
        {
            SetCursorPos(x, y);
        }
    }

    /// <summary>Put the mouse on <paramref name="area"/> (e.g. a monitor): its centre, unless it is inside already.</summary>
    public static void ToArea(TileRect area)
    {
        if (GetCursorPos(out POINT cursor) && AreaTarget(area, cursor.X, cursor.Y) is (int x, int y))
        {
            SetCursorPos(x, y);
        }
    }

    /// <summary>
    /// Where the cursor should go after a command: the centre of the focused window when focus moved to
    /// it or it moved, and the cursor isn't over it already; otherwise null (leave the mouse alone).
    /// </summary>
    internal static (int X, int Y)? WarpTarget(FocusState before, FocusState after, bool cursorOverWindow) =>
        after.Window == IntPtr.Zero || after == before ? null : WindowTarget(after.Frame, cursorOverWindow);

    /// <summary>The centre of a window's <paramref name="frame"/>, or null when there is none or the cursor is over it.</summary>
    internal static (int X, int Y)? WindowTarget(TileRect frame, bool cursorOverWindow) =>
        frame.Width <= 0 || frame.Height <= 0 || cursorOverWindow ? null : Centre(frame);

    /// <summary>The centre of <paramref name="area"/>, or null when it is empty or the cursor is inside it.</summary>
    internal static (int X, int Y)? AreaTarget(TileRect area, int cursorX, int cursorY)
    {
        bool inside = cursorX >= area.Left && cursorX < area.Right && cursorY >= area.Top && cursorY < area.Bottom;
        return area.Width <= 0 || area.Height <= 0 || inside ? null : Centre(area);
    }

    private static (int X, int Y) Centre(TileRect r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    private static bool IsOver(IntPtr window, POINT cursor) =>
        window != IntPtr.Zero && GetAncestor(WindowFromPoint(cursor), GA_ROOT) == window;

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
