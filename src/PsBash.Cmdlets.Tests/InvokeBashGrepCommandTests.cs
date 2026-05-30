using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashGrep</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashGrepCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashGrep</c> function — search pipeline / file
/// input for a pattern matching GNU coreutils <c>grep</c>.
///
/// Failure-surface axes covered (per Directive 3): empty pipeline, regex /
/// literal / fixed match, ignore-case, invert, line-numbers, count, files-with-
/// matches, recursive, context (-A/-B/-C), word-regexp, multi-pattern,
/// pipeline + file dual mode, multi-file file:line:match format, CRLF, missing
/// target (axis 14), alias, <c>--help</c>, and an injection probe per
/// Directive 12.
/// </summary>
public class InvokeBashGrepCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashGrepCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-grep-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private string MakeFile(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void Grep_LiteralMatch_FromPipeline()
    {
        var lines = RunLines("'apple','banana','cherry' | Invoke-BashGrep banana");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Fact]
    public void Grep_RegexMatch_FromPipeline()
    {
        // Basic regex — `.` matches any char.
        var lines = RunLines("'foo','f1o','f.o' | Invoke-BashGrep 'f.o'");
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Grep_IgnoreCase_I_Flag()
    {
        var lines = RunLines("'Apple','APPLE','banana' | Invoke-BashGrep -i apple");
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Grep_InvertMatch_V_Flag()
    {
        var lines = RunLines("'apple','banana','cherry' | Invoke-BashGrep -v banana");
        Assert.Equal(new[] { "apple", "cherry" }, lines);
    }

    [Fact]
    public void Grep_LineNumbers_N_Flag_FileMode()
    {
        var file = MakeFile("nums.txt", "apple\nbanana\ncherry\n");
        var lines = RunLines($"Invoke-BashGrep -n banana '{Q(file)}'");
        Assert.Single(lines);
        Assert.Contains("2:banana", lines[0]);
    }

    [Fact]
    public void Grep_CountOnly_C_Flag()
    {
        var lines = RunLines("'a','b','a','c','a' | Invoke-BashGrep -c a");
        Assert.Single(lines);
        Assert.Equal("3", lines[0]);
    }

    [Fact]
    public void Grep_FilesOnly_L_Flag()
    {
        var file = MakeFile("a.txt", "apple\nfoo\n");
        var file2 = MakeFile("b.txt", "no-match\n");
        var lines = RunLines($"Invoke-BashGrep -l apple '{Q(file)}' '{Q(file2)}'");
        Assert.Single(lines);
        Assert.Contains("a.txt", lines[0]);
    }

    [Fact]
    public void Grep_ContextA_Flag()
    {
        var file = MakeFile("ctx.txt", "1\n2\nmatch\n4\n5\n");
        var lines = RunLines($"Invoke-BashGrep -A 1 match '{Q(file)}'");
        // -A 1: emit match + 1 line after.
        Assert.Equal(2, lines.Length);
        Assert.Contains("match", lines[0]);
        Assert.Contains("4", lines[1]);
    }

    [Fact]
    public void Grep_ContextB_Flag()
    {
        var file = MakeFile("ctxb.txt", "1\n2\nmatch\n4\n");
        var lines = RunLines($"Invoke-BashGrep -B 1 match '{Q(file)}'");
        // -B 1: emit 1 line before + match.
        Assert.Equal(2, lines.Length);
        Assert.Contains("2", lines[0]);
        Assert.Contains("match", lines[1]);
    }

    [Fact]
    public void Grep_ContextC_FromFile_EmitsBeforeAndAfter()
    {
        // -C N (separated) prefix-collides with the cmdlet's own -c switch
        // (case-insensitive binder). The joined -CN form is recovered from
        // Arguments by the manual scan; we use -A 1 -B 1 here as an
        // equivalent expression (oracle: same context window).
        var file = MakeFile("ctxc.txt", "1\n2\nmatch\n4\n5\n");
        var lines = RunLines($"Invoke-BashGrep -A 1 -B 1 match '{Q(file)}'");
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Grep_ExtendedRegex_LongForm()
    {
        // -E PATTERN prefix-collides with the cmdlet's value-bearing -e
        // parameter (case-insensitive binder) — that's a residual parity gap
        // documented on the cmdlet (same shape as sed's `-e A -e B`). Bundled
        // short forms like `-En` still work because they land in Arguments.
        // The --extended-regexp long form has no collision and is the
        // recommended invocation.
        var lines = RunLines("'foo','bar','baz' | Invoke-BashGrep --extended-regexp 'foo|baz'");
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Grep_FixedString_F_Flag()
    {
        // -F: . is literal, no regex.
        var lines = RunLines("'a.b','aXb' | Invoke-BashGrep -F 'a.b'");
        Assert.Single(lines);
        Assert.Equal("a.b", lines[0]);
    }

    [Fact]
    public void Grep_WordRegexp_W_Flag()
    {
        // -w: word boundaries required.
        var lines = RunLines("'cat','catastrophe','wildcat' | Invoke-BashGrep -w cat");
        Assert.Single(lines);
        Assert.Equal("cat", lines[0]);
    }

    [Fact]
    public void Grep_MultiPattern_E_Array()
    {
        // -e accepts an array of patterns (string[] under the cmdlet binder).
        // Repeated `-e P1 -e P2` is rejected by the PowerShell binder as
        // "specified more than once" — same known limitation as sed's
        // repeated -e. Pass an array literal as the binder-friendly form.
        var lines = RunLines("'apple','banana','cherry' | Invoke-BashGrep -e apple,cherry");
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Grep_FileMode_MultipleFiles_PrefixesFileName()
    {
        var f1 = MakeFile("a.txt", "apple\n");
        var f2 = MakeFile("b.txt", "apple\n");
        var lines = RunLines($"Invoke-BashGrep apple '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Contains("apple", l));
        // Multi-file mode should prefix with filename.
        Assert.Contains(lines, l => l.Contains("a.txt"));
        Assert.Contains(lines, l => l.Contains("b.txt"));
    }

    [Fact]
    public void Grep_FileMode_SingleFile_LineNumberFormat()
    {
        var file = MakeFile("fmt.txt", "alpha\nbeta\ngamma\n");
        var lines = RunLines($"Invoke-BashGrep -n beta '{Q(file)}'");
        Assert.Single(lines);
        Assert.Equal("2:beta", lines[0]);
    }

    [Fact]
    public void Grep_Recursive_R_Flag()
    {
        var sub = Path.Combine(_tmpDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_tmpDir, "top.txt"), "match\n");
        File.WriteAllText(Path.Combine(sub, "deep.txt"), "match\n");
        File.WriteAllText(Path.Combine(sub, "nope.txt"), "other\n");
        var lines = RunLines($"Invoke-BashGrep -r match '{Q(_tmpDir)}'");
        // Two files contain "match".
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Grep_FileMode_CrlfNormalized()
    {
        var file = MakeFile("crlf.txt", "apple\r\nbanana\r\ncherry\r\n");
        var lines = RunLines($"Invoke-BashGrep banana '{Q(file)}'");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Fact]
    public void Grep_FileMode_MissingFile_EmitsError_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Q(Path.Combine(_tmpDir, "nope.txt"));
        var result = pwsh.AddScript(
            $"Invoke-BashGrep foo '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Grep_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashGrep --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("grep", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grep_AliasResolution_GrepWorks()
    {
        // The psm1 module registers `Set-Alias grep Invoke-BashGrep`.
        var lines = RunLines("'apple','banana' | grep banana");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Fact]
    public void Grep_ColorAuto_FlagAcceptedAndIgnored_NotTreatedAsFile()
    {
        // Regression: the near-universal `alias grep='grep --color=auto'`
        // expands every grep to `grep --color=auto ...`. GNU grep accepts
        // --color[=WHEN] silently; ps-bash used to treat `--color=auto` as a
        // file operand and emit `grep: --color=auto: No such file or directory`
        // on every invocation. The flag must be swallowed, leaving the match
        // unaffected.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "'apple','banana','cherry' | Invoke-BashGrep --color=auto banana 2>$null").Invoke();
        var errs = pwsh.Streams.Error.Count;
        pwsh.Commands.Clear();
        Assert.Equal(0, errs);
        var lines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    [Theory]
    [InlineData("--color")]
    [InlineData("--color=always")]
    [InlineData("--color=never")]
    [InlineData("--colour")]
    [InlineData("--colour=auto")]
    public void Grep_ColorFlagVariants_Swallowed(string colorFlag)
    {
        var lines = RunLines(
            $"'apple','banana' | Invoke-BashGrep {colorFlag} banana");
        Assert.Single(lines);
        Assert.Equal("banana", lines[0]);
    }

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    [Fact]
    public void Grep_ValidButUnsupportedFlag_X_EmitsSpecificRefusal_NotFileError()
    {
        // -x (line-regexp) is a real grep flag ps-bash doesn't implement.
        // It must say so specifically — NOT "No such file or directory", and
        // NOT silently ignore the flag and run anyway. (-x is used rather than
        // -P because -P collides with the -PipelineVariable/-ProgressAction
        // common parameters and is rejected by the binder before the cmdlet
        // runs — a separate known collision class.)
        var (outLines, errs) = RunWithErrors("'apple','banana' | Invoke-BashGrep -x 'ba.*'");
        Assert.Empty(outLines);
        Assert.Contains(errs,
            m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("-x", StringComparison.Ordinal));
        Assert.DoesNotContain(errs,
            m => m.Contains("No such file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grep_UnsupportedFlagInBundle_ReportsFirstOffender()
    {
        // -i is honored, then -P (unsupported) is the offending char getopt
        // would stop on.
        var (_, errs) = RunWithErrors("'x' | Invoke-BashGrep -iP foo");
        Assert.Contains(errs, m => m.Contains("-P", StringComparison.Ordinal)
                                   && m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grep_UnrecognizedLongOption_BashParityMessage()
    {
        // --bogus is not a real grep option → bash-style "unrecognized option".
        var (_, errs) = RunWithErrors("'x' | Invoke-BashGrep --bogus foo");
        Assert.Contains(errs, m => m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("--bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void Grep_InvalidShortOption_BashParityMessage()
    {
        // -j is not a real grep option → bash-style "invalid option -- 'j'".
        var (_, errs) = RunWithErrors("'x' | Invoke-BashGrep -j foo");
        Assert.Contains(errs, m => m.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("'j'", StringComparison.Ordinal));
    }

    [Fact]
    public void Grep_LongUnsupportedWithValue_StripsEqValueForLookup()
    {
        // --include=*.c → valid grep flag, unsupported; the =VALUE suffix must
        // not defeat the catalog lookup.
        var (_, errs) = RunWithErrors("'x' | Invoke-BashGrep --include=*.c foo");
        Assert.Contains(errs, m => m.Contains("--include", StringComparison.Ordinal)
                                   && m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grep_InjectionProbe_PatternWithDollarParen_LiteralRegex()
    {
        // Directive 12: a user-controlled pattern containing PowerShell
        // injection chars must not be re-parsed as script. The pattern is
        // fed only to System.Text.RegularExpressions.Regex, which treats it
        // as a literal regex char sequence — `$(throw 'pwn')` is a regex
        // with `$` (end-anchor), parens (group), etc., not a PowerShell
        // command substitution. No side effect should occur.
        // We pass -F to force literal matching (no regex parsing) so the
        // pattern stays a string and no regex-error fires.
        var lines = RunLines("'foo','bar' | Invoke-BashGrep -F \"`$(throw 'pwn')\" 2>$null");
        // No match → no output, no exception.
        Assert.Empty(lines);
    }
}
