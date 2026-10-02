using System.Collections;
using System.Globalization;
using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>jq's <c>@format</c> strings: <c>@text @json @csv @tsv @html @uri @sh @base64 @base64d @base32 @base32d</c>.</summary>
internal static class JqFormats
{
    private static JqError Err(string m) => new(m);

    public static bool IsKnown(string name) => name is "@text" or "@json" or "@csv" or "@tsv" or "@html" or "@uri" or "@sh"
        or "@base64" or "@base64d";

    public static string Apply(string name, object? v)
    {
        switch (name)
        {
            case "@text": return JqValue.ToText(v);
            case "@json": return JqValue.ToCompactJson(v);
            case "@html": return Html(JqValue.ToText(v));
            case "@uri": return Uri(JqValue.ToText(v));
            case "@csv": return Csv(v);
            case "@tsv": return Tsv(v);
            case "@sh": return Sh(v);
            case "@base64": return Convert.ToBase64String(Utf8(JqValue.ToText(v)));
            case "@base64d": return Base64Decode(v);
        }
        // jq 1.7 as shipped (oracle) has no @base32/@base32d: any other name is a runtime error.
        throw new JqError($"{name.TrimStart('@')} is not a valid format");
    }

    private static byte[] Utf8(string s) => RawBytes.GetBytes(s);

    private static string Html(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Uri(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Utf8(s))
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string Csv(object? v)
    {
        if (v is not object?[] row) throw Err($"{JqInterp.Describe(v)} cannot be csv-formatted, only array");
        var parts = new List<string>();
        foreach (var item in row)
        {
            switch (item)
            {
                case null: parts.Add(""); break;
                case bool b: parts.Add(b ? "true" : "false"); break;
                case string s: parts.Add("\"" + s.Replace("\"", "\"\"") + "\""); break;
                default:
                    if (JqValue.IsNumber(item)) parts.Add(JqValue.FormatNumber(item));
                    else throw Err($"{JqInterp.Describe(item)} is not valid in a csv row");
                    break;
            }
        }
        return string.Join(",", parts);
    }

    private static string Tsv(object? v)
    {
        if (v is not object?[] row) throw Err($"{JqInterp.Describe(v)} cannot be tsv-formatted, only array");
        var parts = new List<string>();
        foreach (var item in row)
        {
            switch (item)
            {
                case null: parts.Add(""); break;
                case bool b: parts.Add(b ? "true" : "false"); break;
                case string s:
                    parts.Add(s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"));
                    break;
                default:
                    if (JqValue.IsNumber(item)) parts.Add(JqValue.FormatNumber(item));
                    else throw Err($"{JqInterp.Describe(item)} is not valid in a tsv row");
                    break;
            }
        }
        return string.Join("\t", parts);
    }

    private static string Sh(object? v)
    {
        string One(object? item)
        {
            switch (item)
            {
                case string s: return "'" + s.Replace("'", "'\\''") + "'";
                case IDictionary or object?[]: throw Err($"{JqInterp.Describe(item)} can not be escaped for shell");
                default: return JqValue.ToCompactJson(item);
            }
        }
        if (v is object?[] arr) return string.Join(" ", arr.Select(One));
        return One(v);
    }

    private static string Base64Decode(object? v)
    {
        string s = JqValue.ToText(v);
        // jq tolerates missing padding and ignores a trailing partial group; anything outside the alphabet is invalid.
        var clean = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '=') break;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '+' or '/') clean.Append(c);
            else throw Err($"{JqInterp.Describe(v)} is not valid base64 data");
        }
        int rem = clean.Length % 4;
        if (rem == 1) clean.Length -= 1;           // a single leftover character carries no byte
        else if (rem > 1) clean.Append('=', 4 - rem);
        var bytes = Convert.FromBase64String(clean.ToString());
        return RawBytesToString(bytes);
    }

    private static string RawBytesToString(byte[] bytes) => RawBytes.GetString(bytes);

}
