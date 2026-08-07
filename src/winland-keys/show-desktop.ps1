<#
.SYNOPSIS
  Mimic Win+D behavior by minimizing all user-visible desktop windows.
.DESCRIPTION
  Enumerates top-level windows and minimizes eligible app windows, skipping
  shell/desktop and tool/owned windows.
.NOTES
  Bind scripts run with the user's normal (unelevated) token, so windows of
  elevated (administrator) apps are protected by UIPI and stay up.
#>

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ShowDesktop
{
	public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern int GetWindowTextLength(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

	[DllImport("dwmapi.dll")]
	public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

	[DllImport("user32.dll")]
	public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	private const uint GW_OWNER = 4;
	private const int GWL_EXSTYLE = -20;
	private const long WS_EX_TOOLWINDOW = 0x00000080;
	private const int DWMWA_CLOAKED = 14;
	private const int SW_MINIMIZE = 6;

	private static bool IsDesktopWindow(IntPtr hWnd)
	{
		var className = new System.Text.StringBuilder(256);
		int len = GetClassName(hWnd, className, className.Capacity);
		if (len <= 0) return false;

		string name = className.ToString(0, len);
		return string.Equals(name, "Progman", StringComparison.Ordinal)
			|| string.Equals(name, "WorkerW", StringComparison.Ordinal)
			|| string.Equals(name, "Shell_TrayWnd", StringComparison.Ordinal);
	}

	private static bool IsCloaked(IntPtr hWnd)
	{
		int cloaked;
		if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) != 0)
		{
			return false;
		}

		return cloaked != 0;
	}

	public static bool ShouldMinimize(IntPtr hWnd)
	{
		if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return false;
		if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
		if (GetWindowTextLength(hWnd) == 0) return false;
		if (IsDesktopWindow(hWnd)) return false;
		if (IsCloaked(hWnd)) return false;

		long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
		if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

		return true;
	}

	public static void MinimizeAll()
	{
		EnumWindows((hWnd, lParam) =>
		{
			if (ShouldMinimize(hWnd))
			{
				ShowWindow(hWnd, SW_MINIMIZE);
			}

			return true;
		}, IntPtr.Zero);
	}
}
'@

[ShowDesktop]::MinimizeAll()
