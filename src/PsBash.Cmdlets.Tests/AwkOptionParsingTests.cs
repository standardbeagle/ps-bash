using System.Collections.ObjectModel;
using System.Management.Automation;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk option parsing as the transpiler delivers it. <c>awk -v a=1 -v b=2 'BEGIN{print a+b}'</c>
/// crashed ("parameter 'V' is specified more than once": the declared <c>string[] V</c> cannot
/// be repeated on the binder's command line). awk is now on <c>PsEmitter.OrderedArgCommands</c>,
/// so every flag arrives single-quoted in <c>Arguments</c> and the cmdlet's scan handles
/// <c>-v</c>/<c>-F</c>/<c>-f</c>, ending option parsing at the program text (POSIX).
/// Oracle: GNU Awk 5.2.1 via <c>wsl.exe -d Ubuntu-24.04 -- awk …</c>.
/// </summary>
public class AwkOptionParsingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public AwkOptionParsingTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string LastErrors = "";

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        Collection<PSObject> result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        LastErrors = string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString()));
        Assert.True(pwsh.Streams.Error.Count == 0 && !pwsh.HadErrors, $"errors: {script} => {LastErrors}");
        return result
            .Where(o => o is not null)
            .Select(o => (o.Properties["BashText"]?.Value?.ToString() ?? o.ToString()).TrimEnd('\n'))
            .ToArray();
    }

    private string[] Bash(string bash) => Run(PsEmitter.Transpile(bash)!);

    private static string TempFile(string content)
    {
        var f = Path.Combine(Path.GetTempPath(), $"awk{Guid.NewGuid():N}.txt").Replace('\\', '/');
        File.WriteAllText(f, content);
        return f;
    }

    [Fact]
    public void RepeatedDashV_BothAssignmentsApply() =>
        Assert.Equal(new[] { "3" }, Bash("awk -v a=1 -v b=2 'BEGIN{print a+b}'"));

    [Fact]
    public void JoinedDashV_Applies() =>
        Assert.Equal(new[] { "1" }, Bash("awk -va=1 'BEGIN{print a}'"));

    [Fact]
    public void FieldSeparator_And_DashV_WithFile()
    {
        var f = TempFile("a:b\nc:d\n");
        try { Assert.Equal(new[] { "ay", "cy" }, Bash($"awk -F: -v x=y '{{print $1 x}}' '{f}'")); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void LastFieldSeparatorWins()
    {
        var f = TempFile("a:b\nc:d\n");
        try { Assert.Equal(new[] { "a:bZ", "c:dZ" }, Bash($"awk -F: -vx=Z -F, '{{print $1 x}}' '{f}'")); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void DoubleDash_EndsOptions()
    {
        var f = TempFile("a:b\nc:d\n");
        try { Assert.Equal(new[] { "a:b", "c:d" }, Bash($"awk -- '{{print $1}}' '{f}'")); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void FlagsAfterProgramText_AreOperandsNotOptions()
    {
        // GNU: `awk '{print}' f -v` prints f, then warns that file `-v` cannot be opened
        // (exit 2) — a flag after the program is an operand, never an option.
        var f = TempFile("a:b\n");
        try
        {
            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript(PsEmitter.Transpile($"awk '{{print}}' '{f}' -v")!).Invoke();
            pwsh.Commands.Clear();
            var errors = string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString()));
            var text = result.Where(o => o is not null)
                .Select(o => (o.Properties["BashText"]?.Value?.ToString() ?? o.ToString()).TrimEnd('\n'))
                .ToArray();
            Assert.True(text.SequenceEqual(new[] { "a:b" }), $"text=[{string.Join("|", text)}] errors=[{errors}] n={result.Count}");
            Assert.Contains("-v", errors);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void DashV_ValueEscapesAreProcessed() =>
        Assert.Equal(new[] { "a\tb" }, Bash("awk -v 'x=a\\tb' 'BEGIN{print x}'"));

    [Fact]
    public void DirectCall_SingleDashV_StillBindsDeclaredParameter() =>
        // Pester / interactive PowerShell call the cmdlet directly: bare -v is the declared V.
        Assert.Equal(new[] { "1" }, Run("Invoke-BashAwk -v a=1 'BEGIN{print a}'"));

    [Fact]
    public void DirectCall_QuotedFlags_TakeTheArgumentsPath() =>
        Assert.Equal(new[] { "3" }, Run("Invoke-BashAwk '-v' 'a=1' '-v' 'b=2' 'BEGIN{print a+b}'"));
}
