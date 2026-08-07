using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Winland.Keys;

/// <summary>
/// Starts programs with the user's NORMAL (unelevated) token. This daemon runs elevated, and children
/// inherit elevation — so without this, every app a bind launches (browser, editor, terminal) would
/// silently run as administrator. The fix is the classic "borrow the shell's token" technique: Explorer
/// (the desktop shell) always runs unelevated, so we duplicate its token and create the process with
/// that. Requires SeImpersonatePrivilege, which an elevated process has.
///
/// Both entry points return false when the unelevated route is unavailable (no shell running, token
/// APIs refused, UAC disabled) — callers then fall back to a plain, elevated launch.
/// </summary>
internal static class UnelevatedLauncher
{
    /// <summary>Start <paramref name="exePath"/> unelevated, CreateProcess-style (no shell semantics).</summary>
    public static bool Start(string exePath, string arguments, string? workingDirectory = null)
    {
        IntPtr token = GetShellPrimaryToken();
        if (token == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var commandLine = new StringBuilder($"\"{exePath}\" {arguments}".TrimEnd());

            if (!CreateProcessWithTokenW(token, 0, null, commandLine,
                    CREATE_NO_WINDOW, IntPtr.Zero, workingDirectory, ref startup, out PROCESS_INFORMATION process))
            {
                return false;
            }

            CloseHandle(process.hProcess);
            CloseHandle(process.hThread);
            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Run a command line unelevated WITH shell semantics (App Paths lookup, URIs, documents), which
    /// plain CreateProcess doesn't offer: an unelevated cmd.exe runs <c>start "" &lt;command&gt;</c>,
    /// and cmd's <c>start</c> is ShellExecute. cmd treats <c>&amp;</c> as a separator, so a bare URL
    /// with query parameters should be quoted in the bind.
    /// </summary>
    public static bool StartViaShell(string commandLine)
    {
        string cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return Start(cmd, $"/c start \"\" {commandLine}");
    }

    /// <summary>The desktop shell's token, duplicated as a primary token, or zero if unavailable.</summary>
    private static IntPtr GetShellPrimaryToken()
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        GetWindowThreadProcessId(shellWindow, out uint shellPid);
        if (shellPid == 0)
        {
            return IntPtr.Zero;
        }

        IntPtr process = IntPtr.Zero;
        IntPtr shellToken = IntPtr.Zero;
        try
        {
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
            if (process == IntPtr.Zero || !OpenProcessToken(process, TOKEN_DUPLICATE, out shellToken))
            {
                return IntPtr.Zero;
            }

            const uint access = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY
                | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            return DuplicateTokenEx(shellToken, access, IntPtr.Zero,
                SECURITY_IMPERSONATION, TOKEN_PRIMARY, out IntPtr primary) ? primary : IntPtr.Zero;
        }
        finally
        {
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    // ----- Win32 -----

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;

    private const int SECURITY_IMPERSONATION = 2; // SECURITY_IMPERSONATION_LEVEL
    private const int TOKEN_PRIMARY = 1;          // TOKEN_TYPE

    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize;
        public uint dwXCountChars, dwYCountChars;
        public uint dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags,
        string? lpApplicationName, StringBuilder lpCommandLine, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
