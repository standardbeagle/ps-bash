using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

public class ShellArgsTests
{
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
}
