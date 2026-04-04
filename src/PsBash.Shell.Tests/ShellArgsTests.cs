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
