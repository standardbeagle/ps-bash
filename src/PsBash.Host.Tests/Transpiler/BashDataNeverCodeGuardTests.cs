using System.Globalization;
using System.Management.Automation.Language;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Bash data never becomes PowerShell code (docs/specs/emitter-strategy.md §Bash data never becomes PowerShell code).
/// The class: text the emitter or the module wrote into PowerShell source WITHOUT the one quoting primitive, or with a
/// primitive that knew only part of PowerShell's grammar. REGRESSIONS: Resolve-BashCommand and Start-BashBgProcessJob
/// doubled only ASCII <c>'</c> (a U+2019 in a word ran the rest as code); <c>~user</c> was spliced raw; NBSP / U+2028 /
/// U+3000 / VT / FF split a bareword; an escaped CR became a line continuation; <c>flock -c</c> ran any unparseable
/// string as PowerShell; <c>$^</c> / <c>$é</c> read PowerShell variables; <c>1e5=1</c>, <c>$:</c> and a <c>)</c> in
/// a string nested in <c>"$( )"</c> did not parse. Primitives: <see cref="PsBuild"/> (SingleQuote, EscapeBareword,
/// EscapeBarewordChar, HostileInStringSubexpression), <see cref="PsNumberLiteral"/>, <see cref="PowerShellFallback"/>.
/// Oracle note (qa-rubric Directive 1): the oracle is PowerShell's own parser (the property is "the payload stays a
/// string constant", a ps-bash/PowerShell-seam property); bash parity of the values is
/// Differential.Tests BashDataNeverCodeDifferentialTests.
/// </summary>
public class BashDataNeverCodeGuardTests
{
    private const string Sentinel = "SENTINEL";

    // ── 1. The primitive against the real tokenizer, every BMP character ─────────────────────────────────────

    /// <summary>PowerShell's word breaks are exactly <see cref="PsBuild.IsPsWordBreak"/>: every character that makes
    /// <c>Write-Output a?b</c> two arguments (or two statements) is one, and no other.</summary>
    [Fact]
    public void IsPsWordBreak_IsExactlyThePowerShellTokenizersWhitespaceAndNewlines()
    {
        var wrong = new List<string>();
        for (int i = 1; i <= 0xFFFF; i++)
        {
            char c = (char)i;
            if (char.IsSurrogate(c)) continue;
            var ast = Parser.ParseInput("Write-Output a" + c + "b", out _, out var errors);
            bool splits = errors.Length == 0 && (ast.EndBlock.Statements.Count == 2
                || ast.EndBlock.Statements is [PipelineAst { PipelineElements: [CommandAst { CommandElements.Count: 3 }] }]);
            bool tokenizerBreak = splits && c is not (';' or '&' or '|' or '\'' or '"' or '(' or ')' or '{' or '}' or '<' or '>' or ',' or '#' or '`');
            if (tokenizerBreak != PsBuild.IsPsWordBreak(c)) wrong.Add($"U+{i:X4} tokenizer={tokenizerBreak}");
        }
        Assert.True(wrong.Count == 0, "PsBuild.IsPsWordBreak disagrees with PowerShell: " + string.Join(", ", wrong));
    }

    /// <summary>Every BMP character, escaped by <see cref="PsBuild.EscapeBarewordLiteral"/> (an escaped bash
    /// character), is that one character inside one argument: never a word break, a line continuation, an escape
    /// sequence or a string opener.</summary>
    [Fact]
    public void EscapeBarewordLiteral_EveryCharacter_IsItselfInOneArgument()
    {
        var wrong = new List<string>();
        for (int i = 1; i <= 0xFFFF; i++)
        {
            char c = (char)i;
            if (char.IsSurrogate(c)) continue;
            foreach (var (prefix, suffix) in new[] { ("a", "b"), ("", "b"), ("a", "") })
                if (!LeadingSpace(prefix, c) && OneArgument("Write-Output " + prefix + PsBuild.EscapeBarewordLiteral(c.ToString()) + suffix) != prefix + c + suffix)
                    wrong.Add($"U+{i:X4} in `{prefix}?{suffix}`");
        }
        Assert.True(wrong.Count == 0, "EscapeBarewordLiteral broke: " + string.Join(", ", wrong.Take(40)));
    }

