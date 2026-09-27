using System;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="IpcWorker.ParseCallTimeout"/>, the pure parser
/// behind the per-call inactivity timeout.
///
/// Core behavior change (Dart p05w8q0rwG8L): the DEFAULT (env var unset) is
/// now <b>unbounded</b>, matching core bash, which applies no idle timeout to
/// <c>-c</c>/non-interactive runs. ps-bash previously aborted a quiet command
/// after 120s with no host output, killing legitimate long, silent commands
/// (`dotnet test`, `dotnet build`, large downloads). The interactive REPL is
/// PTY-bound and never reaches IpcWorker, so this default only governs the
/// non-interactive modes the bug is about.
/// </summary>
public class IpcWorkerTimeoutTests
{
    [Theory]
    [InlineData(null)]   // env var unset — the default
    [InlineData("")]     // blank
    [InlineData("   ")]  // whitespace only
    public void ParseCallTimeout_Unset_IsUnbounded(string? raw)
    {
        // Unbounded is represented as TimeSpan.Zero (SendRequestAsync treats
        // `<= TimeSpan.Zero` as "no idle timeout").
        Assert.Equal(TimeSpan.Zero, IpcWorker.ParseCallTimeout(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("infinite")]
    [InlineData("never")]
    [InlineData("NONE")]   // case-insensitive
    [InlineData("-5")]     // any non-positive integer
    public void ParseCallTimeout_DisableValues_AreUnbounded(string raw)
    {
        Assert.Equal(TimeSpan.Zero, IpcWorker.ParseCallTimeout(raw));
    }

    [Theory]
    [InlineData("600", 600)]
    [InlineData("120", 120)]
    [InlineData("  90 ", 90)]   // trimmed
    public void ParseCallTimeout_PositiveInteger_IsThatManySeconds(string raw, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), IpcWorker.ParseCallTimeout(raw));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("12x")]
    public void ParseCallTimeout_Unparseable_IsUnbounded(string raw)
    {
        // An unparseable value falls back to the default, which is now unbounded.
        Assert.Equal(TimeSpan.Zero, IpcWorker.ParseCallTimeout(raw));
    }

    // GitHub issue #6: runspace init can exceed the old fixed 20s on a slow machine.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("garbage", "0")]    // unparseable / non-positive fall through
    [InlineData("-3", "none")]
    public void ParseStartupTimeout_Unset_IsSixtySecondDefault(string? startup, string? timeout)
    {
        Assert.Equal(TimeSpan.FromSeconds(60), IpcWorker.ParseStartupTimeout(startup, timeout));
    }

    [Fact]
    public void ParseStartupTimeout_StartupVar_WinsOverTimeoutVar()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), IpcWorker.ParseStartupTimeout("120", "5"));
    }

    [Fact]
    public void ParseStartupTimeout_TimeoutVar_HonoredForBackwardCompat()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), IpcWorker.ParseStartupTimeout(null, "45"));
    }
}
