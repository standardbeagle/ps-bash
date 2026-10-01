using System.Management.Automation;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>&lt;(producer)</c> (psm1 <c>Invoke-ProcessSub</c>) streams the producer into its temp file as records are
/// produced (<see cref="ProcessSubFileWriter"/>) instead of collecting the whole output into a list, a
/// StringBuilder and a string. Asserted by STATE ORDER (bytes are on disk while the producer still runs) and
/// by exact file bytes, never by a memory number.
/// </summary>
public class ProcessSubStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public ProcessSubStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private const string SnapshotFn =
        "function Get-SubNames { $d = Join-Path ([System.IO.Path]::GetTempPath()) 'ps-bash/proc-sub'; " +
        "if (Test-Path $d) { @(Get-ChildItem $d | ForEach-Object Name) } else { @() } }; ";

    [Fact]
    public void ProducerOutput_IsOnDisk_WhileTheProducerIsStillRunning()
    {
        var got = Run(
            SnapshotFn +
            "$before = Get-SubNames; $global:mid = -1; " +
            "$p = Invoke-ProcessSub { 1..120000 | ForEach-Object { " +
            "  if ($_ -eq 110000) { " +
            "    $new = @(Get-SubNames | Where-Object { $before -notcontains $_ }); " +
            "    $dir = Join-Path ([System.IO.Path]::GetTempPath()) 'ps-bash/proc-sub'; " +
            "    $global:mid = (Get-Item (Join-Path $dir $new[0])).Length } " +
            "  \"row $_ of the stream, padded out a little\" } }; " +
            "$final = (Get-Item $p).Length; Remove-Item $p; \"$($global:mid -gt 0) $($global:mid -lt $final) $final\"");
        Assert.StartsWith("True True ", got[0]);
    }

    [Fact]
    public void FileBytes_AreExact_IncludingAMissingFinalNewlineAndEscapedBytes()
    {
        var got = Run(
            "$p1 = Invoke-ProcessSub { 'one'; 'two' }; $a = [BitConverter]::ToString([IO.File]::ReadAllBytes($p1)); " +
            "$p2 = Invoke-ProcessSub { Invoke-BashPrintf 'a\\nb' }; $b = [BitConverter]::ToString([IO.File]::ReadAllBytes($p2)); " +
            "$p3 = Invoke-ProcessSub { Invoke-BashPrintf '%s\\n' x }; $c = [BitConverter]::ToString([IO.File]::ReadAllBytes($p3)); " +
            "$p4 = Invoke-ProcessSub { @() }; $d = [IO.File]::ReadAllBytes($p4).Length; " +
            "$p5 = Invoke-ProcessSub { 'caf' + [char]0xe9 + [char]0xDCE9 + ' ' + [char]0x20ac }; $e = [BitConverter]::ToString([IO.File]::ReadAllBytes($p5)); " +
            "Remove-Item $p1, $p2, $p3, $p4, $p5; \"$a|$b|$c|$d|$e\"");
        Assert.Equal("6F-6E-65-0A-74-77-6F-0A|61-0A-62|78-0A|0|63-61-66-C3-A9-E9-20-E2-82-AC-0A", got[0]);
    }

    [Fact]
    public void ProducerError_DeletesTheTempFile_AndRethrows()
    {
        var got = Run(
            SnapshotFn +
            "$before = Get-SubNames; $err = ''; " +
            "try { [void](Invoke-ProcessSub { 'x'; throw 'producer failed' }) } catch { $err = $_.Exception.Message }; " +
            "$left = @(Get-SubNames | Where-Object { $before -notcontains $_ }).Count; \"$err|$left\"");
        Assert.Equal("producer failed|0", got[0]);
    }

    [Fact]
    public void Writer_RecordsAreBufferedToDisk_NotHeldInMemory()
    {
        string path = Path.Combine(Path.GetTempPath(), "psb-psw-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var w = new ProcessSubFileWriter(path))
            {
                for (int i = 0; i < 50000; i++) w.Add(PSObject.AsPSObject("record number " + i));
                w.Flush();
                Assert.Equal(50000, w.Records);
                long onDisk = new FileInfo(path).Length;
                Assert.True(onDisk > 500_000, $"on disk after flush: {onDisk}");
            }
            Assert.Equal(50000, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }
}
