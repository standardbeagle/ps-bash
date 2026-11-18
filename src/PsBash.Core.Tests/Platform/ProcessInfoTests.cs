using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PsBash.Core.Platform;
using PsBash.Core.Runtime.Processes;
using Xunit;

namespace PsBash.Core.Tests.Platform;

/// <summary>
/// <see cref="ProcessInfo"/>: the byte decoders run on every OS from hand-built buffers (Linux <c>/proc</c> text, a
/// macOS <c>struct proc_bsdinfo</c> laid out per XNU <c>bsd/sys/proc_info.h</c>), and the live lookups are checked
/// against facts the test owns (a child it started, its own uid and process group) on whatever OS runs them — on
/// macOS CI that live half is what proves the libproc offsets.
/// Oracle note (qa-rubric Directive 1): OS process metadata has no bash equivalent; the kernel is the oracle.
/// </summary>
public class ProcessInfoTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // ---------------------------------------------------------------- /proc/{pid}/stat

    [Fact]
    public void ParseProcStat_PlainRecord_ReadsStatePpidAndGroup()
    {
        Assert.True(ProcessInfo.TryParseProcStat("4242 (sleep) S 4100 4242 4100 34816 4242 4194304 ..."u8, out var stat));
        Assert.Equal(new ProcessInfo.ProcStat('S', 4100, 4242), stat);
        Assert.False(stat.IsZombie);
    }

    [Fact]
    public void ParseProcStat_CommWithSpacesAndParens_CutsAfterTheLastParen()
    {
        Assert.True(ProcessInfo.TryParseProcStat("77 (a) b (c)) R 1 77 77 0 -1\n"u8, out var stat));
        Assert.Equal(new ProcessInfo.ProcStat('R', 1, 77), stat);
    }

    [Fact]
    public void ParseProcStat_Zombie_IsZombie()
    {
        Assert.True(ProcessInfo.TryParseProcStat("9 (defunct) Z 8 8 8 0"u8, out var stat));
        Assert.True(stat.IsZombie);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12 (no close paren S 1 1")]
    [InlineData("12 (x)")]
    [InlineData("12 (x) S")]
    [InlineData("12 (x) S 1")]
    [InlineData("12 (x) S notanumber 1")]
    [InlineData("12 (x) SS 1 1")]
    [InlineData("12 (x) S 99999999999 1")]
    public void ParseProcStat_TruncatedOrMalformed_IsRejected(string text)
        => Assert.False(ProcessInfo.TryParseProcStat(Encoding.ASCII.GetBytes(text), out _));

    // ---------------------------------------------------------------- /proc/{pid}/status

    [Fact]
    public void ParseStatus_ReadsUmaskAndRealUid()
    {
        const string status = "Name:\tbash\nUmask:\t0027\nState:\tS (sleeping)\nUid:\t1000\t1001\t1002\t1003\nGid:\t100\t100\t100\t100\n";
        Assert.Equal(0x17, ProcessInfo.ParseStatusUmask(status)); // 0027
        Assert.Equal(1000u, ProcessInfo.ParseStatusRealUid(status));
    }

    [Theory]
    [InlineData("Name:\tx\nUid:\t0\t0\t0\t0\n")]          // kernel before 4.7: no Umask line
    [InlineData("Umask:\t\n")]
    [InlineData("Umask:\t0089\n")]
    public void ParseStatusUmask_AbsentOrMalformed_IsNull(string status)
        => Assert.Null(ProcessInfo.ParseStatusUmask(status));

    [Theory]
    [InlineData("Name:\tx\n")]
    [InlineData("Uid:\tabc\t0\n")]
    [InlineData("Uid:\t-1\t0\n")]
    public void ParseStatusRealUid_AbsentOrMalformed_IsNull(string status)
        => Assert.Null(ProcessInfo.ParseStatusRealUid(status));

    // ---------------------------------------------------------------- macOS struct proc_bsdinfo

    /// <summary>A <c>struct proc_bsdinfo</c> as <c>proc_pidinfo(PROC_PIDTBSDINFO)</c> fills it, every field distinct so a
    /// wrong offset reads a wrong value.</summary>
    private static byte[] BsdInfo(uint status, uint pid, uint ppid, uint pgid, uint ruid)
    {
        var b = new byte[ProcessInfo.BsdInfoSize];
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        U32(0, 0x11);        // pbi_flags
        U32(4, status);      // pbi_status
        U32(8, 0x22);        // pbi_xstatus
        U32(12, pid);        // pbi_pid
        U32(16, ppid);       // pbi_ppid
        U32(20, 501);        // pbi_uid (effective)
        U32(24, 20);         // pbi_gid
        U32(28, ruid);       // pbi_ruid
        U32(32, 21);         // pbi_rgid
        U32(36, 502);        // pbi_svuid
        U32(40, 22);         // pbi_svgid
        Encoding.ASCII.GetBytes("ps-bash-host").CopyTo(b, 48);  // pbi_comm[16]
        Encoding.ASCII.GetBytes("ps-bash-host").CopyTo(b, 64);  // pbi_name[32]
        U32(96, 33);         // pbi_nfiles
        U32(100, pgid);      // pbi_pgid
        U32(104, 44);        // pbi_pjobc
        U32(108, 55);        // e_tdev
        U32(112, 66);        // e_tpgid
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(120), 1_700_000_000); // pbi_start_tvsec
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(128), 123_456);       // pbi_start_tvusec
        return b;
    }

    [Fact]
    public void DecodeBsdInfo_ReadsStatusPidPpidGroupAndRealUid()
    {
        Assert.True(ProcessInfo.TryDecodeBsdInfo(BsdInfo(status: 2, pid: 4242, ppid: 4100, pgid: 4000, ruid: 503), out var info));
        Assert.Equal(new ProcessInfo.BsdInfo(2, 4242, 4100, 4000, 503), info);
        Assert.False(info.IsZombie);
    }

    [Fact]
    public void DecodeBsdInfo_SzombStatus_IsZombie()
    {
        Assert.True(ProcessInfo.TryDecodeBsdInfo(BsdInfo(status: ProcessInfo.SZOMB, pid: 9, ppid: 8, pgid: 8, ruid: 0), out var info));
        Assert.True(info.IsZombie);
    }

    [Fact]
    public void DecodeBsdInfo_ShortBuffer_IsRejected()
        => Assert.False(ProcessInfo.TryDecodeBsdInfo(new byte[ProcessInfo.BsdInfoSize - 1], out _));

    [Fact]
    public void DecodePidList_UsesTheByteCount_AndSkipsEmptySlots()
    {
        int[] buffer = [1, 0, 300, 4242, 77, 88];
        Assert.Equal(new[] { 1, 300, 4242 }, ProcessInfo.DecodePidList(buffer, 4 * sizeof(int)));
        Assert.Empty(ProcessInfo.DecodePidList(buffer, 0));
        Assert.Empty(ProcessInfo.DecodePidList(buffer, -1));
        Assert.Equal(new[] { 1, 300, 4242, 77, 88 }, ProcessInfo.DecodePidList(buffer, 1000)); // never past the buffer
    }

    // ---------------------------------------------------------------- live, on the running OS

    private static JobProcessStart Lingering()
        => OperatingSystem.IsWindows()
            ? new("cmd.exe", ["/c", "ping -n 120 127.0.0.1 >nul"])
            : new("/bin/sh", ["-c", "sleep 120"]);

    [Fact]
    public async Task ParentPid_OfAChildThisProcessStarted_IsThisProcess()
    {
        using var job = JobProcess.Start(Lingering());
        try
        {
            Assert.Equal(Environment.ProcessId, ProcessInfo.ParentPid(job.Pid));
            Assert.False(ProcessInfo.IsZombie(job.Pid));
        }
        finally
        {
            job.KillTree();
            await job.Exited.WaitAsync(Wait);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Queries_OnANonPid_AnswerUnknown(int pid)
    {
        Assert.Null(ProcessInfo.ParentPid(pid));
        Assert.False(ProcessInfo.IsZombie(pid));
        Assert.Empty(ProcessInfo.GroupMembers(pid));
        Assert.False(ProcessInfo.TryGetRealUid(pid, out _));
    }

    [Fact]
    public void Queries_OnAPidThatDoesNotExist_AnswerUnknown()
    {
        const int Gone = int.MaxValue - 7; // above every pid_max (Linux 2^22, macOS 99999, Windows multiples of 4)
        Assert.Null(ProcessInfo.ParentPid(Gone));
        Assert.False(ProcessInfo.IsZombie(Gone));
        Assert.False(ProcessInfo.TryGetRealUid(Gone, out _));
    }

    [SkippableFact]
    public async Task GroupMembers_OfAChildsOwnGroup_HoldsTheChild()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no process groups (JobProcess uses a Job Object there)");
        using var job = JobProcess.Start(Lingering());
        try
        {
            // JobProcess makes the child lead its own group (pgid == pid) unless the test host owns the terminal's
            // foreground group, which a test runner does not.
            Assert.Contains(job.Pid, ProcessInfo.GroupMembers(job.Pid));
        }
        finally
        {
            job.KillTree();
            await job.Exited.WaitAsync(Wait);
        }
    }

    [SkippableFact]
    public void GroupMembers_OfThisProcesssGroup_HoldsThisProcess()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no process groups");
        Assert.Contains(Environment.ProcessId, ProcessInfo.GroupMembers(getpgrp()));
    }

    [SkippableFact]
    public void TryGetRealUid_OfThisProcess_IsGetuid()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no uids");
        Assert.True(ProcessInfo.TryGetRealUid(Environment.ProcessId, out var uid));
        Assert.Equal(getuid(), uid);
    }

    [SkippableFact]
    public void TryGetRealUid_OnWindows_HasNoOpinion()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Unix answer pinned by TryGetRealUid_OfThisProcess_IsGetuid");
        Assert.False(ProcessInfo.TryGetRealUid(Environment.ProcessId, out _));
    }

    [SkippableFact]
    public void ProcessUmask_ProbeDirectory_AgreesWithTheKernelsMask()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no creation mask (ProcessUmask is the conventional 022 there)");
        // Linux: the probe (macOS's only source) must agree with /proc/self/status; macOS: with itself via the cache.
        Assert.Equal(ProcessInfo.ProcessUmask(), ProcessInfo.ProbeUmask());
    }

    [SkippableFact]
    public void ProcessUmask_OnWindows_Is022()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Unix answer pinned by ProcessUmask_ProbeDirectory_AgreesWithTheKernelsMask");
        Assert.Equal(0x12, ProcessInfo.ProcessUmask());
    }

    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern int getpgrp();
}