    /// <summary>Every character <see cref="PsBuild.BarewordNeedsEscape"/> claims (quote marks, braces, <c>$</c>, every
    /// PowerShell word break, the dash marks at a word start), in every position, through
    /// <see cref="PsBuild.EscapeBareword"/> is one literal argument.</summary>
    [Fact]
    public void EscapeBareword_EveryCharacter_IsOneLiteralArgument()
    {
        // Only the characters it claims: a leading ASCII dash, a number, `,` and a leading `@` are other paths' (the
        // emitter quotes dash words and numbers; EscapeLeadingSplatSigil). IsPsWordBreak's completeness is tested above.
        var wrong = new List<string>();
        for (int i = 1; i <= 0xFFFF; i++)
        {
            char c = (char)i;
            if (char.IsSurrogate(c) || !PsBuild.BarewordNeedsEscape(c.ToString(), atWordStart: true)) continue;
            foreach (var (prefix, suffix) in new[] { ("a", "b"), ("", "b"), ("a", ""), ("", "") })
            {
                if (LeadingSpace(prefix, c)) continue;
                string value = prefix + c + suffix;
                bool start = prefix.Length == 0;
                string escaped = PsBuild.EscapeBareword(value, atWordStart: start);
                if (OneArgument("Write-Output " + escaped) != value)
                    wrong.Add($"U+{i:X4} in `{prefix}?{suffix}` -> {escaped}");
            }
        }
        Assert.True(wrong.Count == 0, "EscapeBareword broke: " + string.Join(", ", wrong.Take(40)));
    }

    [Theory]
    [InlineData("a\rb", "a`rb")]
    [InlineData("a\u00A0b", "a`u{00A0}b")]
    [InlineData("a\u2028b", "a`u{2028}b")]
    [InlineData("a\u3000b", "a`u{3000}b")]
    [InlineData("a\vb", "a`vb")]
    public void EscapeBareword_PowerShellOnlyWhitespace_IsAnEscapeSequence(string value, string expected)
        => Assert.Equal(expected, PsBuild.EscapeBareword(value, atWordStart: false));

    /// <summary>PowerShell drops a backtick-escaped space that STARTS an argument; a word starting with an escaped space
    /// is one double-quoted string instead (PsEmitter.EmitWordText, `\ x`), so the bareword primitives never see it.</summary>
    private static bool LeadingSpace(string prefix, char c) => prefix.Length == 0 && c == ' ';

    /// <summary>The value of <paramref name="ps"/>'s one argument when it is exactly one string constant, else null.</summary>
    private static string? OneArgument(string ps)
    {
        var ast = Parser.ParseInput(ps, out _, out var errors);
        if (errors.Length != 0) return null;
        return ast.EndBlock.Statements is [PipelineAst { PipelineElements: [CommandAst { CommandElements: [_, StringConstantExpressionAst arg] }] }]
            ? arg.Value : null;
    }

    // ── 2. Emitter regressions: the emitted PowerShell, parsed ─────────────────────────────────────────────

    [Theory]
    [InlineData("echo $: x", "$:")]
    [InlineData("echo $^ x", "$^")]
    [InlineData("echo $\u00E9$^", "$\u00E9$^")]
    [InlineData("echo x$^y", "x$^y")]
    [InlineData("echo a\\\rb", "a\rb")]
    [InlineData("echo a\rb", "a\rb")]
    [InlineData("echo a\u00A0b", "a\u00A0b")]
    [InlineData("echo a\u00A0#c", "a\u00A0#c")]
    [InlineData("echo z\u00A0@args", "z\u00A0@args")]
    [InlineData("echo c\u2028d", "c\u2028d")]
    [InlineData("echo e\u0085f", "e\u0085f")]
    [InlineData("echo x\u000By", "x\u000By")]
    [InlineData("echo ~a\u2019b", "~a\u2019b")]
    [InlineData("echo ~x\u201Dy", "~x\u201Dy")]
    public void Transpile_DataWord_IsOneStringConstant(string bash, string value)
    {
        var ast = ParseEmitted(bash);
        var cmd = Assert.Single(ast.FindAll(a => a is CommandAst c && c.GetCommandName() == "Invoke-BashEcho", true).Cast<CommandAst>());
        Assert.Equal(value, Assert.IsType<StringConstantExpressionAst>(cmd.CommandElements[1]).Value);
        Assert.Empty(ast.FindAll(a => a is VariableExpressionAst v && v.VariablePath.UserPath is "^" or ":" or "\u00E9", true));
    }

