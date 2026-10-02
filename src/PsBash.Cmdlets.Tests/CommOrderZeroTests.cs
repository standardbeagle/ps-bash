using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// comm <c>--check-order</c> / <c>--nocheck-order</c> (GNU's unsorted-input diagnostics and exit status)
/// and <c>-z</c>. Oracle: GNU coreutils 9.4 (`wsl bash`); every expected output below was read from it.
/// </summary>
public class CommOrderZeroTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public CommOrderZeroTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "cz_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes(content));
        return p.Replace('\\', '/');
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string Bytes(string cmd) =>
        Run($"$r = @({cmd}); [PsBash.Cmdlets.BashRuntime]::RecordStreamText($r).Replace([string][char]0,'@').Replace(\"`n\",'~').Replace(\"`t\",'>')")
            .AssertSuccess().Stdout;

    [Fact]
    public void Default_DisorderAfterAnUnpairableLine_WarnsOncePerFileAndFails()
    {
        string u1 = F("u1", "b\na\nc\n"), s1 = F("s1", "a\nb\nc\n");
        var r = Run($"Invoke-BashComm '{u1}' '{s1}'");
        Assert.Equal(new[] { "\ta", "\t\tb", "a", "\t\tc" }, r.Lines);
        Assert.Equal("comm: file 1 is not in sorted order\ncomm: input is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Default_DisorderInSecondFile()
    {
        string s1 = F("s1", "a\nb\nc\n"), u2 = F("u2", "a\nc\nb\n");
        var r = Run($"Invoke-BashComm '{s1}' '{u2}'");
        Assert.Equal(new[] { "\t\ta", "b", "\t\tc", "\tb" }, r.Lines);
        Assert.Equal("comm: file 2 is not in sorted order\ncomm: input is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Default_NoCheckUntilAnUnpairableLineIsSeen()   // oracle: identical unsorted files are fine
    {
        string u1 = F("u1", "b\na\nc\n");
        Run($"Invoke-BashComm '{u1}' '{u1}'").AssertSuccess();
        // first disorder AFTER the unpaired `z`: only then is it checked (oracle: z, warning, b)
        string e1 = F("e1", "a\nz\nb\n"), e2 = F("e2", "a\n");
        var r = Run($"Invoke-BashComm '{e1}' '{e2}'");
        Assert.Equal(new[] { "\t\ta", "z", "b" }, r.Lines);
        Assert.Equal("comm: file 1 is not in sorted order\ncomm: input is not in sorted order", r.Stderr);
    }

    [Fact]
    public void Default_CheckRunsEvenWhenColumnsAreSuppressed()
    {
        string e1 = F("e1", "a\nz\nb\n"), e2 = F("e2", "a\n");
        var r = Run($"Invoke-BashComm '-12' '{e1}' '{e2}'");
        Assert.Equal(new[] { "a" }, r.Lines);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("comm: input is not in sorted order", r.Stderr);
    }

    [Fact]
    public void CheckOrder_StopsAtFirstDisorderWithoutTheSummaryLine()
    {
        string u1 = F("u1", "b\na\nc\n"), s1 = F("s1", "a\nb\nc\n");
        var r = Run($"Invoke-BashComm '--check-order' '{u1}' '{s1}'");
        Assert.Equal(new[] { "\ta", "\t\tb" }, r.Lines);
        Assert.Equal("comm: file 1 is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void CheckOrder_ChecksPairedFilesToo()   // oracle: --check-order flags a disorder in a file that never had an unpaired line
    {
        string ua = F("ua", "b\na\n");
        var r = Run($"Invoke-BashComm '--check-order' '{ua}' '{ua}'");
        Assert.Equal(new[] { "\t\tb" }, r.Lines);
        Assert.Equal("comm: file 1 is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void NoCheckOrder_NeverChecks_AndLastOptionWins()
    {
        string u1 = F("u1", "b\na\nc\n"), s1 = F("s1", "a\nb\nc\n");
        var r = Run($"Invoke-BashComm '--nocheck-order' '{u1}' '{s1}'").AssertSuccess();
        Assert.Equal(new[] { "\ta", "\t\tb", "a", "\t\tc" }, r.Lines);
        Run($"Invoke-BashComm '--check-order' '--nocheck-order' '{u1}' '{s1}'").AssertSuccess();
        Run($"Invoke-BashComm '--nocheck-order' '--check-order' '{u1}' '{s1}'").AssertFailed(1, "file 1 is not in sorted order");
    }

    [Fact]
    public void Sorted_Input_IsSilentUnderCheckOrder()
    {
        string s1 = F("s1", "a\nb\nc\n");
        Run($"Invoke-BashComm '--check-order' '{s1}' '{s1}'").AssertSuccess();
    }

    [Fact]
    public void Z_NulRecords_ColumnsTotalAndDelimiter()
    {
        string z1 = F("z1", "a\0b\0c\0"), z2 = F("z2", "b\0c\0d");
        Assert.Equal("a@>>b@>>c@>d@", Bytes($"Invoke-BashComm '-z' '{z1}' '{z2}'"));
        Assert.Equal("a@XXb@XXc@Xd@", Bytes($"Invoke-BashComm '-z' '--output-delimiter=X' '{z1}' '{z2}'"));
        Assert.Equal("a@>>b@>>c@>d@1>1>2>total@", Bytes($"Invoke-BashComm '-z' '--total' '{z1}' '{z2}'"));
    }

    [Fact]
    public void Z_NewlineIsDataInsideARecord()
    {
        string z3 = F("z3", "a\nb\0c\0"), z2 = F("z2", "b\0c\0d");
        Assert.Equal("a~b@>b@>>c@>d@", Bytes($"Invoke-BashComm '-z' '{z3}' '{z2}'"));
    }
}
