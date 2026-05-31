using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

public class BashLexerTests
{
    private static List<BashToken> Tokenize(string input) => BashLexer.Tokenize(input);

    private static void AssertTokens(List<BashToken> tokens, params (BashTokenKind Kind, string Value)[] expected)
    {
        // Strip the trailing Eof for comparison convenience.
        var withoutEof = tokens.Where(t => t.Kind != BashTokenKind.Eof).ToList();
        Assert.Equal(expected.Length, withoutEof.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Kind, withoutEof[i].Kind);
            Assert.Equal(expected[i].Value, withoutEof[i].Value);
        }
    }

    // --- Required red-then-green tests from task description ---

    [Fact]
    public void Tokenize_EchoHello_ReturnsTwoWords()
    {
        var tokens = Tokenize("echo hello");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "hello"));
    }

    [Fact]
    public void Tokenize_PipelineGrepFoo_ReturnsWordsAndPipe()
    {
        var tokens = Tokenize("ls | grep foo");

        AssertTokens(tokens,
            (BashTokenKind.Word, "ls"),
            (BashTokenKind.Pipe, "|"),
            (BashTokenKind.Word, "grep"),
            (BashTokenKind.Word, "foo"));
    }

    [Fact]
    public void Tokenize_AssignmentWithCommand_ReturnsAssignmentAndWord()
    {
        var tokens = Tokenize("FOO=bar cmd");

        AssertTokens(tokens,
            (BashTokenKind.AssignmentWord, "FOO=bar"),
            (BashTokenKind.Word, "cmd"));
    }

    [Fact]
    public void Tokenize_RedirectToFile_ReturnsWordsAndRedirect()
    {
        var tokens = Tokenize("echo hello > file");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "hello"),
            (BashTokenKind.Great, ">"),
            (BashTokenKind.Word, "file"));
    }

    [Fact]
    public void Tokenize_AmpGreat_RedirectBothStreams()
    {
        // `&>` must beat the single-char `&` (Amp): without a dedicated token
        // `cmd &> f` lexes as background `&` + bare `> f`.
        var tokens = Tokenize("cmd &> out.log");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.AmpGreat, "&>"),
            (BashTokenKind.Word, "out.log"));
    }

    [Fact]
    public void Tokenize_AmpDGreat_AppendBothStreams()
    {
        var tokens = Tokenize("cmd &>> out.log");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.AmpDGreat, "&>>"),
            (BashTokenKind.Word, "out.log"));
    }

    [Fact]
    public void Tokenize_SemiAmp_StaysSemiPlusAmp_ContextFree()
    {
        // The lexer is context-free: `;&` is Semi+Amp, NOT a dedicated token.
        // The parser detects the Semi+Amp sequence as a case fall-through
        // terminator only inside a case body (so it can't silently drop a
        // command outside case). Adjacent Position proves they're contiguous.
        var tokens = Tokenize("a ;& b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Amp, "&"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_DSemiAmp_StaysSemiSemiAmp_ContextFree()
    {
        var tokens = Tokenize("a ;;& b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Amp, "&"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_AndIf_NotShadowedByAmpGreat()
    {
        // Ordering invariant: `&&` (AndIf) must not be shadowed by the new
        // `&>`/`&>>` checks (which only fire when `&` is followed by `>`).
        var tokens = Tokenize("a && b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.AndIf, "&&"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_TrailingAmpersand_StillBackgrounds_NotAmpGreat()
    {
        // Guard: a bare trailing `&` (not followed by `>`) stays Amp.
        var tokens = Tokenize("sleep 1 &");

        AssertTokens(tokens,
            (BashTokenKind.Word, "sleep"),
            (BashTokenKind.Word, "1"),
            (BashTokenKind.Amp, "&"));
    }

    // --- Operators ---

    [Fact]
    public void Tokenize_AndIf_ReturnsAndIfToken()
    {
        var tokens = Tokenize("a && b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.AndIf, "&&"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_OrIf_ReturnsOrIfToken()
    {
        var tokens = Tokenize("a || b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.OrIf, "||"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_PipeAmp_ReturnsPipeAmpToken()
    {
        var tokens = Tokenize("cmd1 |& cmd2");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd1"),
            (BashTokenKind.PipeAmp, "|&"),
            (BashTokenKind.Word, "cmd2"));
    }

    [Fact]
    public void Tokenize_Semicolon_ReturnsSemiToken()
    {
        var tokens = Tokenize("a; b");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_Ampersand_ReturnsAmpToken()
    {
        var tokens = Tokenize("sleep 1 &");

        AssertTokens(tokens,
            (BashTokenKind.Word, "sleep"),
            (BashTokenKind.Word, "1"),
            (BashTokenKind.Amp, "&"));
    }

    [Fact]
    public void Tokenize_Parens_ReturnsParenTokens()
    {
        var tokens = Tokenize("(a)");

        AssertTokens(tokens,
            (BashTokenKind.LParen, "("),
            (BashTokenKind.Word, "a"),
            (BashTokenKind.RParen, ")"));
    }

    [Fact]
    public void Tokenize_Braces_ReturnsBraceTokens()
    {
        var tokens = Tokenize("{ a; }");

        AssertTokens(tokens,
            (BashTokenKind.LBrace, "{"),
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.RBrace, "}"));
    }

    [Fact]
    public void Tokenize_Bang_ReturnsBangToken()
    {
        var tokens = Tokenize("! cmd");

        AssertTokens(tokens,
            (BashTokenKind.Bang, "!"),
            (BashTokenKind.Word, "cmd"));
    }

    // --- Redirections ---

    [Fact]
    public void Tokenize_DGreat_ReturnsAppendRedirect()
    {
        var tokens = Tokenize("echo x >> file");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "x"),
            (BashTokenKind.DGreat, ">>"),
            (BashTokenKind.Word, "file"));
    }

    [Fact]
    public void Tokenize_DLess_ReturnsHereDocOperator()
    {
        var tokens = Tokenize("cat << EOF");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cat"),
            (BashTokenKind.DLess, "<<"),
            (BashTokenKind.Word, "EOF"));
    }

    [Fact]
    public void Tokenize_DLessDash_ReturnsStripTabsHereDoc()
    {
        var tokens = Tokenize("cat <<- EOF");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cat"),
            (BashTokenKind.DLessDash, "<<-"),
            (BashTokenKind.Word, "EOF"));
    }

    [Fact]
    public void Tokenize_LessAnd_ReturnsDupInputRedirect()
    {
        var tokens = Tokenize("cmd <& 3");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.LessAnd, "<&"),
            (BashTokenKind.Word, "3"));
    }

    [Fact]
    public void Tokenize_GreatAnd_ReturnsDupOutputRedirect()
    {
        var tokens = Tokenize("cmd >& 2");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.GreatAnd, ">&"),
            (BashTokenKind.Word, "2"));
    }

    // --- IoNumber ---

    [Fact]
    public void Tokenize_IoNumber_BeforeGreat()
    {
        var tokens = Tokenize("cmd 2> /dev/null");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.IoNumber, "2"),
            (BashTokenKind.Great, ">"),
            (BashTokenKind.Word, "/dev/null"));
    }

    [Fact]
    public void Tokenize_IoNumber_BeforeLess()
    {
        var tokens = Tokenize("cmd 0< input");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.IoNumber, "0"),
            (BashTokenKind.Less, "<"),
            (BashTokenKind.Word, "input"));
    }

    [Fact]
    public void Tokenize_IoNumber_BeforeGreatAnd()
    {
        var tokens = Tokenize("cmd 2>& 1");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.IoNumber, "2"),
            (BashTokenKind.GreatAnd, ">&"),
            (BashTokenKind.Word, "1"));
    }

    [Fact]
    public void Tokenize_DigitWord_NotBeforeRedirect_StaysWord()
    {
        var tokens = Tokenize("echo 42");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "42"));
    }

    // --- Assignment ---

    [Fact]
    public void Tokenize_AssignmentEmptyValue()
    {
        var tokens = Tokenize("X=");

        AssertTokens(tokens,
            (BashTokenKind.AssignmentWord, "X="));
    }

    [Fact]
    public void Tokenize_AssignmentWithUnderscore()
    {
        var tokens = Tokenize("_MY_VAR=hello");

        AssertTokens(tokens,
            (BashTokenKind.AssignmentWord, "_MY_VAR=hello"));
    }

    [Fact]
    public void Tokenize_WordStartingWithDigit_NotAssignment()
    {
        var tokens = Tokenize("2foo=bar");

        AssertTokens(tokens,
            (BashTokenKind.Word, "2foo=bar"));
    }

    // --- Quoting ---

    [Fact]
    public void Tokenize_SingleQuotedWord_KeepsQuotesInValue()
    {
        var tokens = Tokenize("echo 'hello world'");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "'hello world'"));
    }

    [Fact]
    public void Tokenize_DoubleQuotedWord_KeepsQuotesInValue()
    {
        var tokens = Tokenize("echo \"hello world\"");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "\"hello world\""));
    }

    [Fact]
    public void Tokenize_BackslashEscape_KeepsEscapeInValue()
    {
        var tokens = Tokenize("echo hello\\ world");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "hello\\ world"));
    }

    // --- Newline and whitespace ---

    [Fact]
    public void Tokenize_Newline_ReturnsNewlineToken()
    {
        var tokens = Tokenize("a\nb");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Newline, "\n"),
            (BashTokenKind.Word, "b"));
    }

    [Fact]
    public void Tokenize_WindowsNewline_NormalizesToSingleToken()
    {
        var tokens = Tokenize("a\r\nb");

        AssertTokens(tokens,
            (BashTokenKind.Word, "a"),
            (BashTokenKind.Newline, "\n"),
            (BashTokenKind.Word, "b"));
    }

    // --- Comments ---

    [Fact]
    public void Tokenize_Comment_IsSkipped()
    {
        var tokens = Tokenize("echo hi # this is a comment\ncmd");

        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "hi"),
            (BashTokenKind.Newline, "\n"),
            (BashTokenKind.Word, "cmd"));
    }

    // --- Reserved words ---

    [Theory]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("elif")]
    [InlineData("fi")]
    [InlineData("do")]
    [InlineData("done")]
    [InlineData("case")]
    [InlineData("esac")]
    [InlineData("while")]
    [InlineData("until")]
    [InlineData("for")]
    [InlineData("in")]
    [InlineData("function")]
    public void IsReservedWord_ReturnsTrue(string word)
    {
        Assert.True(BashLexer.IsReservedWord(word));
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("ls")]
    [InlineData("IF")]
    [InlineData("")]
    public void IsReservedWord_ReturnsFalse(string word)
    {
        Assert.False(BashLexer.IsReservedWord(word));
    }

    [Fact]
    public void Tokenize_ReservedWord_EmittedAsWord()
    {
        var tokens = Tokenize("if true; then echo yes; fi");

        AssertTokens(tokens,
            (BashTokenKind.Word, "if"),
            (BashTokenKind.Word, "true"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Word, "then"),
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "yes"),
            (BashTokenKind.Semi, ";"),
            (BashTokenKind.Word, "fi"));
    }

    // --- Eof ---

    [Fact]
    public void Tokenize_EmptyInput_ReturnsOnlyEof()
    {
        var tokens = Tokenize("");

        Assert.Single(tokens);
        Assert.Equal(BashTokenKind.Eof, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_ReturnsOnlyEof()
    {
        var tokens = Tokenize("   \t  ");

        Assert.Single(tokens);
        Assert.Equal(BashTokenKind.Eof, tokens[0].Kind);
    }

    // --- Position tracking ---

    [Fact]
    public void Tokenize_TracksPositions()
    {
        var tokens = Tokenize("echo hello");

        Assert.Equal(0, tokens[0].Position);  // echo
        Assert.Equal(5, tokens[1].Position);  // hello
    }

    [Fact]
    public void Tokenize_AlwaysEndsWithEof()
    {
        var tokens = Tokenize("a b c");

        Assert.Equal(BashTokenKind.Eof, tokens[^1].Kind);
    }

    // --- Complex cases ---

    [Fact]
    public void Tokenize_StderrRedirectToDevNull()
    {
        var tokens = Tokenize("cmd 2>/dev/null");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.IoNumber, "2"),
            (BashTokenKind.Great, ">"),
            (BashTokenKind.Word, "/dev/null"));
    }

    [Fact]
    public void Tokenize_MultipleAssignments()
    {
        var tokens = Tokenize("A=1 B=2 cmd");

        AssertTokens(tokens,
            (BashTokenKind.AssignmentWord, "A=1"),
            (BashTokenKind.AssignmentWord, "B=2"),
            (BashTokenKind.Word, "cmd"));
    }

    [Fact]
    public void Tokenize_PipelineWithRedirect()
    {
        var tokens = Tokenize("cat file | sort > out");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cat"),
            (BashTokenKind.Word, "file"),
            (BashTokenKind.Pipe, "|"),
            (BashTokenKind.Word, "sort"),
            (BashTokenKind.Great, ">"),
            (BashTokenKind.Word, "out"));
    }

    [Fact]
    public void Tokenize_IoNumberAdjacentToRedirect_ReclassifiesAsIoNumber()
    {
        // 2> (no space) — the 2 is an IO number for fd redirection
        var tokens = Tokenize("cmd 2>/dev/null");

        AssertTokens(tokens,
            (BashTokenKind.Word, "cmd"),
            (BashTokenKind.IoNumber, "2"),
            (BashTokenKind.Great, ">"),
            (BashTokenKind.Word, "/dev/null"));
    }

    [Fact]
    public void Tokenize_DigitWithSpaceBeforeRedirect_StaysAsWord()
    {
        // 2 << (with space) — the 2 is a regular word argument, not an IO number
        var tokens = Tokenize("head -n 2 << EOF");

        AssertTokens(tokens,
            (BashTokenKind.Word, "head"),
            (BashTokenKind.Word, "-n"),
            (BashTokenKind.Word, "2"),
            (BashTokenKind.DLess, "<<"),
            (BashTokenKind.Word, "EOF"));
    }

    [Fact]
    public void Tokenize_EmptyBraces_LexesAsWord()
    {
        var tokens = Tokenize("find -exec wc {} +");
        AssertTokens(tokens,
            (BashTokenKind.Word, "find"),
            (BashTokenKind.Word, "-exec"),
            (BashTokenKind.Word, "wc"),
            (BashTokenKind.Word, "{}"),
            (BashTokenKind.Word, "+"));
    }

    [Fact]
    public void Tokenize_EmptyBracesInXargs_LexesAsWord()
    {
        var tokens = Tokenize("xargs -I{} echo");
        AssertTokens(tokens,
            (BashTokenKind.Word, "xargs"),
            (BashTokenKind.Word, "-I{}"),
            (BashTokenKind.Word, "echo"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Port gaps vs bash (oracle-driven). Each case encodes how bash itself
    // tokenizes; ps-bash shortcut behaviours are bugs to be forced green.
    // ─────────────────────────────────────────────────────────────────────

    // Bug A: bash starts a comment only when '#' BEGINS a word. A '#' in the
    // middle of a word is a literal. The lexer broke words on '#' and then ate
    // the rest of the line as a comment, so "abc#def" lost "#def".
    [Fact]
    public void Tokenize_HashMidWord_IsLiteralNotComment()
    {
        var tokens = Tokenize("echo abc#def");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "abc#def"));
    }

    [Fact]
    public void Tokenize_UrlWithFragment_KeepsFragment()
    {
        var tokens = Tokenize("echo http://x/p#section");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "http://x/p#section"));
    }

    // Positive control: '#' at a word boundary IS a comment (must still work).
    [Fact]
    public void Tokenize_HashAtWordStart_IsComment()
    {
        var tokens = Tokenize("echo a #b c");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "a"));
    }

    // Bug B: balanced-scan must be quote-aware. A ')' inside a string inside a
    // command substitution must NOT close the $( ). The whole $(...) is one word.
    [Fact]
    public void Tokenize_CmdSub_WithQuotedCloseParen_StaysOneWord()
    {
        var tokens = Tokenize("echo $(grep \")\" f)");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "$(grep \")\" f)"));
    }

    [Fact]
    public void Tokenize_CmdSub_WithQuotedOpenParen_StaysOneWord()
    {
        var tokens = Tokenize("echo $(grep '(' f) rest");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "$(grep '(' f)"),
            (BashTokenKind.Word, "rest"));
    }

    // Bug B: a $(...) inside double quotes must be scanned as part of the word
    // even though it contains its own '"' chars. The double-quote scan must
    // recurse through $(...) rather than terminating at the inner quote.
    [Fact]
    public void Tokenize_NestedDquoteInCmdSubInDquote_StaysOneWord()
    {
        var tokens = Tokenize("echo \"$(echo \"hi there\")\"");
        AssertTokens(tokens,
            (BashTokenKind.Word, "echo"),
            (BashTokenKind.Word, "\"$(echo \"hi there\")\""));
    }
}