    [Theory]
    [InlineData("1e5=1")]
    [InlineData("08=x")]
    [InlineData("1e5=1 2>&1 | cat")]
    [InlineData(".5=1")]
    [InlineData("0x10=y; echo $?")]
    public void Transpile_NumberShapedCommandName_IsCalledByName(string bash)
    {
        var ast = ParseEmitted(bash);
        Assert.Empty(ast.FindAll(a => a is AssignmentStatementAst, true));
    }

    [Theory]
    [InlineData("echo \"${u:-x)}\"")]
    [InlineData("echo \"${u:-x\u2019)+@(Write-Output SENTINEL)+@(\u2019}\"")]
    [InlineData("declare -A m=([k]=v); echo \"${m['x\u2019)+@(Write-Output SENTINEL)+@(\u2019']}\" ok")]
    [InlineData("u=1; echo \"${u:+a)b}\" \"${v:=c)d}\" \"${w-(e}\"")]
    [InlineData("x=abc; echo \"${x/b/)}\" \"${x#a)}\" \"${x:-\\\")}\"")]
    [InlineData("echo \"pre${u:-\\`x}post\"")]
    // A glob fragment (the parser reads `+(`/`@(` as an extglob even in a quoted ${} word) went into the string raw.
    [InlineData("echo \"${u:-+(\u201C) + (Write-Output SENTINEL) + (\u201C)}\"")]
    [InlineData("echo \"${u:-@(\u2019) + (Write-Output SENTINEL) + (\u2019)}\"")]
    [InlineData("echo \"${u:-+(\\\") + (Write-Output SENTINEL) + (\\\")}\"")]
    [InlineData("y=\"${u:-a\u201C)+(Write-Output SENTINEL)+(\u201Cb}\"; echo \"${u:+a\u201D)+(Write-Output SENTINEL)+(\u201Db}\"")]
    [InlineData("shopt -s extglob\necho +(a\u201Cb|c) @(x\u2019y) a*\u201Cb ?\u201D")]
    public void Transpile_HostileTextInABracedExpansion_StaysData(string bash)
        => AssertDataStaysData(bash);

    // ── 3. Corpus fuzz: every syntactic position × every hostile character class ────────────────────────────

    /// <summary>How the payload is written into a context so it stays bash data.</summary>
    private enum Kind
    {
        Bare,        // every non-alphanumeric ASCII character backslash-escaped
        Raw,         // as is (only payloads with no bash metacharacter)
        Single,      // inside '...'
        Double,      // inside "..."
        DoubleBrace, // inside "${u:-...}" (an unbalanced ' is a bash syntax error there)
        AnsiC,       // inside $'...'
        HereDoc,     // an unquoted here-document body line
        QuotedHereDoc,
    }

