using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// An array subscript that bash reads at RUN time, from a value rather than the script text: an element named in an
/// arithmetic variable's value (<c>x='a[$(cmd)]=1'; $(( x ))</c>), the target of an indirect expansion
/// (<c>ref='a[$(cmd)]'; ${!ref}</c>), an <c>unset</c> operand (<c>unset 'a[$(cmd)]'</c>). bash expands that text as a
/// double-quoted word before it uses it as an index or key (parameter, command and arithmetic expansion, then quote
/// removal; bash's <c>expand_arith_string</c>), so a command substitution in it RUNS. This is bash's documented
/// "array subscript code execution"; ps-bash matches it, and only there.
///
/// <para>The text is transpiled like <c>eval</c> text (never handed to PowerShell as a string) as the assignment
/// <c>__psbash_subscript="TEXT"</c>, run in the caller's session state, and the value read back.</para>
/// </summary>
public static class BashSubscriptExpansion
{
    private const string ResultName = "__psbash_subscript";

    /// <summary>
    /// True when <paramref name="subscript"/> holds a command substitution (<c>$( )</c>, <c>$(( ))</c>, backquotes)
    /// that only a full word expansion can evaluate; other subscripts keep their cheaper paths. With
    /// <paramref name="braced"/> a <c>${...}</c> expansion (<c>a[${c:-1}]</c>) counts too.
    /// </summary>
    public static bool NeedsWordExpansion(string? subscript, bool braced = false) =>
        subscript is not null
        && (subscript.Contains("$(", StringComparison.Ordinal) || subscript.IndexOf('`') >= 0
            || braced && subscript.Contains("${", StringComparison.Ordinal));

    /// <summary>
    /// The subscript text after bash's word expansions, or null when it is not a valid word (an unterminated
    /// substitution: bash's "bad substitution").
    /// </summary>
    public static string? Expand(CommandInvocationIntrinsics invoke, string subscript)
    {
        string script;
        try
        {
            script = PsBash.Core.Transpiler.BashTranspiler.Transpile(
                ResultName + "=\"" + DoubleQuotedBody(subscript) + "\"", PsBash.Core.Transpiler.TranspileContext.Eval);
        }
        catch (Exception ex) when (ex is PsBash.Core.Parser.ParseException or InvalidOperationException)
        {
            return null;
        }

        string? prior = BashVariableStore.Get(ResultName);
        BashVariableStore.Set(ResultName, null);
        try
        {
            invoke.InvokeScript(true, ScriptBlock.Create(script), null);
            return BashVariableStore.Get(ResultName);
        }
        catch (ParseException)
        {
            return null;
        }
        finally
        {
            BashVariableStore.Set(ResultName, prior);
        }
    }

    /// <summary>
    /// False when <paramref name="subscript"/> leaves a quote open (<c>a[1"]</c>): bash does not take such text as an
    /// array reference at all ("invalid variable name"). A backslash escapes the next character outside single quotes.
    /// </summary>
    public static bool QuotesBalanced(string subscript)
    {
        char open = '\0';
        for (int i = 0; i < subscript.Length; i++)
        {
            char c = subscript[i];
            if (open == '\'') { if (c == '\'') open = '\0'; continue; }
            if (c == '\\') { i++; continue; }
            if (c == '"') open = open == '"' ? '\0' : '"';
            else if (c == '\'' && open == '\0') open = '\'';
        }
        return open == '\0';
    }

    /// <summary>
    /// <paramref name="text"/> as the body of a double-quoted word: a <c>"</c> outside any substitution is dropped
    /// (bash removes those quotes from a subscript), so it can neither end the word early nor splice unquoted text in.
    /// The inside of <c>$( )</c>, <c>${ }</c> and backquotes is kept as written, quotes included.
    /// </summary>
    public static string DoubleQuotedBody(string text)
    {
        var sb = new StringBuilder(text.Length);
        int parens = 0, braces = 0;
        bool backquote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool nested = parens > 0 || braces > 0 || backquote;
            if (c == '\\' && i + 1 < text.Length) { sb.Append(c).Append(text[++i]); continue; }
            if (nested && c is '\'' or '"')
            {
                // A quoted string inside a substitution is copied whole, so its parens and braces do not count.
                int end = i + 1;
                while (end < text.Length && text[end] != c)
                    end += c == '"' && text[end] == '\\' ? 2 : 1;
                end = Math.Min(end, text.Length - 1);
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }
            if (c == '"') continue;
            if (c == '$' && i + 1 < text.Length && text[i + 1] is '(' or '{')
            {
                if (text[i + 1] == '(') parens++; else braces++;
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (c == '`') backquote = !backquote;
            else if (parens > 0 && c == '(') parens++;
            else if (parens > 0 && c == ')') parens--;
            else if (braces > 0 && c == '{') braces++;
            else if (braces > 0 && c == '}') braces--;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
