using System.Text;
using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Unit tests for <see cref="InvokeBashTailCommand.SplitFollowChunk"/> — the pure core of
/// the <c>tail -f</c> poll extracted so the withhold/advance logic is testable without a
/// timing-dependent follow loop.
///
/// Regression: the follow rewrite is supposed to withhold a newline-less trailing fragment
/// until its newline arrives. The original cap guard compared the read count against
/// <c>toRead</c>, but for any append &lt;= the cap <c>read == toRead</c>, so the fragment
/// was emitted immediately anyway — defeating the withhold. The fix compares against the
/// CAP. Oracle: GNU tail -f (line-at-a-time on <c>\n</c> boundaries).
/// </summary>
public class InvokeBashTailFollowChunkTests
{
    private const long Cap = 64L << 20;

    private static (int Advance, string[] Lines) Run(string content, long? avail = null, long cap = Cap)
    {
        var buf = Encoding.UTF8.GetBytes(content);
        var (adv, lines) = InvokeBashTailCommand.SplitFollowChunk(buf, buf.Length, avail ?? buf.Length, cap);
        return (adv, lines.ToArray());
    }

    [Fact]
    public void CompleteLines_EmitsAll_AdvancesPastAll()
    {
        var (adv, lines) = Run("a\nb\n");
        Assert.Equal(4, adv);
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void NewlinelessFragment_IsWithheld()
    {
        // THE regression: an incomplete trailing line must NOT be emitted (advance 0).
        var (adv, lines) = Run("abc");
        Assert.Equal(0, adv);
        Assert.Empty(lines);
    }

    [Fact]
    public void CompletePlusFragment_EmitsCompleteHoldsFragment()
    {
        var (adv, lines) = Run("a\nbc");
        Assert.Equal(2, adv); // past "a\n" only
        Assert.Equal(new[] { "a" }, lines);
    }

    [Fact]
    public void Crlf_StrippedToMatchReadLine()
    {
        var (adv, lines) = Run("a\r\nb\r\n");
        Assert.Equal(6, adv);
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void NewlinelessButCapExceeded_ForceEmitsToAvoidStall()
    {
        // A pending run larger than the cap with no newline must force-emit.
        var (adv, lines) = Run("abc", avail: 100, cap: 3);
        Assert.Equal(3, adv);
        Assert.Equal(new[] { "abc" }, lines);
    }

    [Fact]
    public void EmptyRead_NoLines_NoAdvance()
    {
        var (adv, lines) = Run(string.Empty);
        Assert.Equal(0, adv);
        Assert.Empty(lines);
    }
}
