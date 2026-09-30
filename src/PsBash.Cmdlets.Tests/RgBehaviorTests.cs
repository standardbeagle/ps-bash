using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// rg's internal engine in the shape the transpiler emits (every dash word single-quoted) plus DIRECT
/// calls (Pester shape). Oracle: ripgrep 14.1.0 (`wsl rg`): exit 0 match / 1 none / 2 error, -e
/// patterns, repeatable/negated -g, last-wins case flags. `-N` keeps the assertions independent of
/// this engine's default line-number prefix. Runs with the native passthrough OFF (PSBASH_RG_NATIVE unset).
/// </summary>
[Collection("PsBashSearchEnv")]
public class RgBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;
    private readonly string _g;

    public RgBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-rgb-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dir);
        _g = F("g.txt", "a1\nb2\nc3\nd4\ne5\nf6\ng7\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return "'" + p.Replace("'", "''") + "'";
    }

    private string Dir => "'" + _dir.Replace("'", "''") + "'";

    [Fact]
    public void ExitStatus_IsRipgreps_ZeroMatchOneNoMatchTwoError()   // FIX (always 0)
    {
        Run($"Invoke-BashRg '-N' c3 {_g}").AssertSuccess();
        Run($"Invoke-BashRg '-N' nomatch {_g}").AssertFailed(1);
        Run("'a','b' | Invoke-BashRg nomatch").AssertFailed(1);
        Run("'a','b' | Invoke-BashRg a").AssertSuccess();
        var missing = "'" + Path.Combine(_dir, "nofile").Replace("'", "''") + "'";
        Run($"Invoke-BashRg c3 {missing}").AssertFailed(2, "No such file or directory");
        Run($"Invoke-BashRg c3 {missing} {_g}").AssertFailed(2);   // an unreadable path is an error even when another matched
    }

    [Fact]
    public void UsageErrors_ExitTwo()
    {
        Run($"Invoke-BashRg '--bogus' c3 {_g}").AssertFailed(2, "unrecognized option '--bogus'");
        Run($"Invoke-BashRg '-Q' c3 {_g}").AssertFailed(2, "invalid option -- 'Q'");
        Run($"Invoke-BashRg '--ign' c3 {_g}").AssertFailed(2, "unrecognized option");   // no abbreviation
        Run($"Invoke-BashRg '-A' x c3 {_g}").AssertFailed(2, "error parsing flag -A");
        Run($"Invoke-BashRg '--color' bogus c3 {_g}").AssertFailed(2, "choice 'bogus' is unrecognized");
        Run("Invoke-BashRg").AssertFailed(2, "requires at least one pattern");
        Run($"Invoke-BashRg '-t' rust c3 {_g}").AssertFailed(2, "not supported");
        Run($"Invoke-BashRg '-m1' c3 {_g}").AssertFailed(2, "not supported");
    }

    [Fact]
    public void RepeatedE_IsOr_AndMakesEveryOperandAPath()   // FIX (-e was not an option)
    {
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashRg '-N' '-e' c3 '-e' e5 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '-ec3' {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashRg '-N' '-e' c3 '--regexp=e5' {_g}").AssertSuccess().Lines);
        var dash = F("dash.txt", "-x\ny\n");
        Assert.Equal(new[] { "-x" }, Run($"Invoke-BashRg '-N' '-e' '-x' {dash}").AssertSuccess().Lines);
        Assert.Equal(new[] { "-x" }, Run($"Invoke-BashRg '-N' '--' '-x' {dash}").AssertSuccess().Lines);
    }

    [Fact]
    public void CaseFlags_LastOfISAndSmartCaseWins()
    {
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '-i' C3 {_g}").AssertSuccess().Lines);
        Run($"Invoke-BashRg '-N' '-S' C3 {_g}").AssertFailed(1);                       // has uppercase: sensitive
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '-S' c3 {_g}").AssertSuccess().Lines);
        Run($"Invoke-BashRg '-N' '-i' '-s' C3 {_g}").AssertFailed(1);                  // -s last
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '-s' '-i' C3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '-wx' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void Context_ABeatC_InAnyOrder()
    {
        Assert.Equal(new[] { "a1", "b2", "c3", "d4" }, Run($"Invoke-BashRg '-N' '-A1' '-C3' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a1", "b2", "c3", "d4" }, Run($"Invoke-BashRg '-N' '-C3' '-A1' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3", "d4" }, Run($"Invoke-BashRg '-N' '-C1' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void Globs_AreRepeatable_AndNegatable()   // FIX (only one -g, last wins; `!` not understood)
    {
        F("h.rs", "x3\n"); F("i.md", "m3\n");
        string Names(string script) =>
            string.Join("|", Run(script).AssertSuccess().Lines.Select(l => Path.GetFileName(l.Substring(0, l.LastIndexOf(':')).Replace('\\', '/'))).OrderBy(x => x));
        Assert.Equal("h.rs", Names($"Invoke-BashRg '-N' '-g' '*.rs' 3 {Dir}"));
        Assert.Equal("g.txt|h.rs", Names($"Invoke-BashRg '-N' '-g' '*.rs' '-g*.txt' 3 {Dir}"));
        Assert.Equal("g.txt|i.md", Names($"Invoke-BashRg '-N' '-g' '!*.rs' 3 {Dir}"));
        Assert.Equal("h.rs", Names($"Invoke-BashRg '-N' '--glob=*.rs' 3 {Dir}"));
    }

    [Fact]
    public void CountedU_AndHidden_ControlTheDefaultFilters()
    {
        if (Environment.GetEnvironmentVariable("PSBASH_RG_NATIVE") is { Length: > 0 }) return;   // native rg governs filtering
        F(".hid.txt", "c3 hid\n");
        Assert.DoesNotContain(Run($"Invoke-BashRg '-N' 3 {Dir}").Lines, l => l.Contains("hid"));
        Assert.Contains(Run($"Invoke-BashRg '-N' '--hidden' 3 {Dir}").Lines, l => l.Contains("hid"));
        Assert.Contains(Run($"Invoke-BashRg '-N' '-uu' 3 {Dir}").Lines, l => l.Contains("hid"));
        Assert.DoesNotContain(Run($"Invoke-BashRg '-N' '-u' 3 {Dir}").Lines, l => l.Contains("hid"));   // -u = --no-ignore only
    }

    [Fact]
    public void AcceptedNoOps_AndColor_DoNotChangeTheOutput()
    {
        foreach (var f in new[] { "--no-heading", "--no-messages", "--no-config", "--mmap", "--no-mmap" })
            Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '{f}' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '--color' never c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg '-N' '--color=always' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void Help_AndVersion_ViaShortAndLongSpellings()
    {
        Assert.NotEmpty(Run("Invoke-BashRg '-h'").Lines);
        Assert.NotEmpty(Run("Invoke-BashRg '--help'").Lines);
        Assert.NotEmpty(Run("Invoke-BashRg '-V'").Lines);
    }

    // ---- direct PowerShell calls ----

    [Fact]
    public void Direct_BareFlags_ReachTheParser()
    {
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg -N -i C3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3", "d4" }, Run($"Invoke-BashRg -N -A 1 -B 1 c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashRg -N -e c3 {_g}").AssertSuccess().Lines);   // E decoy: one direct -e
    }
}
