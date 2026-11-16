using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// xUnit collection for every test class that bounds a pipeline by the WALL CLOCK
/// (<see cref="BoundedPipeline"/>). <c>DisableParallelization = true</c>: xUnit runs it alone, after every
/// parallel collection has finished, so the time measured is the pipeline's own.
///
/// <para><b>Why a wall-clock bound cannot run beside other classes.</b> On Windows PowerShell sends two things
/// to AMSI under ONE process-wide lock (<c>AmsiUtils.s_amsiLockObject</c>): the text of every script block it
/// compiles (<c>ScanContent</c>; each <see cref="PwshTestFixture.Create"/> scans the whole psm1 again), and every
/// .NET METHOD CALL a script makes (<c>MemberInvocationLoggingOps.LogMemberInvocation</c> to
/// <c>ReportContent</c>). A streaming proxy such as <c>Invoke-BashGrep</c> calls
/// <c>$steppable.Process($_)</c> once per record, so a 60000-record pipeline takes that lock 60000 times, each
/// time queueing behind the scans of the dozen classes running in parallel. Measured on the Windows fleet host
/// in a full run: 10000 records through the proxy took 13.9 s of wall time for 0.64 s of CPU, the same records
/// through the cmdlet directly 0.4 s, and the thread dumps show the pipeline thread in
/// <c>Monitor.Enter</c> under <c>AmsiUtils.WinReportContent</c>. <c>GrepStreamingTests.
/// Context_Before_ManyRecords_OnlyTheWindow</c> hit its 60 s bound that way in 3 of 4 gate runs while taking
/// about 1 s alone. Linux has no AMSI, so it never failed there.</para>
///
/// <para>Members keep their own <see cref="SharedPwshFixture"/>; only the guarantee that nothing else runs is
/// shared (same shape as <see cref="ProcessWorkingDirectoryCollection"/>).</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockBoundCollection
{
    public const string Name = "wall-clock-bound";
}
