using System.Collections.ObjectModel;
using System.Management.Automation;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression: the flags of the command xargs RUNS (<c>xargs -0 basename -a</c>) must reach
/// xargs verbatim and untouched by the PowerShell binder. Emitted bare, the inner <c>-a</c>
/// prefix-matched <c>Invoke-BashXargs</c>'s own <c>-Arguments</c> parameter and the call died
/// with "Missing an argument for parameter 'Arguments'" (exit 127). xargs is on
/// <c>PsEmitter.OrderedArgCommands</c>, so every dash literal is single-quoted.
///
/// Each test transpiles the REAL bash text with <see cref="PsEmitter"/> and runs the emitted
/// PowerShell, so the emitter and the cmdlet are exercised together (the bug lived in the seam).
///
/// Oracle (qa-rubric Directive 1): expected outputs were taken from GNU xargs via
/// <c>wsl.exe -d Ubuntu-24.04 -- bash -c '…'</c>.
/// </summary>
public class XargsInnerCommandFlagTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public XargsInnerCommandFlagTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string LastErrors = "";

    private (string[] Lines, int Errors) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        Collection<PSObject> result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.Count;
        LastErrors = string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString()));
        var lines = new List<string>();
        foreach (var o in result)
        {
            if (o is null) continue;
            var text = o.Properties["BashText"]?.Value?.ToString() ?? o.ToString();
            lines.Add(text.TrimEnd('\n'));
        }
        return (lines.ToArray(), errors);
    }

    /// <summary>Transpile a bash pipeline and run it, asserting it emitted no error record.</summary>
    private string[] Bash(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        var (lines, errors) = Run(ps);
        Assert.True(errors == 0, $"errors while running: {ps} => {LastErrors}");
        return lines;
    }

    /// <summary>Pipe two NUL-terminated records into the transpiled xargs command.</summary>
    private string[] FeedNul(string a, string b, string xargsBash)
    {
        // Real bash text: printf's own `\0` escape supplies the NULs (it used to be built in PowerShell).
        var ps = PsEmitter.Transpile($"printf '{a}\\0{b}\\0' | {xargsBash}")!;
        var (lines, errors) = Run(ps);
        Assert.True(errors == 0, $"errors while running: {ps} => {LastErrors}");
        return lines;
    }

    // ── the reported bug and its siblings ──

    [Fact]
    public void InnerBasenameDashA_WithNullDelim_ReachesBasename()
    {
        // GNU: b / d
        Assert.Equal(new[] { "b", "d" }, FeedNul("a/b", "c/d", "xargs -0 basename -a"));
    }

    [Fact]
    public void InnerGrepDashI_IsNotSwallowedByXargsBinding()
    {
        var f = Path.Combine(Path.GetTempPath(), $"xg{Guid.NewGuid():N}.txt");
        File.WriteAllText(f, "FOO here\nbar\n");
        try
        {
            var q = f.Replace('\\', '/').Replace("'", "''"); // xargs, like GNU, treats backslash as an escape
            var ps = PsEmitter.Transpile("xargs grep -i foo")!;
            var (lines, errors) = Run($"'{q}' | {ps}");
            Assert.True(errors == 0, LastErrors);
            Assert.Contains(lines, l => l.Contains("FOO here"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void InnerEchoDashE_ExpandsEscapes()
    {
        // GNU: "a<TAB>b x" / "a<TAB>b y"
        var lines = Run("'x y' | " + PsEmitter.Transpile("xargs -n1 echo -e 'a\\tb'")!).Lines;
        Assert.Equal(new[] { "a\tb x", "a\tb y" }, lines);
    }

    [Fact]
    public void InnerCpDashV_WithReplaceMode_CopiesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xc{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "dst"));
        File.WriteAllText(Path.Combine(dir, "s.txt"), "data");
        try
        {
            var q = dir.Replace("'", "''");
            var ps = PsEmitter.Transpile("xargs -I{} cp -v {} dst/")!;
            var (_, errors) = Run(
                $"[System.Environment]::CurrentDirectory = '{q}'; Set-Location -LiteralPath '{q}'; 's.txt' | {ps}");
            Assert.True(errors == 0, LastErrors);
            Assert.True(File.Exists(Path.Combine(dir, "dst", "s.txt")));
        }
        finally { Environment.CurrentDirectory = Path.GetTempPath(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void InnerLsDashD_WithParallelFlag_RunsPerItem()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xl{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "aa"));
        Directory.CreateDirectory(Path.Combine(dir, "bb"));
        try
        {
            var q = dir.Replace("'", "''");
            var ps = PsEmitter.Transpile("xargs -P2 -n1 ls -d")!;
            var (lines, errors) = Run(
                $"[System.Environment]::CurrentDirectory = '{q}'; Set-Location -LiteralPath '{q}'; 'aa bb' | {ps}");
            Assert.True(errors == 0, LastErrors);
            Assert.Equal(new[] { "aa", "bb" }, lines.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally { Environment.CurrentDirectory = Path.GetTempPath(); Directory.Delete(dir, true); }
    }

    // ── the other command-running wrappers: time, env, find -exec ──

    [Fact]
    public void Time_InnerEchoDashE_ExpandsEscapes() =>
        Assert.Contains("a\tb", Bash("time echo -e 'a\\tb'"));

    [Fact]
    public void Env_InnerBasenameDashA_ReachesBasename() =>
        Assert.Equal(new[] { "b", "d" }, Bash("env FOO=1 basename -a a/b c/d"));

    [Fact]
    public void Env_InnerGrepDashI_IsNotEnvsIgnoreEnvironment()
    {
        // `env FOO=1 grep -i …`: the `-i` after the command is grep's. GNU stops env's option
        // parsing at the command name.
        var f = Path.Combine(Path.GetTempPath(), $"xe{Guid.NewGuid():N}.txt").Replace('\\', '/');
        File.WriteAllText(f, "FOO here\n");
        try { Assert.Contains("FOO here", Bash($"env X=1 grep -i foo '{f}'")); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void FindExec_InnerGrepDashI_IsNotSwallowedByBinder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xf{Guid.NewGuid():N}").Replace('\\', '/');
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/f.txt", "FOO here\n");
        try
        {
            Assert.Contains("FOO here", Bash($"find '{dir}' -name f.txt -exec grep -i foo {{}} \\;"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── xargs's OWN flags still work when every one arrives single-quoted ──

    [Fact]
    public void Own_I_SeparateToken() =>
        Assert.Equal(new[] { "[a]", "[b]" }, Bash("printf 'a\\nb\\n' | xargs -I {} echo '[{}]'"));

    [Fact]
    public void Own_I_Joined() =>
        Assert.Equal(new[] { "[a]", "[b]" }, Bash("printf 'a\\nb\\n' | xargs -I{} echo '[{}]'"));

    [Fact]
    public void Own_LowerI_DefaultToken() =>
        Assert.Equal(new[] { "[q]" }, Bash("echo q | xargs -i echo '[{}]'"));

    [Fact]
    public void Own_P_SeparateValue_IsNotTakenAsCommand() =>
        // GNU: a / b (two runs). `-P 2` must consume its value, not run "2" as the command.
        Assert.Equal(new[] { "a", "b" }, Bash("printf 'a b' | xargs -P 2 -n 1 echo"));

    [Fact]
    public void Own_N_SeparateValue() =>
        Assert.Equal(new[] { "a", "b" }, Bash("printf 'a b' | xargs -n 1 echo"));

    [Fact]
    public void Own_N_Joined() =>
        Assert.Equal(new[] { "a", "b" }, Bash("printf 'a b' | xargs -n1 echo"));

    [Fact]
    public void Own_ZeroDelim() =>
        Assert.Equal(new[] { "a", "b" }, FeedNul("a", "b", "xargs -0 -n1 echo"));

    [Fact]
    public void Own_R_SkipsEmptyInput() =>
        Assert.Empty(Bash("printf '' | xargs -r echo none"));

    [Fact]
    public void Own_T_TracesAndRuns() =>
        Assert.Equal(new[] { "hi a" }, Bash("echo a | xargs -t echo hi 2>/dev/null"));

    [Fact]
    public void Own_D_SeparateValue() =>
        Assert.Equal(new[] { "1", "2", "3" }, Bash("printf '1,2,3' | xargs -d , -n1 echo"));

    [Fact]
    public void Own_D_Joined() =>
        Assert.Equal(new[] { "1", "2", "3" }, Bash("printf '1,2,3' | xargs -d, -n1 echo"));

    [Fact]
    public void FlagsAfterCommand_BelongToTheCommand_NotToXargs()
    {
        // `-n` after the command name is echo's flag (no newline), not xargs's batch size.
        // GNU: "-n a" is NOT what echo prints — echo -n suppresses the newline, output "a".
        Assert.Equal(new[] { "a" }, Bash("echo a | xargs echo -n"));
    }
}