    private static readonly (Kind Kind, string Context)[] Contexts =
    [
        (Kind.Bare, "echo X"), (Kind.Bare, "echo pre X post"), (Kind.Bare, "printf '%s\\n' X"), (Kind.Bare, "x=X"),
        (Kind.Bare, "x=X; echo \"$x\""), (Kind.Bare, "export E=X"), (Kind.Bare, "E=X true"), (Kind.Bare, "a=(X X)"),
        (Kind.Bare, "for c in X; do echo \"$c\"; done"), (Kind.Bare, "case X in X) echo m;; esac"),
        (Kind.Bare, "[[ X == X ]]"), (Kind.Bare, "test X = X"), (Kind.Bare, "[ X ]"), (Kind.Bare, "echo X > o"),
        (Kind.Bare, "echo ${u:-X}"), (Kind.Bare, "y=${u:-X}"), (Kind.Bare, "echo ${u:+X} ${u-X} ${u:=X}"),
        (Kind.Bare, "f() { echo X; }; f X"), (Kind.Bare, "echo X | grep X"), (Kind.Bare, "cd X"),
        (Kind.Bare, "echo a{b,X}"), (Kind.Bare, "x=a; echo ${x/a/X} ${x#X}"), (Kind.Bare, "declare -A m=([X]=X)"),
        (Kind.Bare, "read -r v <<< X"), (Kind.Bare, "echo X 2>/dev/null"), (Kind.Bare, "command echo X"),
        (Kind.Bare, "if true; then echo X; fi"), (Kind.Bare, "while false; do echo X; done"),
        (Kind.Bare, "echo $(echo X)"), (Kind.Bare, "echo \"$(echo X)\""), (Kind.Bare, "( echo X )"),
        (Kind.Bare, "{ echo X; }"), (Kind.Bare, "printf -v pv '%s' X"), (Kind.Bare, "echo X$v"), (Kind.Bare, "echo $v X"),
        (Kind.Bare, "echo \"$v\"X"), (Kind.Bare, "nohup echo X"), (Kind.Bare, "env E=X printenv E"),
        (Kind.Bare, "echo X | while read -r l; do echo \"$l\"; done"), (Kind.Bare, "local_f() { local l=X; }"),
        (Kind.Bare, "echo X &"), (Kind.Bare, "c=echo; $c X"), (Kind.Bare, "set -- X; echo \"$1\""),
        (Kind.Raw, "echo X"), (Kind.Raw, "echo ~X"), (Kind.Raw, "echo ~X/b"), (Kind.Raw, "cd ~X"), (Kind.Raw, "x=X"),
        (Kind.Raw, "for c in X; do :; done"), (Kind.Raw, "echo aX"), (Kind.Raw, "X"),
        (Kind.Single, "echo 'X'"), (Kind.Single, "x='X'"), (Kind.Single, "echo pre'X'post"), (Kind.Single, "a=('X')"),
        (Kind.Single, "for c in 'X'; do :; done"), (Kind.Single, "case 'X' in 'X') :;; esac"),
        (Kind.Single, "echo ${u:-'X'}"), (Kind.Single, "[[ 'X' == 'X' ]]"), (Kind.Single, "declare -A m=(['X']=1)"),
        (Kind.Single, "c='X'; \"$c\" 2>/dev/null"), (Kind.Single, "w='X'; $w 2>/dev/null"),
        (Kind.Double, "echo \"X\""), (Kind.Double, "x=\"X\""), (Kind.Double, "echo \"X$v\""), (Kind.Double, "echo \"$v X\""),
        (Kind.Double, "echo \"$(echo \"X\")\""), (Kind.Double, "[[ \"X\" == \"X\" ]]"),
        (Kind.Double, "case \"X\" in \"X\") :;; esac"), (Kind.Double, "declare -A m=([\"X\"]=1); echo \"${m[\"X\"]}\""),
        (Kind.Double, "cat <<< \"X\""), (Kind.Double, "test \"X\" = x"),
        (Kind.DoubleBrace, "echo \"${u:-X}\""), (Kind.DoubleBrace, "y=\"${u:-X}\""), (Kind.DoubleBrace, "echo \"${u:+X}\""),
        (Kind.DoubleBrace, "echo \"pre${u:-X}post\""), (Kind.DoubleBrace, "x=a; echo \"${x/a/X}\" \"${x//a/X}\""),
        (Kind.AnsiC, "echo $'X'"), (Kind.AnsiC, "x=$'X'"),
        (Kind.HereDoc, "cat <<EOF\nX\nEOF"), (Kind.HereDoc, "cat <<EOF\npre $v X\nEOF"), (Kind.HereDoc, "cat <<-EOF\n\tX\nEOF"),
        (Kind.QuotedHereDoc, "cat <<'EOF'\nX\nEOF"),
    ];

    private static readonly string[] QuoteMarks = ["\u2018", "\u2019", "\u201A", "\u201B", "\u201C", "\u201D", "\u201E", "'", "\""];
    private static readonly string[] PsWhitespace =
        ["\u000B", "\u000C", "\u0085", "\u00A0", "\u1680", "\u2000", "\u2007", "\u200A", "\u2028", "\u2029", "\u202F", "\u205F", "\u3000"];

