using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// find is an expression language, not getopt: `find [-H|-L|-P] [-D opts] [-Olevel] [PATH...] [EXPR]`.
/// These run in the shape the transpiler emits now that find is on OrderedArgCommands (every dash word
/// single-quoted, so the cmdlet receives the whole expression verbatim and in order; `-o`/`-a`/the
/// argv of `-exec` no longer need force-quoting). Oracle: GNU find 4.9 (`wsl bash`); tests marked FIX
/// assert behavior that used to be wrong. Tree: a.txt, sub/b.txt, d2/c.log.
/// </summary>
public class FindGnuBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public FindGnuBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-findg-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "d2"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), "y\n");
        File.WriteAllText(Path.Combine(_dir, "d2", "c.log"), "z\n");
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(Path.GetTempPath()); Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Runs one find invocation from inside the fixture tree; Lines = the printed paths.</summary>
    private CmdResult Find(string args, bool raw = false)
    {
        string d = _dir.Replace("'", "''");
        string tail = raw ? "" : " | ForEach-Object { $_.BashText }";
        return CmdResult.Run(_fixture.AcquireFresh(),
            $"$__old = Get-Location; Set-Location '{d}'; try {{ Invoke-BashFind {args}{tail} }} finally {{ Set-Location $__old }}");
    }

    private string[] Sorted(string args) => Find(args).AssertSuccess().Lines.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void ExpressionOperators_ReachTheCmdlet_InOrder()   // the emitter used to force-quote only -o/-a
    {
        Assert.Equal(new[] { "./a.txt", "./d2/c.log" }, Sorted(". '-name' a.txt '-o' '-name' c.log"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log" }, Sorted(". '(' '-name' a.txt '-o' '-name' c.log ')' '-print'"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-name' a.txt '-a' '-type' f"));
        Assert.Equal(new[] { "./d2/c.log", "./sub/b.txt" }, Sorted(". '!' '-name' a.txt '-type' f"));
        Assert.Equal(new[] { "./d2/c.log", "./sub/b.txt" }, Sorted(". '-not' '-name' a.txt '-type' f"));
    }

    [Fact]
    public void Path_IsOptionalAndMayBeSeveral()
    {
        Assert.Equal(new[] { "./a.txt" }, Sorted("'-name' a.txt"));
        Assert.Equal(new[] { "d2/c.log", "sub/b.txt" }, Sorted("sub d2 '-type' f"));
    }

    [Fact]
    public void LeadingOptions_AreAccepted()
    {
        foreach (var opt in new[] { "'-H'", "'-P'", "'-O2'", "'-D' opt" })
            Assert.Equal(new[] { "./a.txt" }, Sorted($"{opt} . '-name' a.txt"));
    }

    [Fact]
    public void WordAfterTheExpressionBegan_IsNotASearchPath()   // FIX (it became a second root silently)
    {
        Find(". '-name' a.txt sub").AssertFailed(1, "paths must precede expression: `sub'");
    }

    [Fact]
    public void MissingArguments_AreUsageErrors()   // FIX (silently became an empty pattern)
    {
        Find(". '-name'").AssertFailed(1, "missing argument to `-name'");
        Find(". '-maxdepth'").AssertFailed(1, "missing argument to `-maxdepth'");
        Find(". '-type'").AssertFailed(1, "missing argument to `-type'");
        Find(". '-exec' echo").AssertFailed(1, "missing argument to `-exec'");
        Find(". '-exec' echo '{}'").AssertFailed(1, "missing argument to `-exec'");
    }

    [Fact]
    public void BadValues_AreUsageErrors()
    {
        Find(". '-maxdepth' x").AssertFailed(1, "Expected a positive decimal integer argument to -maxdepth, but got ‘x’");
        Find(". '-mindepth' -1").AssertFailed(1, "Expected a positive decimal integer argument to -mindepth");
        Find(". '-type' x").AssertFailed(1, "Unknown argument to -type: x");
        Find(". '-bogus'").AssertFailed(1, "unknown predicate `-bogus'");
        Find(". '-perm' 644").AssertFailed(1, "unsupported predicate");
    }

    [Fact]
    public void TypeList_IsAnyOf()
    {
        Assert.Equal(new[] { ".", "./a.txt", "./d2", "./d2/c.log", "./sub", "./sub/b.txt" }, Sorted(". '-type' 'f,d'"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f"));
    }

    [Fact]
    public void Depth_OptionsStillApply()
    {
        Assert.Equal(new[] { "./d2", "./sub" }, Sorted(". '-mindepth' 1 '-maxdepth' 1 '-type' d"));
    }

    [Fact]
    public void MissingRoot_IsReported_TheRestStillRuns_ExitOne()
    {
        var r = Find("nosuch . '-name' a.txt");
        Assert.Equal(new[] { "./a.txt" }, r.Lines);
        r.AssertFailed(1, "No such file or directory");
    }

    [Fact]
    public void Exec_ArgvFlagsBelongToTheCommand_AndTerminatorsWork()
    {
        // `-n` is echo's flag (it is in the -exec argv), not a find option or predicate.
        Assert.Contains("./a.txt", Find(". '-name' a.txt '-exec' echo '-n' '{}' ';'", raw: true).AssertSuccess().Stdout);
        var batched = Find(". '-name' '*.txt' '-exec' echo '{}' '+'", raw: true).AssertSuccess().Lines;
        Assert.Contains(batched, l => l.Contains("./a.txt") && l.Contains("./sub/b.txt"));
    }

    [Fact]
    public void Print0_StillEmitsASingleNulJoinedRecord()
    {
        var r = Find(". '-name' a.txt '-print0'").AssertSuccess();
        Assert.Contains("./a.txt", r.Stdout);
    }
}
