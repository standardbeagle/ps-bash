namespace PsBash.Core;

/// <summary>
/// Bash quote removal on raw word text that no tokenizer has split (a subscript read as raw text, an assoc
/// subscript named through <c>${!ref}</c> at run time): single quotes keep everything; inside double quotes a
/// backslash escapes only <c>$ ` " \</c>; outside quotes a backslash escapes the next character. No expansion is
/// done. The one implementation: the emitter (<c>declare -A m=(["k\"j"]=v)</c> keys) and the cmdlets
/// (<c>BashGlob.RemoveQuotes</c>) both call it, so a raw-text site never strips only the outer quotes again.
/// </summary>
public static class BashQuoting
{
    /// <summary>Quote removal on <paramref name="word"/> (see <see cref="BashQuoting"/>); null is empty.</summary>
    public static string RemoveQuotes(string? word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;
        if (word.AsSpan().IndexOfAny('\'', '"', '\\') < 0) return word;
        var sb = new System.Text.StringBuilder(word.Length);
        char quote = '\0';
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else sb.Append(c);
            }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < word.Length && word[i + 1] is '$' or '`' or '"' or '\\') sb.Append(word[++i]);
                else sb.Append(c);
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '\\' && i + 1 < word.Length) sb.Append(word[++i]);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}