    /// <summary>Each payload is shaped to end a PowerShell string, statement or comment and run the sentinel.</summary>
    private static IEnumerable<string> Payloads()
    {
        foreach (var m in QuoteMarks)
        {
            yield return "a" + m + "; Write-Output " + Sentinel + "; " + m + "b";
            yield return "a" + m + ")+(Write-Output " + Sentinel + ")+(" + m + "b";
        }
        foreach (var w in PsWhitespace)
        {
            yield return "a" + w + "Write-Output" + w + Sentinel;
            yield return "a" + w + ";" + w + "Write-Output" + w + Sentinel;
            yield return "a" + w + "#c" + w + "@args" + w + "--%";
        }
        yield return "a\r; Write-Output " + Sentinel;
        yield return "a`\rWrite-Output " + Sentinel;
        yield return "a\nWrite-Output " + Sentinel;
        yield return "$^ $: $\u00E9 $(Write-Output " + Sentinel + ") ${Write-Output}";
        yield return "a); Write-Output " + Sentinel + "; (b";
        yield return "a)+@(Write-Output " + Sentinel + ")+@(b";
        yield return "#> ; Write-Output " + Sentinel + "; <#";
        yield return "<#x#>Write-Output " + Sentinel;
        yield return "@(Write-Output " + Sentinel + ") @{a=1} @x";
        yield return "x\n'@\nWrite-Output " + Sentinel + "\n@'\ny";
        yield return "x\n\"@\nWrite-Output " + Sentinel + "\n@\"\ny";
        yield return "{Write-Output " + Sentinel + "}; Write-Output " + Sentinel + "; {";
        yield return "a`; Write-Output " + Sentinel + "; `$(Write-Output " + Sentinel + ")";
        yield return "--% ; Write-Output " + Sentinel;
        yield return "\u2013; Write-Output " + Sentinel;
        // No bash metacharacter: these also fill the Raw slots (a command name, a `~user`, a bare word).
        foreach (var m in QuoteMarks.Where(m => m[0] > '\u007F')) yield return "x" + m + "y" + m;
        foreach (var w in PsWhitespace) yield return "x" + w + "y";
        yield return "\u2013Write\u2014Output";
    }

    private const string BashMeta = " \t\n;&|()<>'\"\\`$*?[]{}#~=!%^,";

    /// <summary><paramref name="payload"/> written so bash reads it as exactly that data in a <paramref name="kind"/>
    /// slot, or null when the slot cannot hold it (a newline in a bareword, an unbalanced ' in "${...}", an EOF line).</summary>
    private static string? Encode(Kind kind, string payload)
    {
        var sb = new StringBuilder();
        switch (kind)
        {
            case Kind.Bare:
                if (payload.Contains('\n')) return null;
                foreach (char c in payload)
                {
                    if (c < 0x80 && !char.IsAsciiLetterOrDigit(c)) sb.Append('\\');
                    sb.Append(c);
                }
                return sb.ToString();
            case Kind.Raw:
                return payload.Any(c => BashMeta.Contains(c) || c == '\r' || c == '@' || c == '-' || c == '+') ? null : payload;
            case Kind.Single:
                return payload.Replace("'", "'\\''");
            case Kind.Double:
            case Kind.DoubleBrace:
                if (kind == Kind.DoubleBrace && payload.Contains('\'')) return null;
                foreach (char c in payload)
                {
                    if (c is '"' or '\\' or '$' or '`' || (kind == Kind.DoubleBrace && c == '}')) sb.Append('\\');
                    sb.Append(c);
                }
                return sb.ToString();
            case Kind.AnsiC:
                foreach (char c in payload)
                {
                    if (c is '\\' or '\'') sb.Append('\\');
                    sb.Append(c);
                }
                return sb.ToString();
            case Kind.HereDoc:
            case Kind.QuotedHereDoc:
                if (payload.Split('\n').Any(l => l.Trim() == "EOF")) return null;
                if (kind == Kind.QuotedHereDoc) return payload;
                foreach (char c in payload)
                {
                    if (c is '\\' or '$' or '`') sb.Append('\\');
                    sb.Append(c);
                }
                return sb.ToString();
        }
        return null;
    }

