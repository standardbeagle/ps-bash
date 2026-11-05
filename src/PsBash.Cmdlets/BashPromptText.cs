using System.Text;

namespace PsBash.Cmdlets;

/// <summary>bash <c>${v@P}</c> (prompt escapes) and <c>${v@E}</c> (<c>$'...'</c> escapes). Emitted PowerShell reaches it as <c>[PsBash.Cmdlets.BashPromptText]::Expand(...)</c>.</summary>
public static class BashPromptText
{
    public static string Expand(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        for (int i = 0; i < prompt.Length; i++)
        {
            char c = prompt[i];
            if (c != '\\' || i + 1 >= prompt.Length) { sb.Append(c); continue; }
            char n = prompt[++i];
            switch (n)
            {
                case 'a': sb.Append('\a'); break;
                case 'e': sb.Append('\x1b'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                case '$': sb.Append('$'); break;
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
                    var pwd = BashVariableStore.Get("PWD") ?? Environment.CurrentDirectory;
                    var home = BashVariableStore.Get("HOME");
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
                    sb.Append(InvokeBashDateCommand.ConvertDateFormat(now, fmt.Length == 0 ? "%X" : fmt));
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
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
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
