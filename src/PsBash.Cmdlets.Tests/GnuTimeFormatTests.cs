using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU time 1.9 options and report format; expectations checked against <c>/usr/bin/time</c> in WSL. ps-bash's
/// external <c>time</c> took <c>-f</c> as the command (<c>command time -f '%e %M' true</c>: "-f: command not found",
/// oils shell-bugs "./configure idiom").
/// </summary>
public class GnuTimeFormatTests
{
    private static GnuTimeStats Stats(int exit = 0, double elapsed = 0.05, string cmd = "true")
        => new(TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(0.01), TimeSpan.FromSeconds(0.02), 1234, exit, cmd);

    [Theory]
    [InlineData(new[] { "-f", "%e", "true" }, "%e", false, 2)]
    [InlineData(new[] { "--format=%M", "true" }, "%M", false, 1)]
    [InlineData(new[] { "--format", "%M", "true" }, "%M", false, 2)]
    [InlineData(new[] { "-ffmt", "true" }, "fmt", false, 1)]
    [InlineData(new[] { "-p", "true" }, GnuTimeFormat.Portable, false, 1)]
    [InlineData(new[] { "-q", "-f", "x", "--", "-cmd" }, "x", true, 4)]
    [InlineData(new[] { "true", "-f", "x" }, null, false, 0)]
    public void Parse_LeadingOptions_StopAtTheCommand(string[] args, string? format, bool quiet, int commandIndex)
    {
        var o = GnuTimeFormat.Parse(args);
        Assert.Equal((format, quiet, commandIndex, (string?)null, (string?)null), (o.Format, o.Quiet, o.CommandIndex, o.Unsupported, o.Error));
    }

    [Fact]
    public void Parse_FormatWithoutItsArgument_IsAUsageError()
        => Assert.Equal("time: option requires an argument -- 'f'", GnuTimeFormat.Parse(["-f"]).Error);

    [Theory]
    [InlineData("-o", "-o")]
    [InlineData("-ofile", "-o")]
    [InlineData("--output=f", "--output")]
    [InlineData("-a", "-a")]
    [InlineData("-v", "-v")]
    public void Parse_OutputAppendVerbose_AreRefused(string flag, string reported)
        => Assert.Equal(reported, GnuTimeFormat.Parse([flag, "true"]).Unsupported);

    [Fact]
    public void Render_EscapesUnknownSpecifiersAndPercent_AsGnuTime()
        // /usr/bin/time -f 'a\tb\\c\qd %Q %% %x %Z' true
        => Assert.Equal("a\tb\\c?\\qd ?Q % 0 4096\n", GnuTimeFormat.Render(@"a\tb\\c\qd %Q %% %x %Z", Stats(), quiet: false));

    [Fact]
    public void Render_NonZeroStatus_PrefixesTheExitLine_UnlessQuiet()
    {
        Assert.Equal("Command exited with non-zero status 1\n1\n", GnuTimeFormat.Render("%x", Stats(exit: 1), quiet: false));
        Assert.Equal("1\n", GnuTimeFormat.Render("%x", Stats(exit: 1), quiet: true));
    }

    [Fact]
    public void Render_Portable_IsThreeLines()
        => Assert.Equal("real 0.05\nuser 0.01\nsys 0.02\n", GnuTimeFormat.Render(GnuTimeFormat.Portable, Stats(), quiet: false));

    // /usr/bin/time -p false: no exit line; -f 'real %e\nuser %U\nsys %S' false: the exit line, then the report.
    [Fact]
    public void Render_PortableAfterFailure_HasNoExitLine_TheSameFormatViaF_Has()
    {
        Assert.Equal("real 0.05\nuser 0.01\nsys 0.02\n", GnuTimeFormat.Render(GnuTimeFormat.Portable, Stats(exit: 1), quiet: false));
        Assert.Equal("Command exited with non-zero status 1\nreal 0.05\nuser 0.01\nsys 0.02\n",
            GnuTimeFormat.Render(@"real %e\nuser %U\nsys %S", Stats(exit: 1), quiet: false));
    }

    [Theory]
    [InlineData(new[] { "-p", "-f", "x", "false" }, "x")]
    [InlineData(new[] { "-f", "x", "-p", "false" }, GnuTimeFormat.Portable)]
    public void Parse_LastOfPAndF_Wins(string[] args, string format)
        => Assert.Equal(format, GnuTimeFormat.Parse(args).Format);

    [Fact]
    public void Render_ClockAndCommandLine()
    {
        // /usr/bin/time -f '%E|%C' sleep 0.05 -> 0:00.05|sleep 0.05
        Assert.Equal("0:00.05|sleep 0.05\n", GnuTimeFormat.Render("%E|%C", Stats(cmd: "sleep 0.05"), quiet: false));
        Assert.Equal("1:01:01\n", GnuTimeFormat.Render("%E", Stats(elapsed: 3661.5), quiet: false));
    }

    [Fact]
    public void Render_ElapsedMemoryAndCpu()
        => Assert.Equal("0.05 1234 60%\n", GnuTimeFormat.Render("%e %M %P", Stats(), quiet: false));
}
