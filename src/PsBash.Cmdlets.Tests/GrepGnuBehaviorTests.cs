using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end grep behavior in the shape the transpiler emits (every dash word single-quoted, which
/// lands verbatim in Arguments) plus DIRECT calls (the Pester shape, through the psm1 literal-args
/// proxy). Oracle: GNU grep 3.11 (`wsl bash`); tests marked FIX assert output that used to be wrong.
/// Fixture file g.txt = a1 b2 c3 d4 e5 f6 g7 (one per line).
/// </summary>
public class GrepGnuBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;
    private readonly string _g;

    public GrepGnuBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-grepg-" + Guid.NewGuid().ToString("N").Substring(0, 12));
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

    [Fact]
    public void ContextShorthand_NumInAnyBundlePosition()   // FIX (-1 was an unknown option)
    {
        Assert.Equal(new[] { "b2", "c3", "d4" }, Run($"Invoke-BashGrep '-1' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "2-b2", "3:c3", "4-d4" }, Run($"Invoke-BashGrep '-1n' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "2-b2", "3:c3", "4-d4" }, Run($"Invoke-BashGrep '-n1' c3 {_g}").AssertSuccess().Lines);
        // A later -NUM element replaces the earlier one (GNU: -12 -3 is context 3, not 123).
        Assert.Equal(new[] { "a1", "b2", "c3", "d4", "e5", "f6" }, Run($"Invoke-BashGrep '-12' '-3' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void GroupSeparator_DividesNonAdjacentGroups_AndFiles()   // FIX (no `--`, context lines used ':')
    {
        Assert.Equal(new[] { "b2", "c3", "--", "f6", "g7" },
            Run($"Invoke-BashGrep '-A1' '-e' b2 '-e' f6 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "2:b2", "3-c3", "--", "6:f6", "7-g7" },
            Run($"Invoke-BashGrep '-nA1' '-e' b2 '-e' f6 {_g}").AssertSuccess().Lines);

        var h = F("h.txt", "c3\nz\nz\nc3\n");
        var gp = Path.Combine(_dir, "g.txt"); var hp = Path.Combine(_dir, "h.txt");
        Assert.Equal(new[] { $"{gp}:c3", $"{gp}-d4", "--", $"{hp}:c3", $"{hp}-z", "--", $"{hp}:c3" },
            Run($"Invoke-BashGrep '-A1' c3 {_g} {h}").AssertSuccess().Lines);
        // -o prints only the matches (no context lines); -c ignores context.
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-C1' '-o' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "1" }, Run($"Invoke-BashGrep '-A1' '-c' c3 {_g}").AssertSuccess().Lines);
        // pipeline input: same separators, "(standard input)" as the -H label
        Assert.Equal(new[] { "(standard input):c3", "(standard input)-d4" },
            Run("'a1','b2','c3','d4','e5' | Invoke-BashGrep '-H' '-A1' c3").AssertSuccess().Lines);
        // GNU prints the separator whenever any context option was given, even -A0.
        Assert.Equal(new[] { "b2", "--", "d4" },
            Run("'b2','x','d4' | Invoke-BashGrep '-A0' '-e' b2 '-e' d4").AssertSuccess().Lines);
    }

    [Fact]
    public void ExplicitAfterBefore_BeatContext_WhateverTheOrder()
    {
        Assert.Equal(new[] { "c3", "d4" }, Run($"Invoke-BashGrep '-C0' '-A1' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3" }, Run($"Invoke-BashGrep '-B1' '-C0' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void BadContextAndMaxCount_AreUsageErrors()   // FIX (the value was silently eaten)
    {
        Run($"Invoke-BashGrep '-A' x c {_g}").AssertFailed(2, "grep: x: invalid context length argument");
        Run($"Invoke-BashGrep '-m' x c {_g}").AssertFailed(2, "grep: invalid max count");
        Run($"Invoke-BashGrep '-A'").AssertFailed(2, "option requires an argument -- 'A'");
    }

    [Fact]
    public void NegativeMaxCount_MeansUnlimited()   // FIX (printed nothing)
    {
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-m' '-1' c {_g}").AssertSuccess().Lines);
        Run($"Invoke-BashGrep '-m0' c {_g}").AssertFailed(1);
    }

    [Fact]
    public void ConflictingMatchers_AreAnError()   // FIX (the last one used to win silently)
    {
        Run($"Invoke-BashGrep '-E' '-F' c {_g}").AssertFailed(2, "grep: conflicting matchers specified");
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-E' '-E' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void RepeatedE_AndBundledE_AllReachTheCmdlet()
    {
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashGrep '-e' c '-e' e {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-ie' C {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-ec3' {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a1", "b2", "d4", "e5", "f6", "g7" }, Run($"Invoke-BashGrep '-ve' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void PatternFileBundle_WithAnEInItsName_IsNotSplit()   // FIX (the proxy split `-fpe.txt` at the e)
    {
        var p = F("pe.txt", "c3\n");
        var name = Path.Combine(_dir, "pe.txt");
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-f{name.Replace("'", "''")}' {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-f' {p} {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void MissingPatternFile_IsFatalExit2()   // FIX (reported, then searched anyway)
    {
        Run($"Invoke-BashGrep '-f' '{_dir.Replace("'", "''")}/nonexistent' {_g}")
            .AssertFailed(2, "No such file or directory");
    }

    [Fact]
    public void EmptyPatternFile_MatchesNothing_ExitOne()
    {
        var e = F("empty.txt", "");
        Run($"Invoke-BashGrep '-f' {e} {_g}").AssertFailed(1);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-e' c3 '-f' {e} {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void DoubleDash_EndsOptions_PatternMayStartWithDash()   // FIX (the proxy treated `-e` after -- as an option)
    {
        var d = F("dash.txt", "-e\nx\n");
        Assert.Equal(new[] { "-e" }, Run($"Invoke-BashGrep '--' '-e' {d}").AssertSuccess().Lines);
        Run($"Invoke-BashGrep '--' '-x' {_g}").AssertFailed(1);
    }

    [Fact]
    public void Color_AndLineBuffered_AreAccepted()
    {
        // --colour=always colours (GNU default SGR); auto / bare --colo stay plain off a terminal.
        Assert.Equal(new[] { "\u001b[01;31m\u001b[Kc3\u001b[m\u001b[K" }, Run($"Invoke-BashGrep '--colour=always' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '--colo' c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '--line-buffered' c3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void ObsoleteY_IsIgnoreCase()
    {
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep '-y' C3 {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void LastOfLowerL_UpperL_Wins_AndHBeatsH_ByPosition()
    {
        Assert.Contains("g.txt", Run($"Invoke-BashGrep '-L' '-l' c3 {_g}").AssertSuccess().Stdout);
        var two = F("two.txt", "c3\n");
        Assert.Contains(":c3", Run($"Invoke-BashGrep '-h' '-H' c3 {_g}").AssertSuccess().Stdout);
        Assert.Equal(new[] { "c3", "c3" }, Run($"Invoke-BashGrep '-H' '-h' c3 {_g} {two}").AssertSuccess().Lines);
    }

    [Fact]
    public void UsageErrors_ExitTwo()
    {
        Run($"Invoke-BashGrep '-Q' x {_g}").AssertFailed(2, "grep: invalid option -- 'Q'");
        Run($"Invoke-BashGrep '--bogus' x {_g}").AssertFailed(2, "grep: unrecognized option '--bogus'");
        Run($"Invoke-BashGrep '--in' x {_g}").AssertFailed(2, "ambiguous");
        Run("Invoke-BashGrep").AssertFailed(2, "usage");
    }

    // ---- direct PowerShell calls: bare flags through the proxy, in original order ----

    [Fact]
    public void Direct_RepeatedE_Bundles_AndCommaList()
    {
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashGrep -e c -e e {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashGrep -e c,e {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep -ie C3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a1", "b2" }, Run($"Invoke-BashGrep -ve 'c|d|e|f|g' -E {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void Direct_BareUpperE_IsExtendedRegex_NotAPatternValue()
    {
        Assert.Equal(new[] { "c3", "e5" }, Run($"Invoke-BashGrep -E 'c3|e5' {_g}").AssertSuccess().Lines);
        // -e must NOT silently switch the regex dialect (the old raw-line scan did): BRE `c+` is literal.
        Run($"Invoke-BashGrep -e 'c+' {_g}").AssertFailed(1);
        Assert.Equal(new[] { "c3" }, Run($"Invoke-BashGrep -E 'c+' {_g}").AssertSuccess().Lines);
    }

    [Fact]
    public void Direct_ContextAndCounts()
    {
        Assert.Equal(new[] { "b2", "c3", "d4" }, Run($"Invoke-BashGrep -A 1 -B 1 c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "2-b2", "3:c3" }, Run($"Invoke-BashGrep -n -B1 c3 {_g}").AssertSuccess().Lines);
        Assert.Equal(new[] { "1" }, Run($"Invoke-BashGrep -c c3 {_g}").AssertSuccess().Lines);
    }
}
