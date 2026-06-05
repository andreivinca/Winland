<#
.SYNOPSIS
  Focus an already-running app's window, or launch it if it isn't running.
.PARAMETER App
  Executable / command to launch (e.g. firefox, code, notepad).
.PARAMETER Match
  Process name to match an existing window against. Defaults to App's file name without extension.
.PARAMETER LaunchArgs
  Arguments passed to the app when launching it. Everything after a "--" token is captured here, so
  dash-prefixed args pass through verbatim, e.g.  launch-or-focus wt.exe WindowsTerminal -- -d E:\
.EXAMPLE
  launch-or-focus.ps1 firefox
  launch-or-focus.ps1 code Code
  launch-or-focus.ps1 wt.exe WindowsTerminal -- -d E:\
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$App,
    [Parameter(Position = 1)][string]$Match,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$LaunchArgs
)

if ([string]::IsNullOrWhiteSpace($Match)) {
    $Match = [System.IO.Path]::GetFileNameWithoutExtension($App)
}

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LaunchFocus {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int c);
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] public static extern bool SpiGet(uint a, uint b, ref uint c, uint d);
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] public static extern bool SpiSet(uint a, uint b, UIntPtr c, uint d);
  static bool Cloaked(IntPtr h){ int v; if (DwmGetWindowAttribute(h,14,out v,4)!=0) return false; return v!=0; }
  // Find a top-level app window owned by one of the given process ids. Prefer a non-minimized one.
  public static IntPtr FindWindow(HashSet<int> pids){
    IntPtr best = IntPtr.Zero;
    EnumWindows((h,l)=>{
      if(!IsWindowVisible(h)) return true;
      if(GetWindow(h,4)!=IntPtr.Zero) return true;       // GW_OWNER != 0 -> owned/tool window
      if(GetWindowTextLength(h)==0) return true;
      if(Cloaked(h)) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      if(!pids.Contains((int)pid)) return true;
      best = h;
      if(!IsIconic(h)) return false;                     // found a visible one -> stop
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static void Focus(IntPtr h){
    if(IsIconic(h)) ShowWindow(h, 9);                    // SW_RESTORE
    uint orig = 0; bool got = SpiGet(0x2000,0,ref orig,0);
    if(got) SpiSet(0x2001,0,UIntPtr.Zero,0);             // clear foreground-lock timeout
    SetForegroundWindow(h);
    BringWindowToTop(h);
    if(got) SpiSet(0x2001,0,(UIntPtr)orig,0);
  }

  public static bool IsForegroundInSet(HashSet<int> pids){
    IntPtr h = GetForegroundWindow();
    if(h == IntPtr.Zero) return false;
    uint pid;
    GetWindowThreadProcessId(h, out pid);
    return pids.Contains((int)pid);
  }
}
'@

$pids = New-Object 'System.Collections.Generic.HashSet[int]'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -ieq $Match } |
    ForEach-Object { [void]$pids.Add($_.Id) }

$target = [IntPtr]::Zero
if ($pids.Count -gt 0) {
    $target = [LaunchFocus]::FindWindow($pids)
}

if ($target -ne [IntPtr]::Zero -and -not [LaunchFocus]::IsForegroundInSet($pids)) {
    [LaunchFocus]::Focus($target)
}
elseif ($null -eq $LaunchArgs -or $LaunchArgs.Count -eq 0) {
    Start-Process $App
}
else {
    Start-Process $App -ArgumentList $LaunchArgs
}
