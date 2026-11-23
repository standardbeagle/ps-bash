namespace PsBash.Core.Parser;

/// <summary>
/// The PowerShell source builder: the ONE place that knows how to construct the
/// recurring PowerShell-text fragments the emitter needs. Every fragment that has
/// bitten us before — quoting/escaping at the bash↔PowerShell seam, the exit-code
/// test wrapper, output suppression, scriptblock isolation, RC-7 word-split
/// splatting, the null-safe pipeline probe — lives here as a named, tested
/// primitive instead of being hand-concatenated at each call site.
///
/// <para>
/// WHY THIS EXISTS. Hand-built PS strings drifted: the negated-pipeline condition
/// omitted the <c>[void]</c> that the general-command condition had (so
/// <c>if ! echo X</c> captured <c>X</c> into a 2-element array and took the wrong
/// branch), one double-quote literal branch forgot to escape backticks while its
/// twin did, and the exit-code scope flipped between bare <c>$LASTEXITCODE</c> and
/// <c>$global:LASTEXITCODE</c> between sites. Centralizing kills the whole class:
/// fix once here, every call site is correct, and the escaping is unit-tested
/// against the failure axes (embedded quote / backtick / <c>$</c>).
/// </para>
///
/// <para>
/// CONVENTIONS. (1) Exit-code scope is ALWAYS <c>$global:LASTEXITCODE</c> — the
/// automatic <c>$LASTEXITCODE</c> reads the same value, but a scriptblock that
/// ASSIGNS it must target global, so we use the explicit form everywhere for one
/// consistent shape. (2) Any command whose output must not pollute a boolean is
/// wrapped in <see cref="Void"/>. (3) Double-quote escaping is backtick-FIRST
/// (it is the escape char that the <c>$</c>/<c>"</c> replacements introduce).
/// </para>
/// </summary>
public static class PsBuild
{
    // ─────────────────────────────── Portable /tmp ───────────────────────────────

    /// <summary>
    /// Runtime-evaluated temp directory for a bash <c>/tmp</c> operand. The transpiled text
    /// may run on any OS: Windows has no <c>/tmp</c> (map to <c>$env:TEMP</c>), while on
    /// Linux/macOS <c>$env:TEMP</c> is usually unset (keep the literal <c>/tmp</c>).
    /// <c>$env:OS</c> is <c>Windows_NT</c> only on Windows.
    /// </summary>
    public const string TempDirExpr = "$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')";

    /// <summary>
    /// Word form of <c>/tmp/REST</c>: <c>"TempDirExpr/REST"</c>. Double-quoted because a bare
    /// <c>$(…)/rest</c> is two arguments in PowerShell. <paramref name="rest"/> is emitted word
    /// text and must already be safe inside double quotes (no bare <c>"</c>); a REST containing
    /// one falls back to the Windows-only legacy form.
    /// </summary>
    public static string TempPath(string rest)
        => rest.Contains('"')
            ? "$env:TEMP\\" + rest
            : "\"" + TempDirExpr + "/" + rest + "\"";

    /// <summary>
    /// <paramref name="valueExpr"/> (a PowerShell VALUE expression) through the runtime path policy:
    /// <c>[PsBash.Cmdlets.BashRuntime]::MapPath([string](expr))</c> — <c>/tmp</c> → <c>$env:TEMP</c> and
    /// unix drive paths on Windows, unchanged elsewhere (PsBash.Core.RuntimePath). For paths only
    /// known at run time (<c>cd $d</c>, <c>[ -e "$d/f" ]</c>); the Cmdlets static, not the Transpiler
    /// type, so it resolves in any runspace that has the cmdlets loaded.
    /// </summary>
    public static string RuntimeMapPath(string valueExpr)
        => "[PsBash.Cmdlets.BashRuntime]::MapPath([string](" + valueExpr + "))";

    /// <summary>
    /// The OUTBOUND twin of <see cref="RuntimeMapPath"/>: <paramref name="valueExpr"/> (a filesystem path)
    /// as a bash script sees it, <c>[PsBash.Cmdlets.BashRuntime]::ToShellPath([string](expr))</c> —
    /// <c>C:\x</c> → <c>C:/x</c> on Windows, unchanged elsewhere (PsBash.Core.BashPath). For values only
    /// known at run time: <c>$PWD</c> / <c>$OLDPWD</c> after a move, the temp directory in a glob pattern.
    /// </summary>
    public static string RuntimeToShellPath(string valueExpr)
        => "[PsBash.Cmdlets.BashRuntime]::ToShellPath([string](" + valueExpr + "))";

    /// <summary>
    /// A path word whose value is only known at run time (<c>2&gt; $dir/f</c>), mapped through the
    /// runtime path policy (<see cref="RuntimeMapPath"/>) when it is evaluated — the run-time twin of
    /// the emitter's literal <c>/c/…</c> and <c>/tmp/</c> rewrites. <paramref name="argWord"/> is
    /// emitted ARGUMENT-mode text (<c>$env:d/f</c>, <c>"$env:d/f"</c>), so it is passed as an
    /// argument to a script block, never spliced into an expression (where <c>/</c> divides).
    /// </summary>
    public static string RuntimeWindowsPath(string argWord)
        => "$(& { " + RuntimeMapPath("$args[0]") + " } " + argWord + ")";
    // ─────────────────────────────── Quoting / escaping ───────────────────────────────

    /// <summary>The chars that must be escaped inside a PowerShell double-quoted string.</summary>
    // PowerShell's tokenizer ends a string at the Unicode quote marks too, not only at ' and ": U+2018-U+201B are
    // single quotes and U+201C-U+201E double quotes (CharExtensions.IsSingleQuote / IsDoubleQuote). Every escape below
    // covers the whole set; escaping only the ASCII mark let `echo 'it’s'` close the literal mid-word and run the rest
    // as code (spec emitter-strategy.md §Quote marks; guard PsQuoteMarkGuardTests).
    private const string SingleQuoteMarks = "'\u2018\u2019\u201A\u201B";
    private const string DoubleQuoteMarks = "\"\u201C\u201D\u201E";

    private static readonly System.Buffers.SearchValues<char> s_doubleQuoteSpecials =
        System.Buffers.SearchValues.Create("`$" + DoubleQuoteMarks);

    private static readonly System.Buffers.SearchValues<char> s_singleQuoteMarks =
        System.Buffers.SearchValues.Create(SingleQuoteMarks);

    // A token PowerShell starts with one of these is a -parameter, exactly like ASCII '-' (CharExtensions.IsDash):
    // bash `echo –n x` must print "–n x", not run `echo -n`. ASCII '-' itself stays bare: flags pass through as text.
    private const string DashMarks = "\u2013\u2014\u2015";

    /// <summary>
    /// True for a character PowerShell's tokenizer ends an unquoted word at (CharExtensions.IsWhitespace plus the
    /// newlines): ASCII space, tab, VT, FF, CR, LF, U+0085 (NEL), U+00A0 (NBSP) and every Unicode separator
    /// (<see cref="char.IsSeparator(char)"/>: U+1680, U+2000-U+200A, U+2028, U+2029, U+202F, U+205F, U+3000). bash splits
    /// a word only at space, tab and newline, so the rest are ordinary word characters to it: <c>a&#xA0;#c</c> is ONE bash
    /// word, which PowerShell read as <c>a</c> and a comment (the next line ran as code), and <c>a&#xA0;@x</c> splatted
    /// <c>$x</c>. Guard BashDataNeverCodeGuardTests (exhaustive over the BMP against the real tokenizer).
    /// </summary>
    public static bool IsPsWordBreak(char c)
        => c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u00A0' || (c > '\u00FF' && char.IsSeparator(c));

    /// <summary>
    /// True when bash LITERAL text spliced into an unquoted PowerShell word holds a character PowerShell reads as
    /// syntax there although bash does not: <c>{</c>/<c>}</c> (a scriptblock), a quote mark (<c>'</c>, <c>"</c>,
    /// U+2018-U+201E open a string), <c>$</c> (bash's <c>$^</c>, <c>$:</c>, <c>$é</c>, a trailing <c>$</c> are literal
    /// text; PowerShell read <c>$^</c> as its automatic variable and <c>$:</c> failed the whole script), a character
    /// PowerShell ends a word at (<see cref="IsPsWordBreak"/>), or, at the start of the word, a dash mark (U+2013-U+2015
    /// make a parameter) or <c>--%</c> (PowerShell's stop-parsing token, which turned the rest of the line into text).
    /// </summary>
    public static bool BarewordNeedsEscape(string text, bool atWordStart)
    {
        if (text.Length == 0) return false;
        if (atWordStart && (DashMarks.Contains(text[0]) || text.StartsWith("--%", StringComparison.Ordinal))) return true;
        foreach (char c in text)
            if (BarewordCharNeedsEscape(c)) return true;
        return false;
    }

    private static bool BarewordCharNeedsEscape(char c)
        => c is '{' or '}' or '$' || IsSingleQuoteMark(c) || IsDoubleQuoteMark(c) || IsPsWordBreak(c);