    [Fact]
    public void EveryPosition_EveryHostileClass_EmitsParseablePowerShell_AndTheSentinelNeverRuns()
    {
        var failures = new List<string>();
        var rejected = new List<string>();
        int cases = 0;
        foreach (var (kind, context) in Contexts)
        {
            foreach (var payload in Payloads())
            {
                if (Encode(kind, payload) is not { } encoded) continue;
                string bash = context.Replace("X", encoded);
                cases++;
                if (DataLeak(bash) is not { } why) continue;
                (why.StartsWith(BashParseError, StringComparison.Ordinal) ? rejected : failures).Add($"{why}\n    bash: {Show(bash)}");
            }
        }
        Assert.True(cases > 2000, $"corpus shrank to {cases} cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases leaked:\n" + string.Join("\n", failures.Take(40)));
        // A clean bash parse error runs nothing, but the corpus is valid bash: a known parser gap, ratcheted. `\}` and a
        // nested `{` in a double-quoted `${u:-...}` / `${x/a/...}` word (bash 5.2 accepts `"${u:-{W\}; {}"`).
        Assert.True(rejected.Count <= KnownParserGaps,
            $"{rejected.Count} valid-bash cases are parse errors (ratchet {KnownParserGaps}):\n" + string.Join("\n", rejected.Take(40)));
    }

    private const int KnownParserGaps = 15;
    private const string BashParseError = "bash parse error";

    /// <summary>Why <paramref name="bash"/>'s emission lets data act as code, or null when it does not.</summary>
    private static string? DataLeak(string bash)
    {
        string ps;
        try { ps = BashTranspiler.Transpile(bash); }
        catch (ParseException ex) { return BashParseError + " (the corpus is valid bash): " + ex.Message; }
        catch (Exception ex) { return "transpiler crash: " + ex.GetType().Name + ": " + ex.Message; }
        var ast = Parser.ParseInput(ps, out _, out var errors);
        if (errors.Length != 0) return "emitted PowerShell does not parse: " + errors[0].Message + "\n    ps: " + Show(ps);
        // The emitter's own code may call Write-Output (`cd` prints $PWD); the payload's call names the sentinel.
        if (ast.Find(a => a is CommandAst c && c.GetCommandName() is "Write-Output"
                && c.CommandElements.Any(e => e is StringConstantExpressionAst { Value: Sentinel }), true) is { } cmd)
            return "the payload ran as a command: " + Show(cmd.Extent.Text);
        if (ast.Find(a => a is VariableExpressionAst v && v.VariablePath.UserPath is "^" or ":" or "\u00E9" or "Write-Output", true) is { } read)
            return "the payload read a PowerShell variable: " + read.Extent.Text;
        return null;
    }

    private static void AssertDataStaysData(string bash)
    {
        if (DataLeak(bash) is { } why) Assert.Fail(why + "\n    bash: " + Show(bash));
    }

    private static ScriptBlockAst ParseEmitted(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        var ast = Parser.ParseInput(ps, out _, out var errors);
        Assert.True(errors.Length == 0, $"`{Show(ps)}` does not parse: {string.Join("; ", errors.Select(e => e.Message))}");
        return ast;
    }

    private static string Show(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(c is < ' ' or > '~' ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString());
        return sb.ToString();
    }

