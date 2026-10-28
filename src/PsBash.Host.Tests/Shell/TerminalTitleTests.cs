using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

public class TerminalTitleTests
{
    [Fact]
    public void Running_ControlCharsAndEscapes_AreStripped()
    {
        var t = TerminalTitle.Running("echo hi\u001b]0;evil\u0007\nls");
        Assert.DoesNotContain('\u001b', t);
        Assert.DoesNotContain('\u0007', t);
        Assert.DoesNotContain('\n', t);
    }

    [Fact]
    public void Running_LongCommand_IsTruncated() =>
        Assert.True(TerminalTitle.Running(new string('x', 500)).Length <= 80);

    [Theory]
    [InlineData(@"C:\Users\a\proj", "ps-bash: proj")]
    [InlineData("/home/a/proj/", "ps-bash: proj")]
    [InlineData("/", "ps-bash: /")]
    public void Idle_UsesLeafDirectory(string cwd, string expected) =>
        Assert.Equal(expected, TerminalTitle.Idle(cwd));

    [Fact]
    public void Sequence_IsOsc0() =>
        Assert.Equal("\u001b]0;t\u0007", TerminalTitle.Sequence("t"));
}
