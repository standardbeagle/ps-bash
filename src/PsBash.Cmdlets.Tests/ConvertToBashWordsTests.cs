using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Word splitting of an unquoted expansion (bash "Word Splitting"): the pure splitter, the
/// <c>ConvertTo-BashWords</c> cmdlet the transpiler emits for <c>for f in $(cmd)</c> / <c>cmd $(cmd)</c>,
/// and the <c>Invoke-BashRedirect</c> / <c>Invoke-BashEnv</c> behaviours the same change relies on.
/// Expected values are bash 5.2 (oracle-checked; end to end in
/// <c>ExpansionRedirectEnvDifferentialTests</c>).
/// </summary>
public class ConvertToBashWordsTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ConvertToBashWordsTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    // ───────────── pure splitter ─────────────

    [Theory]
    [InlineData("a b c", null, "a|b|c")]
    [InlineData("  a   b  ", null, "a|b")]
    [InlineData("a\nb\tc", null, "a|b|c")]
    [InlineData("", null, "")]
    [InlineData("   ", null, "")]
    [InlineData("a:b c", ":", "a|b c")]
    [InlineData("a::b:", ":", "a||b")]
    [InlineData(":a", ":", "|a")]
    [InlineData(" a : b ", " :", "a|b")]
    [InlineData("a b", "", "a b")]
    [InlineData("a, b", ", ", "a|b")]
    public void Split_FollowsBashIfsRules(string text, string? ifs, string expectedJoined)
    {
        var words = BashWordSplitter.Split(text, ifs);

        Assert.Equal(expectedJoined, string.Join("|", words));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("a?c", true)]
    [InlineData("[ab]", true)]
    [InlineData("plain", false)]
    public void HasGlobChars_DetectsPatternCharacters(string word, bool expected) =>
        Assert.Equal(expected, BashWordSplitter.HasGlobChars(word));

    [Theory]
    [InlineData("*", @"C:\work\x", @"C:\work", "x")]
    [InlineData("d/*", @"C:\work\d\y", @"C:\work", "d/y")]
    [InlineData("/c/*", @"C:\other\z", @"C:\work", @"C:\other\z")]
    [InlineData("C:\\*", @"C:\other\z", @"C:\work", @"C:\other\z")]
    public void RelativizeMatch_RelativePatternGivesRelativePath(string pattern, string match, string cwd, string expected)
    {
        if (!OperatingSystem.IsWindows()) return; // drive-letter forms only exist on Windows
        Assert.Equal(expected, BashWordSplitter.RelativizeMatch(pattern, match, cwd));
    }

    // ───────────── cmdlet ─────────────

    [Fact]
    public void Cmdlet_SplitsOnWhitespaceByDefault()
    {
        var r = Run("@(ConvertTo-BashWords 'a b  c').Count; ConvertTo-BashWords 'a b  c'").AssertSuccess();

        Assert.Equal(new[] { "3", "a", "b", "c" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_EmptyOrBlankTextYieldsNoWord()
    {
        var r = Run("@(ConvertTo-BashWords '').Count; @(ConvertTo-BashWords '   ').Count; @(ConvertTo-BashWords $null).Count")
            .AssertSuccess();

        Assert.Equal(new[] { "0", "0", "0" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_HonoursIfsFromTheBashVariableStore()
    {
        var r = Run("$env:IFS = ':'; try { ConvertTo-BashWords 'a:b c' } finally { $env:IFS = $null }").AssertSuccess();

        Assert.Equal(new[] { "a", "b c" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_GlobWordWithoutMatchStaysLiteral()
    {
        var r = Run("ConvertTo-BashWords 'no_such_prefix_*'").AssertSuccess();

        Assert.Equal(new[] { "no_such_prefix_*" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_GlobWordExpandsToRelativeMatches()
    {
        var previousCwd = Environment.CurrentDirectory;
        var dir = Path.Combine(Path.GetTempPath(), "psb-w-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x"), "1");
            File.WriteAllText(Path.Combine(dir, "y"), "2");
            var r = Run($"Push-Location '{dir}'; [Environment]::CurrentDirectory = '{dir}'; try {{ ConvertTo-BashWords '*' }} finally {{ Pop-Location }}")
                .AssertSuccess();

            Assert.Equal(new[] { "x", "y" }, r.Lines);
        }
        finally { Environment.CurrentDirectory = previousCwd; Directory.Delete(dir, true); }
    }

    // ───────────── Invoke-BashRedirect: a target that cannot be opened ─────────────

    [Fact]
    public void Redirect_MissingDirectory_FailsWithBashMessageAndStatus1()
    {
        var r = CmdResultRunTolerant("@() | Invoke-BashRedirect -Path 'nodir_psb/f'");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("bash: nodir_psb/f: No such file or directory", r.Error);
    }

    [Fact]
    public void Redirect_NoRecords_StillCreatesAndTruncatesTarget()
    {
        var file = Path.Combine(Path.GetTempPath(), "psb-r-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(file, "old content");
        try
        {
            Run($"@() | Invoke-BashRedirect -Path '{file}'").AssertSuccess();
            Assert.Equal("", File.ReadAllText(file));

            Run($"@() | Invoke-BashRedirect -Path '{file}.new' -Append").AssertSuccess();
            Assert.True(File.Exists(file + ".new"));
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".new");
        }
    }

    // Runs a script whose pipeline is expected to end in a terminating error; returns exit status + message.
    private (int ExitCode, string Error) CmdResultRunTolerant(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0; try { " + script + " } catch { $_.Exception.Message }; $global:LASTEXITCODE");
        var output = pwsh.Invoke().Select(o => o?.ToString() ?? "").ToList();
        pwsh.Commands.Clear();
        return (int.Parse(output[^1]), string.Join("\n", output.Take(output.Count - 1)));
    }
}