    // ── 4. Source guards ──────────────────────────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static IEnumerable<(string File, int Line, string Text)> Lines(string relativeDir, string pattern, Func<string, bool>? include = null)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDir), pattern, SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep) || file.Contains(".Tests" + sep)) continue;
            if (include is not null && !include(Path.GetFileName(file))) continue;
            int n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)) continue;
                yield return (Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'), n, line.Trim());
            }
        }
    }

    // (a) The module and the host's .ps1 resources: code is never built from values. Every place that turns a string
    // into code is listed here with why its string is constant code, a PsBuild-made literal, or the user's own command.
    private static readonly (string Fragment, string Why)[] ReviewedScriptCompiles =
    [
        ("[scriptblock]::Create($arrExpr)", "PsBuild.ShellArrayExpr: constant code per array name"),
        ("[scriptblock]::Create([PsBash.Core.Parser.PsBuild]::ShellOptsExpr)", "constant PsBuild code"),
        ("[scriptblock]::Create('$__bw = @(' + ($pre -join ', ')", "words via PsBuild.SingleQuote; LoopMode is [int]"),
        ("[scriptblock]::Create([PsBash.Core.Parser.PsBuild, PsBash.Transpiler]::BuiltinAsSourceScript($Name))", "constant per builtin"),
        ("[scriptblock]::Create($__lc)", "PsBuild.RunTimeLoopControl / LoopModeSet: constant code"),
        ("$Command = [scriptblock]::Create($__bgPrefix", "names and values via PsBuild.SingleQuote"),
        ("Invoke-Expression $BrowseCommand", "the browse TUI's own PowerShell command, typed by the user as PowerShell"),
    ];

    private static readonly Regex ScriptCompile = new(
        @"\[scriptblock\]::Create\(|Invoke-Expression|\biex\b|\.AddScript\(|NewScriptBlock\(|ExpandString\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Re-derived PowerShell quoting: doubling only the ASCII ' (U+2018-U+201B also end a PowerShell string).
    private static readonly Regex AdHocQuoteDoubling = new(
        @"-replace\s*(""'""|'''')\s*,\s*(""''""|'''''')|\.Replace\(\s*""'""\s*,\s*""''""\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void ModuleScripts_NeverBuildCodeFromValues()
    {
        var offenders = new List<string>();
        foreach (var (file, line, text) in Lines("src", "*.ps*1", f => f.EndsWith(".psm1", StringComparison.Ordinal) || f.EndsWith(".ps1", StringComparison.Ordinal)))
        {
            if (AdHocQuoteDoubling.IsMatch(text))
                offenders.Add($"{file}:{line}: ad-hoc quoting (use [PsBash.Core.Parser.PsBuild, PsBash.Transpiler]::SingleQuote): {text}");
            if (ScriptCompile.IsMatch(text) && !ReviewedScriptCompiles.Any(r => text.Contains(r.Fragment, StringComparison.Ordinal)))
                offenders.Add($"{file}:{line}: a string becomes code here; pass values as arguments ($args) or quote them with PsBuild.SingleQuote, then review it into ReviewedScriptCompiles: {text}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // (b) The emitter: raw AST text (a literal's Value, a tilde's User, an expansion's Suffix / Pattern) never reaches
    // emitted PowerShell through C# interpolation or concatenation; it goes through PsBuild.
    private static readonly Regex RawDataInterpolation = new(
        @"\$@?""[^""]*\{[A-Za-z_]\w*\.(Value|User|Suffix|Pattern|Raw|Text)\}", RegexOptions.Compiled);

    private static readonly Regex RawDataConcatenation = new(
        @"""\s*\+\s*[a-z]\w*\.(Value|User|Suffix|Pattern|Raw|Text)\b|\b[a-z]\w*\.(Value|User|Suffix|Pattern|Raw|Text)\s*\+\s*""",
        RegexOptions.Compiled);

    private static readonly (string Fragment, string Why)[] ReviewedConcatenations =
    [
        ("throw new ParseException(\"alias \" + t.Value", "a diagnostic, not PowerShell"),
        ("PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.Escape(\"~\" + ts.User))", "through PsBuild.SingleQuote"),
        ("\"\\\"${\" + b.Name + b.Suffix + \"}\\\"\"", "bash text for a message, then PsBuild.SingleQuote"),
        ("new WordPart.Literal(\"~\" + ts.User", "rebuilds an AST part, not PowerShell"),
        ("WordPart.SingleQuoted sq when used > 0 => \"'\" + sq.Value + \"'\"", "rebuilds bash text, not PowerShell"),
        ("PsBuild.EscapeBareword(\"~\" + ts.User", "through PsBuild.EscapeBareword"),
        ("EscapeGlobLiteral(\"~\" + ts.User)", "a glob literal, then quoted through PsBuild"),
        ("BashQuoteText]::Transform('\" + bvs.Suffix[1]", "Suffix matched ^@[AKk]$: one letter of three"),
    ];

    [Fact]
    public void Emitter_NeverInterpolatesRawBashTextIntoPowerShell()
    {
        var offenders = new List<string>();
        foreach (var (file, line, text) in Lines(Path.Combine("src", "PsBash.Transpiler", "Parser"), "*.cs",
                     f => f.StartsWith("PsEmitter", StringComparison.Ordinal) || f is "FusedLane.cs" or "StdinReaders.cs"))
        {
            if (RawDataInterpolation.IsMatch(text))
                offenders.Add($"{file}:{line}: raw bash text in an interpolated string: {text}");
            if (RawDataConcatenation.IsMatch(text) && !ReviewedConcatenations.Any(r => text.Contains(r.Fragment, StringComparison.Ordinal)))
                offenders.Add($"{file}:{line}: raw bash text concatenated into a string (quote it through PsBuild, or review it in): {text}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // A C# string that becomes PowerShell code built inline from a value: constant code, a PsBuild literal, or emitted
    // PowerShell only.
    private static readonly Regex InlineBuiltScript = new(
        @"(ScriptBlock\.Create|\.AddScript|NewScriptBlock|InvokeScript)\(\s*(\$""|""[^""]*""\s*\+|[A-Za-z_]\w*\s*\+)", RegexOptions.Compiled);

    private static readonly (string Fragment, string Why)[] ReviewedInlineScripts =
    [
        ("ScriptBlock.Create(asSource + (stdin.Count > 0 ? \"$input | \" : \"\") + \". $__psbash_cr @__psbash_ca\" + capture)", "constant code; the command is $args"),
        ("InvokeScript(\"param($v) $global:\" + StoreVar + \" = $v\", created)", "StoreVar is a const name; the value is an argument"),
        ("ScriptBlock.Create(\". \" + PsBash.Core.Parser.PsBuild.SingleQuote(resolvedPath))", "through PsBuild.SingleQuote"),
        ("ps.AddScript(\"param($d) Set-Location -LiteralPath $d; \" + Script)", "Script is emitted PowerShell; the cwd is an argument"),
        ("InvokeScript(\"exit \" + status.ToString(System.Globalization.CultureInfo.InvariantCulture))", "an int"),
    ];

    [Fact]
    public void ProductCode_BuildsScriptsOnlyFromConstantsAndPsBuildLiterals()
    {
        var offenders = new List<string>();
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core", "PsBash.Transpiler", "PsBash.Shell" })
            foreach (var (file, line, text) in Lines(Path.Combine("src", project), "*.cs"))
                if (InlineBuiltScript.IsMatch(text) && !ReviewedInlineScripts.Any(r => text.Contains(r.Fragment, StringComparison.Ordinal)))
                    offenders.Add($"{file}:{line}: {text}");
        Assert.True(offenders.Count == 0,
            "A script is built inline from a value: pass the value as an argument ($args) or quote it with PsBuild, then review it in:\n"
            + string.Join("\n", offenders));
    }

    // Text the bash parser rejected runs as PowerShell only through PowerShellFallback (the launcher and flock -c).
    // REGRESSION: flock's `catch { return bash; }` ran any unparseable -c string as PowerShell.
    private static readonly Regex RawFallback = new(
        @"catch\s*(\([^)]*\))?\s*\{\s*return\s+[A-Za-z_]\w*\s*;\s*\}", RegexOptions.Compiled);

    [Fact]
    public void ParseFailureFallsBackToPowerShellOnlyThroughOnePolicy()
    {
        var offenders = new List<string>();
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core", "PsBash.Transpiler", "PsBash.Shell" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep)) continue;
                string text = File.ReadAllText(file);
                string rel = Path.GetRelativePath(RepoRoot(), file);
                if (Path.GetFileName(file) != "PowerShellFallback.cs" && text.Contains("LooksLikePowerShell(", StringComparison.Ordinal))
                    offenders.Add($"{rel}: re-derives the PowerShell-fallback heuristic (use PowerShellFallback.Allows)");
                if (!text.Contains("ParseException", StringComparison.Ordinal)) continue;
                foreach (Match m in RawFallback.Matches(text))
                {
                    // A bare `catch { return x; }` near a transpile call: the bash text itself returned as the script.
                    int from = Math.Max(0, m.Index - 400);
                    if (text.AsSpan(from, m.Index - from).IndexOf("Transpile(", StringComparison.Ordinal) >= 0)
                        offenders.Add($"{rel}: `{m.Value}` after a Transpile call runs unparseable bash as PowerShell");
                }
            }
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("echo PWNED )", false)]
    [InlineData("Write-Output PWNED; fi", true)]
    [InlineData("if (", false)]
    public void PowerShellFallback_OnlyForTextThatLooksLikePowerShell(string text, bool allowed)
    {
        var error = Assert.Throws<ParseException>(() => BashTranspiler.Transpile(text));
        Assert.Equal(allowed && Environment.GetEnvironmentVariable(PowerShellFallback.OptOutVariable) is null,
            PowerShellFallback.Allows(text, error));
    }
}
