using System.Text;

namespace PsBash.Cmdlets;

/// <summary>bash <c>${v@P}</c> (prompt escapes) and <c>${v@E}</c> (<c>$'...'</c> escapes). Emitted PowerShell reaches it as <c>[PsBash.Cmdlets.BashPromptText]::Expand(...)</c>.</summary>
public static class BashPromptText
{
    /// <summary>
    /// Decodes the prompt escapes, then applies the double-quote backslash removal of bash's <c>promptvars</c> pass
    /// to the result: <c>\$</c> decodes to <c>\$</c>, so <c>PS1='\\$'</c> prints <c>$</c> and <c>PS1='\\\$'</c>
    /// prints <c>\$</c>. Text an escape inserts (<c>\w</c>, <c>\u</c>, a date) is quoted first, as bash quotes it,
    /// so a backslash in a Windows path survives. The same pass expands <c>$name</c> and <c>${name}</c>
    /// (<c>PS1='$x$y'</c>); the value is inserted as is, neither decoded nor quote-removed, as in bash.
    /// </summary>
    /// <remarks>Security: command substitution (<c>$(cmd)</c>, backquotes) and arithmetic in the prompt are left as
    /// written. bash runs them; ps-bash never runs code that comes from a variable's value.</remarks>
    public static string Expand(string? prompt) => ExpandParametersAndRemoveQuoting(Decode(prompt), BashVariableStore.Get);

    /// <summary>
    /// The double-quote pass over the decoded prompt: a backslash before <c>$ ` " \</c> or a newline is dropped,
    /// and an unescaped <c>$name</c> / <c>${name}</c> is replaced by <paramref name="lookup"/>'s value (unset is empty).
    /// </summary>
    internal static string ExpandParametersAndRemoveQuoting(string text, Func<string, string?> lookup)
    {
        if (text.IndexOf('\\') < 0 && text.IndexOf('$') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '$' or '`' or '"' or '\\' or '\n') { sb.Append(text[++i]); continue; }
            if (c == '$' && i + 1 < text.Length)
            {
                int start = i + 1;
                bool braced = text[start] == '{';
                int nameStart = braced ? start + 1 : start;
                int end = nameStart;
                if (end < text.Length && (char.IsAsciiLetter(text[end]) || text[end] == '_'))
                    while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
                if (end > nameStart && (!braced || (end < text.Length && text[end] == '}')))
                {
                    sb.Append(lookup(text[nameStart..end]) ?? "");
                    i = braced ? end : end - 1;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Decode(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;
        var sb = new Quoting();
        var now = DateTimeOffset.Now;
        for (int i = 0; i < prompt.Length; i++)
        {
            char c = prompt[i];
            if (c != '\\' || i + 1 >= prompt.Length) { sb.AppendRaw(c); continue; }
            char n = prompt[++i];
            switch (n)
            {
                case 'a': sb.Append('\a'); break;
                case 'e': sb.Append('\x1b'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.AppendRaw('\\'); break;
                case '$': sb.AppendRaw("\\$"); break;
                case '[': case ']': break;
                case 'u': sb.Append(BashVariableStore.Get("USER") ?? Environment.UserName); break;
                case 'h': { var h = Environment.MachineName.ToLowerInvariant(); int dot = h.IndexOf('.'); sb.Append(dot > 0 ? h[..dot] : h); break; }
                case 'H': sb.Append(Environment.MachineName.ToLowerInvariant()); break;
                case 's': sb.Append("bash"); break;
                case 'v': sb.Append("5.2"); break;
                case 'V': sb.Append("5.2.21"); break;
                case 'j': sb.Append('0'); break;
                case 'l': sb.Append("tty"); break;
                case '!': case '#': sb.Append('1'); break;
                case 'w': case 'W':
                {
                    // $PWD already holds the shell form (C:/x, docs/specs/paths.md); the fallback and HOME may be native.
                    var pwd = BashVariableStore.Get("PWD") ?? PsBash.Core.BashPath.ToShell(Environment.CurrentDirectory);
                    var home = BashVariableStore.Get("HOME") is { } rawHome ? PsBash.Core.BashPath.ToShell(rawHome) : null;
                    if (n == 'W') { var t = pwd.TrimEnd('/', '\\'); int s = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\')); sb.Append(pwd == home ? "~" : t.Length == 0 ? "/" : t[(s + 1)..]); break; }
                    if (!string.IsNullOrEmpty(home) && pwd == home) sb.Append('~');
                    else if (!string.IsNullOrEmpty(home) && pwd.StartsWith(home + "/", StringComparison.Ordinal)) sb.Append('~').Append(pwd[home.Length..]);
                    else sb.Append(pwd);
                    break;
                }
                case 'd': sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, "%a %b %d")); break;
                case 't': sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, "%H:%M:%S")); break;
                case 'T': sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, "%I:%M:%S")); break;
                case '@': sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, "%I:%M %p")); break;
                case 'A': sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, "%H:%M")); break;
                case 'D' when i + 1 < prompt.Length && prompt[i + 1] == '{':
                {
                    int close = prompt.IndexOf('}', i + 2);
                    if (close < 0) { sb.Append("\\D"); break; }
                    var fmt = prompt[(i + 2)..close];
                    sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, fmt.Length == 0 ? "%H:%M:%S" : fmt));
                    i = close;
                    break;
                }
                case >= '0' and <= '7':
                {
                    int v = n - '0', digits = 1;
                    while (digits < 3 && i + 1 < prompt.Length && prompt[i + 1] is >= '0' and <= '7') { v = v * 8 + (prompt[++i] - '0'); digits++; }
                    sb.Append((char)(v & 0xFF));
                    break;
                }
                default: sb.AppendRaw('\\').AppendRaw(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The decoded prompt: <c>AppendRaw</c> keeps prompt text as written (quote removal applies to it);
    /// <c>Append</c> is text an escape inserted, backslash-quoted so quote removal leaves it unchanged.
    /// </summary>
    private sealed class Quoting
    {
        private readonly StringBuilder _sb = new();
        public Quoting AppendRaw(char c) { _sb.Append(c); return this; }
        public Quoting AppendRaw(string s) { _sb.Append(s); return this; }
        public Quoting Append(char c)
        {
            if (c is '$' or '`' or '"' or '\\' or '\n') _sb.Append('\\');
            _sb.Append(c);
            return this;
        }
        public Quoting Append(string? s)
        {
            if (s is null) return this;
            foreach (var c in s) Append(c);
            return this;
        }
        public override string ToString() => _sb.ToString();
    }

    /// <summary><c>${v@E}</c>: expands backslash escapes the way <c>$'...'</c> does.</summary>
    public static string Unescape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { sb.Append(c); continue; }
            char n = text[++i];
            switch (n)
            {
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'e': case 'E': sb.Append('\x1b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '?': sb.Append('?'); break;
                case 'x':
                {
                    int v = 0, d = 0;
                    while (d < 2 && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1])) { v = v * 16 + Convert.ToInt32(text[++i].ToString(), 16); d++; }
                    if (d == 0) sb.Append("\\x"); else sb.Append((char)v);
                    break;
                }
                case >= '0' and <= '7':
                {
                    int v = n - '0', d = 1;
                    while (d < 3 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7') { v = v * 8 + (text[++i] - '0'); d++; }
                    sb.Append((char)(v & 0xFF));
                    break;
                }
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }
}