    /// <summary>
    /// <paramref name="text"/> (bash literal text) with each character <see cref="BarewordNeedsEscape"/> names escaped
    /// (<see cref="EscapeBarewordChar"/>), for splicing into an unquoted PowerShell word. The escapes mean the same
    /// inside a PowerShell double-quoted string, so the result is also valid there.
    /// </summary>
    public static string EscapeBareword(string text, bool atWordStart)
    {
        if (!BarewordNeedsEscape(text, atWordStart)) return text;
        var sb = new System.Text.StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (BarewordCharNeedsEscape(c)) sb.Append(EscapeBarewordChar(c));
            else if (i == 0 && atWordStart && (DashMarks.Contains(c) || text.StartsWith("--%", StringComparison.Ordinal))) sb.Append('`').Append(c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One bash-literal character as unquoted PowerShell word text that denotes exactly it (also valid inside a
    /// PowerShell double-quoted string). An ASCII letter or digit stays bare (a backtick before one is a PowerShell escape:
    /// <c>`n</c>, <c>`a</c>, <c>`0</c>). CR, LF, VT, FF and tab are their escape sequences: a backtick before a raw CR or LF
    /// is PowerShell's line CONTINUATION, so bash's escaped CR (<c>a\&lt;CR&gt;</c>) glued the next line's emitted code
    /// into the word. A non-ASCII word break (<see cref="IsPsWordBreak"/>) is <c>`u{XXXX}</c>. Anything else gets a backtick.
    /// </summary>
    public static string EscapeBarewordChar(char c) => c switch
    {
        '\r' => "`r",
        '\n' => "`n",
        '\t' => "`t",
        '\v' => "`v",
        '\f' => "`f",
        _ when char.IsAsciiLetterOrDigit(c) => c.ToString(),
        _ when c > '\u007F' && IsPsWordBreak(c) => "`u{" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + "}",
        _ => "`" + c,
    };

    /// <summary>A bash ESCAPED literal (<c>\x</c>: every character literal) as unquoted PowerShell word text, each character
    /// through <see cref="EscapeBarewordChar"/>.</summary>
    public static string EscapeBarewordLiteral(string text)
    {
        if (text.Length == 1) return EscapeBarewordChar(text[0]);
        var sb = new System.Text.StringBuilder(text.Length * 2);
        foreach (char c in text) sb.Append(EscapeBarewordChar(c));
        return sb.ToString();
    }

    /// <summary>
    /// True when bash text that the emitter would render as a string literal NESTED inside the <c>$( )</c> of a
    /// PowerShell double-quoted string holds a character that scan misreads: PowerShell finds the end of an in-string
    /// <c>$( )</c> by counting parens without honouring the quotes inside it, so a <c>(</c> or <c>)</c> in the nested
    /// literal moved the end (<c>"$($env:u ? $env:u : 'x)')"</c> failed the whole script), a quote mark left a string
    /// open, a backtick escaped the closing mark, and a double-quote mark is rewritten even inside a nested literal.
    /// Such a value is emitted outside the string (the emitter's join form), never inside it.
    /// </summary>
    public static bool HostileInStringSubexpression(string? text)
    {
        if (text is null) return false;
        foreach (char c in text)
            if (c is '(' or ')' or '`' || IsSingleQuoteMark(c) || IsDoubleQuoteMark(c)) return true;
        return false;
    }
    /// <summary>
    /// <paramref name="emitted"/> (a whole unquoted PowerShell COMMAND ARGUMENT) single-quoted when PowerShell would read
    /// it as a numeric literal that prints differently (<c>1e5</c> → 100000, <c>0x10</c> → 16, <c>08</c> → 8,
    /// <c>1kb</c> → 1024); otherwise unchanged. A canonical integer (<c>5</c>, <c>-3</c>) stays bare. The rule is
    /// <see cref="PsNumberLiteral"/>; spec emitter-strategy.md §Number-shaped words.
    /// </summary>
    public static string QuoteArgumentNumber(string emitted)
        => PsNumberLiteral.IsArgumentNumber(emitted) && !PsNumberLiteral.IsCanonicalInteger(emitted)
            ? SingleQuote(emitted)
            : emitted;

    /// <summary>
    /// True when bash literal <paramref name="text"/> may stand BARE as a PowerShell EXPRESSION value (an array element,
    /// a for-list item, a test operand) and still mean the same text: only a canonical integer
    /// (<see cref="PsNumberLiteral.IsCanonicalInteger"/>). Every other literal there is single-quoted: <c>+5</c>,
    /// <c>08</c>, <c>1.</c>, <c>1e5</c> are numbers in expression mode and <c>1.2.3</c> is a number and a member access.
    /// </summary>
    public static bool IsBareIntegerValue(string text) => PsNumberLiteral.IsCanonicalInteger(text);

    /// <summary>
    /// True when emitted command-word text, standing first in a PowerShell statement, is an EXPRESSION, a value
    /// PowerShell prints instead of a name it runs: a quoted string (<c>"`nWrite-Output x"</c> from <c>$'\nWrite-Output x'</c>),
    /// a variable, a parenthesized expression. Such a word runs only after <c>&amp; </c>: then it is a command name, and
    /// one bash would not find is "command not found" (127). (A number-shaped name is quoted before it gets here.)
    /// </summary>
    public static bool CommandWordReadsAsExpression(string commandWord) =>
        commandWord.Length > 0
        && (commandWord[0] is '\'' or '"' or '$' or '('
            || IsSingleQuoteMark(commandWord[0]) || IsDoubleQuoteMark(commandWord[0]));

    /// <summary>True for a character that opens or closes a PowerShell single-quoted string.</summary>
    public static bool IsSingleQuoteMark(char c) => SingleQuoteMarks.Contains(c);

    /// <summary>True for a character that opens or closes a PowerShell double-quoted string.</summary>
    public static bool IsDoubleQuoteMark(char c) => DoubleQuoteMarks.Contains(c);

    /// <summary>
    /// Wrap <paramref name="value"/> in a PowerShell single-quoted literal, escaping
    /// embedded single quotes by doubling (<c>'</c> → <c>''</c>) — the only escape a
    /// PS single-quoted string honors. Use for literal paths, positional args, and any
    /// value that must reach PowerShell verbatim with no expansion.
    /// </summary>
    public static string SingleQuote(string value) => "'" + EscapeForSingleQuote(value) + "'";

    /// <summary>
    /// Escape <paramref name="value"/> for placement INSIDE a PowerShell single-quoted string (no surrounding quotes):
    /// every single-quote mark (<c>'</c> and U+2018-U+201B) is doubled, the one escape such a string honors. The
    /// common case (no mark) returns <paramref name="value"/> itself.
    /// </summary>
    public static string EscapeForSingleQuote(string value)
    {
        int at = value.AsSpan().IndexOfAny(s_singleQuoteMarks);
        if (at < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length + 4);
        sb.Append(value, 0, at);
        for (int i = at; i < value.Length; i++)
        {
            char c = value[i];
            if (IsSingleQuoteMark(c)) sb.Append(c);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>[[ lhs =~ word ]]</c>: <paramref name="lhs"/> and <paramref name="word"/> are PowerShell expressions, the word
    /// one built by <see cref="EreWord"/>. The match runs in <c>BashPatternMatch.EreTest</c> (THE POSIX ERE primitive,
    /// <see cref="BashEre"/>); status 2 (a regex regcomp rejects) marks the test bad and leaves BASH_REMATCH alone, as
    /// bash does, and otherwise BASH_REMATCH becomes the groups (an empty array after a failed match).
    /// </summary>
    public static string EreTest(string lhs, string word) =>
        "$( $__psbash_ere = [PsBash.Cmdlets.BashPatternMatch]::EreTest((" + lhs + "), (" + word + ")); "
        + "if ($__psbash_ere.Status -eq 2) { $global:__BashTestBad = $true } "
        + "else { $global:BASH_REMATCH = $__psbash_ere.Groups }; $__psbash_ere.Status -eq 0 )";

    /// <summary>The <c>=~</c> word from its pieces (<see cref="EreLiteral"/>, <see cref="EreQuoted"/>,
    /// <see cref="EreUnquoted"/>), in order.</summary>
    public static string EreWord(IReadOnlyList<string> pieces) =>
        pieces.Count == 0 ? "''" : pieces.Count == 1 ? pieces[0] : "(" + string.Join(" + ", pieces) + ")";

    /// <summary>Literal text of the <c>=~</c> word: QUOTED (matches literally) or unquoted (regex text).</summary>
    public static string EreLiteral(string text, bool quoted) =>
        quoted ? EreQuoted(SingleQuote(text))
        : text.Contains(BashEre.Ctlesc) ? EreUnquoted(SingleQuote(text))
        : SingleQuote(text);

    /// <summary>A quoted part of the <c>=~</c> word whose value is the PowerShell expression <paramref name="expr"/>.</summary>
    public static string EreQuoted(string expr) => "[PsBash.Cmdlets.BashPatternMatch]::EreQuote(" + expr + ")";

    /// <summary>An unquoted part (an expansion) of the <c>=~</c> word: its value is regex text.</summary>
    public static string EreUnquoted(string expr) => "[PsBash.Cmdlets.BashPatternMatch]::EreRaw(" + expr + ")";

    /// <summary>
    /// Escape <paramref name="value"/> for placement INSIDE a PowerShell double-quoted
    /// string (returns the inner text, no surrounding quotes): a backtick goes before every
    /// backtick (PowerShell's escape char), <c>$</c> (starts variable expansion) and
    /// double-quote mark (<c>"</c> and U+201C-U+201E end the string). Called once per literal string part during emission — the hottest
    /// builder — so the clean case is allocation-free: a single <see cref="System.Buffers.SearchValues{T}"/>
    /// scan returns the original string with no rebuild when nothing needs escaping
    /// (vs. three full Replace passes).
    /// </summary>
    public static string EscapeForDoubleQuote(string value)
    {
        int at = value.AsSpan().IndexOfAny(s_doubleQuoteSpecials);
        if (at < 0)
            return value;
        var sb = new System.Text.StringBuilder(value.Length + 4);
        sb.Append(value, 0, at);
        for (int i = at; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '`' or '$' || IsDoubleQuoteMark(c)) sb.Append('`');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wrap <paramref name="value"/> in a PowerShell double-quoted string with the
    /// inner text escaped via <see cref="EscapeForDoubleQuote"/>.
    /// </summary>
    public static string DoubleQuote(string value) => "\"" + EscapeForDoubleQuote(value) + "\"";

    /// <summary>
    /// Backtick-escape only the double-quote marks (<c>"</c>, U+201C-U+201E) of text that is already PowerShell
    /// double-quoted-string content whose <c>$</c> expansions must stay live (a heredoc body rendered as an ordinary
    /// string). Returns <paramref name="value"/> itself when it has none.
    /// </summary>
    public static string EscapeDoubleQuoteMarks(string value)
    {
        if (value.AsSpan().IndexOfAny(DoubleQuoteMarks) < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length + 4);
        foreach (char c in value)
        {
            if (IsDoubleQuoteMark(c)) sb.Append('`');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ─────────────────────────── Output suppression / wrapping ────────────────────────

    /// <summary>Suppress an expression's pipeline output: <c>[void](expr)</c>.</summary>
    public static string Void(string expr) => "[void](" + expr + ")";

    /// <summary>Isolate a body in a scriptblock invocation: <c>&amp; { body }</c>.</summary>
    public static string Subshell(string body) => "& { " + body + " }";

    /// <summary>
    /// A bash SUBSHELL scope: <c>&amp; { save; try { BODY } finally { restore } }</c>. The child scope
    /// isolates PS-scoped state (arrays, functions, <c>set -e</c>'s <c>$ErrorActionPreference</c>,
    /// <c>set -u</c>'s StrictMode); <c>PsBash.Cmdlets.BashShellState</c> saves and restores what a scope
    /// does not — env-var bash variables, the <c>$global:</c> shell flags/positionals/traps, <c>shopt</c>, the
    /// working directory and <c>pushd</c> stack — and reports a <c>set -x</c> to undo. Without it
    /// <c>( set -e; true ); false; echo ok</c> stopped the PARENT at <c>false</c>, and <c>( x=1 ); echo $x</c>
    /// printed 1. <c>$?</c> is deliberately not restored.
    /// The temp is per child scope, so nested subshells never share it.
    /// </summary>
    public static string ShellStateScope(string body) =>
        "& { " + ShellStateGuard(body) + " }";

    /// <summary>
    /// Statements that report a parameter error (<c>x: unbound variable</c>, <c>${x:?msg}</c>) on the
    /// ERROR STREAM where the failing command runs, then end the script with the typed exception. The
    /// report happens before the throw so an enclosing <c>2&gt; f</c> / <c>2&gt;/dev/null</c> applies to it
    /// (a thrown error escapes a group's redirect); the exception is marked reported so no catcher
    /// prints it again.
    /// </summary>
    public static string ParamErrorThrowStatements(string prefixLiteral, string messageExpr) =>
        "$__psbash_pe = [PsBash.Cmdlets.BashParamErrorException]::Reported(" + prefixLiteral + ", " + messageExpr + "); "
        + "Write-Error -Message (" + DiagnosticPrefix + " + $__psbash_pe.Message) -ErrorAction Continue; throw $__psbash_pe";

    /// <summary>
    /// Under <c>set -u</c>, an arithmetic expression that reads an unset name: <c>Invoke-BashArith</c>
    /// reports it on the error stream and leaves the name in <c>$global:__BashUnbound</c>; this ends the
    /// script (a cmdlet exception would only end that one statement). The value otherwise passes through.
    /// </summary>
    /// <summary>
    /// Statement placed after a hoisted word expansion: when the expansion FAILED (a bad <c>${!ref}</c>, an
    /// arithmetic error) the runtime sets <c>$global:__BashExpansionFailed</c>, and bash abandons the command
    /// (status 1, already set by the runtime) instead of running it with an empty word.
    /// </summary>
    public const string AbandonIfExpansionFailed =
        "if ($global:__BashExpansionFailed) { $global:__BashExpansionFailed = $false; return }; ";

    /// <summary>The runtime function behind <see cref="ExpansionError"/> (the emitter looks for it to abandon the command).</summary>
    public const string ExpansionErrorFunction = "Write-BashExpansionError";

    /// <summary>
    /// A word whose expansion fails without ending a script (a bad substitution): the runtime reports
    /// <c>prefix + message</c>, sets status 1 and the <c>$global:__BashExpansionFailed</c> mark that abandons the
    /// command (<see cref="AbandonIfExpansionFailed"/>), and yields nothing. Under <c>bash -c</c> it ends the shell
    /// as a parameter error does, since bash discards the rest of the command string.
    /// </summary>
    public static string ExpansionError(string prefixLiteral, string messageExpr) =>
        ExpansionErrorFunction + " " + ParenSafeLiteral(prefixLiteral) + " " + messageExpr;

    /// <summary>
    /// A single-quoted literal that may sit inside a <c>"$( … )"</c> string. PowerShell finds the end of that
    /// subexpression by counting parens WITHOUT honouring the quotes inside, so a literal holding an unbalanced
    /// <c>(</c> or <c>)</c> (`${ case x in x) …;}`) ended it early ("Missing closing ')'"). Such a literal becomes a
    /// parenthesised join with <c>[char]40</c>/<c>[char]41</c>; any other expression is returned unchanged.
    /// </summary>
    public static string ParenSafeLiteral(string expr)
    {
        if (expr.Length < 2 || expr[0] != '\'' || expr[^1] != '\'' || (expr.IndexOf('(') < 0 && expr.IndexOf(')') < 0))
            return expr;
        for (int i = 1; i < expr.Length - 1; i++)
            if (expr[i] == '\'' && expr[i + 1] != '\'') return expr;
            else if (expr[i] == '\'') i++;
        return "(" + expr.Replace("(", "' + [char]40 + '").Replace(")", "' + [char]41 + '") + ")";
    }

    /// <summary>
    /// One top-level command of a script, <c>-c</c> string, eval or sourced text whose expansions can discard the
    /// input line: while it runs the runtime throws a discard error (<c>BashParamErrorException.Discard</c>) for a bad
    /// substitution or an invalid indirect name instead of abandoning only one command. The catch ends the command
    /// with status 1 and sets the line mark, so the commands after it on its line (<see cref="UnlessGlobFailed"/>) are
    /// dropped too; the next line runs. A parameter error that is not a discard (unbound variable) passes on and ends
    /// the shell, except in an interactive one (<c>BashLineDiscard.Discards</c>).
    /// </summary>
    public static string DiscardLineScope(string statement) =>
        "try { [PsBash.Cmdlets.BashLineDiscard]::Enter(); " + statement
        + " } catch [PsBash.Cmdlets.BashParamErrorException] { if (-not [PsBash.Cmdlets.BashLineDiscard]::Discards($_.Exception)) { throw }; "
        + "$global:LASTEXITCODE = 1; $global:__BashFailGlob = $true } finally { [PsBash.Cmdlets.BashLineDiscard]::Leave() }";

    /// <summary>
    /// Emitted text that can raise a line discard (<see cref="DiscardLineScope"/>): an indirect expansion, a bad
    /// substitution, or an arithmetic WORD (<c>$((1/0))</c>, written <c>$(Invoke-BashArith ..)</c> or under nounset
    /// <c>$__psbash_av = Invoke-BashArith ..</c>: the forms the cmdlet's IsWordExpansion recognises; an indexed
    /// subscript <c>${a[1/0]}</c> is written the same way), or an element assignment whose subscript is not a plain
    /// integer (<c>a[1/0]=x</c>: Set-BashElement evaluates it as a word). The arithmetic command <c>(( 1/0 ))</c> is not
    /// a word: it fails with status 1 and the line goes on, as in bash. Also a builtin handed an element operand
    /// (<c>unset 'a[1/0]'</c>, <c>read</c>, <c>printf -v</c>, <c>test -v</c> / <c>[ -v ]</c>): it evaluates the subscript at
    /// run time, and one that is not an expression drops the line (<c>BashArrayElement.FailSubscript</c>).
    /// </summary>
    public static bool CanDiscardLine(string emitted) =>
        emitted.Contains(UsageDiscardMark, StringComparison.Ordinal)
        || emitted.Contains(AssignmentDiscardMark, StringComparison.Ordinal)
        || s_shiftExtraOperand.IsMatch(emitted)
        || emitted.Contains("Get-BashIndirectValue", StringComparison.Ordinal)
        || emitted.Contains(ExpansionErrorFunction, StringComparison.Ordinal)
        || emitted.Contains("$(Invoke-BashArith", StringComparison.Ordinal)
        || emitted.Contains("$__psbash_av = Invoke-BashArith", StringComparison.Ordinal)
        || (emitted.Contains("Set-BashElement ", StringComparison.Ordinal) && s_computedElementKey.IsMatch(emitted))
        || (s_elementOperandBuiltin.IsMatch(emitted) && s_elementOperand.IsMatch(emitted));

    /// <summary>
    /// Emitted text that can print a shell diagnostic naming the line (<c>t.sh: line 2: ro: readonly variable</c>), so the
    /// statement records its line first (<c>PsEmitter.LateLineRecord</c>): it builds the prefix
    /// (<see cref="DiagnosticPrefix"/>: <see cref="ShellDiagnosticLine"/>, a readonly or parameter error), can discard the
    /// line (<see cref="CanDiscardLine"/>), or expands a glob (failglob's <c>no match</c>). Quote-blind on purpose: a false
    /// hit only records a line.
    /// </summary>
    public static bool MayReportDiagnostic(string emitted) =>
        emitted.Contains(DiagnosticPrefix, StringComparison.Ordinal)
        || emitted.Contains("ConvertTo-BashGlob", StringComparison.Ordinal)
        || emitted.Contains("ConvertTo-BashWords", StringComparison.Ordinal)
        || CanDiscardLine(emitted);

    /// <summary><c>shift 1 2</c>: Invoke-BashShift reports "too many arguments" and drops the line (<c>BashLineDiscard.UsageError</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex s_shiftExtraOperand =
        new(@"\bInvoke-BashShift\s+[^\s;|}]+\s+[^\s;|}]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    /// <summary>A builtin that evaluates an element operand's subscript itself (<c>BashArrayElement.FailSubscript</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex s_elementOperandBuiltin =
        new(@"\bInvoke-Bash(?:Unset|Read|Printf|Test)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A word that starts <c>NAME[</c> (<c>'a[i]'</c>, <c>"m[$env:k]"</c>, a glob-hoisted <c>'a[i]'</c>). Quote-blind on
    /// purpose: a false hit only arms a line scope, which changes nothing unless a discard is raised.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_elementOperand =
        new(@"[\s'""][A-Za-z_][A-Za-z0-9_]*\[", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>Set-BashElement $a "key"</c> whose key may fail: not a non-negative integer literal (evaluated as arithmetic,
    /// or a negative index past the start, <c>b[-5]=x</c>: "bad array subscript", the rest of the line dropped).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_computedElementKey =
        new(@"Set-BashElement \$\S+ ""(?!(?:0|[1-9][0-9]*)"")", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>An assignment consumes the mark without abandoning anything: only its own status (1) records the failure.</summary>
    public const string ClearExpansionFailed = "$global:__BashExpansionFailed = $false";

    /// <summary>
    /// Runs <paramref name="statements"/> only when no word expanded before it failed; otherwise consumes the mark and
    /// runs nothing, keeping the status (1) the failure set. A compound command (`case $((1/0)) in`) is abandoned whole.
    /// </summary>
    public static string UnlessExpansionFailed(string statements) =>
        "if ($global:__BashExpansionFailed) { " + ClearExpansionFailed + " } else { " + statements + " }";

    /// <summary>Follows every bash write to PATH: on Windows the <c>:</c> separators a script wrote become <c>;</c>.</summary>
    public const string NormalizePathVariable = "[PsBash.Cmdlets.BashRuntime]::NormalizePathVariable()";

    // ─────────────────── the export attribute (PsBash.Core.ShellExports, via BashVariableStore) ───────────────────
    // Every scalar variable is `$env:NAME`, exported or not; these calls are what makes one exported. The only
    // text the emitter writes for the attribute (guard: ChildEnvironmentGuardTests).

    private static string NameList(IEnumerable<string> names) => string.Join(", ", names.Select(SingleQuote));

    /// <summary><c>export NAME...</c> / <c>declare -x NAME...</c>: the names get the export attribute.</summary>
    public static string MarkExported(IEnumerable<string> names)
        => "[PsBash.Cmdlets.BashVariableStore]::Export(" + NameList(names) + ")";

    /// <summary><see cref="MarkExported"/> for a name known only at run time (<paramref name="nameExpression"/> is PowerShell).</summary>
    public static string MarkExportedAtRunTime(string nameExpression)
        => "[PsBash.Cmdlets.BashVariableStore]::Export(" + nameExpression + ")";

    /// <summary><c>export -n NAME...</c> / <c>declare +x NAME...</c>: the names lose the export attribute.</summary>
    public static string MarkUnexported(IEnumerable<string> names)
        => "[PsBash.Cmdlets.BashVariableStore]::Unexport(" + NameList(names) + ")";

    /// <summary><c>set -a</c> / <c>set +a</c>.</summary>
    public static string SetAllExport(bool on)
        => "[PsBash.Cmdlets.BashVariableStore]::SetAllExport(" + (on ? "$true" : "$false") + ")";

    /// <summary>
    /// <c>NAME=v cmd</c>: the names are exported for the command. Goes BEFORE the binding's <c>try</c>; its
    /// <c>finally</c> starts with <see cref="TempExportEnd"/>.
    /// </summary>
    public static string TempExportBegin(IEnumerable<string> names)
        => "[PsBash.Cmdlets.BashVariableStore]::TempExport(" + NameList(names) + ")";

    /// <summary>Ends <see cref="TempExportBegin"/>: each name has the attribute it had before the command.</summary>
    public const string TempExportEnd = "[PsBash.Cmdlets.BashVariableStore]::EndTempExport()";

    /// <summary>
    /// The 64-bit value of an <c>Invoke-BashArith</c> invocation used as a number (a slice bound): written as a WORD
    /// (<c>$(</c> right before it, InvokeBashArithmeticCommand.IsWordExpansion), so a failed operand abandons the command
    /// as <c>$(( 08 ))</c> does. A failed evaluation yields no value, which casts to 0.
    /// </summary>
    public static string ArithValue(string invocation) => "[long]$(" + invocation + ")";

    /// <summary>
    /// The run-time twin of <c>BashArithmeticParser.TryPlainDecimal</c>: the number when <paramref name="textExpr"/> is a
    /// canonical decimal, else <c>$null</c> (the caller then hands the text to <c>Invoke-BashArith</c>).
    /// </summary>
    public static string ArithPlainDecimal(string textExpr)
        => "[PsBash.Core.Parser.BashArithmeticParser, PsBash.Transpiler]::PlainDecimal(" + textExpr + ")";

    public static string ArithNounsetGuard(string invocation) =>
        "$($__psbash_av = " + invocation + "; if ($global:__BashUnbound) { $__psbash_un = $global:__BashUnbound; $global:__BashUnbound = $null; "
        + "throw [PsBash.Cmdlets.BashParamErrorException]::Reported($__psbash_un + ': ', 'unbound variable') }; $__psbash_av)";

    /// <summary>
    /// The save / <c>try</c> / restore of <see cref="ShellStateScope"/> WITHOUT its child <c>&amp; { }</c>: for a
    /// caller that is already inside a script block of its own and must keep that block's <c>$input</c> (a
    /// compound pipeline stage), which a nested <c>&amp; { }</c> would hide.
    /// </summary>
    public static string ShellStateGuard(string body) =>
        ShellStateSave + "; try { "
        + body
        + " }" + ParamErrorCatch + SubshellExitCatch + " finally { " + ShellStateRestore + " }";

    /// <summary>The save half of <see cref="ShellStateGuard"/> (into <c>$__psbash_ss</c>).</summary>
    private const string ShellStateSave = "$__psbash_ss = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState)";

    /// <summary>The restore half of <see cref="ShellStateGuard"/> (from <c>$__psbash_ss</c>), with the <c>set -x</c> it reports.</summary>
    private const string ShellStateRestore =
        "$__psbash_xt =[PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $__psbash_ss); "
        + "if ($null -ne $__psbash_xt) { if ($__psbash_xt) { Set-PSDebug -Trace 1 } else { Set-PSDebug -Off } }";

    /// <summary>A parameter error ends the subshell it happens in (reported once, status 1), not the script.</summary>
    private const string ParamErrorCatch =
        " catch [PsBash.Cmdlets.BashParamErrorException] { if (-not $_.Exception.Data['psbash.reported']) { Write-Error -Message (" + DiagnosticPrefix + " + $_.Exception.Message) -ErrorAction Continue }; $global:LASTEXITCODE = 1 }";
    /// <summary>
    /// An <c>exit</c> that <see cref="DynamicExit"/> threw from a callee ends the enclosing subshell scope with its status.
    /// </summary>
    private const string SubshellExitCatch =
        " catch [PsBash.Cmdlets.BashSubshellExitException] { $global:LASTEXITCODE = $_.Exception.Status }";

    /// <summary>
    /// The PowerShell statement that ends the shell with <paramref name="status"/>, for code that runs where the
    /// emitter cannot know whether a subshell encloses it: a function body, which may be called from
    /// <c>f | cat</c>, <c>(f)</c>, <c>$(f)</c>. bash's <c>exit</c> there ends only that subshell. When a subshell scope
    /// (<see cref="ShellStateGuard"/>, whose <c>$__psbash_ss</c> a callee sees through PowerShell's dynamic scoping) is
    /// open, the status is thrown as <c>BashSubshellExitException</c> for that scope to catch; otherwise it is the plain
    /// <c>exit</c>. <paramref name="status"/> is evaluated once.
    /// </summary>
    public static string DynamicExit(string status) =>
        "$__psbash_xc = " + status + "; if ($ExecutionContext.SessionState.PSVariable.Get('__psbash_ss')) { $global:LASTEXITCODE = $__psbash_xc; "
        + "throw [PsBash.Cmdlets.BashSubshellExitException]::new([int]$__psbash_xc) }; exit $__psbash_xc";

    /// <summary>The pipeline element in front of a <see cref="StreamingReadLoopStage"/>: it stops the upstream once the loop has ended.</summary>
    public const string PipeGate = "Invoke-BashPipeGate";

    /// <summary>
    /// A <c>while read</c> loop as a pipe stage (<c>producer | while read l; do …; done</c>), run ONE RECORD AT A TIME as the
    /// records arrive. A script block's statements only start once its upstream has finished (they are its <c>end</c>
    /// block, and <c>$input</c> there is the collected input), so a body after a slow or endless producer saw nothing until
    /// the producer ended; a <c>process</c> block runs for each record as it is written. The stage is a subshell
    /// (<see cref="ShellStateGuard"/>: saved in <c>begin</c>, restored in <c>end</c>, or in <c>clean</c> when a later stage
    /// stopped the pipeline). Between records the UPSTREAM runs, so each record's pass keeps the loop's status to itself
    /// (<paramref name="statusVar"/>; the shell's <c>$?</c> is put back for the producer) and the loop's status is the last
    /// body command's, or 0. A pass that does not finish its record (<c>break</c>, <c>exit</c>, a parameter error) leaves
    /// <paramref name="gateVar"/><c>.Done</c> set: later records are dropped and <see cref="PipeGate"/> stops the producer.
    /// <paramref name="perRecord"/> handles <c>$_</c> and clears <c>Done</c> when it has handled the whole record.
    /// </summary>
    public static string StreamingReadLoopStage(string gateVar, string statusVar, string perRecord)
    {
        string entryVar = statusVar + "in";
        return "& { begin { " + gateVar + " = [PsBash.Cmdlets.BashPipeGate]::Claim(); " + statusVar + " = 0; " + ShellStateSave + " } "
            + "process { if (" + gateVar + ".Done) { return }; " + entryVar + " = $global:LASTEXITCODE; $global:LASTEXITCODE = 0; "
            + gateVar + ".Done = $true; try { " + perRecord + " }" + ParamErrorCatch + SubshellExitCatch
            + " finally { " + statusVar + " = $global:LASTEXITCODE; $global:LASTEXITCODE = " + entryVar + " } } "
            + "end { $global:LASTEXITCODE = " + statusVar + "; " + ShellStateRestore + "; $__psbash_ss = $null } "
            + "clean { if ($null -ne $__psbash_ss) { " + ShellStateRestore + " } } }";
    }

    /// <summary>
    /// Ends the enclosing subshell scope (<see cref="ShellStateGuard"/>) with <paramref name="status"/>: the guard catches
    /// the exception and sets the status. For code that is inside such a scope but not lexically inside its script block
    /// (eval/source text, a simple pipe stage), where a PowerShell <c>return</c> would leave only the nearest block.
    /// </summary>
    public static string SubshellExitThrow(string status) =>
        "$global:LASTEXITCODE = " + status + "; throw [PsBash.Cmdlets.BashSubshellExitException]::new([int]$global:LASTEXITCODE)";

    /// <summary>
    /// The variable through which a call site tells the eval/source/trap text it runs what a <c>break</c>/<c>continue</c>
    /// with no loop of its own does there: 0 = the caller's loop (bare keyword), 1 = bash's "only meaningful in a loop"
    /// warning (a function, a subshell, the top of the script), 2 = end the enclosing subshell (a command substitution,
    /// a simple pipe stage). PowerShell's dynamic scoping hands it to the text, and a script block or function that sets
    /// its own value hides the caller's.
    /// </summary>
    public const string LoopModeVar = "__psbash_lm";

    /// <summary>The call-site assignment of <see cref="LoopModeVar"/> (a statement that outputs nothing).</summary>
    public static string LoopModeSet(int mode) => "$" + LoopModeVar + " = " + mode + "; ";

    /// <summary>
    /// A <c>break</c>/<c>continue</c> at the top of eval/source/trap text, decided by the mode its call site set
    /// (<see cref="LoopModeVar"/>). A bare PowerShell keyword there is dynamically scoped: it left the loop of whatever
    /// called the subshell or function the text ran in (and ended the whole script when there was none).
    /// </summary>
    public static string RunTimeLoopControl(string keyword, string warning) =>
        "$__psbash_lmv = $ExecutionContext.SessionState.PSVariable.GetValue('" + LoopModeVar + "'); "
        + "if ($__psbash_lmv -eq 1) { " + warning + " } elseif ($__psbash_lmv -eq 2) { " + SubshellExitThrow("0") + " } else { " + keyword + " }";
    /// <summary><see cref="RunTimeLoopControl(string, string)"/> with bash's own warning (Resolve-BashCommand: <c>c=break; $c</c>).</summary>
    public static string RunTimeLoopControl(string keyword) => RunTimeLoopControl(keyword, LoopControlWarning(keyword));

    /// <summary>bash's warning for a <c>break</c>/<c>continue</c> with no loop in reach; the status is 0.</summary>
    public static string LoopControlWarning(string keyword) =>
        ShellDiagnosticLine(SingleQuote(keyword + ": only meaningful in a `for', `while', or `until' loop")) + "; " + SetStatus(0);

    /// <summary>Wrap in a subexpression: <c>$(expr)</c>.</summary>
    public static string Subexpr(string expr) => "$(" + expr + ")";

    /// <summary>
    /// The numeric compare of two arithmetic operands of <c>[[ a OP b ]]</c> (<paramref name="lhs"/>/<paramref name="rhs"/>
    /// are <c>Invoke-BashArith</c> invocations): false when an operand fails, as bash's <c>[[</c> is (status 1, the right
    /// operand not evaluated after a failed left one). A failed operand writes no value; read as 0 it made
    /// <c>while [[ x -eq 1/0 ]]</c> loop forever.
    /// </summary>
    public static string ArithCompare(string lhs, string op, string rhs) =>
        "$(if ($null -ne ($__psbash_al = " + lhs + ") -and $null -ne ($__psbash_ar = " + rhs + ")) { [long]$__psbash_al "
        + op + " [long]$__psbash_ar } else { $false })";

    /// <summary>
    /// One element of an array that may be unset: <c>$(if ($null -ne $a) { $a[index] })</c>. Indexing <c>$null</c>
    /// throws "Cannot index into a null array" (it aborted the whole command line); bash reads an element of an
    /// unset array as empty.
    /// </summary>
    public static string NullSafeElement(string arrayVar, string index)
        => "$(if ($null -ne " + arrayVar + ") { " + arrayVar + "[" + index + "] })";

    /// <summary>
    /// `c=s; c[1]=t`: before an element write, a variable that so far holds only a scalar becomes an array whose
    /// element 0 is that scalar (bash: `declare -a c=([0]="s" [1]="t")`).
    /// </summary>
    /// <summary>
    /// A function's write target for array <paramref name="name"/> it did not declare local: a calling function's local
    /// array of that name, else the global (<c>VarFrameStack.ArraySlot</c>). Reads and assigns like a variable.
    /// </summary>
    public static string ArraySlot(string name)
        => "[PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, " + SingleQuote(name) + ").Value";

    /// <summary>
    /// A write target as a COMMAND argument (<c>Set-BashElement T ...</c>): an <see cref="ArraySlot"/> is wrapped in
    /// parentheses, since argument mode reads <c>[Type]::M()</c> as text; a variable stays as it is.
    /// </summary>
    public static string TargetArg(string target) => IsArraySlot(target) ? "(" + target + ")" : target;

    /// <summary>True for a write target <see cref="ArraySlot"/> made, or the one <see cref="BindArraySlot"/> bound.</summary>
    public static bool IsArraySlot(string target)
        => target.StartsWith("[PsBash.Cmdlets.VarFrameStack]::ArraySlot(", StringComparison.Ordinal) || target == BoundArraySlot;

    /// <summary>The write target after <see cref="BindArraySlot"/>: the bound slot's value.</summary>
    public const string BoundArraySlot = "$__psbSlot.Value";

    /// <summary>
    /// A statement binding an <see cref="ArraySlot"/> target's variable to <c>$__psbSlot</c>, so a write that mentions
    /// the target more than once (<c>a+=(x)</c> reads and assigns it; <c>a[1]=x</c> seeds, reads and assigns it) looks
    /// it up once (<see cref="BoundArraySlot"/>). REGRESSION: every mention was a run-time lookup.
    /// </summary>
    public static string BindArraySlot(string slotTarget)
        => "$__psbSlot = " + slotTarget[..^".Value".Length];

    /// <summary>A function body's first statement: the locals it holds as PowerShell variables, which its callees' array writes reach (<see cref="ArraySlot"/>).</summary>
    public static string LocalArrays(IEnumerable<string> names)
    {
        var list = string.Join(", ", names.Select(SingleQuote));
        return list.Length == 0 ? "" : "[PsBash.Cmdlets.VarFrameStack]::LocalArrays(" + list + "); ";
    }

    public static string SeedArrayFromScalar(string arrayVar, string scalarVar)
        => "if ($null -eq " + arrayVar + " -and $null -ne " + scalarVar + ") { " + arrayVar + " = @(" + scalarVar + ") }";

    /// <summary>
    /// `a=z` / `a+=z` where a may be an array: an array (or associative array) takes the value as element 0 (key "0");
    /// otherwise <paramref name="scalarWrite"/>, the ordinary scalar assignment, runs.
    /// </summary>
    public static string WriteElementZeroOrScalar(string arrayVar, string value, bool append, string scalarWrite)
        => "if (" + arrayVar + " -is [array] -or " + arrayVar + " -is [System.Collections.IDictionary]) { " + arrayVar
           + " = Set-BashElement " + TargetArg(arrayVar) + " '0' (" + value + ")" + (append ? " -Append" : "") + " } else { " + scalarWrite + " }";

    /// <summary>
    /// The value of one emitted COMMAND-MODE argument, as an expression: <c>&amp; { $args[0] } arg</c>. An argument
    /// is command-mode text (<c>x=$(…)</c>, <c>-o$(…)</c>): assigned directly (<c>$v = x=$(…)</c>) it parses as a
    /// command; a param-less script block takes it, dash-led words included, exactly as the command would.
    /// </summary>
    public static string ArgValue(string commandModeArg) => "& { $args[0] } " + commandModeArg;

    /// <summary>
    /// Suppress an emitted statement's output, choosing the form that survives a
    /// statement LIST. <c>(...)</c> (grouping) cannot hold <c>stmt1; stmt2</c>
    /// ("Missing closing ')'"), only the subexpression <c>$(...)</c> can — so a value
    /// containing <c>"; "</c> uses <c>[void]$(...)</c>, a single statement the cheaper
    /// <c>[void](...)</c>. Centralizes the choice the &amp;&amp;/|| chain made inline.
    /// </summary>
    public static string VoidStatement(string text) =>
        text.Contains("; ", System.StringComparison.Ordinal)
            ? "[void]$(" + text + ")"
            : "[void](" + text + ")";

    /// <summary>
    /// True when <paramref name="psText"/> is a PowerShell statement LIST — i.e. it
    /// contains a <c>;</c> at nesting depth 0, outside any quoted span. Such text
    /// CANNOT head a pipeline: <c>$__saved = 1; try { … } finally { … } | Foo</c>
    /// is the parse error "An empty pipe element is not allowed", because the
    /// pipe binds only to the trailing <c>finally</c> block.
    /// <para>
    /// The scan is quote- and nesting-aware on purpose. A naive
    /// <c>Contains(";")</c> misfires on a <c>;</c> inside a string operand
    /// (<c>Invoke-BashEcho "a;b"</c>) or inside an already-wrapped child scope
    /// (<c>&amp; { a; b }</c>) — both of which ARE valid pipeline heads. That
    /// "quote-blind scan" is a recurring bug family in this transpiler, so this
    /// helper is the single place the rule is expressed.
    /// </para>
    /// </summary>
    public static bool IsStatementList(string psText)
    {
        int paren = 0, brace = 0, bracket = 0;
        for (int i = 0; i < psText.Length; i++)
        {
            char c = psText[i];
            switch (c)
            {
                case '\'':
                case '"':
                    i = SkipQuotedSpan(psText, i);
                    break;

                case '`': i++; break;               // escape: skip the escaped char
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
                case '{': brace++; break;
                case '}': if (brace > 0) brace--; break;
                case '[': bracket++; break;
                case ']': if (bracket > 0) bracket--; break;

                case ';':
                    if (paren == 0 && brace == 0 && bracket == 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="psText"/> has a <c>"; "</c> separator outside every quoted span, at any nesting
    /// depth (so <c>if (…) { a; b }</c>, which cannot be a pipe element either, still counts). A <c>;</c> inside a
    /// string operand does not: <c>Invoke-BashBash '-c' 'cat; exit 33'</c> is one command, and wrapping it in
    /// <c>&amp; { }</c> as a pipe stage cut it off from its piped input.
    /// </summary>
    public static bool HasUnquotedStatementSeparator(string psText)
    {
        for (int i = 0; i < psText.Length; i++)
        {
            switch (psText[i])
            {
                case '\'':
                case '"':
                    i = SkipQuotedSpan(psText, i);
                    break;
                case '`': i++; break;
                case ';':
                    if (i + 1 < psText.Length && psText[i + 1] == ' ') return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The index of the quote closing the span opened at <paramref name="open"/> (or the end of the text).
    /// Single-quoted text is literal ('' reads as close-then-reopen); double-quoted text honours backtick escapes and
    /// <c>$( … )</c> subexpressions, which may hold quotes and semicolons of their own.</summary>
    private static int SkipQuotedSpan(string psText, int open)
    {
        int i = open + 1;
        if (psText[open] == '\'')
        {
            while (i < psText.Length && psText[i] != '\'') i++;
            return i;
        }
        for (int depth = 0; i < psText.Length; i++)
        {
            if (psText[i] == '`') { i++; continue; }
            if (psText[i] == '$' && i + 1 < psText.Length && psText[i + 1] == '(') { depth++; i++; continue; }
            if (psText[i] == ')' && depth > 0) { depth--; continue; }
            if (psText[i] == '"' && depth == 0) break;
        }
        return i;
    }

    /// <summary>
    /// The bash file-comparison test operators <c>-nt</c> / <c>-ot</c> / <c>-ef</c>
    /// as a PowerShell boolean expression. These were not implemented at all: the
    /// emitter fell through to joining the operands with spaces, producing the
    /// never-valid <c>$env:a -ef $env:b</c> (Go's etetest.sh).
    /// <para>
    /// Missing-operand semantics follow bash exactly (verified against the oracle):
    /// <c>a -nt b</c> is true when a exists and b does not; <c>a -ot b</c> is true
    /// when b exists and a does not; <c>a -ef b</c> is false unless BOTH exist.
    /// </para>
    /// <para>
    /// <c>-ef</c> is "same file". bash compares device+inode; the portable .NET
    /// equivalent is a resolved-path compare, so a symlink and its target match
    /// (<c>ResolvedTarget</c>, null for a non-link, falls back to
    /// <c>FullName</c>). Hard links to the same inode are NOT detected — a
    /// documented approximation, not a silent wrong answer for the common case.
    /// </para>
    /// </summary>
    public static string FileComparisonTest(string lhs, string rhs, string op)
    {
        const string a = "$__psbash_ftA";
        const string b = "$__psbash_ftB";
        string probe =
            a + " = Get-Item -LiteralPath " + lhs + " -Force -ErrorAction SilentlyContinue; " +
            b + " = Get-Item -LiteralPath " + rhs + " -Force -ErrorAction SilentlyContinue; ";

        string body = op switch
        {
            "-nt" => "if ($null -eq " + a + ") { $false } elseif ($null -eq " + b + ") { $true } " +
                     "else { " + a + ".LastWriteTimeUtc -gt " + b + ".LastWriteTimeUtc }",
            "-ot" => "if ($null -eq " + b + ") { $false } elseif ($null -eq " + a + ") { $true } " +
                     "else { " + a + ".LastWriteTimeUtc -lt " + b + ".LastWriteTimeUtc }",
            _     => "if ($null -eq " + a + " -or $null -eq " + b + ") { $false } " +
                     "else { (" + a + ".ResolvedTarget ?? " + a + ".FullName) -eq " +
                     "(" + b + ".ResolvedTarget ?? " + b + ".FullName) }",
        };

        // `$( & { … } )`: the probe is a statement LIST, so it needs a subexpression,
        // and the temp names stay inside the child scope (a top-level assignment
        // would leak into the runspace and shadow a transpiled bash variable).
        return "$(& { " + probe + body + " })";
    }

    // ───────────────────────────────── Exit-code tests ────────────────────────────────

    /// <summary>
    /// The test for a condition that RAN AS A STATEMENT just before it (see
    /// <see cref="HoistedCondition"/>): <c>$global:LASTEXITCODE -eq 0</c> (<c>-ne</c> when negated).
    /// </summary>
    public static string LastStatusTest(bool negate = false) =>
        "$global:LASTEXITCODE " + (negate ? "-ne" : "-eq") + " 0";

    /// <summary>Set bash's <c>$?</c> to a fixed status: <c>$global:LASTEXITCODE = N</c>.</summary>
    public static string SetStatus(int status) => SetStatusTo(status.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Set bash's <c>$?</c> to the value of a PowerShell expression (a saved status variable, an int expression).</summary>
    public static string SetStatusTo(string statusExpr) => "$global:LASTEXITCODE = " + statusExpr;

    /// <summary>
    /// The condition of an <c>if</c>/<c>while</c>/<c>until</c> (or an operand in one) that is the builtin <c>true</c>,
    /// <c>:</c> or <c>false</c>: an expression that sets the status as the builtin does, then tests it. A bare
    /// <c>$true</c> ran no command, so the branch saw the status from before the condition (<c>false; if true; then echo
    /// $?; fi</c> printed 1; bash prints 0).
    /// </summary>
    public static string ConstantStatusCondition(bool succeeds) => ConditionStatus(succeeds ? "0" : "1");

    /// <summary>
    /// A condition that is a test, not a command (<c>[[ ]]</c>, <c>[ ]</c>, <c>(( ))</c>, <c>true</c>): sets bash's
    /// <c>$?</c> to <paramref name="statusExpr"/> (an int expression: 0, 1, or 2 for a bad <c>[ ]</c> operand) and is
    /// true when it is 0. bash runs a condition like any command, so the branch, the loop body, and an <c>else</c> see
    /// its status; a bare boolean left the status of the command before.
    /// </summary>
    public static string ConditionStatus(string statusExpr) => "((" + SetStatusTo(statusExpr) + ") -eq 0)";

    /// <summary>The status of a PowerShell boolean expression: 0 when true, 1 when false.</summary>
    public static string BoolStatus(string boolExpr) => "[int](-not (" + boolExpr + "))";

    /// <summary>
    /// A builtin usage error that drops the rest of the input line (bash <c>no_args</c>: <c>break 1 2</c>, <c>return 1 2</c>,
    /// <c>exit 1 2</c> "too many arguments"): <c>NAME: line N: MESSAGE</c>, status 1, then the discard. Inside a line scope
    /// (<see cref="DiscardLineScope"/>, which <see cref="CanDiscardLine"/> asks for) the discard error ends the line, a loop
    /// or function on it included; outside one the line mark skips the commands after it on its line (<see cref="UnlessGlobFailed"/>).
    /// Under <c>bash -c</c> the whole command string is the input, so the shell ends, status 1 (a subshell ends only itself):
    /// the reported parameter error, which a line scope passes on. The runtime twin is <c>BashLineDiscard.UsageError</c>.
    /// REGRESSION: a script went on with the rest of the line.
    /// </summary>
    public static string UsageDiscard(string message) =>
        ShellDiagnosticLine(SingleQuote(message)) + "; " + SetStatus(1) + "; " + UsageDiscardMark;

    /// <summary>
    /// A refused assignment (<c>r=2</c> with r read-only, <c>a[]=1</c>, a write through a circular nameref):
    /// <c>NAME: line N: MESSAGE</c>, status 1, then the rest of the input line is dropped. Inside a line scope the discard
    /// error ends the line, a loop or the functions it called included (<c>g() { readonly y=1; y=2; echo no; }; g; echo no</c>);
    /// outside one the line mark skips the commands after it on its line. Unlike <see cref="UsageDiscard"/>, <c>bash -c</c>
    /// goes on with its next line. Under <c>set -e</c>, or in a non-interactive posix-mode shell, the shell ends with 1
    /// instead, even in an errexit-exempt context (<c>r=2 || echo</c>; <c>BashParamErrorException.AssignmentEnd</c>). REGRESSION: in a function the refusal only set
    /// the status, and the function and its caller's line went on; under set -e the script went on to its next line.
    /// </summary>
    public static string AssignmentError(string message) =>
        ShellDiagnosticLine(SingleQuote(message)) + "; " + SetStatus(1) + "; " + AssignmentDiscardMark;

    /// <summary>The discard step of <see cref="AssignmentError"/> (and the run-time readonly guard); <see cref="CanDiscardLine"/> recognises it.</summary>
    internal const string AssignmentDiscardMark =
        "if ($global:__BashErrexit -or (" + PosixOn + " -and -not [PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell)) { throw [PsBash.Cmdlets.BashParamErrorException]::AssignmentEnd() }; "
        + "if ([PsBash.Cmdlets.BashLineDiscard]::Armed) { throw [PsBash.Cmdlets.BashParamErrorException]::Discard('', 'assignment error') }; $global:__BashFailGlob = $true";

    /// <summary>The discard step of <see cref="UsageDiscard"/>; <see cref="CanDiscardLine"/> recognises it.</summary>
    internal const string UsageDiscardMark =
        "if ($env:PSBASH_DASH_C -eq '1') { throw [PsBash.Cmdlets.BashParamErrorException]::UsageEnd() }; "
        + "if ([PsBash.Cmdlets.BashLineDiscard]::Armed) { throw [PsBash.Cmdlets.BashParamErrorException]::Discard('', 'usage error') }; $global:__BashFailGlob = $true";

    /// <summary>
    /// <c>set -n</c> / <c>set -o noexec</c>: a non-interactive shell reads on but runs nothing more, so the (sub)shell
    /// ends here with status 0 and its EXIT trap does not fire. An interactive shell ignores the option.
    /// </summary>
    public const string NoExecStop =
        "if (-not [PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell) { $global:__BashTrapEXIT = $null; $global:LASTEXITCODE = 0; exit 0 } else { $global:LASTEXITCODE = 0 }";

    /// <summary>
    /// The status <c>exit N</c> leaves with: the low 8 bits of N, as bash keeps them (<c>exit 256</c> is 0, <c>exit 257</c>
    /// is 1, <c>exit -2</c> is 254). A Windows process status is 32 bits, so <c>exit 256</c> reached the caller as 256.
    /// A value that is not a number is bash's <c>exit: V: numeric argument required</c>, status 2. REGRESSION: it was
    /// passed on unchanged, so <c>( exit w ); echo $?</c> printed <c>w</c>.
    /// </summary>
    public static string StatusByte(string valueExpr) =>
        "$(& { $__psb_es = [string](" + valueExpr + "); if ($__psb_es -match '^\\s*[+-]?[0-9]{1,27}\\s*$') "
        + "{ [int]((([decimal]$__psb_es.Trim() % 256) + 256) % 256) } else { "
        + ShellDiagnosticLine("'exit: ' + $__psb_es + ': numeric argument required'") + "; 2 } })";

    /// <summary>
    /// An <c>if</c>/<c>while</c> condition that runs a command, hoisted to statement position:
    /// <c>condStatements; </c> — the caller then tests <see cref="LastStatusTest"/>. Unlike
    /// <see cref="ExitCodeTest"/>, whose expression position forces <c>[void]</c>, the command's
    /// OUTPUT streams like any statement's (bash: <c>if echo in; then …</c> prints <c>in</c>).
    /// </summary>
    public static string HoistedCondition(string condStatements) => condStatements + "; ";

    /// <summary>
    /// <see cref="ExitCodeTest"/> for the runtime <c>test</c> evaluator: its status 2 (a malformed expression) is recorded in
    /// <c>$global:__BashTestBad</c> so the enclosing test statement ends with 2, not the 1 of a plain false.
    /// </summary>
    public static string ExitCodeTestFlaggingBad(string emittedCmd) =>
        "(& { " + VoidStatement(emittedCmd) + "; if ($global:LASTEXITCODE -eq 2) { $global:__BashTestBad = $true }; $global:LASTEXITCODE -eq 0 })";

    /// <summary>
    /// A boolean expression that runs <paramref name="emittedCmd"/> and tests its EXIT
    /// CODE (bash semantics), suitable inside <c>if (...)</c>/<c>while (...)</c>:
    /// <c>(&amp; { [void](cmd); $global:LASTEXITCODE -eq 0 })</c>. The <see cref="Void"/>
    /// is NOT optional — without it the scriptblock returns the command's output objects
    /// alongside the boolean, and PowerShell evaluates the resulting multi-element array
    /// as truthy, silently inverting the condition.
    /// </summary>
    /// <param name="emittedCmd">The already-emitted PowerShell command/pipeline text to test.</param>
    /// <param name="negate">
    /// <c>false</c> → test success (<c>-eq 0</c>); <c>true</c> → test failure
    /// (<c>-ne 0</c>), i.e. bash <c>! cmd</c> which succeeds when <paramref name="emittedCmd"/> fails.
    /// </param>
    public static string ExitCodeTest(string emittedCmd, bool negate = false) =>
        // Suppress the command's output via VoidStatement, which picks [void]$(...) over
        // [void](...) when the emitted text is a statement LIST (contains "; "). A grouping
        // paren cannot hold `stmt1; stmt2` ("Missing closing ')'"), so a multi-statement
        // command like `cd DIR` (which emits an if/else block) would produce unparseable
        // PowerShell inside `if cd DIR; then …`. The subexpression form survives it.
        "(& { " + VoidStatement(emittedCmd) + "; $global:LASTEXITCODE " + (negate ? "-ne" : "-eq") + " 0 })";

    /// <summary>
    /// <c>time [-p] pipeline</c>: run the pipeline's statement text, then write bash's timing report to the error
    /// stream. Statement scope (not a script block) so assignments in the pipeline stay visible; the report writer
    /// leaves <c>$?</c> and the exit code alone, so the status is the pipeline's. <paramref name="markVar"/> is unique
    /// per nesting depth (<c>time { time a; }</c>).
    /// </summary>
    public static string Timed(string pipelineText, bool posix, string markVar) =>
        markVar + " = [PsBash.Cmdlets.BashTimeKeyword]::Start(); try { " + pipelineText + " } finally { "
        + "$__psbash_tr = [PsBash.Cmdlets.BashTimeKeyword]::Report(" + markVar + ", " + (posix ? "$true" : "$false") + "); "
        + "if ($null -ne $__psbash_tr) { Write-BashHostStderr -Message $__psbash_tr } }";

    /// <summary>
    /// A fixed error line on the PowerShell error stream, so <c>2&gt;$null</c> / <c>2&gt;&amp;1</c> / <c>2&gt; f</c> see
    /// it (<c>[Console]::Error</c> bypassed every redirect). A shell diagnostic that needs bash's
    /// <c>NAME: line N: </c> prefix uses <see cref="ShellDiagnosticLine"/>.
    /// </summary>
    public static string StderrLine(string message) =>
        "Write-Error -Message " + SingleQuote(message) + " -ErrorAction Continue";

    /// <summary>
    /// A shell construct ps-bash does not run by design (<c>select</c>, <c>coproc</c>): one line on stderr naming it, status 2.
    /// The one wording for every such construct, so a script that hits one says what is missing instead of failing
    /// later in some unrelated way (a dropped loop, <c>coproc: command not found</c>).
    /// </summary>
    public static string NotSupported(string construct) =>
        ShellDiagnosticLine(SingleQuote(construct + ": not supported by ps-bash")) + "; " + SetStatus(2);

    /// <summary>True while <c>set -o posix</c> is on (a remembered option of InvokeBashShoptCommand).</summary>
    public const string PosixOn = "[PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('posix')";

    /// <summary>
    /// <paramref name="posixForm"/> while <c>set -o posix</c> is on, else <paramref name="normalForm"/>: a special builtin
    /// behaves differently in POSIX mode (a prefix assignment persists, it wins over a function of its name), and the
    /// option is only known at run time.
    /// </summary>
    public static string PosixElse(string posixForm, string normalForm) =>
        "if (" + PosixOn + ") { " + posixForm + " } else { " + normalForm + " }";

    /// <summary>
    /// <c>name() { ...; }</c> where name is a special builtin, in POSIX mode: bash reports
    /// <c>`name': is a special builtin</c> with the source prefix; the caller then ends the shell with status 2 (a
    /// subshell only itself: PsEmitter.EndShell).
    /// </summary>
    public static string SpecialBuiltinRedefined(string name) =>
        ShellDiagnosticLine(SingleQuote("`" + name + "': is a special builtin"));

    // ──────────────────────── Exit-code propagation (&& / || chains) ───────────────────

    /// <summary>
    /// Drive <c>$global:LASTEXITCODE</c> AND <c>$?</c> from a boolean, for use as an
    /// operand of PowerShell's pipeline-chain operators (which check <c>$?</c>, not the
    /// exit code): <c>$(if (boolExpr) { $global:LASTEXITCODE = 0 } else {
    /// $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue })</c>.
    /// The empty <c>Write-Error</c> flips <c>$?</c> to false so a following <c>||</c>
    /// fires. <paramref name="boolExpr"/> must already be a parenthesized PS boolean.
    /// </summary>
    public static string SetExitFromBool(string boolExpr) =>
        "$(if (" + boolExpr + ") { $global:LASTEXITCODE = 0 } "
        + "else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// Bridge a plain pipeline's <c>$global:LASTEXITCODE</c> to <c>$?</c> WITHOUT
    /// re-running or suppressing it: emit the pipeline as a statement, then append this
    /// <c>$(if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })</c>
    /// as the chain-operator operand. Unlike <see cref="SetExitFromBool"/> this keeps
    /// the pipeline's real stdout flowing (it is the command's output in the chain).
    /// </summary>
    public static string SignalFailIfNonZero() =>
        "$(if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// <see cref="SignalFailIfNonZero"/> for an operand whose failure may show in EITHER signal:
    /// a cmdlet error clears <c>$?</c> (still the operand's at the start of this subexpression),
    /// while a bash function's <c>return 3</c> or a subshell's <c>exit 4</c> sets only the exit
    /// code and leaves <c>$?</c> true. Fails the chain when either says so.
    /// </summary>
    public static string SignalFailIfFailed() =>
        "$(if (-not $? -or $global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// A standalone <c>[ ... ]</c> / <c>[[ ... ]]</c> test as its OWN statement: bash
    /// is silent and sets only the exit code. Emit a form that sets
    /// <c>$global:LASTEXITCODE</c> and produces no stdout:
    /// <c>$(if (boolExpr) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })</c>.
    /// (No <c>Write-Error</c> — a bare test outside a chain does not signal <c>$?</c>.)
    /// </summary>
    public static string SilentExitFromBool(string boolExpr) =>
        "$(if (" + boolExpr + ") { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })";

    // ─────────────────────────────── errexit (set -e) ───────────────────────────────

    /// <summary>
    /// The runtime counter of enclosing errexit-EXEMPT contexts (an <c>if</c>/<c>while</c>
    /// condition, a non-final <c>&amp;&amp;</c>/<c>||</c> operand, <c>!</c>, a command
    /// substitution). bash ignores <c>-e</c> for everything run inside one — including the
    /// body of a FUNCTION called there, which only a runtime counter can see. Initialised
    /// per command by the host and by <c>set -e</c>.
    /// </summary>
    public const string ErrexitSuppressVar = "$global:__BashErrexitSuppress";

    /// <summary>Whether errexit fires right now: <c>set -e</c> is on and no exempt context is active.</summary>
    private const string ErrexitArmed = "$global:__BashErrexit -and -not " + ErrexitSuppressVar;

    /// <summary>
    /// After a command at statement position under <c>set -e</c>: leave when it failed.
    /// <paramref name="exitStatement"/> is how to leave from here — a real <c>exit</c>, or a
    /// scoped <c>return</c> inside a subshell. <paramref name="status"/> is the status to test.
    /// </summary>
    public static string ErrexitCheck(string exitStatement, string status = "$global:LASTEXITCODE") =>
        ErrTrapCheck(status) + "if (" + ErrexitArmed + " -and " + status + " -ne 0) { " + exitStatement + " }";

    /// <summary>
    /// Run the <c>trap ... ERR</c> action when the statement just run failed outside an exempt context
    /// (a condition, a non-final <c>&amp;&amp;</c>/<c>||</c> operand, a negated pipeline). The action sees
    /// the failing status in <c>$?</c>/<c>$LASTEXITCODE</c>, which it leaves unchanged.
    /// </summary>
    /// <summary>
    /// A redirected compound command at statement position under an ERR trap / errexit: its status is the body's
    /// (whose own statements were checked), so only a redirect that could not be opened (the body never ran) runs
    /// <paramref name="check"/> — Invoke-BashRedirect (an output file) and <see cref="InputRedirectGuard"/> (an input
    /// file) set <see cref="RedirectFailedVariable"/>.
    /// </summary>
    public static string RedirectFailureChecked(string compound, string check) =>
        RedirectFailedVariable + " = $false; " + compound
        + "; if (" + RedirectFailedVariable + ") { " + RedirectFailedVariable + " = $false; $global:LASTEXITCODE = 1; " + check + " }";

    public static string ErrTrapCheck(string status = "$global:LASTEXITCODE") =>
        "if (" + status + " -ne 0 -and -not " + ErrexitSuppressVar + (ErrTrapNeedsErrtrace ? " -and $global:__BashErrtrace" : "") + " -and -not $global:__BashInErr -and (Test-Path variable:global:__BashTrapERR) -and $global:__BashTrapERR) { "
        + "$__psbErr = " + status + "; " + (TrapLine > 0 && !InTrapAction ? "$env:__psbLine = " + TrapLine + "; " : "")
        // The action does not run itself again (a failing command in it is not an ERR), and a DEBUG action that fails
        // runs it (InTrapAction text carries the checks).
        + "$global:__BashInErr = $true; try { & $global:__BashTrapERR } finally { $global:__BashInErr = $false }; $global:LASTEXITCODE = $__psbErr }; ";

    /// <summary>
    /// The current value an array append (<c>s+=(x)</c>, <c>declare -A s+=([k]=v)</c>) builds on: the array, else the
    /// variable's SCALAR value, which lives in the environment. bash keeps it as element 0
    /// (<c>s=world; s+=(x)</c> is <c>([0]="world" [1]="x")</c>); the append started from nothing and lost it.
    /// <paramref name="scalarExpr"/> is that scalar value (<c>${env:s}</c>, or the target's for a run-time nameref).
    /// </summary>
    public static string ArrayAppendBase(string arrayTarget, string scalarExpr) =>
        "(" + arrayTarget + " ?? " + scalarExpr + ")";

    /// <summary>
    /// <c>a=([k]=v …)</c> on a variable the emitter does not know to be associative: an associative <c>a</c> (declared
    /// in a sourced file, a caller) takes the keys as strings, anything else is an INDEXED array whose subscripts are
    /// arithmetic. Only the chosen branch runs, so a subscript's side effects (<c>[i++]</c>) happen only for the indexed
    /// form. The <c>,</c> keeps a one-element array an array through <c>$( )</c>.
    /// </summary>
    public static string AssocOrIndexed(string arrayTarget, string assocExpr, string indexedExpr) =>
        "$(if (" + arrayTarget + " -is [PsBash.Cmdlets.BashAssoc]) { " + assocExpr + " } else { ,(" + indexedExpr + ") })";

    /// <summary>
    /// <paramref name="text"/> as one BASH word in single quotes (<c>it's</c> is <c>'it'\''s'</c>): text handed to a
    /// run-time <c>eval</c> that must stay literal.
    /// </summary>
    public static string BashSingleQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";
    /// <summary>
    /// <c>declare -A a</c> when <c>a</c> is an indexed array (<c>declare -a</c> when it is associative): bash reports
    /// <c>declare: a: cannot convert indexed to associative array</c> (the <paramref name="builtin"/> as invoked, after
    /// the source prefix), sets status 1 and leaves <c>a</c> as it was; otherwise
    /// <paramref name="declaration"/> runs.
    /// </summary>
    public static string ArrayKindGuard(string builtin, string name, bool toAssoc, string declaration) =>
        "if ([PsBash.Cmdlets.BashAssoc]::" + (toAssoc ? "IsIndexedValue" : "IsAssocValue") + "((Get-Variable -Name " + SingleQuote(name)
        + " -ValueOnly -ErrorAction Ignore))) { "
        + ShellDiagnosticLine(SingleQuote(builtin + ": " + name + ": cannot convert " + (toAssoc ? "indexed to associative" : "associative to indexed") + " array"))
        + "; $global:LASTEXITCODE = 1 } else { " + declaration + " }";
    /// <summary>The end of the message <see cref="KeyOnlySubscriptGuard"/> reports (callers recognise a refused write by it).</summary>
    public const string BadArithSubscriptMessage = ": arithmetic syntax error in array subscript";
    /// <summary>
    /// <c>a['2']=3</c> / <c>b[0+]=y</c>: a subscript that is no arithmetic expression is a KEY for an associative
    /// <paramref name="name"/> (<paramref name="assignment"/> runs, status 0), and for anything else an arithmetic syntax
    /// error: status 1 and nothing assigned. Which one it is is known only when it runs.
    /// </summary>
    public static string KeyOnlySubscriptGuard(string name, string written, string assignment) =>
        "if ([PsBash.Cmdlets.BashAssoc]::IsAssocValue((Get-Variable -Name " + SingleQuote(name) + " -ValueOnly -ErrorAction Ignore))) { "
        + assignment + "; " + SetStatus(0) + " } else { " + ShellDiagnosticLine(SingleQuote(written + BadArithSubscriptMessage))
        + "; " + SetStatus(1) + " }";
    /// <summary>
    /// A plain assignment to <paramref name="names"/> in a script that made names read-only at run time
    /// (<c>readonly $names</c>, <c>readonly</c> in a function): when one of them is read-only now, report
    /// <c>NAME: readonly variable</c> and set status 1 instead of assigning.
    /// </summary>
    public static string RuntimeReadonlyGuard(IEnumerable<string> names, string assignment) =>
        "if ($__psbRo = [PsBash.Cmdlets.BashShellState]::FirstReadonly(" + string.Join(", ", names.Select(SingleQuote))
        + ")) { Write-Error -Message (" + DiagnosticPrefix + " + $__psbRo + ': readonly variable') -ErrorAction Continue; $global:LASTEXITCODE = 1; "
        + AssignmentDiscardMark + " } else { "
        + assignment + " }";

    /// <summary>
    /// Run the <c>trap ... DEBUG</c> action before a command. The action sees the command's line as <c>$LINENO</c>,
    /// does not itself trigger DEBUG, and leaves the status the command will be run with unchanged.
    /// <paramref name="errexitExit"/> (a script that can turn on <c>set -e</c>): an action that fails while errexit is
    /// on ends the script with the action's status, as bash does (the action runs under the shell's options).
    /// </summary>
    public static string DebugTrapRun(int line, string? errexitExit = null) =>
        "if ((Test-Path variable:global:__BashTrapDEBUG) -and $global:__BashTrapDEBUG -and -not $global:__BashInDebug) { "
        + "$global:__BashInDebug = $true; $__psbDbg = $global:LASTEXITCODE; " + (line > 0 && !InTrapAction ? "$env:__psbLine = " + line + "; " : "")
        + "try { & $global:__BashTrapDEBUG } finally { $global:__BashInDebug = $false }; "
        + (errexitExit is null ? "" : "if (" + ErrexitArmed + " -and $global:LASTEXITCODE -ne 0) { " + errexitExit + " }; ")
        + "$global:LASTEXITCODE = $__psbDbg }; ";

    /// <summary>
    /// After a statement of a script that can set a trap: run the traps of signals sent to this shell since the last
    /// statement (<c>PsBash.Core.ShellSignals</c>; bash runs a trap between commands). One static read when none is
    /// pending; <c>Invoke-BashPendingSignals</c> runs each trap (or the signal's default action) and keeps <c>$?</c>.
    /// </summary>
    public const string SignalTrapPoll = "if ([PsBash.Cmdlets.ShellSignalPoll]::Pending) { Invoke-BashPendingSignals }";

    /// <summary>The value of <c>$-</c>: the single-letter options currently on, in bash's order.</summary>
    public const string ShellFlagsExpr =
        "$f = ''; if ($global:__BashErrexit) { $f += 'e' }; if ($global:__BashNoglob) { $f += 'f' }; $f += 'h'; "
        // `bash -i -c 'echo $-'` is `himBHc`: interactive, with monitor and histexpand on by default.
        + "if ([PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell) { $f += 'i' }; if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('monitor')) { $f += 'm' }; "
        + "if ($global:__BashNounset) { $f += 'u' }; if ($global:__BashVerbose) { $f += 'v' }; if ($global:__BashXtrace) { $f += 'x' }; $f += 'B'; if ($global:__BashNoclobber) { $f += 'C' }; if ($global:__BashErrtrace) { $f += 'E' }; "
        + "if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('histexpand')) { $f += 'H' }; "
        // `c` under `bash -c` (last, as in bash's `hBCc`): the launcher's PSBASH_DASH_C mark, never inherited by a nested shell.
        // `s` when the commands come from stdin: a piped script or `bash -s` (`hBs`, the launcher's PSBASH_STDIN_SHELL mark),
        // an interactive shell (`himBHs`); a script file has neither.
        + "if ($env:PSBASH_DASH_C -eq '1') { $f += 'c' } elseif ($env:PSBASH_STDIN_SHELL -eq '1' -or [PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell) { $f += 's' }; $f";

    /// <summary>The value of <c>$SHELLOPTS</c>: the <c>set -o</c> options currently on, alphabetical, colon-joined.</summary>
    public const string ShellOptsExpr =
        "$o = @('braceexpand'); if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('emacs')) { $o += 'emacs' }; "
        + "if ($global:__BashErrexit) { $o += 'errexit' }; if ($global:__BashErrtrace) { $o += 'errtrace' }; $o += 'hashall'; "
        + "foreach ($__psbOpt in 'histexpand', 'history') { if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn($__psbOpt)) { $o += $__psbOpt } }; $o += 'interactive-comments'; "
        + "if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('monitor')) { $o += 'monitor' }; "
        + "if ($global:__BashNoclobber) { $o += 'noclobber' }; if ($global:__BashNoglob) { $o += 'noglob' }; "
        + "if ($global:__BashNounset) { $o += 'nounset' }; if ($global:__BashPipefail) { $o += 'pipefail' }; if ($global:__BashVerbose) { $o += 'verbose' }; "
        + "if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('vi')) { $o += 'vi' }; "
        + "if ($global:__BashXtrace) { $o += 'xtrace' }; $o -join ':'";

    /// <summary>
    /// Emitting the text of a trap action (<see cref="PsEmitter.TrapActionSignal"/>): the ERR / DEBUG runs inside it keep
    /// the <c>$LINENO</c> of the command the trap fired for (bash: the action's commands report that line), so they do
    /// not set it from the action's own line numbers.
    /// </summary>
    [ThreadStatic] public static bool InTrapAction;

    /// <summary><c>${BASH_LINENO[@]}</c>: the line each active function was called from, innermost first (then 0 in a script file).</summary>
    public const string BashLineNoArray = "[PsBash.Cmdlets.VarFrameStack]::BashLineNo($global:BASH_SOURCE)";

    /// <summary>
    /// <c>${BASH_SOURCE[@]}</c>: a frame per function (the file it was defined in) between the files being read
    /// (<c>$global:BASH_SOURCE</c>), as bash keeps it beside FUNCNAME (<c>VarFrameStack.BashSource</c>).
    /// </summary>
    public const string BashSourceArray = "[PsBash.Cmdlets.VarFrameStack]::BashSource($global:BASH_SOURCE)";

    /// <summary>A here-string's stdin text: the expanded word <paramref name="valueExpr"/> (an assignment-value expression)
    /// plus the newline bash always appends.</summary>
    public static string HereStringValue(string valueExpr) => "([string](" + valueExpr + ") + \"`n\")";

    /// <summary>
    /// A statement on <paramref name="line"/> starts: a function it calls reports that line in BASH_LINENO, and a
    /// shell diagnostic it raises (<c>NAME: line N: x: command not found</c>) shows it (<see cref="DiagnosticPrefix"/>).
    /// </summary>
    public static string SetCallLine(int line) => "[PsBash.Cmdlets.VarFrameStack]::SetLine(" + line + "); ";

    /// <summary>
    /// bash's source prefix for a diagnostic from the shell itself, evaluated at run time: <c>script.sh: line 5: </c>,
    /// <c>bash: line 1: </c> under <c>-c</c>, <c>bash: </c> interactive (PsBash.Cmdlets.ShellDiagnostic, the one builder;
    /// docs/specs/error-handling.md "Diagnostic prefix").
    /// </summary>
    public const string DiagnosticPrefix = "[PsBash.Cmdlets.ShellDiagnostic]::Prefix($global:BASH_SOURCE, $global:BashPositional0)";

    /// <summary>
    /// Run <paramref name="statement"/> (the text of a run-time <c>eval</c>, transpiled alone so its lines count from 1)
    /// with its recorded lines counted from the eval's own line, as bash numbers them (VarFrameStack.BeginEvalLines).
    /// </summary>
    public static string EvalLinesScope(string statement) =>
        "[PsBash.Cmdlets.VarFrameStack]::BeginEvalLines(); try { " + statement
        + " } finally { [PsBash.Cmdlets.VarFrameStack]::EndEvalLines() }";

    /// <summary>
    /// Run <paramref name="statement"/> (a sourced file's text) on the file's own lines, also when sourced inside an eval,
    /// and put the caller back on its line afterwards (VarFrameStack.BeginSourceLines).
    /// </summary>
    public static string SourceLinesScope(string statement) =>
        "[PsBash.Cmdlets.VarFrameStack]::BeginSourceLines(); try {\n" + statement
        + "\n} finally { [PsBash.Cmdlets.VarFrameStack]::EndSourceLines() }";

    /// <summary>
    /// One <c>NAME=VALUE</c> of a traced assignment, for Write-BashXtrace: it prints the name bare and quotes only the
    /// value (<c>+ sp='p q'</c>; an empty value bare, <c>+ v=</c>). <paramref name="valueExpr"/> is a PowerShell expression.
    /// </summary>
    public static string XtraceAssignment(string name, string valueExpr) =>
        "@{ n = " + SingleQuote(name) + "; v = " + valueExpr + " }";

    /// <summary>
    /// <c>local</c> with no operand: bash's error outside a function (status 1), else status 0. Decided at run time, so a
    /// run-time <c>local</c> (<see cref="BuiltinAsSourceScript"/>) sees the caller's function. (bash also lists the
    /// function's locals: the emitter does that only for locals it sees declared, PsEmitter.EmitLocalListing.)
    /// </summary>
    public static string BareLocal => LocalInFunction(SetStatus(0));

    /// <summary>
    /// <paramref name="inFunction"/> when a function is running, else bash's <c>local: can only be used in a function</c>
    /// (status 1), which bash reports before it looks at any option or operand (<c>local -Q</c> at the top level).
    /// </summary>
    public static string LocalInFunction(string inFunction) =>
        "if ([PsBash.Cmdlets.VarFrameStack]::Current.Depth -eq 0) { " + ShellDiagnosticLine("'local: can only be used in a function'")
        + "; " + SetStatus(1) + " } else { " + inFunction + " }";
    /// <summary>
    /// A builtin the emitter writes, given an option it does not have (<see cref="BuiltinOptions"/>): bash's
    /// <c>PREFIX NAME: OPT: invalid option</c> and <c>NAME: usage: SYNOPSIS</c> (the text is the runtime's, so the synopsis
    /// is the one <c>help -s</c> prints), status 2, and the builtin does nothing.
    /// </summary>
    public static string BuiltinInvalidOption(string builtin, string option) =>
        ShellDiagnosticLine("[PsBash.Cmdlets.BashRuntime]::BuiltinInvalidOption(" + SingleQuote(builtin) + ", " + SingleQuote(option) + ")")
        + "; " + SetStatus(2);

    /// <summary>
    /// <see cref="BuiltinInvalidOption"/> decided at run time, for words the emitter cannot read (<c>cd $o</c>):
    /// <paramref name="argsExpr"/> (a PowerShell array expression) is scanned for an option not in
    /// <paramref name="letters"/>; one is the usage error (status 2), else <paramref name="body"/> runs.
    /// </summary>
    public static string RunTimeBuiltinOptionCheck(string builtin, string argsExpr, string letters, string body) =>
        "$__psbash_optErr = [PsBash.Cmdlets.BashRuntime]::BuiltinOptionError(" + SingleQuote(builtin) + ", [string[]]" + argsExpr + ", "
        + SingleQuote(letters) + ", '', " + DiagnosticPrefix + "); "
        + "if ($null -ne $__psbash_optErr) { Write-Error -Message $__psbash_optErr -ErrorAction Continue; " + SetStatus(2) + " } else { " + body + " }";
    /// <summary>A shell diagnostic <paramref name="message"/> (a PowerShell string expression) with bash's source prefix, on stderr.
    /// A message that is one single-quoted literal names a variable by its bash name (<c>path: readonly variable</c>, not the
    /// store name the AST carries: <see cref="BashNameCase.ToBashText"/>); an expression is code and is left alone.</summary>
    public static string ShellDiagnosticLine(string message) =>
        "Write-Error -Message (" + DiagnosticPrefix + " + " + (IsSingleQuotedLiteral(message) ? BashNameCase.ToBashText(message) : message) + ") -ErrorAction Continue";

    /// <summary>True when <paramref name="text"/> is exactly one <see cref="SingleQuote"/> literal (<c>'a''b'</c>).</summary>
    private static bool IsSingleQuotedLiteral(string text)
    {
        if (text.Length < 2 || text[0] != '\'' || text[^1] != '\'') return false;
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '\'') continue;
            if (text[i + 1] != '\'' || i + 1 == text.Length - 1) return false;
            i++;
        }
        return true;
    }

    /// <summary>Source line of the statement being emitted; the ERR trap reads it as <c>$LINENO</c>.</summary>
    [ThreadStatic] public static int TrapLine;

    /// <summary>True inside a function or subshell: bash fires the ERR trap there only under <c>set -E</c>.</summary>
    [ThreadStatic] public static bool ErrTrapNeedsErrtrace;

    /// <summary>
    /// Run statement(s) with errexit suppressed (an exempt context): no command inside —
    /// nor any function it calls — may end the script for failing.
    /// </summary>
    public static string ErrexitSuppressed(string statements) =>
        ErrexitSuppressVar + "++; try { " + statements + " } finally { " + ErrexitSuppressVar + "-- }";

    /// <summary>
    /// A negated pipeline (<c>! cmd</c>) at statement position: bash ignores errexit inside it only when <c>set -e</c> is
    /// on as it starts. Turned on inside (<c>f() { set -e; false; }; ! f</c>) it takes effect at once.
    /// <paramref name="mark"/> is a variable unique to this nesting level that remembers whether it suppressed.
    /// </summary>
    public static string ErrexitSuppressedIfOn(string statements, string mark) =>
        mark + " = [bool]$global:__BashErrexit; if (" + mark + ") { " + ErrexitSuppressVar + "++ }; try { " + statements
        + " } finally { if (" + mark + ") { " + ErrexitSuppressVar + "-- } }";

    /// <summary>
    /// A compound pipe stage of a pipeline at an armed <c>set -e</c> statement: the stage is a subshell that inherits
    /// <c>-e</c>, so its statements' errexit checks may fire although the pipeline as a whole runs under
    /// <see cref="ErrexitSuppressed"/>. Sets the suppression back to what it was before that pipeline
    /// (<paramref name="mark"/>, <see cref="ErrexitSuppressedFrom"/>) for the stage, and restores it however the stage
    /// ends. Absolute, never a decrement: stages of one pipeline run interleaved (a streamed read loop handles a record
    /// while its producer stage is still inside its own lift), so two decrements took the counter below zero, which
    /// reads as "suppressed" and let the loop run on past a failing command.
    /// </summary>
    public static string ErrexitLifted(string statements, string mark) =>
        mark + "l = " + ErrexitSuppressVar + "; " + ErrexitSuppressVar + " = " + mark + "; try { " + statements
        + " } finally { " + ErrexitSuppressVar + " = " + mark + "l }";

    /// <summary>
    /// <see cref="ErrexitSuppressed"/> for a pipeline with a stage that lifts it (<see cref="ErrexitLifted"/>):
    /// <paramref name="mark"/>, a variable unique to this nesting level, remembers the suppression before the pipeline.
    /// </summary>
    public static string ErrexitSuppressedFrom(string mark, string statements) =>
        mark + " = " + ErrexitSuppressVar + "; " + ErrexitSuppressed(statements);

    /// <summary>
    /// A command substitution's body under <c>set -e</c> in a script that mentions <c>inherit_errexit</c>: its statements
    /// carry errexit checks, and whether they may fire is decided at run time. With <c>shopt -s inherit_errexit</c> the
    /// substitution inherits <c>-e</c> (<c>$(echo one; false; echo two)</c> is <c>one</c>); without it the body runs
    /// suppressed, exactly as <see cref="ErrexitSuppressed"/> (bash turns <c>-e</c> off in a substitution).
    /// </summary>
    public static string InheritErrexitCapture(string statements) =>
        "$__psbash_ie = [PsBash.Cmdlets.InvokeBashShoptCommand]::ShoptOn('inherit_errexit'); if (-not $__psbash_ie) { "
        + ErrexitSuppressVar + "++ }; try { " + statements + " } finally { if (-not $__psbash_ie) { " + ErrexitSuppressVar + "-- } }";

    /// <summary>
    /// The FINAL operand of an <c>&amp;&amp;</c>/<c>||</c> list at statement position: it is NOT
    /// exempt (bash exits when it runs and fails), so it lifts one level of the list's
    /// suppression, and records its status in <c>$global:__BashErrexitTail</c> — the list's
    /// own status cannot tell "the last command failed" from "an earlier one failed and
    /// short-circuited it". Dot-sourced: current scope, streams, and is a valid chain operand.
    /// </summary>
    public static string ErrexitFinalOperand(string operand) =>
        ". { " + ErrexitSuppressVar + "--; try { " + operand + " } finally { " + ErrexitSuppressVar
        + "++ }; $global:__BashErrexitTail = $global:LASTEXITCODE }";

    /// <summary>
    /// An <c>&amp;&amp;</c>/<c>||</c> list at statement position under <c>set -e</c>: every operand
    /// runs suppressed except the final one (<see cref="ErrexitFinalOperand"/>), then the
    /// script leaves only if that final command ran and failed.
    /// </summary>
    public static string ErrexitAndOrList(string chain, string exitStatement) =>
        "$global:__BashErrexitTail = 0; " + ErrexitSuppressed(chain) + "; "
        + ErrexitCheck(exitStatement, "$global:__BashErrexitTail");

    // ─────────────────────────── RC-7 unquoted word-split splat ────────────────────────

    /// <summary>
    /// The array an unquoted ordinary <c>$var</c> operand expands into, for <c>@</c>-splatting:
    /// <c>@(ConvertTo-BashWords varRef)</c> — bash's word splitting on <c>$IFS</c> (leading / trailing IFS
    /// whitespace discarded, an empty or blank value gives NO word) followed by pathname expansion of every
    /// word that has a glob character. The OUTER <c>@(...)</c> is required: an empty result must stay an
    /// empty array that splats to nothing (a bare <c>$null</c> would inject one spurious empty argument).
    /// </summary>
    public static string WordSplitArray(string varRef) =>
        "@(ConvertTo-BashWords " + varRef + ")";

    /// <summary>
    /// The fields of one word given as segments (<c>PsBash.Cmdlets.BashWordFields</c>): <paramref name="segments"/> is a
    /// comma list of <see cref="WordSegment"/> / <see cref="WordSegmentGroup"/> items. The one general form of word
    /// expansion (spec emitter-strategy.md "Word expansion: one primitive").
    /// </summary>
    public static string WordFieldsArray(string segments) =>
        "@(ConvertTo-BashWords -Parts @(" + segments + "))";

    /// <summary>One segment of <see cref="WordFieldsArray"/>: its kind, then its value (a PowerShell expression; anything but
    /// a quoted literal is parenthesised so it stays one element of the comma list).</summary>
    public static string WordSegment(int kind, string valueExpr)
    {
        string k = kind.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool bare = valueExpr.Length > 1 && valueExpr[0] == '\'' && valueExpr[^1] == '\'' && valueExpr.IndexOf('\'', 1) == valueExpr.Length - 1
            || valueExpr == "$null";
        return k + ", " + (bare ? valueExpr : "(" + valueExpr + ")");
    }

    /// <summary>
    /// A group of segments chosen at run time (<c>${v:-word}</c>: the value or the word): <paramref name="probeExpr"/> is
    /// <c>x</c> exactly when <paramref name="thenSegments"/> apply, otherwise <paramref name="elseSegments"/> (either may be
    /// empty). The group is one element, an array, in the place of a kind.
    /// </summary>
    public static string WordSegmentGroup(string probeExpr, string thenSegments, string elseSegments) =>
        "$(if ([string](" + probeExpr + ") -ceq 'x') { ,@(" + thenSegments + ") } else { ,@(" + elseSegments + ") })";

    /// <summary>An unquoted list expansion alone (<c>$@</c>, <c>${a[@]}</c>, <c>${@:2}</c>): the values joined with IFS[0] and
    /// split again; with an empty IFS each value is its own field.</summary>
    public static string ListFieldsArray(string valuesExpr) =>
        "@(ConvertTo-BashWords -Prefix '' -Suffix '' -Fields " + valuesExpr + ")";

    /// <summary>An unquoted arithmetic result alone (<c>$((n))</c>): split on IFS (it holds no glob character), without the
    /// cmdlet: <c>IFS=1</c> makes <c>$((11+0))</c> two empty fields.</summary>
    public static string NumberFieldsArray(string valueExpr) =>
        "@([PsBash.Cmdlets.BashWordFields]::SplitNumber(" + valueExpr + "))";

    /// <summary>
    /// The array an unquoted GLOB word (<c>*.txt</c>, <c>src/*</c>, <c>$d/[ab]*</c>) pathname-expands into:
    /// <c>@(ConvertTo-BashGlob &lt;pattern&gt; [-ForCmdlet])</c>. <paramref name="patternExpr"/> is a PowerShell
    /// expression for the pattern text in bash's dialect (quoted glob characters already backslash-escaped, see
    /// <c>PsEmitter.EmitGlobPatternExpr</c>). The outer <c>@(...)</c> keeps an empty result (nullglob) an
    /// empty array that splats to nothing.
    /// </summary>
    public static string GlobWordArray(string patternExpr) =>
        "@(ConvertTo-BashGlob " + patternExpr + ")";

    // ───────────────────────── Stdout to stderr (`>&2`) ──────────────────────────

    /// <summary>
    /// The tail stage that sends a command's stdout to bash's stderr (<c>cmd &gt;&amp;2</c>): each
    /// record becomes an error-stream record (<c>Write-BashHostStderr</c>), so a LATER <c>2&gt;</c>
    /// on an enclosing command, <c>2&gt;&amp;1</c>, or <c>2&gt;/dev/null</c> applies to it as bash
    /// expects, while <c>$?</c> and the exit status stay the command's own.
    /// </summary>
    public const string StdoutToStderrStage = " | Write-BashHostStderr";

    /// <summary>
    /// A bare <c>2&gt;&amp;1</c> (stderr to the terminal's stdout): PowerShell's merge plus a stage
    /// that turns each merged error record into a stdout text record. Left as ErrorRecords, an
    /// enclosing <c>2&gt; f</c> stage or a capture would read them as stderr again —
    /// <c>{ echo e &gt;&amp;2; } 2&gt;&amp;1</c> under an outer <c>2&gt; f</c> wrote <c>e</c> to f.
    /// </summary>
    public const string MergeStderrIntoStdout = " 2>&1 | Invoke-BashRedirect -MergeErrors";

    // ───────────────────────── Redirect target without output ──────────────────────────

    /// <summary>
    /// Opens a <c>&gt; file</c> / <c>&gt;&gt; file</c> target for a command that writes NOTHING (<c>:</c>,
    /// <c>true</c>, <c>false</c>, a bare <c>&gt; f</c>): <c>@() | Invoke-BashRedirect -Path f [-Append]</c>.
    /// The cmdlet opens its target in <c>BeginProcessing</c> (truncate, or create for <c>-Append</c>)
    /// whether or not a record arrives, which is exactly bash's "a redirection creates the file".
    /// </summary>
    public static string TouchRedirectTarget(string target, bool append, bool force = false) =>
        "@() | Invoke-BashRedirect -Path " + target + (append ? " -Append" : "") + (force ? " -Force" : "");

    // ───────────────────────── Descriptor table steps ──────────────────────────

    /// <summary>
    /// <c>Invoke-BashExecFd N 'OP' TARGET</c>: open, duplicate or close numbered descriptor <paramref name="fd"/> in the
    /// shell's descriptor table (operator <c>&lt;&lt;&lt;</c>: TARGET is the text to read). <paramref name="targetArg"/> is an
    /// emitted argument (a quoted word, or a parenthesized expression).
    /// </summary>
    public static string ExecFd(int fd, string op, string targetArg) =>
        "Invoke-BashExecFd " + fd.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + SingleQuote(op) + " " + targetArg;

    /// <summary><c>{name}OP TARGET</c>: the runtime allocates a descriptor (10 up), opens it and stores its number in name.</summary>
    public static string ExecFdNamed(string variable, string op, string targetArg) =>
        "Invoke-BashExecFd -Variable " + SingleQuote(variable) + " -Operator " + SingleQuote(op) + " -Target " + targetArg;

    /// <summary><c>N&gt;&amp;-</c>: close descriptor <paramref name="fd"/>.</summary>
    public static string ExecFdClose(int fd) => ExecFd(fd, ">&", "'-'");

    /// <summary>
    /// The <c> | Invoke-BashRedirect …</c> stage that applies a command's final stdout/stderr
    /// destinations (see <c>PsEmitter.AppendRedirectTail</c>). Targets are emitted ARGUMENT-mode
    /// words (<c>'f'</c>, <c>$env:d/f</c>, <c>$null</c>); <paramref name="stdoutPath"/> null = stdout
    /// passes through, <c>"$null"</c> = discarded. <paramref name="truncate"/> / <paramref name="touch"/>
    /// are superseded targets, passed through <see cref="ArgWordArray"/> so each stays an argument.
    /// <paramref name="openPlan"/> (null: none needed) is the order bash opens the targets in and where each open error
    /// is reported (PsEmitter.RedirectOpenPlan, PsBash.Cmdlets.RedirectOpenPlan).
    /// </summary>
    public static string RedirectStage(
        string? stdoutPath, bool append, string? errorPath, bool errorAppend, bool passErrors,
        IReadOnlyList<string> truncate, IReadOnlyList<string> touch,
        bool force = false, bool errorForce = false, string? openPlan = null, bool readWrite = false)
    {
        var sb = new System.Text.StringBuilder(" | Invoke-BashRedirect");
        if (stdoutPath is not null)
        {
            sb.Append(" -Path ").Append(stdoutPath);
            if (append) sb.Append(" -Append");
            if (force) sb.Append(" -Force");
            if (readWrite) sb.Append(" -ReadWrite");
        }
        if (errorPath is not null)
        {
            sb.Append(" -ErrorPath ").Append(errorPath);
            if (errorAppend) sb.Append(" -ErrorAppend");
            if (errorForce) sb.Append(" -ErrorForce");
        }
        if (passErrors) sb.Append(" -PassErrors");
        if (truncate.Count > 0) sb.Append(" -Truncate ").Append(ArgWordArray(truncate));
        if (touch.Count > 0) sb.Append(" -Touch ").Append(ArgWordArray(touch));
        if (openPlan is not null) sb.Append(" -OpenPlan ").Append(SingleQuote(openPlan));
        return sb.ToString();
    }

    /// <summary>
    /// An array of ARGUMENT-mode words as one argument: <c>@(&amp; { $args } w1 w2)</c>. The words are
    /// arguments to a script block, never spliced into an expression (where <c>/</c> divides and a
    /// bare word is a command).
    /// </summary>
    public static string ArgWordArray(IReadOnlyList<string> argWords) =>
        "@(& { $args } " + string.Join(' ', argWords) + ")";

    // ───────────────────────────── Bash pattern tests ─────────────────────────────

    // Every bash pattern context goes to the runtime with the pattern as CANONICAL text (BashPattern: quoted parts
    // backslash-escaped, an unquoted expansion's value active), built by PsEmitter.EmitGlobPatternExpr; the one
    // compiler matches it there. No context builds a regex here (guard PatternCompilerGuardTests).

    /// <summary>
    /// <c>[PsBash.Cmdlets.BashPatternMatch]::MatchesPattern(value, pattern)</c>: a bash pattern test (case arm,
    /// <c>[[ == ]]</c>) — case-sensitive unless <c>shopt -s nocasematch</c> is on at run time.
    /// </summary>
    public static string PatternTest(string valueExpr, string patternExpr) =>
        "[PsBash.Cmdlets.BashPatternMatch]::MatchesPattern(" + valueExpr + ", " + patternExpr + ")";

    /// <summary>
    /// An unquoted expansion spliced into the pattern of a <c>case</c> arm or a glob word: its value, with its extglob
    /// syntax made literal while <c>shopt extglob</c> is off (<c>BashPatternMatch.ValuePattern</c>).
    /// </summary>
    public static string PatternValue(string valueExpr) =>
        "[PsBash.Cmdlets.BashPatternMatch]::ValuePattern(" + valueExpr + ")";

    /// <summary><c>${v#p}</c> <c>${v##p}</c> (<paramref name="suffix"/> false) and <c>${v%p}</c> <c>${v%%p}</c>.</summary>
    public static string PatternRemove(string valueExpr, string patternExpr, bool suffix, bool longest) =>
        "[PsBash.Cmdlets.BashPatternMatch]::Remove(" + valueExpr + ", " + patternExpr + ", "
        + (suffix ? "$true" : "$false") + ", " + (longest ? "$true" : "$false") + ")";

    /// <summary>Which <c>${v/p/r}</c> form <see cref="PatternSubstitute"/> emits.</summary>
    public enum SubstituteForm { First = 0, All = 1, Start = 2, End = 3 }

    /// <summary><c>${v/p/r}</c> and its <c>// /# /%</c> forms; <paramref name="templateExpr"/> is the replacement template
    /// (literal parts escaped with <c>BashPattern.EscapeReplacement</c>, a bare <c>&amp;</c> the match).</summary>
    public static string PatternSubstitute(string valueExpr, string patternExpr, string templateExpr, SubstituteForm form) =>
        "[PsBash.Cmdlets.BashPatternMatch]::Substitute(" + valueExpr + ", " + patternExpr + ", " + templateExpr + ", "
        + ((int)form).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    /// <summary><c>${v^p}</c> <c>${v^^p}</c> <c>${v,p}</c> <c>${v,,p}</c>; <paramref name="patternExpr"/> null when no
    /// pattern was written.</summary>
    public static string PatternCaseModify(string valueExpr, string? patternExpr, bool upper, bool all) =>
        "[PsBash.Cmdlets.BashPatternMatch]::ModifyCase(" + valueExpr + ", " + (patternExpr ?? "$null") + ", "
        + (upper ? "$true" : "$false") + ", " + (all ? "$true" : "$false") + ")";

    /// <summary>An unquoted expansion's value in a replacement template (inserted as it is while
    /// <c>patsub_replacement</c> is on).</summary>
    public static string ReplacementText(string valueExpr) =>
        "[PsBash.Cmdlets.BashPatternMatch]::ReplacementText(" + valueExpr + ")";

    /// <summary>A quoted run-time part of a replacement template: its literal text.</summary>
    public static string LiteralReplacement(string valueExpr) =>
        "[PsBash.Cmdlets.BashPatternMatch]::LiteralReplacement(" + valueExpr + ")";

    /// <summary>
    /// <c>[[ a == b ]]</c> with a run-time RHS: ordinal string equality, ignore-case under
    /// <c>nocasematch</c> (PowerShell's <c>-eq</c> is case-insensitive).
    /// </summary>
    public static string StringEqualsTest(string leftExpr, string rightExpr) =>
        "[PsBash.Cmdlets.BashPatternMatch]::StringEquals(" + leftExpr + ", " + rightExpr + ")";

    /// <summary>
    /// A <c>[ ]</c> expression whose words are only known at run time, evaluated by the runtime <c>test</c> as a
    /// boolean: <paramref name="wordItems"/> are the <c>;</c>-separated items of the word array. Status 2 flags a
    /// bad expression (<c>$global:__BashTestBad</c>). LASTEXITCODE is left as it was, as an inline test leaves it:
    /// <c>while [ $n -lt 4 ]</c> ended with the false test's 1 where bash's loop status is its body's.
    /// </summary>
    public static string RunTimeTest(string wordItems) =>
        "$(& { $__psbT = @(" + wordItems + "); $__psbS = $global:LASTEXITCODE; Invoke-BashTest @__psbT; "
        + "$__psbR = $global:LASTEXITCODE; $global:LASTEXITCODE = $__psbS; "
        + "if ($__psbR -eq 2) { $global:__BashTestBad = $true }; $__psbR -eq 0 })";

    // ─────────────────────── return out of eval text ────────────────────────

    /// <summary>The global that carries a <c>return</c> in run-time eval text out to the function that ran the eval.</summary>
    public const string EvalReturnVar = "$global:__BashEvalReturn";

    /// <summary>
    /// Emitted before the PowerShell <c>return</c> of a bash <c>return</c> in eval or source text (outside any function
    /// in that text): the <c>return</c> only leaves the <c>Invoke-Expression</c>, so the flag tells the eval site to
    /// leave its function too. bash's <c>f() { eval return; echo x; }</c> never prints x.
    /// </summary>
    public const string EvalReturnSignal = EvalReturnVar + " = $true; ";

    /// <summary>
    /// After a run-time eval: return when its text returned. Inside a function the flag is spent here
    /// (<paramref name="inFunction"/>); in eval text the flag stays set, so the next eval site out returns as well.
    /// </summary>
    public static string EvalReturnCheck(bool inFunction) =>
        "if (" + EvalReturnVar + ") { " + (inFunction ? EvalReturnVar + " = $false; " : "") + "return }";

    // ─────────────────────── a pipe stage's starting status ────────────────────────

    /// <summary>The status from before the pipeline, captured by <see cref="WithPipeEntryStatus"/>.</summary>
    public const string PipeEntryStatusVar = "$__psbash_pst";

    /// <summary>
    /// The first statement of a pipe stage whose body is shell code: it starts with the status from before the
    /// pipeline (bash: each stage is a subshell of the shell as it was), not what the stages started before it left.
    /// </summary>
    public const string SeedStageStatus = "$global:LASTEXITCODE = " + PipeEntryStatusVar + "; ";

    /// <summary>
    /// A pipeline whose stages <see cref="SeedStageStatus"/>: the status is captured before any stage starts, in a
    /// dot-sourced block, so the capture is a statement wherever the pipeline sits and a stage's child scope reads it.
    /// </summary>
    public static string WithPipeEntryStatus(string pipelineText) =>
        ". { " + PipeEntryCapture + "; " + pipelineText + " }";

    /// <summary>The capture statement of <see cref="WithPipeEntryStatus"/>.</summary>
    public const string PipeEntryCapture = PipeEntryStatusVar + " = $global:LASTEXITCODE";

    // ─────────────────────── pipefail / PIPESTATUS pipeline ────────────────────────

    /// <summary>The slot array each stage of a <see cref="PipefailPipeline"/> writes its status into (Invoke-BashPipeStage -Slot).</summary>
    public const string PipeStatusSlots = "$__pf";

    /// <summary>
    /// A pipeline whose stages (<paramref name="stages"/> of them, each an <c>Invoke-BashPipeStage -Slot $__pf</c>) report
    /// their own status: PIPESTATUS is every stage's status, and under pipefail the status is the last stage that failed
    /// (0 when none did). ONE pipeline element (<c>. { … }</c>: dot-sourced, so it runs in the current scope and its output
    /// streams) valid wherever a pipeline is: a statement, an <c>&amp;&amp;</c>/<c>||</c> operand, a condition, inside
    /// <c>$( )</c>. As the bare statement list it used to be (<c>$__pf = …; stages; …</c>), only its first statement, an
    /// assignment, was the operand after <c>&amp;&amp;</c> and the whole script failed to parse ("Unexpected token '='").
    /// </summary>
    public static string PipefailPipeline(int stages, string stagesText) =>
        ". { " + PipeStatusSlots + " = [int[]]::new(" + stages.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); "
        + stagesText + "; $global:PIPESTATUS = " + PipeStatusSlots + "; "
        + "if ($global:__BashPipefail) { $global:LASTEXITCODE = 0; foreach ($__s in " + PipeStatusSlots + ") { if ($__s -ne 0) { $global:LASTEXITCODE = $__s } } } }";

    /// <summary>
    /// One stage of a <see cref="PipefailPipeline"/>: <paramref name="stageBlock"/> run with its status kept in slot
    /// <paramref name="index"/>. The first stage reads no pipe, so its block runs in a child block of its own
    /// (<c>&amp; { … }</c>): Invoke-BashPipeStage steps the block as a steppable pipeline, and PowerShell drops a
    /// <c>2&gt;&amp;1</c> that sits in a steppable pipeline's own top-level pipeline (the error goes to the stage's stderr), so
    /// <c>awk -Q 2&gt;&amp;1 | wc -l; echo ${PIPESTATUS[0]}</c> printed the usage text and counted 0 lines. A later
    /// stage's block must receive the pipe, so it stays as it is.
    /// </summary>
    public static string PipefailStage(int index, string stageBlock) =>
        "Invoke-BashPipeStage -Slot " + PipeStatusSlots + " -Index " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " -Script { " + (index == 0 ? "& { " + stageBlock + " }" : stageBlock) + " }";

    /// <summary>The global <c>Invoke-BashFc</c> leaves a re-run command in (<c>fc -s</c>, <c>fc -e -</c>), for <see cref="FcTakeRerun"/>.</summary>
    public const string FcRerunVariable = "__BashFcRerun";

    /// <summary>
    /// <c>fc</c>, up to its re-run: <paramref name="fcCommand"/> (the <c>Invoke-BashFc</c> call) with the re-run slot
    /// cleared first, then the command it chose moved into <c>$__psbash_eval_src</c> (null when it chose none, which
    /// <see cref="RunBashTextCall"/> skips). The emitter follows it with that call, as eval does: a cmdlet must not run
    /// script itself.
    /// </summary>
    public static string FcTakeRerun(string fcCommand) =>
        "$global:" + FcRerunVariable + " = $null; " + fcCommand + "; $__psbash_eval_src = $global:" + FcRerunVariable
        + "; $global:" + FcRerunVariable + " = $null; ";

    /// <summary>
    /// The call head of a builtin the emitter implements, run at run time as its own static form with the words it gets
    /// (<see cref="BuiltinAsSourceScript"/>): <c>set -o "$opt"</c>, whose option is known only then.
    /// </summary>
    public static string BuiltinAtRunTime(string name) =>
        "& ([scriptblock]::Create([PsBash.Core.Parser.PsBuild, PsBash.Transpiler]::BuiltinAsSourceScript(" + SingleQuote(name) + ")))";

    /// <summary>
    /// A command whose name is known only at run time and whose form depends on it: <paramref name="whenNamed"/> when the
    /// name (<paramref name="nameValue"/>, a string expression) is exactly <paramref name="name"/>, else
    /// <paramref name="otherwise"/>. One pipeline element (dot-sourced: the caller's scope).
    /// </summary>
    public static string RunTimeNameBranch(string nameValue, string name, string whenNamed, string otherwise) =>
        ". { if ([string](" + nameValue + ") -ceq " + SingleQuote(name) + ") { " + whenNamed + " } else { " + otherwise + " } }";

    // ─────────────────────── bash text run at run time ────────────────────────

    /// <summary>
    /// The ONE emitted call of bash text run at run time (<c>eval</c>, <c>fc -s</c>: PsEmitter.RunTimeBashTextCall): the
    /// text in <c>$__psbash_eval_src</c>, unless blank, run by <see cref="RunBashTextAtRunTime"/> under the nesting bound
    /// (5: <c>history -s 'fc -s'; fc -s</c> or a self-evaluating eval fails instead of taking the host down), with the call
    /// site's loop mode (<paramref name="loopModeSet"/>, <see cref="LoopModeSet"/> or empty: what a <c>break</c> in the
    /// text leaves), its function's locals (<paramref name="localsArg"/>) and <paramref name="returnCheck"/>
    /// (<see cref="EvalReturnCheck"/> or empty: a <c>return</c> in the text leaves the function it ran in). A call site
    /// that called the runner itself missed some of these (fc's <c>return 7</c> did not return;
    /// guard <c>RunBashTextCallGuardTests</c>).
    /// </summary>
    public static string RunBashTextCall(string label, string loopModeSet, string localsArg, string returnCheck) =>
        // Get-Variable -ErrorAction SilentlyContinue: the depth probe is safe under Set-StrictMode -Version Latest
        // (set -u), where a plain $global:X throws on its first read.
        "if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { "
        + "$__psbash_eval_depth = [int](Get-Variable -Name '__BashEvalDepth' -Scope Global -ValueOnly -ErrorAction SilentlyContinue); "
        + "if ($__psbash_eval_depth -ge 5) { throw '" + label + ": nesting depth limit (5) exceeded; possible infinite recursion' }; "
        + "$global:__BashEvalDepth = $__psbash_eval_depth + 1; "
        + "try { " + loopModeSet + RunBashTextAtRunTime(localsArg, label) + " } finally { $global:__BashEvalDepth = $__psbash_eval_depth } "
        + (returnCheck.Length > 0 ? returnCheck + " " : "") + "}";

    /// <summary>
    /// Transpile the bash text in <c>$__psbash_eval_src</c> as eval text and run it here (Invoke-Expression: the
    /// caller's scope), its lines counted from the call's own line. A syntax error in it is the command's own failure
    /// (<c>ps-bash: LABEL: …</c>, status 2), not an exception out of the script. Private: an emitted call goes through
    /// <see cref="RunBashTextCall"/>; the one other caller, <see cref="BuiltinAsSourceScript"/>, runs a static builtin
    /// command (<c>eval '…'</c>, <c>fc -s</c>) whose own emitted form makes that call.
    /// </summary>
    /// <param name="localsArg"><c>, [string[]]@(…)</c>: the calling function's <c>local</c> names, or <c>, $null</c>.</param>
    /// <param name="label">The command named in a syntax-error diagnostic.</param>
    private static string RunBashTextAtRunTime(string localsArg, string label) =>
        "$__psbash_eval_pwsh = $null; try { $__psbash_eval_pwsh = [PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile("
        + "$__psbash_eval_src, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval" + localsArg
        + ", ($null -ne (Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue))) } "
        + "catch { Write-Error -Message (" + DiagnosticPrefix + " + '" + label + ": ' + $(if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message })) -ErrorAction Continue; $global:LASTEXITCODE = 2 }; "
        + "if ($null -ne $__psbash_eval_pwsh) { " + EvalReturnVar + " = $false; " + EvalLinesScope("Invoke-Expression $__psbash_eval_pwsh") + " }";

    /// <summary>
    /// The body of the script block Resolve-BashCommand (and <c>command</c> / <c>builtin</c>) runs for a builtin named at
    /// run time that the emitter implements (<see cref="BashBuiltins.RunAsSource"/>): its operands (<c>$args</c>) become
    /// the static command <c>NAME 'op'…</c> (<see cref="BashBuiltins.SourceFor"/>), run through
    /// <see cref="RunBashTextAtRunTime"/>, so <c>c=eval; $c 'x=1'</c> is exactly <c>eval 'x=1'</c>. Records piped into the
    /// block become that command's stdin. Created unbound by the caller, it runs in the caller's session state.
    /// The block is a scope of its own (<c>&amp;</c>), so a <c>local</c> it declares parks the caller's value
    /// (<c>$__lsd_NAME</c>) here: <see cref="PublishParkedLocals"/> hands it to the bash function that called.
    /// </summary>
    public static string BuiltinAsSourceScript(string name)
    {
        var run = RunBashTextAtRunTime(", $null", name);
        return "$__psbash_eval_src = [PsBash.Core.Parser.BashBuiltins, PsBash.Transpiler]::SourceFor(" + SingleQuote(name) + ", [string[]]$args); "
            + "$__psbash_bsin = @(" + StageInput + "); "
            + "try { if ($__psbash_bsin.Count -gt 0) { " + StdinScope(run, "__psbash_bsin_prev", "$__psbash_bsin") + " } else { " + run + " } } "
            + "finally { " + PublishParkedLocals + " }";
    }

    /// <summary>
    /// Move each <c>$__lsd_NAME</c> slot (a <c>local</c>'s parked caller value) parked in this scope up to the nearest bash
    /// function's scope (the one holding <c>$__bp</c>, <see cref="FunctionPrologue"/>), whose end restores it. Run where a
    /// <c>local</c> may have run in a scope of its own: <c>h() { c=local; $c v=7; }</c> leaked v past h.
    /// </summary>
    public const string PublishParkedLocals =
        "foreach ($__psbash_lsd in @(Get-Variable -Name '__lsd_*' -Scope 0 -ErrorAction Ignore)) { "
        + "for ($__psbash_s = 0; $__psbash_s -lt 64; $__psbash_s++) { "
        + "try { $__psbash_fn = Get-Variable -Name __bp -Scope $__psbash_s -ErrorAction Ignore } catch { break }; if ($null -eq $__psbash_fn) { continue }; "
        + "if ($__psbash_s -gt 0 -and $null -eq (Get-Variable -Name $__psbash_lsd.Name -Scope $__psbash_s -ValueOnly -ErrorAction Ignore)) { "
        + "Set-Variable -Name $__psbash_lsd.Name -Value $__psbash_lsd.Value -Scope $__psbash_s }; break } }";

    // ─────────────────────── Null-safe pipeline text extraction ────────────────────────

    /// <summary>
    /// A <c>ForEach-Object</c> body that extracts a pipeline object's <c>BashText</c>
    /// (else stringifies it), null-safe. The <c>$null -ne $_</c> guard is load-bearing:
    /// <c>$_.PSObject.Properties['BashText']</c> throws "Cannot index into a null array"
    /// on a <c>$null</c> item, and short-circuit order means the guard MUST precede the
    /// property probe. Used to drain <c>while read</c> input.
    /// </summary>
    public const string NullSafeBashText =
        "if ($null -ne $_ -and $_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" }";

    /// <summary>
    /// The PowerShell value of an array bash computes (<see cref="PsBash.Core.ShellVariables.Computed"/>): the call
    /// stack, the version parts, the groups, the directory stack, the alias table. Null for any other name, whose
    /// elements are its own PowerShell variable. The one table for these: every array read the emitter writes asks it,
    /// and so does <c>Get-BashIndirectValue</c> for <c>${!ref}</c>.
    /// </summary>
    public static string? ShellArrayExpr(string name) => name switch
    {
        "BASH_VERSINFO" => "$global:BashVersionInfo",
        "BASH_SOURCE" => "@(" + BashSourceArray + ")",
        "FUNCNAME" => "@(Get-BashFuncNames)",
        "BASH_LINENO" => "@(" + BashLineNoArray + ")",
        "GROUPS" => "@([PsBash.Cmdlets.ShellArrays]::Groups())",
        "DIRSTACK" => "@([PsBash.Cmdlets.ShellArrays]::DirStack($ExecutionContext.SessionState))",
        "BASH_ALIASES" => "$([PsBash.Cmdlets.ShellArrays]::Aliases($ExecutionContext.SessionState))",
        _ => null,
    };

    // ───────────────────────────── Bash function definitions ─────────────────────────────

    /// <summary>
    /// The text every bash function body starts with (<c>function f {</c> + this + body + <see cref="FunctionEpilogue"/>
    /// <c>}</c>): save/restore of <c>$global:BashPositional</c> so a recursive call sees its own <c>$1 $@ $#</c>. Also
    /// the signature <c>declare -f</c>/<c>-F</c> use to tell a bash-defined function from the runtime's own.
    /// A call <c>f -i x</c> is emitted bare, so PowerShell marks the <c>-i</c> in <c>$args</c> as a parameter name, and
    /// a later splat (<c>grep "$@"</c>) bound it as one ("-i" is ambiguous / a prefix of InputObject). Each string is
    /// re-read as a plain <c>[string]</c>, which drops that mark; other values (a FileInfo, an int) are kept as they are.
    /// The body starts without the caller's ERR trap unless errtrace is on (<c>ErrTrapInheritance</c>; <c>$__bet</c>).
    /// </summary>
    public const string FunctionPrologue = " $__bp = $global:BashPositional; $global:BashPositional = @(foreach ($__psba in $args) { if ($__psba -is [string]) { [string]$__psba } else { $__psba } }); $__bet = [PsBash.Cmdlets.VarFrameStack]::EnterFunction($ExecutionContext.SessionState, $MyInvocation.MyCommand.Name); try { ";

    /// <summary>
    /// The statement after every bash function definition: it records the file the definition ran in, the function's
    /// BASH_SOURCE entry and diagnostic name while it runs (<c>PsBash.Cmdlets.FunctionSources</c>).
    /// </summary>
    public static string FunctionDefined(string name) =>
        "[PsBash.Cmdlets.FunctionSources]::Defined($ExecutionContext.SessionState, " + SingleQuote(name) + ")";

    /// <summary>The text every bash function body ends with (see <see cref="FunctionPrologue"/>).</summary>
    public const string FunctionEpilogue = " } finally { [PsBash.Cmdlets.VarFrameStack]::ExitFunction($ExecutionContext.SessionState, $__bet); $global:BashPositional = $__bp } ";

    // ─────────────────── bash variable contexts (PsBash.Cmdlets.VarFrameStack) ───────────────────

    /// <summary>
    /// <c>local -A m</c> / <c>local -a q</c> with no value: a new local of that kind in this call's scope (an empty
    /// <c>BashAssoc</c> / array), unless this call already holds <paramref name="name"/> as that kind (bash keeps a
    /// redeclared local's elements). Held as the other kind: bash's <c>local: q: cannot convert associative to indexed
    /// array</c>, status 1, the local unchanged; else status 0. A caller's variable of either kind is shadowed, not converted.
    /// <paramref name="builtin"/> is the word as invoked (<c>local</c>, or <c>declare</c> in a function).
    /// </summary>
    public static string DeclareLocalArray(string builtin, string name, bool assoc)
    {
        string held = "$__psbLk.Value";
        string other = "[PsBash.Cmdlets.BashAssoc]::" + (assoc ? "IsIndexedValue(" : "IsAssocValue(") + held + ")";
        string same = assoc ? "[PsBash.Cmdlets.BashAssoc]::IsAssocValue(" + held + ")" : "(" + held + " -is [array])";
        return "$__psbLk = Get-Variable -Name " + SingleQuote(name) + " -Scope 0 -ErrorAction Ignore; "
            + "if ($null -eq $__psbLk) { [PsBash.Cmdlets.VarFrameStack]::LocalAttributes(" + SingleQuote(name) + ") }; "
            + "if ($null -ne $__psbLk -and " + other + ") { "
            + ShellDiagnosticLine(SingleQuote(builtin + ": " + name + ": cannot convert "
                + (assoc ? "indexed to associative" : "associative to indexed") + " array"))
            + "; $global:LASTEXITCODE = 1 } else { if ($null -eq $__psbLk -or -not " + same + ") { $" + name + " = "
            // Declared, not yet assigned: `declare -p` shows `declare -A m` (no `=()`), as for a top-level `declare -A m`.
            + (assoc ? "[PsBash.Cmdlets.BashAssoc]::Unset()" : "@(); [PsBash.Cmdlets.BashShellState]::MarkAttribute(" + SingleQuote(name) + ", '~')")
            + " }; $global:LASTEXITCODE = 0 }";
    }

    /// <summary>Text of every <see cref="DeclareLocalArray"/>, which sets the status itself on each path.</summary>
    public const string DeclareLocalArrayMark = "$__psbLk = Get-Variable -Name ";

    /// <summary>
    /// <c>local name</c>: records the local's frame and yields the value a <c>local</c> without an initializer leaves
    /// ($null = unset). Called with the name's current value, so it must run before any assignment.
    /// </summary>
    public static string DeclareLocal(string name)
        => "[PsBash.Cmdlets.VarFrameStack]::DeclareLocal(" + SingleQuote(name) + ", $env:" + name + ")";

    /// <summary>
    /// The run-time <c>local NAME</c> (a shell-variable local, and every local of text whose function is unknown when it is
    /// emitted: eval text, a sourced file, <c>local "$1"</c>): park the caller's value in this call's <c>$__lsd_NAME</c>
    /// once (the function's finally puts it back, PsEmitter.EmitFunction), then take the frame (<see cref="DeclareLocal"/>).
    /// With <paramref name="keepsValue"/> an initializer follows and assigns (it may still read the caller's value);
    /// without, the name takes the value a bare <c>local</c> leaves. <paramref name="inWrittenFunction"/>: see
    /// <see cref="NotParked"/>. One source for <c>local</c> and <see cref="LocalWhenInFunction"/>.
    /// </summary>
    public static string RunTimeLocal(string name, bool keepsValue, bool inWrittenFunction) =>
        "if (" + NotParked(name, inWrittenFunction) + ") { $__lsd_" + name + " = @{ v = $env:" + name + " } }; "
        + (keepsValue ? "[void]" + DeclareLocal(name) : "$env:" + name + " = " + DeclareLocal(name));

    /// <summary>
    /// "This call has not parked NAME yet". A written function sets its <c>$__lsd_NAME</c> to $null on entry, so the plain
    /// read sees its own slot. Text built at run time has no such entry: PowerShell's dynamic scope would show it the
    /// CALLING function's slot, so it looks in its own scope only. REGRESSION: <c>d3(){ declare "$1"; d4 "$2"; }; d4(){
    /// declare "$1"; }</c> took d3's slot as d4's own, so d4's declaration wrote d3's local (bash: each its own).
    /// </summary>
    public static string NotParked(string name, bool inWrittenFunction) =>
        inWrittenFunction ? "$null -eq $__lsd_" + name
            : "$null -eq (Get-Variable -Name '__lsd_" + name + "' -Scope 0 -ValueOnly -ErrorAction Ignore)";

    /// <summary>
    /// <c>declare</c>/<c>typeset</c> (no <c>-g</c>) in text that cannot know whether a function runs it: while one runs,
    /// NAME becomes its local (<see cref="RunTimeLocal"/>, the run-time <c>local</c>'s own frame); at the top level the
    /// declaration that follows is global. Emitted before the declaration's write.
    /// </summary>
    public static string LocalWhenInFunction(string name, bool keepsValue) =>
        InFunctionAtRunTime + RunTimeLocal(name, keepsValue, inWrittenFunction: false) + " }";

    /// <summary>
    /// A declaration in text that cannot know whether a function runs it, whose two forms differ in more than a frame:
    /// <paramref name="inFunction"/> while one runs (<c>local -a a=(1 2)</c>, <c>declare -A m</c>: the function's own
    /// array, PsBuild.DeclareLocalArray), else <paramref name="atTop"/> (the shell's global).
    /// </summary>
    public static string WhenInFunction(string inFunction, string atTop) =>
        InFunctionAtRunTime + inFunction + " } else { " + atTop + " }";

    /// <summary>The opening of a block that runs only while a bash function runs (<see cref="LocalWhenInFunction"/>).</summary>
    private const string InFunctionAtRunTime = "if ([PsBash.Cmdlets.VarFrameStack]::Current.Depth -gt 0) { ";

    /// <summary>
    /// A function call's temp binding frame (<c>name=v f</c>, owned by the called function), pushed once the binding is
    /// in force; the value it hides is the <c>$__saved_name</c> the binding restores.
    /// </summary>
    public static string PushTempFrame(string name)
        => "$__ptf_" + name + " = [PsBash.Cmdlets.VarFrameStack]::Temp(" + SingleQuote(name) + ", $__saved_" + name + ", $true)";

    /// <summary>
    /// <c>name=v eval ...</c> / <c>source</c> / <c>.</c>: saves name's value on the runtime's binding stack (these run in
    /// this PowerShell scope, where a nested binding would overwrite a <c>$__saved_name</c>) and pushes its frame.
    /// </summary>
    public static string BindSameScope(string name)
        => "[PsBash.Cmdlets.VarFrameStack]::BindSameScope(" + SingleQuote(name) + ", $env:" + name + ")";

    /// <summary>Ends <see cref="BindSameScope"/>: restores name's saved value, unless <c>unset</c> took the binding's frame.</summary>
    public static string UnbindSameScope(string name)
        => "$env:" + name + " = [PsBash.Cmdlets.VarFrameStack]::UnbindSameScope(" + SingleQuote(name) + ", $env:" + name + ")";

    /// <summary>
    /// Ends <see cref="BindSameScope"/> in POSIX mode, before a special builtin: the name keeps the value it has now
    /// (<c>set -o posix; v=t :</c> leaves v=t), unless <c>unset</c> took the binding's frame during the builtin.
    /// </summary>
    public static string PersistSameScope(string name)
        => "[PsBash.Cmdlets.VarFrameStack]::PersistSameScope(" + SingleQuote(name) + ")";

    /// <summary>
    /// Ends <see cref="PushTempFrame"/>: restores <c>$__saved_name</c>, unless <c>unset</c> took the frame (then later
    /// writes went to the outer variable and stay: <c>v=t f</c>, f: <c>unset v; v=new</c> leaves new).
    /// </summary>
    public static string RestoreTempBinding(string name)
        => "$env:" + name + " = [PsBash.Cmdlets.VarFrameStack]::EndTemp(" + SingleQuote(name) + ", $__ptf_" + name
           + ", $__saved_" + name + ", $env:" + name + ")";

    /// <summary>
    /// A function's local <c>name</c> ends (its <c>$__lsd_name</c> holds the caller's value): restored unless a helper
    /// unset the local (<c>unlocal v; v=new</c> leaves new).
    /// </summary>
    public static string RestoreLocal(string name)
        => "$env:" + name + " = [PsBash.Cmdlets.VarFrameStack]::LeaveLocal(" + SingleQuote(name) + ", $__lsd_" + name + ".v, $env:" + name + ")";

    /// <summary>
    /// A loop binds its variable for one pass: the shell variable (its one storage, environment-model.md section 8),
    /// set to <paramref name="valueExpr"/> as a string. Through .NET directly: a fifth of the cost of a
    /// <c>$env:name =</c> provider write, which a loop pays on every pass. Through the store, never
    /// <c>System.Environment</c>: in a background job the store is the job's own (background-jobs.md section 3).
    /// </summary>
    public static string SetShellScalar(string name, string valueExpr)
        => "[PsBash.Cmdlets.BashVariableStore]::Set(" + SingleQuote(name) + ", [string](" + valueExpr + "))";

    /// <summary>
    /// <c>name=value</c> / <c>name+=value</c> (<paramref name="append"/>) whose attributes may have been set by text built at
    /// run time (<c>declare "$1"</c>, <c>eval "$c"</c>, <c>$d x</c>), which the emitter cannot see: the write consults the
    /// run-time attribute table (<c>BashShellState.AssignScalar</c>: nameref target, <c>-u</c>/<c>-l</c>); for an <c>-i</c>
    /// target the value is evaluated as arithmetic here (the sum, for <c>+=</c>) and stored with <c>AssignEvaluated</c>.
    /// <paramref name="resetStatus"/>: the statement's status is 0 (a plain write; the arithmetic path keeps what the
    /// evaluation left, as a static <c>declare -i</c> write does). Emitted only in a unit that runs such text
    /// (PsEmitter._runTimeAttrs); every other unit keeps the plain write.
    /// </summary>
    public static string AssignAttributed(string name, string valueExpr, bool append, bool resetStatus)
    {
        string quoted = SingleQuote(name);
        string arith = append
            ? "Invoke-BashArith (" + SingleQuote(name + "+(") + " + $__psbAv + ')')"
            : "Invoke-BashArith $__psbAv";
        return "if ([PsBash.Cmdlets.BashShellState]::AssignScalar(" + quoted + ", ($__psbAv = [string](" + valueExpr + ")), "
            + (append ? "$true" : "$false") + ")) { " + (resetStatus ? "$global:LASTEXITCODE = 0" : "") + " } "
            + "else { [PsBash.Cmdlets.BashShellState]::AssignEvaluated(" + quoted + ", [string](" + arith + ")) }";
    }

    /// <summary>
    /// <c>name=value</c> / <c>name+=value</c> where <paramref name="name"/> is a nameref whose target is known only at run
    /// time (declared with no target, or a <c>for</c> variable): <c>BashVariableLookup.AssignThroughNameref</c> names the
    /// target on the first write and writes it after. A value that names no variable is an assignment error
    /// (<c>`5': not a valid identifier</c>, status 1, the rest of the line dropped, as bash does).
    /// </summary>
    public static string AssignThroughNameref(string name, string valueExpr, bool append)
        => "if ($__psbNr = [PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, " + SingleQuote(name)
           + ", [string](" + valueExpr + "), " + (append ? "$true" : "$false") + ")) { "
           + ShellDiagnosticLine("$__psbNr") + "; " + SetStatus(1) + "; "
           // A target `a[1/0]`: bash's subscript expansion error, which also ends `bash -c` (BashArrayElement.FailSubscript).
           + "if ([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) { " + UsageDiscardMark + " }; "
           + AssignmentDiscardMark + " }";

    /// <summary>
    /// Before <c>name=(...)</c> / <c>name+=(...)</c> where <paramref name="name"/> is a nameref the run time resolves: one
    /// with no target yet stops being a nameref, with bash's warning, and the array is its own
    /// (<c>typeset -n ref; ref=(x y); echo $ref</c> warns "ref: removing nameref attribute" and prints x). One with a
    /// target is left alone: the write goes to the target. REGRESSION: the array went nowhere and <c>$ref</c> was empty.
    /// </summary>
    public static string ArrayAssignToNameref(string name)
        => "if ([PsBash.Cmdlets.BashVariableLookup]::DropTargetlessNameref(" + SingleQuote(name) + ")) { "
           + ShellDiagnosticLine(SingleQuote("warning: " + name + ": removing nameref attribute")) + " }";

    /// <summary>
    /// <c>$name</c> where <paramref name="name"/> is a nameref whose target is known only at run time: the target's value.
    /// THE scalar read of such a name: every scalar expansion form (<c>${r:-x}</c>, <c>${#r}</c>, <c>${r#p}</c>,
    /// <c>${r/a/b}</c>, <c>${r^^}</c>, <c>${r@Q}</c>, a slice) applies its operator to this value, and the assigning
    /// (<c>${r:=x}</c>) and error (<c>${r?}</c>) forms recognize it (<see cref="ReadThroughNamerefName"/>). Its array
    /// forms read <c>$name</c>, which follows the reference (<c>BashVariableLookup.ViewNameref</c>).
    /// </summary>
    public static string ReadThroughNameref(string name)
        => ReadThroughNamerefPrefix + SingleQuote(name) + "))";

    private const string ReadThroughNamerefPrefix = "$([PsBash.Cmdlets.BashVariableLookup]::ReadThroughNameref($ExecutionContext.SessionState, ";

    /// <summary>The name a <see cref="ReadThroughNameref"/> expression reads, or null when <paramref name="varRef"/> is none.</summary>
    public static string? ReadThroughNamerefName(string varRef)
    {
        if (!varRef.StartsWith(ReadThroughNamerefPrefix + "'", StringComparison.Ordinal) || !varRef.EndsWith("'))", StringComparison.Ordinal))
            return null;
        string name = varRef[(ReadThroughNamerefPrefix.Length + 1)..^3];
        return name.Length > 0 && name.AsSpan().IndexOfAny('\'', ')') < 0 ? name : null;
    }

    /// <summary>
    /// <c>${name=word}</c> / <c>${name:=word}</c>'s assignment where <paramref name="name"/> is a nameref whose target is
    /// known only at run time: the value goes to the target (or names it, while there is none), and is the expansion.
    /// A target <c>a[1/0]</c> is bash's subscript expansion error, as for <see cref="AssignThroughNameref"/>: the line is
    /// dropped and <c>bash -c</c> ends. REGRESSION: the error mark was never taken here, so it outlived the expansion and a
    /// later, unrelated failure (<c>declare -n q; q=1bad</c>) ended the shell.
    /// </summary>
    public static string AssignDefaultThroughNameref(string name, string valueExpr)
        => "$($__psbV = [string](" + valueExpr + "); if ($__psbNr = [PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, "
           + SingleQuote(name) + ", $__psbV, $false)) { " + ShellDiagnosticLine("$__psbNr") + "; "
           + "if ([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) { " + SetStatus(1) + "; " + UsageDiscardMark + " } }; $__psbV)";

    /// <summary>
    /// <c>${!name}</c> where <paramref name="name"/> is a nameref whose target is known only at run time: the name of the
    /// variable it stands for (bash inverts <c>!</c> for a nameref); one with no target is "invalid indirect expansion".
    /// </summary>
    public static string IndirectThroughNameref(string name)
        => "$(if ($null -ne ($__psbNn = [PsBash.Cmdlets.BashVariableLookup]::NamerefName(" + SingleQuote(name) + "))) { $__psbNn } else { "
           + ExpansionError(SingleQuote(name + ": "), "'invalid indirect expansion'") + " })";

    /// <summary>The name of the variable <paramref name="name"/> stands for at run time (itself when it is no nameref), as a PowerShell expression.</summary>
    public static string NamerefTargetName(string name)
        => "([PsBash.Cmdlets.BashShellState]::ResolveNameref(" + SingleQuote(name) + "))";

    /// <summary>
    /// <c>declare -n name</c> / <c>local -n name</c> with no target: <c>$name</c> follows the reference from here on
    /// (<c>BashVariableLookup.ViewNameref</c>), in the scope of the declaration. Dot-sourced on purpose: a script block
    /// that dot-sources anything is compiled without local slots, and in a block WITH them (a subshell's or function's
    /// body that assigns <c>$name</c>, as <c>r+=(z)</c> does) <c>$name</c> would hold a copy of the view's value taken when it
    /// was made, and an assignment would replace the view instead of writing the target.
    /// </summary>
    public static string ViewNameref(string name)
        => ". { [PsBash.Cmdlets.BashVariableLookup]::ViewNameref($ExecutionContext.SessionState, " + SingleQuote(name) + ") }";

    /// <summary>
    /// <c>declare -n name=$v</c> / <c>local -n name=$1</c> (<paramref name="builtin"/>): a reference to the variable the value
    /// names at run time (<c>BashVariableLookup.DeclareNameref</c>), whose <c>$name</c> follows it (<see cref="ViewNameref"/>,
    /// made first so the block is compiled without local slots). A value that names no variable is bash's diagnostic and
    /// status 1 (a warning and 0 for a function's reference to itself); the command goes on.
    /// </summary>
    public static string DeclareNameref(string builtin, string name, string valueExpr, bool inFunction)
        => ViewNameref(name) + "; if ($__psbNr = [PsBash.Cmdlets.BashVariableLookup]::DeclareNameref($ExecutionContext.SessionState, " + SingleQuote(name)
           + ", [string](" + valueExpr + "), " + (inFunction ? "$true" : "$false") + ")) { "
           + ShellDiagnosticLine(SingleQuote(builtin + ": ") + " + $__psbNr") + "; " + SetStatusTo("($__psbNr -like 'warning:*' ? 0 : 1)")
           + " } else { " + SetStatus(0) + " }";

    /// <summary>
    /// One pass of <c>for name in ...</c> where <paramref name="name"/> is a nameref: the word (<paramref name="valueExpr"/>)
    /// becomes its target. A word that names no variable ends the loop with bash's diagnostic, status 1.
    /// </summary>
    public static string BindLoopNameref(string name, string valueExpr)
        => "if (-not [PsBash.Cmdlets.BashVariableLookup]::BindLoopNameref($ExecutionContext.SessionState, " + SingleQuote(name) + ", [string](" + valueExpr + "))) { "
           + ShellDiagnosticLine("('`' + " + valueExpr + " + ''': not a valid identifier')") + "; " + SetStatus(1) + "; break }; ";

    /// <summary><c>typeset +n name</c>: no longer a nameref; the variable takes the reference's target name as its value.</summary>
    public static string ClearNameref(string name)
        => "$env:" + name + " = [PsBash.Cmdlets.BashShellState]::ClearNameref(" + SingleQuote(name) + ", $env:" + name + ")";


    /// <summary>
    /// <c>${v@P}</c> of the PowerShell expression <paramref name="value"/>: the prompt escapes, with the shell's session
    /// for the counters <c>\j</c> (job table), <c>\!</c> and <c>\#</c> (PsBash.Cmdlets.BashPromptText, ShellReader).
    /// </summary>
    public static string PromptExpand(string value) =>
        "[PsBash.Cmdlets.BashPromptText]::Expand(" + value + ", $ExecutionContext.SessionState)";

    /// <summary>
    /// Run before each top-level command the shell's reader takes in (an input line; a multi-line compound command is
    /// one): bash's <c>current_command_number</c> (prompt <c>\#</c>) counts it, and under <c>set -o history</c> its text
    /// <paramref name="line"/> becomes a history entry (prompt <c>\!</c>, <c>history</c>). <paramref name="line"/> is
    /// null for a script that never turns history on (no text to carry). PsBash.Cmdlets.ShellReader.
    /// </summary>
    public static string ReaderCommand(string? line) =>
        line is null
            ? "[PsBash.Cmdlets.ShellReader]::Next(); "
            : "[PsBash.Cmdlets.ShellReader]::Next($ExecutionContext.SessionState, " + SingleQuote(line) + "); ";

    /// <summary>
    /// The trailing <c>-Text '…'</c> of an <c>Invoke-BashBackground</c> call: the job's command as written, which
    /// <c>fg</c> prints when it brings the job back (bash: <c>sleep 5 &amp; fg</c> prints <c>sleep 5</c>). Empty when the
    /// parser could not slice it (the job table then names it <c>bash-bg</c>).
    /// </summary>
    public static string JobText(string? source) =>
        string.IsNullOrEmpty(source) ? "" : " -Text " + SingleQuote(source);

    private const string FunctionSourceOpen = "<#bash:";
    private const string FunctionSourceClose = "#> ";

    /// <summary>
    /// The function body's bash source, carried inside the PowerShell function as a comment right after
    /// <see cref="FunctionPrologue"/> so it travels with the definition (subshell copies, <c>export -f</c>). Base64 of the
    /// UTF-16 text: no character of the source can end the comment or be re-encoded. Read back by
    /// <see cref="TryReadFunctionSource"/> for <c>declare -f</c> / <c>type</c>.
    /// </summary>
    public static string FunctionSourceMark(string? source) =>
        string.IsNullOrEmpty(source) ? ""
            : FunctionSourceOpen + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(source)) + FunctionSourceClose;

    /// <summary>The bash source a function definition carries (<see cref="FunctionSourceMark"/>), or null when it has none.</summary>
    public static string? TryReadFunctionSource(string? definition)
    {
        if (definition is null || !definition.StartsWith(FunctionPrologue, StringComparison.Ordinal)) return null;
        int at = FunctionPrologue.Length;
        if (string.CompareOrdinal(definition, at, FunctionSourceOpen, 0, FunctionSourceOpen.Length) != 0) return null;
        at += FunctionSourceOpen.Length;
        int end = definition.IndexOf("#>", at, StringComparison.Ordinal);
        if (end < 0) return null;
        try { return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(definition[at..end])); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// bash's <c>declare -f</c> layout for a body written as <paramref name="source"/>: <c>NAME () </c>, <c>{ </c>, the
    /// body indented four spaces, <c>}</c>. A brace body is unwrapped and re-indented; any other compound body
    /// (<c>( … )</c>, a loop) is printed inside the braces, as bash does. Lines of a body that holds a here-document
    /// are left exactly as written (indenting would change the document and hide its terminator).
    /// </summary>
    public static string FormatFunctionSource(string name, string source)
    {
        string text = source.Replace("\r\n", "\n").Trim();
        bool heredoc = text.Contains("<<", StringComparison.Ordinal);
        if (text.Length >= 2 && text[0] == '{' && text[^1] == '}')
            text = text[1..^1];
        var lines = new List<string>(text.Split('\n'));
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        var sb = new System.Text.StringBuilder();
        sb.Append(name).Append(" () \n{ \n");
        if (heredoc)
        {
            // Command lines are indented; a here-document's body and terminator stay exactly as written, and bash
            // prints a blank line after the terminator.
            string? terminator = null;
            bool stripTabs = false;
            foreach (var line in lines)
            {
                if (terminator is not null)
                {
                    sb.Append(line).Append('\n');
                    if ((stripTabs ? line.TrimStart('\t') : line) == terminator) { terminator = null; sb.Append('\n'); }
                    continue;
                }
                sb.Append("    ").Append(line.Trim()).Append('\n');
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(?<!<)<<(?!<)(-?)\s*(['""]?)([^\s'"";|&<>()]+)\2");
                if (m.Success) { terminator = m.Groups[3].Value.Replace("\\", ""); stripTabs = m.Groups[1].Value == "-"; }
            }
        }
        else
        {
            int common = int.MaxValue;
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0) continue;
                int indent = 0;
                while (indent < line.Length && line[indent] is ' ' or '\t') indent++;
                if (indent < common) common = indent;
            }
            if (common == int.MaxValue) common = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Length >= common ? lines[i][common..].TrimEnd() : lines[i].Trim();
                // bash prints the last command without its `;` (`{ echo hi; }` -> `echo hi`).
                if (i == lines.Count - 1 && line.EndsWith(';') && !line.EndsWith(";;", StringComparison.Ordinal)) line = line[..^1].TrimEnd();
                if (line.Length == 0) sb.Append('\n'); else sb.Append("    ").Append(line).Append('\n');
            }
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // ───────────────────── Compound-command stdin (shared cursor) ──────────────────────
    // Stage stdin model (docs/specs/emitter-strategy.md "Stage stdin"). A pipe stage's input is `$input` of the
    // stage's OWN script block only: any block nested inside it (a redirect's `. { }`, a guard, a loop, a function, a
    // `$( )`) sees an empty one. So the stage's block hands it on at once, in exactly one of three ways, all built here:
    //   * StdinScope / StdinScopeLazy: a compound stage turns it into the shared stdin cursor every reader consumes;
    //   * ForwardStageInput: a simple stage's wrapper pipes it into the one command it wraps;
    //   * DrainStageInput: a stage whose stdin is something else (its own `< f`, a command that never reads) drops it.
    // PsBuild is the only place the emitted text names `$input` (PsBuildStageStdinGuardTests).

    /// <summary>The records piped into a pipe stage's own script block. Only the primitives below use it.</summary>
    public const string StageInput = "$input";

    /// <summary>The prefix that pipes a stage block's input into the one command it wraps (see <see cref="ForwardStageInput"/>).</summary>
    public const string ForwardedStageInput = StageInput + " | ";

    /// <summary>
    /// A simple command inside its pipe stage's script block (an env prefix's save/restore, a splat prelude, a subshell
    /// guard): the block's input is piped into the command, which PowerShell would otherwise never hand it.
    /// </summary>
    public static string ForwardStageInput(string command) => ForwardedStageInput + command;

    /// <summary>
    /// <see cref="ForwardStageInput"/> for a stand-in that may also run outside a pipeline (a <c>#!</c> file run in place
    /// of a command, ProgramFile.StandInScript): the block's records are piped in when there are any, and otherwise the
    /// command runs with no pipeline input at all, so it keeps the shell's stdin instead of reading an empty pipe.
    /// </summary>
    /// <summary>
    /// The statement a stand-in script block runs first so it can pass its arguments on: a copy of <c>$args</c> as plain
    /// strings in <c>$__psbash_argv</c>, splatted with <see cref="ArgvSplat"/>.
    /// <para>
    /// <b>Invariant:</b> a stand-in (a <c>#!</c> file's interpreter, a job's program through Invoke-BashNative) gives its
    /// command the argv the script wrote. Never splat <c>$args</c> itself: PowerShell re-creates every element that was
    /// typed as a parameter token, so <c>-e</c> binds to the callee's parameters ("ambiguous: -ErrorAction
    /// -ErrorVariable", or silently dropped). Any other array splats as literal strings. A bare <c>--</c> or
    /// <c>-x:y</c> never reaches <c>$args</c> intact, which is why the emitter quotes those (QuotedDashOperand).
    /// Guard: StandInArgvGuardTests.
    /// </para>
    /// </summary>
    public const string ArgvLiterals = "$__psbash_argv = [string[]]$args";

    /// <summary>The splat of <see cref="ArgvLiterals"/>.</summary>
    public const string ArgvSplat = "@__psbash_argv";

    public static string ForwardStageInputIfAny(string command) => ForwardStageInputIfAny(command, "");

    /// <summary>
    /// <see cref="ForwardStageInputIfAny(string)"/> with <paramref name="feed"/> (<c>"stage | "</c>) between the input
    /// and the command when there is input: a native program's records become its exact stdin bytes
    /// (<see cref="NativeStdinFeed"/>).
    /// </summary>
    public static string ForwardStageInputIfAny(string command, string feed) =>
        "$__psbash_in = @(" + StageInput + "); if ($__psbash_in.Count -gt 0) { $__psbash_in | " + feed + command + " } else { " + command + " }";

    /// <summary>
    /// A pipe stage whose stdin is not the pipe (<c>… | cmd &lt; f</c>, <c>… | seq 3</c>): the block drains the upstream
    /// records (so the producer is not refused) and runs <paramref name="body"/> once, as bash does.
    /// </summary>
    public static string DrainStageInput(string body) => "& { $null = @(" + StageInput + "); " + body + " }";

    /// <summary>
    /// A pipeline source that yields what is LEFT of the compound command's shared stdin
    /// (<c>$global:__BashStdIn</c>, a <c>Queue[object]</c> of the original records), one record at a
    /// time. Prepended (<c>feed | cmd</c>) to a stdin-reading command inside a stdin scope. Lazy:
    /// a consumer that stops the pipeline early (<c>head</c>) leaves the rest queued, and
    /// <c>read</c> / a later command advance the same cursor. Empty queue = empty input (EOF).
    /// Each record is taken with ONE atomic <c>TryDequeue</c> (<c>StdinCursor.TryDequeue</c> / <c>Queue.TryDequeue</c>): a
    /// background job reads the same cursor, and <c>Count -gt 0</c> then <c>Dequeue()</c> let it take the record in
    /// between, so <c>Dequeue</c> threw at end of input (guard: SharedStdinTakeGuardTests).
    /// </summary>
    public const string StdinFeed =
        "& { $__psbash_q = $global:__BashStdIn; $__psbash_r = $null; while ($__psbash_q -and $__psbash_q.TryDequeue([ref]$__psbash_r)) { $__psbash_r } }";

    /// <summary>
    /// Wrap <paramref name="body"/> so it runs with the incoming pipeline (<c>$input</c>) as ITS stdin:
    /// the previous queue is saved, a fresh one filled from <c>$input</c> (the records are kept as
    /// objects), and the previous queue is restored in a <c>finally</c> so a nested compound pipe stage
    /// never clobbers its parent's stdin. The result is a statement list for the inside of a
    /// <c>&amp; { … }</c> block — the block is what receives the pipeline.
    /// </summary>
    /// <param name="body">The statements that read the pipeline as their stdin.</param>
    /// <param name="saveVar">The variable that holds the previous queue. A DOT-SOURCED scope shares its caller's
    /// variables, so it needs a name of its own or a nested one overwrites the outer save.</param>
    /// <param name="source">The records that become the stdin: the incoming pipeline, or an array expression (a read loop's
    /// own <c>&lt; file</c> run inline, so a <c>return</c> in it still leaves the function).</param>
    public static string StdinScope(string body, string saveVar = "__psbash_stdin_prev", string source = StageInput) =>
        "$" + saveVar + " = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
        + "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); "
        + "foreach ($__psbash_stdin_line in " + source + ") { $global:__BashStdIn.Enqueue($__psbash_stdin_line) }; "
        + "try { " + body + " } finally { $global:__BashStdIn = $" + saveVar + " }";

    /// <summary>
    /// A <c>while read</c> loop's condition over the shared stdin: the next LINE (null at end of input), taken through the
    /// same cursor every other reader in the loop advances (<c>SharedStdinLine.Next</c>).
    /// </summary>
    public const string SharedStdinNextLine = "[PsBash.Cmdlets.SharedStdinLine]::Next($global:__BashStdIn)";

    private static readonly System.Text.RegularExpressions.Regex SharedStdinReaderPattern = new(
        @"__BashStdIn|\bInvoke-Bash(?:Read|Mapfile)\b(?!-)|\bResolve-BashCommand -Fed\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when text emitted INSIDE a stdin scope shows a reader of that stdin: a fed reader (<see cref="StdinFeed"/>), a
    /// <c>read</c> / <c>mapfile</c>, a read loop over it, or a run-time command name that may turn out to read it. This is
    /// the TEXT half of the emitter's stdin probe and must only be asked through it (PsEmitter.ProbeStdinScope, guarded by
    /// PsBuildStageStdinGuardTests): a command whose text shows no reader may still read -- a function, a program, eval /
    /// source text -- and only the probe knows about those. Asked alone, this said "reads nothing" for
    /// <c>while read a; do f; done</c> with <c>f() { read b; }</c>, and the loop streamed past f's read.
    /// </summary>
    public static bool ReadsSharedStdin(string emitted) => SharedStdinReaderPattern.IsMatch(emitted);

    /// <summary>
    /// <see cref="InputRedirectGuard"/> without its own scope, for a loop whose variables must stay in the caller's scope
    /// (a function's `local`s are plain PowerShell variables).
    /// </summary>
    public static string InputRedirectGuardInline(string target, string ok, string? onFail = null, string? failTail = null) =>
        "$__psbash_ir = [string](Write-Output " + target + "); Invoke-BashTest -e $__psbash_ir; if ($global:LASTEXITCODE -eq 0) { " + ok
        + " } else { " + InputRedirectFailure(failTail, onFail) + " }";
    /// <summary>
    /// A compound command's <c>&lt; file</c> that cannot be opened fails the command: bash reports
    /// <c>file: No such file or directory</c>, sets status 1 and never runs the body. The failure raises
    /// <see cref="RedirectFailedVariable"/>, as a failed output open does, for the ERR trap / errexit check that
    /// <see cref="RedirectFailureChecked"/> puts after the statement (a check inside this <c>&amp; { }</c> block could
    /// not leave a function or subshell). <paramref name="clearsNested"/>: no output redirect of this command reports
    /// on the flag after the body (Invoke-BashRedirect clears it when its own open worked), so a nested failure the body
    /// already handled (<c>{ { cat; } &lt; missing || :; } &lt; in</c>) is cleared here, not taken for this command's.
    /// </summary>
    public static string InputRedirectGuard(string target, string ok, string? failTail = null, bool clearsNested = true) =>
        "& { $__psbash_ir = [string](Write-Output " + target + "); Invoke-BashTest -e $__psbash_ir; if ($global:LASTEXITCODE -eq 0) { " + ok
        + (clearsNested ? "; " + RedirectFailedVariable + " = $false" : "")
        + " } else { " + InputRedirectFailure(failTail, RedirectFailedVariable + " = $true") + " } }";

    /// <summary>The global a redirect that could not be opened raises (Invoke-BashRedirect's
    /// <c>RedirectFailedVariable</c>, <see cref="InputRedirectGuard"/>), read by <see cref="RedirectFailureChecked"/>.</summary>
    public const string RedirectFailedVariable = "$global:__BashRedirectFailed";

    /// <summary>
    /// The failure branch of every input-redirect guard (<see cref="InputRedirectGuard"/>, <see cref="InputRedirectGuardInline"/>,
    /// <see cref="SimpleInputRedirectGuard"/>): a <c>&lt; file</c> that cannot be opened. bash reports it as a shell
    /// diagnostic (<c>s.sh: line 3: file: No such file or directory</c>, through <see cref="ShellDiagnosticLine"/>), sets
    /// status 1, and the command never runs. bash opens a command's redirects left to right, so the report goes where fd 2
    /// points when the <c>&lt;</c> is reached: <paramref name="failTail"/> is the redirect tail of the output redirects
    /// written BEFORE it, applied to the report alone (<c>cmd 2&gt;/dev/null &lt; f</c> is silent, <c>cmd &gt;out &lt; f</c>
    /// still creates <c>out</c>); null when there are none. <paramref name="onFail"/> runs last (the errexit exit).
    /// See docs/specs/error-handling.md "Redirect open errors".
    /// </summary>
    public static string InputRedirectFailure(string? failTail, string? onFail = null)
    {
        string report = ShellDiagnosticLine("$__psbash_ir + ': No such file or directory'");
        if (failTail is { Length: > 0 }) report = "& { " + report + " }" + failTail;
        return report + "; $global:LASTEXITCODE = 1" + (onFail is null ? "" : "; " + onFail);
    }

    /// <summary>
    /// After a <c>for</c> loop: a loop whose body never ran has status 0 (<c>false; for x in $(true); do :; done</c> leaves 0),
    /// whatever its word list's expansions left. <paramref name="iterVar"/> counts the iterations that started.
    /// </summary>
    public static string ZeroIterationStatus(string iterVar) =>
        "if (" + iterVar + " -eq 0) { $global:LASTEXITCODE = 0 }";

    /// <summary>
    /// <see cref="ZeroIterationStatus"/> for a list that globs: a <c>failglob</c> error in the list ends the <c>for</c> with
    /// status 1 (<c>shopt -s failglob; for x in *.ZZ; do :; done</c>), so the reset to 0 is skipped when the glob cmdlet
    /// flagged it. <see cref="FailGlobReset"/> clears the flag before the list is expanded.
    /// </summary>
    public static string ZeroIterationStatusAfterGlob(string iterVar) =>
        "if (-not $global:__BashFailGlob) { " + ZeroIterationStatus(iterVar) + " }";

    /// <summary>Clears the <c>failglob</c> flag before a <c>for</c> list that globs (see <see cref="ZeroIterationStatusAfterGlob"/>).</summary>
    public const string FailGlobReset = "$global:__BashFailGlob = $false; ";

    /// <summary><paramref name="statement"/> run only when no <c>failglob</c> error happened since <see cref="FailGlobReset"/>:
    /// bash abandons the rest of the input line after one (<c>echo *.ZZ; echo x</c>).</summary>
    public static string UnlessGlobFailed(string statement) => "if (-not $global:__BashFailGlob) { " + statement + " }";

    /// <summary><see cref="UnlessGlobFailed"/> for an operand after <c>&amp;&amp;</c> / <c>||</c>: a failed glob (or a bad
    /// substitution) abandons the whole list, so <c>echo *.ZZ || echo alt</c> must not fall back. The operand stays a
    /// pipeline (<c>$( … )</c>) for the chain operator; when skipped, the status stays the error's 1.</summary>
    public static string ChainOperandUnlessGlobFailed(string operand) =>
        "$(if (-not $global:__BashFailGlob) { " + operand + " })";

    /// <summary>True when emitted text can abandon the rest of its line: a glob (failglob) or a bad substitution.</summary>
    public static bool CanAbandonLine(string emitted) =>
        emitted.Contains("ConvertTo-BashGlob", StringComparison.Ordinal)
        || emitted.Contains(ExpansionErrorFunction, StringComparison.Ordinal);

    /// <summary>
    /// eval / source / trap text that <see cref="CanAbandonLine"/>: a failure in it abandons the rest of ITS line only,
    /// so the mark is cleared when the text ends and the line that ran it goes on (`eval 'echo *.ZZ'; echo $?` prints 1).
    /// The status the failure set is kept.
    /// </summary>
    public static string EndOfEvalText(string body) => body + "\n$global:__BashFailGlob = $false";

    /// <summary>
    /// A simple command's <c>&lt; file</c> that cannot be opened: bash reports it, sets status 1 and never runs the
    /// command (<c>echo potato &lt; /missing || echo hello</c> prints only hello). Dot-sourced, so what the command
    /// assigns (<c>read x &lt; f</c> in a function, whose locals are PowerShell variables) stays in the caller's scope,
    /// and still one pipeline element for a following <c>| …</c> or <c>|| …</c>.
    /// </summary>
    /// <para>The failure raises <see cref="RedirectFailedVariable"/>: a builtin whose own status errexit ignores
    /// (<c>true &lt; missing</c>) is checked on it (<see cref="RedirectFailureChecked"/>).</para>
    public static string SimpleInputRedirectGuard(string target, string ok, string? failTail = null) =>
        ". { $__psbash_ir = [string](Write-Output " + target + "); Invoke-BashTest -e $__psbash_ir; if ($global:LASTEXITCODE -eq 0) { " + ok
        + " } else { " + InputRedirectFailure(failTail, RedirectFailedVariable + " = $true") + " } }";

    /// <summary>
    /// A function body that reads stdin: when the function is called with a pipe (<c>echo x | f</c>,
    /// <c>f &lt;&lt;&lt; x</c>) that pipe is its stdin (<see cref="StdinScope"/>); called without one it keeps the
    /// stdin of its caller (the enclosing compound's queue, or the launcher's).
    /// </summary>
    public static string FunctionStdinScope(string body) =>
        "$__psbash_fin = $MyInvocation.ExpectingInput; "
        + "if ($__psbash_fin) { $__psbash_fprev = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
        + "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); "
        + "foreach ($__psbash_fl in " + StageInput + ") { $global:__BashStdIn.Enqueue($__psbash_fl) } }; "
        + "try { " + body + " } finally { if ($__psbash_fin) { $global:__BashStdIn = $__psbash_fprev } }";

    /// <summary>
    /// Like <see cref="StdinScope"/> but the stdin is a lazily-pulled background PRODUCER
    /// (<c>New-BashLazyStdin</c>) instead of the incoming pipeline: for an unbounded producer
    /// (<c>yes | { head -n1; }</c>) that a pipe could never hand over (the stage after a pipe starts only
    /// when its upstream has finished). The scope's <c>finally</c> closes the cursor, which stops the producer.
    /// A statement list: callers wrap it in <c>&amp; { … }</c>.
    /// </summary>
    public static string StdinScopeLazy(string producerCommand, string body) =>
        "$__psbash_stdin_prev = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
        + "$global:__BashStdIn = New-BashLazyStdin " + SingleQuote(producerCommand) + "; "
        + "try { " + body + " } finally { try { $global:__BashStdIn.Close() } catch { }; $global:__BashStdIn = $__psbash_stdin_prev }";

    /// <summary>
    /// <c>cmd &lt; /dev/null</c>: run <paramref name="body"/> with an EMPTY stdin (end of input at once) instead
    /// of whatever stdin surrounds it — under the launcher's forwarded stdin "the surrounding stdin" is a live
    /// pipe, so simply dropping the redirect let <c>read</c>, <c>eval</c>'s commands and natives consume it.
    /// Dot-sourced so the command's side effects (<c>read</c>'s variable, <c>eval</c>'s functions) stay in
    /// the caller's scope; <paramref name="depth"/> makes the save variable unique per lexical nesting level
    /// (a dot-sourced block shares its caller's scope, so an inner scope must not overwrite the outer save).
    /// </summary>
    public static string EmptyStdinScope(string body, int depth)
    {
        var prev = "$__psbash_nullin_prev" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ". { " + prev + " = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
            + "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); "
            + "try { " + body + " } finally { $global:__BashStdIn = " + prev + " } }";
    }

    /// <summary>
    /// Give a statement-list command (a builtin the emitter expands inline: <c>cd</c>, <c>eval</c>,
    /// <c>source</c>, <c>read</c>, …) its redirect tail: <c>. { statements } 2&gt;$null</c>. Dot-sourced, not
    /// <c>&amp; { }</c>, so the builtin's side effects (variables, functions, cwd) land in the caller's scope;
    /// a bare tail after a statement list would bind only to its LAST statement.
    /// </summary>
    public static string WithRedirectTail(string statements, Action<System.Text.StringBuilder> appendTail)
    {
        var sb = new System.Text.StringBuilder(". { ").Append(statements).Append(" }");
        appendTail(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Set while a body that a redirect put in a script block (<c>{ return 6; } 2&gt;x</c> is <c>. { ... } 2&gt;$null</c>)
    /// may have left by a PowerShell <c>return</c>, which leaves only that block: <see cref="GroupPassingReturn"/>.
    /// </summary>
    public const string GroupReturnVar = "$global:__PsBashGroupReturn";

    /// <summary>
    /// The body of such a block: <see cref="GroupReturnVar"/> is raised when it starts and cleared at its end, so it is
    /// still raised after the block only when a return left the body midway. A redirect that fails before the body runs
    /// (<c>{ return 6; } &gt;/nonexistent/x</c>, <c>return 6 &lt;missing</c>) leaves it clear: bash goes on (status 1).
    /// </summary>
    public static string GroupBody(string body) => GroupReturnVar + " = $true; " + body.TrimEnd(' ', ';') + "; " + GroupReturnVar + " = $false";

    /// <summary>
    /// The block (<paramref name="construct"/>, whose body went through <see cref="GroupBody"/>) with what makes a
    /// <c>return</c> in it leave the caller: the flag is cleared before it, and still raised after it only when the body
    /// left by a <c>return</c>, so the caller returns too (bash: <c>f() { { return 6; } 2&gt;/dev/null; echo no; }</c> returns 6).
    /// The flag is raised again after the check, for an enclosing block whose body goes on (a function called in it may
    /// have run blocks of its own). A <c>break</c> leaves past the check, as it should.
    /// </summary>
    public static string GroupPassingReturn(string construct) =>
        GroupReturnVar + " = $false; " + construct + "; if (" + GroupReturnVar + ") { return }; " + GroupReturnVar + " = $true";

    /// <summary>
    /// A native program run with the surrounding shared stdin (<c>$global:__BashStdIn</c>) as its REAL
    /// process stdin. <c>Enter-BashNativeStdin</c> hands the program an OS pipe that a background pump fills
    /// from the shared stdin; <c>Exit-BashNativeStdin</c> (after the program exits) stops the pump and puts
    /// back everything the program did not read. Unlike piping the records into the program
    /// (<c>feed | prog</c>), the program starts at once and the command ends when the PROGRAM ends — a
    /// program that never reads stdin (<c>git --version</c>) is not held hostage by a stdin that never ends.
    /// <paramref name="name"/> is the literal command word: the pipe is only installed when it resolves to an
    /// application (a function or alias of that name runs as before).
    /// </summary>
    public static string NativeStdinScope(string name, string command) =>
        "& { $__psbash_native_stdin = Enter-BashNativeStdin " + SingleQuote(name) + "; try { " + command
        + " } finally { Exit-BashNativeStdin $__psbash_native_stdin } }";

    /// <summary>
    /// The pipe stage that turns the records flowing into a program into the bytes bash would write
    /// (<c>… | ConvertTo-BashNativeStdin 'prog' | prog</c>). PowerShell's own native pipe appends <c>\r\n</c>
    /// to every record; the stage hands a real program raw bytes instead, and a function / alias / cmdlet of
    /// that name its text as before. <paramref name="name"/> is the literal command word.
    /// </summary>
    public static string NativeStdinFeed(string name) => "ConvertTo-BashNativeStdin " + SingleQuote(name);

    // ───────────────────────── Positional-parameter expansion ──────────────────────────

    /// <summary>
    /// The positional parameters are the shell's own list (<c>$global:BashPositional</c>: set by a function call,
    /// <c>set --</c>, <c>shift</c>, <c>source</c> with arguments), not the script's <c>$args</c>. A <c>$null</c> test, never
    /// truthiness: an EMPTY list is still the shell's. The truthiness test read <c>shift</c>-ed-to-empty as "none" and fell
    /// back to the function's own <c>$args</c>, so <c>while [ "$1" ]; do shift; done</c> in a function never ended and
    /// <c>set --; echo "$1"</c> printed the script's first argument. Every positional site tests through this
    /// (guard <c>PositionalFallbackGuardTests</c>).
    /// </summary>
    public const string PositionalIsShells = "$null -ne $global:BashPositional";

    /// <summary>
    /// The PowerShell subexpression for a bash positional-parameter expansion:
    /// <c>$@</c>/<c>$*</c> (the whole list), <c>$#</c> (the count), or a 1-based
    /// positional index (<c>$1</c>..<c>$9</c>, <c>${10}</c>, …). Every site prefers
    /// <c>$global:BashPositional</c> — set inside function bodies (see
    /// <c>EmitFunction</c> save/restore) and by <c>set --</c> — falling back to the
    /// script's own <c>$args</c> at top level. This is the ONE source for that
    /// preference-fallback shape; it used to be hand-copied at every positional call
    /// site and had to change in lockstep.
    /// </summary>
    /// <param name="sigil"><c>"@"</c>, <c>"*"</c>, <c>"#"</c>, or a base-10 positional index string.</param>
    public static string BuildPositionalExpansion(string sigil) =>
        sigil switch
        {
            "@" or "*" => "$(if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional } else { $args })",
            "#" => "$(if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional.Count } else { $args.Count })",
            _ when PositionalIndex.TryParse(sigil, out int oneBased) =>
                "$(if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional[" + (oneBased - 1) + "] } "
                + "else { $args[" + (oneBased - 1) + "] })",
            _ => throw new System.ArgumentException($"Not a positional sigil: '{sigil}'", nameof(sigil)),
        };

    /// <summary>
    /// PowerShell expression for the separator `"$*"` / `"${a[*]}"` join with: the first character of IFS, a space
    /// when IFS is unset (bash). A `local IFS=...` is a lexical `$IFS`, which wins over the shell variable and may be
    /// empty (join with nothing); an empty `$env:IFS` cannot be told from unset (setting it removes it).
    /// </summary>
    public const string IfsJoinSeparator =
        "$(& { $__psbash_ifs = if ($null -ne $IFS) { [string]$IFS } else { $env:IFS }; "
        + "if ($null -eq $__psbash_ifs) { ' ' } elseif ($__psbash_ifs.Length -eq 0) { '' } else { $__psbash_ifs.Substring(0, 1) } })";

    // ───────────────────────────── Special-variable mapping ────────────────────────────

    /// <summary>A bare <c>$PWD</c>: the shell variable, else PowerShell's location.</summary>
    public const string PwdRead = "$(if ($null -ne $env:PWD) { $env:PWD } else { [PsBash.Cmdlets.BashRuntime]::ToShellPath([string]($ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath)) })";

    /// <summary><c>$BASHPID</c>. A <c>$( )</c> whose text reads it must take the subshell scope that sets it.</summary>
    public const string BashPidRead = "$([PsBash.Cmdlets.BashRuntime]::BashPid($ExecutionContext.SessionState))";

    /// <summary>The read of <c>$BASH_SUBSHELL</c>: the depth <c>BashShellState.Save</c> counts. A substitution that reads it
    /// gets the subshell scope (PsEmitter.PipelineHead), as one that reads <see cref="BashPidRead"/> does.</summary>
    public const string SubshellDepthRead = "[PsBash.Cmdlets.BashRuntime]::SubshellDepth($ExecutionContext.SessionState)";

    /// <summary>
    /// The single source of truth for how a bash special variable (<c>$?</c>, <c>$$</c>,
    /// <c>$RANDOM</c>, positional params, …) maps to PowerShell. Returns <c>null</c> when
    /// <paramref name="name"/> is not a recognized special variable OR when the braced
    /// (<c>${name}</c>) form of a recognized special variable has no dedicated mapping —
    /// in both cases the caller falls back to its own plain/braced <c>$env:</c> reference.
    /// <para>
    /// <paramref name="braced"/> selects between the plain-<c>$name</c> emission (used for
    /// a bare <c>$name</c> word) and the brace-quoted <c>${name}</c> emission (used inside
    /// double quotes when the following character would otherwise be misparsed, e.g.
    /// <c>"$x:suffix"</c>). A computed variable reads the same in both forms (<c>${RANDOM}</c> once fell through to
    /// <c>${env:RANDOM}</c>, empty). Only <c>BASH_VERSINFO</c> (an array, whose element reads go through
    /// <c>PsEmitter.ShellArrayExpr</c>) and multi-digit positionals have a plain mapping alone.
    /// The variables bash binds at shell start are ordinary variables (<see cref="PsBash.Core.ShellVariables"/>); the
    /// ones mapped here read the variable first and fall back to the start value outside a shell host.
    /// </para>
    /// <para>
    /// <paramref name="inDoubleQuote"/> only affects <c>$0</c>: inside double quotes (or
    /// always, for the braced form) it must be wrapped as <c>$(...)</c> so PowerShell's
    /// string interpolation invokes the property access rather than treating
    /// <c>$MyInvocation.MyCommand.Name</c> literally.
    /// </para>
    /// </summary>

    public static string? TryMapSpecialVar(string name, bool braced, bool inDoubleQuote)
    {
        switch (name)
        {
            case "null":
            case "true":
            case "false":
            case "HOME":
            case "LASTEXITCODE":
                return braced ? "${" + name + "}" : "$" + name;
            case "PWD":
                // The shell variable (`cd` and each invocation write $env:PWD in the shell form C:/x; `PWD=foo` assigns
                // it and does not move the shell: bash prints foo, `pwd` still prints the directory). The current
                // location, in the same shell form, is only the fallback when no PWD was ever set.
                return braced ? null : PwdRead;
            case "?":
                return braced ? "${global:LASTEXITCODE}" : "$global:LASTEXITCODE";
            case "RANDOM":
                return "$(Get-Random -Maximum 32768)";
            case "SRANDOM":
                // 32 random bits from the system generator (bash: getrandom(2)), never repeating a seed.
                return "$([System.BitConverter]::ToUInt32([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(4), 0))";
            case "BASH_SUBSHELL":
                return "$(" + SubshellDepthRead + ")";
            case "LINENO" when TrapLine > 0:
                // The line of the statement being emitted. The lexer writes it in for `$LINENO` / `${LINENO}`; an
                // operator form (`${LINENO+set}`, `${LINENO:-x}`, `${#LINENO}`) reaches here.
                return SingleQuote(TrapLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case "*" when inDoubleQuote:
                // "$*" joins the positionals with the first character of IFS (a space by default).
                return "$(@(if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional } else { $args }) -join " + IfsJoinSeparator + ")";
            case "@":
            case "*":
            case "#":
                return BuildPositionalExpansion(name);
            case "0":
                // $0 is the name the launcher was given (`-c CMD NAME`, or the script path as typed;
                // $global:BashPositional0), default `bash`. $MyInvocation.MyCommand.Name was the
                // CALLING function/script's command name: empty at top level, the function's name in
                // a function. Always a $(...) subexpression, so quoted and bare uses share one text.
                return "$(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })";
            case "FUNCNAME":
                // A bare `$FUNCNAME` is element 0 of the call stack: the running function ('' outside one).
                // Outside a function it is unset: empty, or "unbound variable" under `set -u`.
                return "$($__psbash_fn = @(Get-BashFuncNames)[0]; if ($null -eq $__psbash_fn -and $global:__BashNounset) { "
                    + ParamErrorThrowStatements(SingleQuote("FUNCNAME: "), "'unbound variable'") + " }; $__psbash_fn)";
            case "BASH_LINENO":
                // Element 0 of the call-line stack: the line the running function was called from.
                return "$(@(" + BashLineNoArray + ")[0])";
            case "BASH_SOURCE":
                // A bare `$BASH_SOURCE` / `${BASH_SOURCE##*/}` is element 0: the running script file ('' under -c), in a
                // function the file it was defined in. It read the (unset) environment variable of that name.
                return "$(@(" + BashSourceArray + ")[0])";
            case "$":
                return braced ? "${PID}" : "$PID";
            case "!":
                return braced ? "${global:BashBgLastPid}" : "$global:BashBgLastPid";
            case "-":
                return "$(" + ShellFlagsExpr + ")";
            case "_":
                return braced ? "${global:BashLastArg}" : "$global:BashLastArg";
            case "SECONDS":
                return "$([math]::Floor(([DateTime]::UtcNow - $global:BashStartTime).TotalSeconds))";
            case "PPID":
                // Bound at shell start (ShellVariables.SeedShellStart); the parent of this process outside a shell host.
                return "$(if ($null -ne $env:PPID) { $env:PPID } else { (Get-Process -Id $PID -ErrorAction SilentlyContinue).Parent.Id })";
            // Variables bash defines about the machine and the shell. Computed on read; an env variable of the
            // same name wins for HOSTNAME and OSTYPE (bash only sets those when the environment lacks them).
            case "UID":
            case "EUID":
                return "$(if ($IsWindows) { [long][System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Split('-')[-1] } else { [long](id -u) })";
            case "OSTYPE":
                return "$(if ($null -ne $env:OSTYPE) { $env:OSTYPE } elseif ($IsWindows) { 'msys' } elseif ($IsMacOS) { 'darwin' } else { 'linux-gnu' })";
            case "HOSTTYPE":
                return "$(if ($null -ne $env:HOSTTYPE) { $env:HOSTTYPE } else { [PsBash.Core.ShellVariables]::HostType() })";
            case "MACHTYPE":
                return "$(if ($null -ne $env:MACHTYPE) { $env:MACHTYPE } else { [PsBash.Core.ShellVariables]::MachType() })";
            case "HOSTNAME":
                return "$(if ($null -ne $env:HOSTNAME) { $env:HOSTNAME } else { [System.Environment]::MachineName.ToLowerInvariant() })";
            case "SHELL":
                // bash sets SHELL at startup, unexported, when the environment lacks it (BashRuntime.DefaultShell); `unset SHELL`
                // (Invoke-BashUnset) hides that value.
                return "$(if ($null -ne $env:SHELL) { $env:SHELL } elseif (-not $global:__BashShellUnset) { [PsBash.Cmdlets.BashRuntime]::DefaultShell() })";
            case "PS4":
                return "$(if ($null -ne $env:PS4) { $env:PS4 } else { '+ ' })";
            case "BASHPID":
                // A subshell's own id (BashRuntime.SubshellPidVariable, set by the subshell scope); $PID outside one.
                return BashPidRead;
            case "SHELLOPTS":
                return "$(" + ShellOptsExpr + ")";
            case "BASHOPTS":
                return "$([PsBash.Cmdlets.InvokeBashShoptCommand]::BashOpts())";
            case "EPOCHSECONDS":
                return "$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())";
            case "EPOCHREALTIME":
                return "$(([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000).ToString().Insert(([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000).ToString().Length - 6, '.'))";
            case "BASH_VERSION":
                return "$(if ($null -ne $env:BASH_VERSION) { $env:BASH_VERSION } else { $global:BashVersion })";
            case "BASH_VERSINFO":
                return braced ? null : "$global:BashVersionInfo";
            default:
                // Single-digit positional ($1..$9): identical in both plain and braced form.
                if (name.Length == 1 && name[0] is >= '1' and <= '9')
                    return BuildPositionalExpansion(name);

                // Multi-digit positional (${10}, ${11}, …) only arises from the braced form
                // — a bare $10 lexes as $1 followed by literal "0" (see BashParser.ParseSimpleVar)
                // — and the plain-form call site is the only one that ever handles it. A bash
                // variable name can never be all-digits, so an all-digit name here is
                // unambiguously a positional index. An index beyond int range is an unset
                // parameter -> empty string in bash (PositionalIndex saturates it).
                if (!braced && name.Length >= 2 && IsAllDigits(name))
                    return BuildPositionalExpansion(name);

                return null;
        }
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }
}
