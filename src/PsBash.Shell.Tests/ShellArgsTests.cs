using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

public class ShellArgsTests
{
    [Fact]
    public void Parse_VerboseBeforeCompactCommand_EnablesVerboseDiagnostics()
    {
        var result = ShellArgs.Parse(["--verbose", "--compact-output", "-c", "git status"]);

        Assert.True(result.Verbose);
        Assert.True(result.CompactOutput);
        Assert.Equal("git status", result.Command);
    }
    [Fact]
    public void Parse_EmptyArgs_ReturnsDefaults()
    {
        var result = ShellArgs.Parse([]);

        Assert.Null(result.Command);
        Assert.False(result.Interactive);
        Assert.False(result.Login);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandFlag_SetsCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo hello"]);

        Assert.Equal("echo hello", result.Command);
        Assert.False(result.Interactive);
        Assert.False(result.Login);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandThenVersionFlag_RunsCommand_NotVersionBanner()
    {
        // Bash: `-c 'cmd' --version` runs cmd (--version is $0), no version print.
        var result = ShellArgs.Parse(["-c", "echo hi", "--version"]);

        Assert.Equal("echo hi", result.Command);
        Assert.False(result.ShowVersion);
        Assert.Contains("--version", result.ScriptArgs);
    }

    [Fact]
    public void Parse_CommandThenPositionals_CapturesThemAsScriptArgs()
    {
        // Bash: `-c 'cmd' name a b` → the trailing args are positionals, not dropped.
        var result = ShellArgs.Parse(["-c", "echo $1", "name", "a", "b"]);

        Assert.Equal("echo $1", result.Command);
        Assert.Equal(new[] { "name", "a", "b" }, result.ScriptArgs);
    }

    [Fact]
    public void Parse_InteractiveFlag_SetsInteractive()
    {
        var result = ShellArgs.Parse(["-i"]);

        Assert.True(result.Interactive);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_StdinFlag_SetsReadFromStdin()
    {
        var result = ShellArgs.Parse(["-s"]);

        Assert.True(result.ReadFromStdin);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_LoginLongFlag_SetsLogin()
    {
        var result = ShellArgs.Parse(["--login"]);

        Assert.True(result.Login);
    }

    [Fact]
    public void Parse_LoginShortFlag_SetsLogin()
    {
        var result = ShellArgs.Parse(["-l"]);

        Assert.True(result.Login);
    }

    [Fact]
    public void Parse_LoginWithCommand_SetsBoth()
    {
        var result = ShellArgs.Parse(["--login", "-c", "ls -la"]);

        Assert.True(result.Login);
        Assert.Equal("ls -la", result.Command);
    }

    [Fact]
    public void Parse_LoginShortWithCommand_SetsBoth()
    {
        var result = ShellArgs.Parse(["-l", "-c", "ls -la"]);

        Assert.True(result.Login);
        Assert.Equal("ls -la", result.Command);
    }

    [Fact]
    public void Parse_EndOfOptions_StopsProcessing()
    {
        var result = ShellArgs.Parse(["--", "-i", "-s"]);

        Assert.False(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_FlagsBeforeEndOfOptions_AreProcessed()
    {
        var result = ShellArgs.Parse(["-i", "--", "-s"]);

        Assert.True(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandWithEndOfOptions_CommandParsedBeforeSeparator()
    {
        var result = ShellArgs.Parse(["-c", "echo test", "--", "-i"]);

        Assert.Equal("echo test", result.Command);
        Assert.False(result.Interactive);
    }

    [Fact]
    public void Parse_CommandFlagWithoutArgument_CommandIsNull()
    {
        var result = ShellArgs.Parse(["-c"]);

        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_CommandFlagAtEnd_CommandIsNull()
    {
        var result = ShellArgs.Parse(["-i", "-c"]);

        Assert.True(result.Interactive);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_AllFlagsCombined_AllSet()
    {
        var result = ShellArgs.Parse(["-i", "-s", "--login", "-c", "whoami"]);

        Assert.True(result.Interactive);
        Assert.True(result.ReadFromStdin);
        Assert.True(result.Login);
        Assert.Equal("whoami", result.Command);
    }

    [Fact]
    public void Parse_UnknownFlags_Ignored()
    {
        var result = ShellArgs.Parse(["--verbose", "-x", "-c", "echo hi"]);

        Assert.Equal("echo hi", result.Command);
        Assert.False(result.Interactive);
    }

    [Fact]
    public void Parse_CommandWithQuotedString_PreservesCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo \"hello world\""]);

        Assert.Equal("echo \"hello world\"", result.Command);
    }

    [Fact]
    public void Parse_CommandWithRedirection_PreservesFullCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo err 2>/dev/null"]);

        Assert.Equal("echo err 2>/dev/null", result.Command);
    }

    [Fact]
    public void Parse_RecordValueEquality_Works()
    {
        var a = ShellArgs.Parse(["-c", "echo hi"]);
        var b = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Equal(a, b);
    }

    // Regression: Claude Code on Windows invokes ps-bash as `-lc "cmd"`.
    // Prior parser had no `-lc` case, dropped both flags + command, fell into
    // interactive mode. Captured live via PSBASH_TRACE.
    [Fact]
    public void Parse_BundledLoginCommand_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-lc", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_BundledCommandLogin_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-cl", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_BundledInteractiveCommand_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-ic", "echo hi"]);

        Assert.True(result.Interactive);
        Assert.Equal("echo hi", result.Command);
    }

    // Regression: Claude Code also invokes ps-bash as `-c -l "cmd"`.
    // Prior parser greedily took `-l` as the command, then dropped the
    // real command, then tried to execute `-l` as a PowerShell command,
    // producing `"The term '-l' is not recognized"`. Captured live.
    [Fact]
    public void Parse_CommandThenLogin_SkipsLoginFlagAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "-l", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_CommandThenMultipleFlags_SkipsAllAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "-l", "-i", "--noprofile", "echo hi"]);

        Assert.True(result.Login);
        Assert.True(result.Interactive);
        Assert.True(result.NoProfile);
        Assert.Equal("echo hi", result.Command);
    }

    // Real ps-bash invocation captured from Claude Code's snapshot bootstrap.
    // The command string starts with `shopt` and contains pipes, redirects,
    // quotes — must round-trip intact through the -c skip logic.
    [Fact]
    public void Parse_ClaudeCodeSnapshotPattern_PreservesFullCommand()
    {
        var cmd = "shopt -u extglob 2>/dev/null || true && eval 'git status' < /dev/null && pwd -P >| /tmp/x";
        var result = ShellArgs.Parse(["-c", "-l", cmd]);

        Assert.True(result.Login);
        Assert.Equal(cmd, result.Command);
    }

    // The CURRENT Claude Code wrapper (2026-05-28): the snapshot bootstrap now
    // injects a TEMP/TMP env-setup as a multi-var bare assignment before the
    // command. The full string — multi-var assignment, pipes, redirects, quotes,
    // force-clobber `>|` — must round-trip intact through the `-c -l` skip logic.
    [Fact]
    public void Parse_ClaudeCodeEnvSetupWrapper_PreservesFullCommand()
    {
        var cmd = "shopt -u extglob 2>/dev/null || true && "
                + "TEMP='C:\\Temp' TMP='C:\\Temp' && "
                + "eval 'git status' < /dev/null && pwd -P >| /tmp/x";
        var result = ShellArgs.Parse(["-c", "-l", cmd]);

        Assert.True(result.Login);
        Assert.Equal(cmd, result.Command);
    }

    // Regression: `ps-bash -c "git log --oneline -20"` was reported to fail
    // with "The term '-l' is not recognized" — i.e. somewhere `--oneline` was
    // being peeled apart as a short-flag collision (-o / -n / -e / -l / -i / -n / -e).
    // The Args layer must pass the full quoted command string to the
    // transpiler intact, even though it contains `--word` tokens whose first
    // character is also a recognized short flag.
    [Theory]
    [InlineData("git log --oneline -20")]
    [InlineData("git log --list")]
    [InlineData("git diff --name-only HEAD~1")]
    [InlineData("grep --include='*.cs' -r foo .")]
    [InlineData("ls --long --color=auto")]
    public void Parse_CommandWithLongFlagStartingWithShortFlagLetter_PreservesFullCommand(string cmd)
    {
        var result = ShellArgs.Parse(["-c", cmd]);

        Assert.Equal(cmd, result.Command);
        Assert.False(result.Login);
        Assert.False(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_UnixPathsFlag_SetsTrue()
    {
        var result = ShellArgs.Parse(["--unix-paths", "-c", "echo hi"]);
        Assert.True(result.UnixPaths);
    }

    [Fact]
    public void Parse_WindowsPathsFlag_SetsFalse()
    {
        var result = ShellArgs.Parse(["--windows-paths", "-c", "echo hi"]);
        Assert.False(result.UnixPaths);
    }

    [Fact]
    public void Parse_NoPathsFlag_LeavesUnixPathsNull()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);
        Assert.Null(result.UnixPaths);
    }

    [Fact]
    public void Parse_RecordWithExpression_CreatesModifiedCopy()
    {
        var original = ShellArgs.Parse(["-i"]);
        var modified = original with { Command = "ls" };

        Assert.True(modified.Interactive);
        Assert.Equal("ls", modified.Command);
        Assert.Null(original.Command);
    }

    // ── M3: file-arg (ScriptPath / ScriptArgs) ───────────────────────────────

    [Fact]
    public void Parse_ScriptPathAndArgs_SetsScriptPathAndScriptArgs()
    {
        var result = ShellArgs.Parse(["script.sh", "a", "b"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Equal(["a", "b"], result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_LoginFlagBeforeScriptPath_SetsLoginAndScriptPath()
    {
        var result = ShellArgs.Parse(["-l", "script.sh", "a"]);

        Assert.True(result.Login);
        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Equal(["a"], result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_EndOfOptionsThenScriptPath_SetsScriptPath()
    {
        var result = ShellArgs.Parse(["--", "script.sh"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_CommandFlagTakesPrecedenceOverScriptPath()
    {
        // When -c is given, ScriptPath should not be set from remaining args
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Equal("echo hi", result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_NoPositionalArgs_ScriptPathIsNull()
    {
        var result = ShellArgs.Parse(["-l"]);

        Assert.Null(result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
    }

    [Fact]
    public void Parse_ScriptPathOnly_NoScriptArgs()
    {
        var result = ShellArgs.Parse(["script.sh"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
    }

    // PTY-9 follow-on: `--ps` / `--raw-ps` flag — bypasses bash transpile and
    // forwards the command body to the host runspace as raw PowerShell. The
    // in-band entry point for tests that need to drive raw PS probes
    // ([Console]::ReadKey, etc.) under the same launcher pipeline as bash.
    [Fact]
    public void Parse_RawPsFlag_SetsRawPowerShell()
    {
        var result = ShellArgs.Parse(["--ps", "-c", "echo 'raw'"]);

        Assert.True(result.RawPowerShell);
        Assert.Equal("echo 'raw'", result.Command);
    }

    [Fact]
    public void Parse_RawPsLongAlias_SetsRawPowerShell()
    {
        var result = ShellArgs.Parse(["--raw-ps", "-c", "$PSVersionTable"]);

        Assert.True(result.RawPowerShell);
        Assert.Equal("$PSVersionTable", result.Command);
    }

    [Fact]
    public void Parse_NoRawPsFlag_DefaultsFalse()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.False(result.RawPowerShell);
    }

    // Regression: `ps-bash --version` had no parser case, fell through to the
    // default (a `-`-prefixed token is not a ScriptPath), leaving Command and
    // ScriptPath null — which dropped the launcher into interactive/stdin mode
    // and hung when no tty was attached.
    [Fact]
    public void Parse_VersionLongFlag_SetsShowVersion()
    {
        var result = ShellArgs.Parse(["--version"]);

        Assert.True(result.ShowVersion);
        Assert.Null(result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_VersionShortFlag_SetsShowVersion()
    {
        var result = ShellArgs.Parse(["-V"]);

        Assert.True(result.ShowVersion);
    }

    [Fact]
    public void Parse_HelpLongFlag_SetsShowHelp()
    {
        var result = ShellArgs.Parse(["--help"]);

        Assert.True(result.ShowHelp);
        Assert.Null(result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_NoInfoFlags_DefaultsFalse()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.False(result.ShowVersion);
        Assert.False(result.ShowHelp);
    }

    [Fact]
    public void Parse_CompactOutputFlag_SetsTrue()
    {
        var result = ShellArgs.Parse(["--compact-output", "-c", "echo hi"]);

        Assert.True(result.CompactOutput);
        Assert.Equal("echo hi", result.Command);
    }

    [Theory]
    [InlineData("--caveman")]
    [InlineData("--wenyan")]
    public void Parse_CompactOutputAliases_SetTrue(string flag)
    {
        var result = ShellArgs.Parse([flag, "-c", "echo hi"]);

        Assert.True(result.CompactOutput);
    }

    [Fact]
    public void Parse_NoCompactOutputFlag_SetsFalse()
    {
        var result = ShellArgs.Parse(["--no-compact-output", "-c", "echo hi"]);

        Assert.False(result.CompactOutput);
    }

    [Fact]
    public void Parse_NoCompactOutputFlag_LeavesNull()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Null(result.CompactOutput);
    }

    [Fact]
    public void Parse_CommandThenCompactOutput_SkipsFlagAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "--compact-output", "echo hi"]);

        Assert.True(result.CompactOutput);
        Assert.Equal("echo hi", result.Command);
    }

    // A literal `--version` passed as the -c command body must remain the
    // command — only a top-level `--version` flag flips ShowVersion.
    [Fact]
    public void Parse_VersionAsCommandBody_DoesNotSetShowVersion()
    {
        var result = ShellArgs.Parse(["-c", "echo --version"]);

        Assert.False(result.ShowVersion);
        Assert.Equal("echo --version", result.Command);
    }

    // ── --timeout flag (per-command idle timeout the caller can set) ─────────

    [Fact]
    public void Parse_NoTimeoutFlag_TimeoutIsNull()
    {
        Assert.Null(ShellArgs.Parse(["-c", "echo hi"]).Timeout);
    }

    [Fact]
    public void Parse_TimeoutSeconds_SetsTimeout()
    {
        var result = ShellArgs.Parse(["--timeout", "600", "-c", "echo hi"]);

        Assert.Equal("600", result.Timeout);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_TimeoutEqualsForm_SetsTimeout()
    {
        Assert.Equal("600", ShellArgs.Parse(["--timeout=600", "-c", "echo hi"]).Timeout);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("0")]
    [InlineData("infinite")]
    public void Parse_TimeoutDisableValues_PassThroughVerbatim(string value)
    {
        // The launcher forwards the raw value to PSBASH_TIMEOUT; IpcWorker owns
        // interpretation (none/0/off/infinite => unbounded).
        Assert.Equal(value, ShellArgs.Parse(["--timeout", value, "-c", "echo hi"]).Timeout);
    }

    [Fact]
    public void Parse_TimeoutWithoutValue_DoesNotThrow()
    {
        // Trailing --timeout with no value: tolerated, leaves Timeout null.
        var result = ShellArgs.Parse(["--timeout"]);
        Assert.Null(result.Timeout);
    }
}
