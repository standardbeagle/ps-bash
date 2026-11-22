using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Guard for the ONE brace sequence expander, <see cref="BraceSequence"/> (docs/specs/parser-grammar.md §Brace sequence
/// expressions). A nested sequence (<c>{x,{1..10..3}}</c>) went through a second copy that dropped the step and the
/// padding, and any <c>..</c> body was taken for a sequence (<c>{1..$n}</c> expanded). The table is bash 5.2.21's
/// output; each sequence must give it at top level (the <see cref="WordPart.BracedRange"/> the emitter expands, or a
/// tuple) and nested inside a comma expansion.
/// </summary>
public class BraceSequenceGuardTests
{
    /// <summary>(body, bash's words joined by <c>|</c>; an empty field is an empty word).</summary>
    public static IEnumerable<object[]> Sequences() => new[]
    {
        new object[] { "1..5", "1|2|3|4|5" },
        new object[] { "5..1", "5|4|3|2|1" },
        new object[] { "1..10..3", "1|4|7|10" },
        new object[] { "1..10..-3", "1|4|7|10" },
        new object[] { "1..3..0", "1|2|3" },
        new object[] { "-3..3..2", "-3|-1|1|3" },
        new object[] { "+1..3", "1|2|3" },
        new object[] { "01..03", "01|02|03" },
        new object[] { "0001..3", "0001|0002|0003" },
        new object[] { "01..10..3", "01|04|07|10" },
        new object[] { "001..3..2", "001|003" },
        new object[] { "-01..2", "-01|000|001|002" },
        new object[] { "-05..5..5", "-05|000|005" },
        new object[] { "a..e", "a|b|c|d|e" },
        new object[] { "c..g..2", "c|e|g" },
        new object[] { "Z..b..3", "Z|]|`" },
        new object[] { "Z..b", "Z|[||]|^|_|`|a|b" },
    };

    // A generated backtick stays escaped word text (BraceSequence.GeneratedBacktick); the word it ends up in is a
    // literal backtick.
    private static List<string> Unescaped(List<string> words) => words.ConvertAll(w => w.Replace("\\`", "`"));

    private static List<string> TopLevel(string body)
    {
        var part = Assert.Single(BashParser.DecomposeWord("{" + body + "}"));
        return part switch
        {
            // The emitter's call for a BracedRange (PsEmitter.ExpandBrace).
            WordPart.BracedRange r => new BraceSequence(false, r.Start, r.End, r.Step, r.ZeroPad).Words(),
            WordPart.BracedTuple t => t.Items.ToList(),
            _ => throw new Xunit.Sdk.XunitException($"{{{body}}} parsed as {part}"),
        };
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void Sequence_TopLevelAndNested_AreBashsWords(string body, string expected)
    {
        var want = expected.Split('|').ToList();
        Assert.Equal(want, Unescaped(TopLevel(body)));
        Assert.Equal(want.Prepend("x").ToList(), Unescaped(BashParser.BraceExpandForTest("{x,{" + body + "}}")));
        Assert.Equal(want.Select(w => "p" + w + "s").Prepend("q").ToList(),
            Unescaped(BashParser.BraceExpandForTest("{q,p{" + body + "}s}")));
    }

    // Not a sequence (bash expand_seqterm rejects it): the braces stay literal while the word around them expands.
    [Theory]
    [InlineData("1..$n")]
    [InlineData("a..$n")]
    [InlineData("$n..5")]
    [InlineData("1..3..$n")]
    [InlineData("1..3..x")]
    [InlineData("1..3..")]
    [InlineData("..3")]
    [InlineData("1..")]
    [InlineData("1..2..3..4")]
    [InlineData("@..C")]
    [InlineData("9..a")]
    [InlineData("1...3")]
    [InlineData("ab..c")]
    public void NotASequence_StaysLiteral(string body)
    {
        Assert.False(BraceSequence.TryParse(body, out _));
        var parts = BashParser.DecomposeWord("x{" + body + "}y");
        Assert.DoesNotContain(parts, p => p is WordPart.BracedRange or WordPart.BracedTuple);
        Assert.Contains(parts, p => p is WordPart.Literal { Value: var v } && v.Contains('{'));
    }

    [Fact]
    public void Transpile_NestedSteppedRange_KeepsTheStep()
    {
        string ps = PsEmitter.Transpile("echo {x,{1..10..3}}")!;
        Assert.Contains("@('x',1,4,7,10)", ps);
    }

    // A generated backtick with text after it in its word never closes: bash's bad substitution.
    [Fact]
    public void CharSequence_BacktickBeforeText_IsABadSubstitution()
    {
        Assert.Contains(BashParser.DecomposeWord("-{z..A}-"),
            p => p is WordPart.BracedVarSub { Suffix: { } s } && s.StartsWith("#BAD:", StringComparison.Ordinal));
        Assert.IsType<WordPart.BracedTuple>(Assert.Single(BashParser.DecomposeWord("{z..A}")));
    }

    [Fact]
    public void FromPasswd_ReturnsTheHomeField_FirstEntryWins()
    {
        string[] passwd =
        {
            "# comment:x:0:0::/nope:/bin/sh",
            "root:x:0:0:root:/root:/bin/bash",
            "bin:x:1:1:bin:/bin:/usr/sbin/nologin",
            "root:x:0:0:dup:/second:/bin/sh",
            "short:x:2",
        };
        Assert.Equal("/root", UserHome.FromPasswd(passwd, "root"));
        Assert.Equal("/bin", UserHome.FromPasswd(passwd, "bin"));
        Assert.Null(UserHome.FromPasswd(passwd, "short"));
        Assert.Null(UserHome.FromPasswd(passwd, "nosuchuserx"));
        Assert.Null(UserHome.FromPasswd(passwd, "# comment"));
    }

    // ~user: the passwd home on Linux/macOS; Windows has none (intentional-differences.md, `~user`).
    [SkippableFact]
    public void TildeUser_ResolvesFromPasswd_OffWindows()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no passwd database: ~root stays literal (intentional difference)");
        Skip.IfNot(UserHome.Lookup("root") is "/root", "this host has no root entry with home /root in /etc/passwd");
        Assert.Equal(new WordPart.SingleQuoted("/root"), BashParser.DecomposeWord("~root/z")[0]);
        Assert.IsNotType<WordPart.SingleQuoted>(BashParser.DecomposeWord("~nosuchuserx")[0]);
    }

    // `w=a:~+:~-`: `$env:PWD:` in a PS string is a drive-qualified name that swallowed the colon.
    [Fact]
    public void Transpile_TildePlusMinusBeforeColon_AreBraced()
    {
        string ps = PsEmitter.Transpile("w=a:~+:~-")!;
        Assert.Contains("${env:PWD}", ps);
        Assert.Contains("${env:OLDPWD}", ps);
        Assert.DoesNotContain("$env:PWD:", ps);
    }
}
