using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Tests;

/// <summary>
/// The lifetime contract Winland's companions rely on, checked with real (hidden, harmless) processes:
/// closing the job ends what is in it, and what THOSE processes start is not in it — the apps a
/// shortcut opens must never close with Winland.
/// </summary>
[TestClass]
public sealed class KillOnCloseJobTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Dispose_EndsTheProcessesInTheJob()
    {
        using Process member = StartHidden("ping -n 60 127.0.0.1");
        var job = new KillOnCloseJob();
        job.Add(member);

        job.Dispose();

        Assert.IsTrue(member.WaitForExit(Generous), "the job's process should have been ended");
    }

    [TestMethod]
    public void Add_AProcessAlreadyInAnotherJob_JoinsAnEmptyOneAndEndsWithIt()
    {
        // Like the indicator (started via the logon service, which puts it in a job) or winland-keys
        // under a Winland started by Task Scheduler: already in a job, it can still join an empty one.
        using Process member = StartHidden("ping -n 60 127.0.0.1");
        using var outer = new KillOnCloseJob();
        outer.Add(member);
        var own = new KillOnCloseJob();
        own.Add(member);

        own.Dispose();

        Assert.IsTrue(member.WaitForExit(Generous), "closing its own job should have ended it");
    }

    [TestMethod]
    public void ProcessesStartedByAMember_AreNotInTheJobAndOutliveIt()
    {
        // cmd waits a second (so it is in the job before it starts anything), then starts a child.
        using Process member = StartHidden("ping -n 2 127.0.0.1 >nul & ping -n 60 127.0.0.1 >nul");
        var job = new KillOnCloseJob();
        job.Add(member);

        Process? child = WaitForChild(member.Id, "PING.EXE");
        Assert.IsNotNull(child, "cmd should have started its ping child");
        using (child)
        {
            try
            {
                job.Dispose();

                Assert.IsTrue(member.WaitForExit(Generous), "the member itself should have been ended");
                Assert.IsFalse(child.WaitForExit(500), "the member's child must not end with the job");
            }
            finally
            {
                child.Kill();
            }
        }
    }

    private static Process StartHidden(string commandLine) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commandLine}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    /// <summary>The first process named <paramref name="exeName"/> whose parent is <paramref name="parentId"/>.</summary>
    private static Process? WaitForChild(int parentId, string exeName)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (DateTime.UtcNow < deadline)
        {
            foreach ((int pid, int ppid, string exe) in Snapshot())
            {
                if (ppid == parentId && string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return Process.GetProcessById(pid);
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static List<(int Pid, int ParentPid, string Exe)> Snapshot()
    {
        var processes = new List<(int, int, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                processes.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return processes;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
