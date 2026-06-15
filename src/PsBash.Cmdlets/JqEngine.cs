using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PsBash.Cmdlets;

/// <summary>
/// C# port of the psm1 jq interpreter (REFACTOR-2 Phase F6 follow-on).
///
/// Oracle: <c>Invoke-JqFilter</c>, <c>ConvertTo-JqJson</c>, and the
/// <c>*-Jq*</c> helper web in <c>src/PsBash.Module/PsBash.psm1</c>. The
/// surface ported here is the one <c>Invoke-BashJq</c> exercises: pipe and
/// comma splitting, the <c>//</c> alternative, dot-path field/index/iterate,
/// array / object construction, string interpolation, recursion <c>..</c>,
/// numeric/string/bool/null literals, comparison and boolean operators inside
/// <c>select</c>, the <c>map</c>, <c>length</c>, <c>type</c>, <c>keys</c>,
/// <c>values</c>, <c>not</c> builtins, <c>if</c>/<c>elif</c>/<c>else</c>/<c>end</c>,
/// variable bindings <c>expr as $v | ...</c>, and emission flags <c>-r -c -S</c>.
///
/// The JSON input is parsed via <see cref="System.Text.Json"/> into the same
/// nested-hashtable / object[] shape the psm1 oracle produced via
/// <c>ConvertFrom-Json -AsHashtable</c>, so all downstream code paths see the
/// same value graph the oracle saw.
/// </summary>
internal static class JqEngine
{
    // ── JSON parsing (matches ConvertFrom-Json -AsHashtable) ───────────────

    /// <summary>
    /// Parses a JSON document into the nested hashtable / object[] / boxed
    /// primitive graph the psm1 jq oracle expected. Throws on malformed input.
    /// </summary>
    public static object? ParseJson(string jsonText)
    {
        using var doc = JsonDocument.Parse(
            jsonText,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
            });
        return ConvertElement(doc.RootElement);
    }

    public static object? ParseJson(Stream jsonStream)
    {
        using var doc = JsonDocument.Parse(
            jsonStream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
            });
        return ConvertElement(doc.RootElement);
    }

    private static object? ConvertElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long lv))
                {
                    if (lv >= int.MinValue && lv <= int.MaxValue) return (int)lv;
                    return lv;
                }
                return el.GetDouble();
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var child in el.EnumerateArray())
                {
                    list.Add(ConvertElement(child));
                }
                return list.ToArray();
            }
            case JsonValueKind.Object:
            {
                // Use an ordered case-sensitive dictionary; matches PowerShell
                // ConvertFrom-Json -AsHashtable ordering closely enough for
                // emit-back. Hashtable preserves insertion order under .NET 8+
                // is not guaranteed — use OrderedDictionary instead.
                var dict = new System.Collections.Specialized.OrderedDictionary();
                foreach (var prop in el.EnumerateObject())
                {
                    dict[prop.Name] = ConvertElement(prop.Value);
                }
                return dict;
            }
        }
        return null;
    }

    // ── JSON emission (ConvertTo-JqJson parity) ───────────────────────────

    public static string ToJson(object? value, bool compact, bool sortKeys, bool rawOutput)
    {
        if (value == null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is int or long or double or decimal or float)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }
        if (value is string s)
        {
            if (rawOutput) return s;
            return EncodeJsonString(s);
        }
        if (value is IDictionary dict)
        {
            return EmitDict(dict, compact, sortKeys);
        }
        if (value is IList list)
        {
            return EmitList(list, compact, sortKeys);
        }
        // Fallback for unexpected types
        return EncodeJsonString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string EmitList(IList list, bool compact, bool sortKeys)
    {
        if (list.Count == 0) return "[]";
        var items = new List<string>(list.Count);
        foreach (var item in list)
        {
            items.Add(ToJson(item, compact, sortKeys, rawOutput: false));
        }
        if (compact)
        {
            return "[" + string.Join(",", items) + "]";
        }
        var inner = string.Join(",\n", items.ConvertAll(x => "  " + x));
        return "[\n" + inner + "\n]";
    }

    private static string EmitDict(IDictionary dict, bool compact, bool sortKeys)
    {
        var keys = new List<string>();
        foreach (var k in dict.Keys)
        {
            keys.Add(k?.ToString() ?? string.Empty);
        }
        if (sortKeys)
        {
            keys.Sort(StringComparer.Ordinal);
        }
        if (keys.Count == 0) return "{}";

        var pairs = new List<string>(keys.Count);
        foreach (var k in keys)
        {
            string kJson = EncodeJsonString(k);
            string vJson = ToJson(dict[k], compact, sortKeys, rawOutput: false);
            if (compact)
            {
                pairs.Add(kJson + ":" + vJson);
            }
            else
            {
                pairs.Add("  " + kJson + ": " + vJson);
            }
        }
        if (compact)
        {
            return "{" + string.Join(",", pairs) + "}";
        }
        return "{\n" + string.Join(",\n", pairs) + "\n}";
    }

    private static string EncodeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ── Filter evaluation ─────────────────────────────────────────────────

    public sealed class JqException : Exception
    {
        public JqException(string message) : base(message) { }
    }

    /// <summary>
    /// Evaluates a jq filter against an input value, returning the (possibly
    /// empty) stream of result values. <paramref name="variables"/> is the
    /// scope for <c>$var</c> bindings and may be null.
    /// </summary>
    public static List<object?> Evaluate(object? data, string filter, Dictionary<string, object?>? variables)
    {
        variables ??= new Dictionary<string, object?>();
        return EvalFilter(data, filter.Trim(), variables);
    }

    private static List<object?> EvalFilter(object? data, string filter, Dictionary<string, object?> variables)
    {
        if (filter.Length == 0)
        {
            return new List<object?> { data };
        }

        // Top-level pipe split
        var pipeSegments = SplitTopLevel(filter, '|');
        if (pipeSegments.Count > 1)
        {
            var current = new List<object?> { data };
            var scope = variables;
            foreach (var rawSeg in pipeSegments)
            {
                var seg = rawSeg.Trim();
                // expr as $var | rest
                var asMatch = TryParseAsBinding(seg);
                if (asMatch != null)
                {
                    var (bindingExpr, varName) = asMatch.Value;
                    var bound = new List<object?>();
                    foreach (var item in current)
                    {
                        bound.AddRange(EvalFilter(item, bindingExpr, scope));
                    }
                    var newScope = new Dictionary<string, object?>(scope)
                    {
                        [varName] = bound.ToArray()
                    };
                    scope = newScope;
                    continue;
                }
                var next = new List<object?>();
                foreach (var item in current)
                {
                    next.AddRange(EvalFilter(item, seg, scope));
                }
                current = next;
            }
            return current;
        }

        // Top-level comma split
        var commaSegments = SplitTopLevel(filter, ',');
        if (commaSegments.Count > 1)
        {
            var results = new List<object?>();
            foreach (var seg in commaSegments)
            {
                results.AddRange(EvalFilter(data, seg.Trim(), variables));
            }
            return results;
        }

        // // alternative
        int altIdx = FindTopLevelStr(filter, "//");
        if (altIdx >= 0)
        {
            string leftExpr = filter.Substring(0, altIdx).Trim();
            string rightExpr = filter.Substring(altIdx + 2).Trim();
            // jq's `a // b` yields *every* non-false/non-null output of `a`; only
            // when `a` produces no truthy value at all does it fall back to `b`.
            // (The old code returned just the first truthy value, collapsing a
            // stream like `.[] // "x"` over [1,2,3] to a single `1`.)
            var leftResults = EvalFilter(data, leftExpr, variables);
            var kept = new List<object?>();
            foreach (var val in leftResults)
            {
                if (!IsFalsy(val)) { kept.Add(val); }
            }
            if (kept.Count > 0) { return kept; }
            return EvalFilter(data, rightExpr, variables);
        }

        // if-then-elif-else-end
        if (filter.StartsWith("if ", StringComparison.Ordinal))
        {
            return EvalIf(data, filter, variables);
        }

        // Boolean and / or (lower precedence than arithmetic). Whitespace-delimited
        // so they can't be confused with identifiers.
        foreach (var boolOp in new[] { " or ", " and " })
        {
            int bi = FindTopLevelStr(filter, boolOp);
            if (bi >= 0)
            {
                bool isOr = boolOp == " or ";
                var lefts = EvalFilter(data, filter.Substring(0, bi).Trim(), variables);
                var rights = EvalFilter(data, filter.Substring(bi + boolOp.Length).Trim(), variables);
                var res = new List<object?>();
                foreach (var l in lefts)
                    foreach (var r in rights)
                        res.Add(isOr ? (!IsFalsy(l) || !IsFalsy(r)) : (!IsFalsy(l) && !IsFalsy(r)));
                return res;
            }
        }

        // Arithmetic: + - (additive, lowest), then * / % (multiplicative). Split on
        // the LAST top-level whitespace-surrounded operator (left-associative). The
        // mandatory surrounding spaces keep `.a-b`, `-1`, and negative literals safe.
        var arith = FindLastSpacedArithOp(filter, additive: true)
                    ?? FindLastSpacedArithOp(filter, additive: false);
        if (arith != null)
        {
            var (opIdx, op) = arith.Value;
            var lefts = EvalFilter(data, filter.Substring(0, opIdx).Trim(), variables);
            var rights = EvalFilter(data, filter.Substring(opIdx + 3).Trim(), variables);
            var res = new List<object?>();
            foreach (var l in lefts)
                foreach (var r in rights)
                    res.Add(ApplyArith(l, op, r));
            return res;
        }

        // Recursive descent ..
        if (filter == "..")
        {
            return Recurse(data);
        }

        // Variable reference $name
        if (filter.StartsWith("$", StringComparison.Ordinal) && IsVariableToken(filter))
        {
            if (variables.TryGetValue(filter, out var v))
            {
                if (v is IList list)
                {
                    var copy = new List<object?>(list.Count);
                    foreach (var x in list) copy.Add(x);
                    return copy;
                }
                return new List<object?> { v };
            }
            return new List<object?> { null };
        }

        // Identity
        if (filter == ".") return new List<object?> { data };

        // Array construction [expr]
        if (filter.StartsWith("[", StringComparison.Ordinal)
            && MatchingBracket(filter, '[', ']', 0) == filter.Length - 1)
        {
            string inner = filter.Substring(1, filter.Length - 2);
            var items = EvalFilter(data, inner, variables);
            return new List<object?> { items.ToArray() };
        }

        // Object construction {k: v, ...}
        if (filter.StartsWith("{", StringComparison.Ordinal)
            && MatchingBracket(filter, '{', '}', 0) == filter.Length - 1)
        {
            string inner = filter.Substring(1, filter.Length - 2).Trim();
            var result = new System.Collections.Specialized.OrderedDictionary();
            var pairs = SplitTopLevel(inner, ',');
            foreach (var rawPair in pairs)
            {
                string pair = rawPair.Trim();
                if (pair.Length == 0) continue;
                int colonIdx = FindTopLevelChar(pair, ':');
                string keyPart;
                List<object?> vals;
                if (colonIdx >= 0)
                {
                    keyPart = pair.Substring(0, colonIdx).Trim();
                    string valExpr = pair.Substring(colonIdx + 1).Trim();
                    if (keyPart.Length >= 2 && keyPart.StartsWith("\"") && keyPart.EndsWith("\""))
                    {
                        keyPart = keyPart.Substring(1, keyPart.Length - 2);
                    }
                    else if (keyPart.Length >= 2 && keyPart[0] == '('
                        && MatchingBracket(keyPart, '(', ')', 0) == keyPart.Length - 1)
                    {
                        // Computed key: {(expr): v} — evaluate expr and use its
                        // first result (stringified) as the object key.
                        var keyVals = EvalFilter(data, keyPart.Substring(1, keyPart.Length - 2).Trim(), variables);
                        keyPart = keyVals.Count > 0
                            ? Convert.ToString(keyVals[0], CultureInfo.InvariantCulture) ?? string.Empty
                            : string.Empty;
                    }
                    vals = EvalFilter(data, valExpr, variables);
                }
                else
                {
                    keyPart = pair.TrimStart('.');
                    vals = EvalFilter(data, "." + keyPart, variables);
                }
                result[keyPart] = vals.Count == 1 ? vals[0] : vals.ToArray();
            }
            return new List<object?> { result };
        }

        // String literal with interpolation
        if (filter.Length >= 2 && filter[0] == '"' && filter[filter.Length - 1] == '"')
        {
            string strContent = filter.Substring(1, filter.Length - 2);
            string interp = ResolveStringInterpolation(strContent, data, variables);
            return new List<object?> { interp };
        }

        // Builtins
        switch (filter)
        {
            case "keys":
                return new List<object?> { KeysOf(data) };
            case "values":
                return new List<object?> { ValuesOf(data) };
            case "length":
                return new List<object?> { LengthOf(data) };
            case "type":
                return new List<object?> { TypeOf(data) };
            case "not":
                return new List<object?> { IsFalsy(data) };
            case "true":
                return new List<object?> { true };
            case "false":
                return new List<object?> { false };
            case "null":
                return new List<object?> { null };
            case "add":
                return new List<object?> { AddOf(data) };
            case "tostring":
                return new List<object?> { data is string ts ? ts : JqToString(data) };
            case "tonumber":
                return new List<object?> { ToNumber(data) };
            case "ascii_downcase":
                return new List<object?> { data is string ds ? ds.ToLowerInvariant() : data };
            case "ascii_upcase":
                return new List<object?> { data is string us ? us.ToUpperInvariant() : data };
            case "sort":
                return new List<object?> { SortArray(data, null, variables) };
            case "unique":
                return new List<object?> { UniqueArray(data, null, variables) };
            case "reverse":
                return new List<object?> { ReverseValue(data) };
            case "first":
                return new List<object?> { data is IList fl && fl.Count > 0 ? fl[0] : null };
            case "last":
                return new List<object?> { data is IList ll && ll.Count > 0 ? ll[ll.Count - 1] : null };
            case "min":
                return new List<object?> { MinMax(data, wantMax: false) };
            case "max":
                return new List<object?> { MinMax(data, wantMax: true) };
            case "flatten":
                return new List<object?> { FlattenArray(data) };
            case "to_entries":
                return new List<object?> { ToEntries(data) };
            case "from_entries":
                return new List<object?> { FromEntries(data) };
            case "@base64":
                return new List<object?> { Convert.ToBase64String(Encoding.UTF8.GetBytes(data is string b64s ? b64s : JqToString(data))) };
            case "@base64d":
                return new List<object?> { data is string b64d ? Encoding.UTF8.GetString(Convert.FromBase64String(b64d)) : null };
            case "@json":
                return new List<object?> { JqSerialize(data) };
            case "@csv":
                return new List<object?> { RowToDelimited(data, ',', quote: true) };
            case "@tsv":
                return new List<object?> { RowToDelimited(data, '\t', quote: false) };
        }

        // has(KEY) — object/array membership test.
        if (filter.StartsWith("has(", StringComparison.Ordinal) && filter.EndsWith(")", StringComparison.Ordinal))
        {
            string inner = filter.Substring(4, filter.Length - 5).Trim();
            return new List<object?> { HasKey(data, inner) };
        }

        // group_by(expr) / sort_by(expr) / unique_by(expr)
        foreach (var (name, prefix) in new[] { ("group_by", "group_by("), ("sort_by", "sort_by("), ("unique_by", "unique_by(") })
        {
            if (filter.StartsWith(prefix, StringComparison.Ordinal) && filter.EndsWith(")", StringComparison.Ordinal))
            {
                string keyExpr = filter.Substring(prefix.Length, filter.Length - prefix.Length - 1);
                return new List<object?> { name switch
                {
                    "group_by" => GroupBy(data, keyExpr, variables),
                    "sort_by" => SortArray(data, keyExpr, variables),
                    _ => UniqueArray(data, keyExpr, variables),
                } };
            }
        }

        // String builtins: join(sep), split(sep), startswith(s), endswith(s), ltrimstr/rtrimstr.
        foreach (var (name, prefix) in new[]
                 { ("join", "join("), ("split", "split("), ("startswith", "startswith("),
                   ("endswith", "endswith("), ("ltrimstr", "ltrimstr("), ("rtrimstr", "rtrimstr(") })
        {
            if (filter.StartsWith(prefix, StringComparison.Ordinal) && filter.EndsWith(")", StringComparison.Ordinal))
            {
                string argExpr = filter.Substring(prefix.Length, filter.Length - prefix.Length - 1).Trim();
                string argVal = UnquoteJqString(argExpr);
                return new List<object?> { StringBuiltin(name, data, argVal) };
            }
        }

        // map(expr)
        if (filter.StartsWith("map(", StringComparison.Ordinal) && filter.EndsWith(")", StringComparison.Ordinal))
        {
            int close = MatchingBracket(filter, '(', ')', 3);
            if (close == filter.Length - 1)
            {
                string innerExpr = filter.Substring(4, filter.Length - 5);
                var items = new List<object?>();
                if (data is IList list)
                {
                    foreach (var elem in list)
                    {
                        items.AddRange(EvalFilter(elem, innerExpr, variables));
                    }
                }
                return new List<object?> { items.ToArray() };
            }
        }

        // select(expr)
        if (filter.StartsWith("select(", StringComparison.Ordinal) && filter.EndsWith(")", StringComparison.Ordinal))
        {
            int close = MatchingBracket(filter, '(', ')', 6);
            if (close == filter.Length - 1)
            {
                string expr = filter.Substring(7, filter.Length - 8);
                bool keep = EvalSelect(data, expr, variables);
                if (keep) return new List<object?> { data };
                return new List<object?>();
            }
        }

        // Dot-path
        if (filter.StartsWith(".", StringComparison.Ordinal))
        {
            return ResolveDotPath(data, filter);
        }

        // Numeric literal
        if (IsNumericLiteral(filter))
        {
            // Match oracle: it cast to [double] always; mirror that for safety.
            return new List<object?> { double.Parse(filter, CultureInfo.InvariantCulture) };
        }

        throw new JqException($"jq: unknown filter: {filter}");
    }

    private static (string bindingExpr, string varName)? TryParseAsBinding(string seg)
    {
        // ^(.+?)\s+as\s+(\$\w+)\s*$
        int asIdx = -1;
        // search for "\s+as\s+" at top level (not inside quotes/parens)
        int depth = 0; bool inStr = false;
        for (int i = 0; i < seg.Length - 4; i++)
        {
            char c = seg[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < seg.Length) { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            if (depth == 0 && char.IsWhiteSpace(c)
                && i + 3 < seg.Length
                && seg[i + 1] == 'a' && seg[i + 2] == 's'
                && (char.IsWhiteSpace(seg[i + 3])))
            {
                // Check left boundary: char before is letter/digit/punct from expr is fine; we just need "as" between whitespace
                asIdx = i;
                break;
            }
        }
        if (asIdx < 0) return null;
        string left = seg.Substring(0, asIdx).Trim();
        string rest = seg.Substring(asIdx + 1).TrimStart();
        // rest starts with "as"
        if (!rest.StartsWith("as", StringComparison.Ordinal)) return null;
        rest = rest.Substring(2).TrimStart();
        if (rest.Length < 2 || rest[0] != '$') return null;
        int p = 1;
        while (p < rest.Length && (char.IsLetterOrDigit(rest[p]) || rest[p] == '_')) p++;
        string varName = rest.Substring(0, p);
        string after = rest.Substring(p).Trim();
        if (after.Length > 0) return null;
        if (left.Length == 0) return null;
        return (left, varName);
    }

    private static bool IsVariableToken(string s)
    {
        if (s.Length < 2 || s[0] != '$') return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private static bool IsNumericLiteral(string s)
    {
        if (s.Length == 0) return false;
        int i = 0;
        if (s[0] == '-') i++;
        if (i >= s.Length) return false;
        bool sawDigit = false;
        while (i < s.Length && char.IsDigit(s[i])) { sawDigit = true; i++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i])) { sawDigit = true; i++; }
        }
        return sawDigit && i == s.Length;
    }

    private static bool IsFalsy(object? v)
    {
        if (v == null) return true;
        if (v is bool b && !b) return true;
        return false;
    }

    // ── Builtins ──────────────────────────────────────────────────────────

    private static object?[] KeysOf(object? data)
    {
        if (data is IDictionary dict)
        {
            var names = new List<string>();
            foreach (var k in dict.Keys) names.Add(k?.ToString() ?? string.Empty);
            names.Sort(StringComparer.Ordinal);
            return names.ToArray().Cast<object?>().ToArray();
        }
        if (data is IList list)
        {
            var arr = new object?[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = i;
            return arr;
        }
        return Array.Empty<object?>();
    }

    private static object?[] ValuesOf(object? data)
    {
        if (data is IDictionary dict)
        {
            var vals = new List<object?>();
            foreach (var v in dict.Values) vals.Add(v);
            return vals.ToArray();
        }
        if (data is IList list)
        {
            var arr = new object?[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }
        return Array.Empty<object?>();
    }

    private static int LengthOf(object? data)
    {
        if (data == null) return 0;
        if (data is string s) return s.Length;
        if (data is IList list) return list.Count;
        if (data is IDictionary dict) return dict.Count;
        return 0;
    }

    private static string TypeOf(object? data)
    {
        if (data == null) return "null";
        if (data is bool) return "boolean";
        if (data is int or long or double or decimal or float) return "number";
        if (data is string) return "string";
        if (data is IList) return "array";
        if (data is IDictionary) return "object";
        return "unknown";
    }

    /// <summary>jq <c>add</c>: sum numbers / concatenate strings or arrays /
    /// merge objects across an array's elements. Empty or non-array → null.</summary>
    private static object? AddOf(object? data)
    {
        if (data is not IList list || list.Count == 0) return null;
        // Numbers → sum.
        if (list[0] is int or long or double or decimal or float)
        {
            double sum = 0;
            foreach (var e in list) sum += Convert.ToDouble(e, CultureInfo.InvariantCulture);
            return sum;
        }
        // Strings → concat.
        if (list[0] is string)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in list) sb.Append(e?.ToString() ?? string.Empty);
            return sb.ToString();
        }
        // Arrays → concat into one list.
        if (list[0] is IList)
        {
            var combined = new List<object?>();
            foreach (var e in list)
            {
                if (e is IList el) foreach (var x in el) combined.Add(x);
            }
            return combined.ToArray();
        }
        return null;
    }

    /// <summary>jq <c>tostring</c> for non-string scalars: numbers/bools/null
    /// render like jq's compact output.</summary>
    private static string JqToString(object? data)
    {
        if (data == null) return "null";
        if (data is bool b) return b ? "true" : "false";
        if (data is double d) return d.ToString(CultureInfo.InvariantCulture);
        if (data is int or long or decimal or float) return Convert.ToString(data, CultureInfo.InvariantCulture) ?? string.Empty;
        return data.ToString() ?? string.Empty;
    }

    /// <summary>jq <c>tonumber</c>: parse a string to a number; pass numbers through.</summary>
    private static object? ToNumber(object? data)
    {
        if (data is int or long or double or decimal or float) return data;
        if (data is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
        throw new JqException("jq: cannot be parsed as a number");
    }

    // ── array / object / string operators (no arithmetic evaluator needed) ────

    private static int TypeRank(object? v) => v switch
    {
        null => 0,
        bool => 1,
        int or long or double or decimal or float => 2,
        string => 3,
        IList => 4,
        IDictionary => 5,
        _ => 6,
    };

    /// <summary>jq's total value ordering: null &lt; bool &lt; number &lt; string &lt; array &lt; object.</summary>
    private static int JqCompare(object? a, object? b)
    {
        int ta = TypeRank(a), tb = TypeRank(b);
        if (ta != tb) return ta.CompareTo(tb);
        switch (ta)
        {
            case 1: return ((bool)a!).CompareTo((bool)b!);
            case 2: return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
            case 3: return string.CompareOrdinal((string)a!, (string)b!);
            case 4:
            {
                var la = (IList)a!; var lb = (IList)b!;
                int n = Math.Min(la.Count, lb.Count);
                for (int i = 0; i < n; i++) { int c = JqCompare(la[i], lb[i]); if (c != 0) return c; }
                return la.Count.CompareTo(lb.Count);
            }
            default: return 0;
        }
    }

    private static object? KeyOf(object? elem, string? keyExpr, Dictionary<string, object?> vars)
        => keyExpr == null ? elem : EvalFilter(elem, keyExpr, vars).Count > 0 ? EvalFilter(elem, keyExpr, vars)[0] : null;

    private static object?[] SortArray(object? data, string? keyExpr, Dictionary<string, object?> vars)
    {
        if (data is not IList list) return Array.Empty<object?>();
        var items = new List<object?>();
        foreach (var e in list) items.Add(e);
        items.Sort((x, y) => JqCompare(KeyOf(x, keyExpr, vars), KeyOf(y, keyExpr, vars)));
        return items.ToArray();
    }

    private static object?[] UniqueArray(object? data, string? keyExpr, Dictionary<string, object?> vars)
    {
        var sorted = SortArray(data, keyExpr, vars);
        var result = new List<object?>();
        object? prevKey = null; bool first = true;
        foreach (var item in sorted)
        {
            var key = KeyOf(item, keyExpr, vars);
            if (first || JqCompare(key, prevKey) != 0) { result.Add(item); prevKey = key; first = false; }
        }
        return result.ToArray();
    }

    private static object? ReverseValue(object? data)
    {
        if (data is string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
        if (data is IList list) { var r = new List<object?>(); for (int i = list.Count - 1; i >= 0; i--) r.Add(list[i]); return r.ToArray(); }
        return data;
    }

    private static object? MinMax(object? data, bool wantMax)
    {
        if (data is not IList list || list.Count == 0) return null;
        object? best = list[0];
        for (int i = 1; i < list.Count; i++)
        {
            int c = JqCompare(list[i], best);
            if (wantMax ? c > 0 : c < 0) best = list[i];
        }
        return best;
    }

    private static object?[] FlattenArray(object? data)
    {
        var result = new List<object?>();
        void Walk(object? v)
        {
            if (v is IList l) foreach (var e in l) Walk(e);
            else result.Add(v);
        }
        if (data is IList top) foreach (var e in top) Walk(e);
        return result.ToArray();
    }

    private static object?[] ToEntries(object? data)
    {
        var result = new List<object?>();
        if (data is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["key"] = e.Key?.ToString(),
                    ["value"] = e.Value,
                });
            }
        }
        return result.ToArray();
    }

    private static object FromEntries(object? data)
    {
        // Insertion-ordered: jq preserves entry order through to_entries/from_entries
        // round-trips. A plain Dictionary would re-order by hash.
        var obj = new System.Collections.Specialized.OrderedDictionary();
        if (data is IList list)
        {
            foreach (var entry in list)
            {
                if (entry is IDictionary d)
                {
                    string? key = (d["key"] ?? d["k"] ?? d["name"])?.ToString();
                    object? val = d.Contains("value") ? d["value"] : d.Contains("v") ? d["v"] : null;
                    if (key != null) obj[key] = val;
                }
            }
        }
        return obj;
    }

    private static object?[] GroupBy(object? data, string keyExpr, Dictionary<string, object?> vars)
    {
        if (data is not IList list) return Array.Empty<object?>();
        var withKeys = new List<(object? key, object? val)>();
        foreach (var e in list) withKeys.Add((KeyOf(e, keyExpr, vars), e));
        withKeys.Sort((x, y) => JqCompare(x.key, y.key));

        var groups = new List<List<object?>>();
        object? prev = null; bool first = true;
        foreach (var (key, val) in withKeys)
        {
            if (first || JqCompare(key, prev) != 0) { groups.Add(new List<object?>()); prev = key; first = false; }
            groups[groups.Count - 1].Add(val);
        }
        var outArr = new object?[groups.Count];
        for (int i = 0; i < groups.Count; i++) outArr[i] = groups[i].ToArray();
        return outArr;
    }

    private static bool HasKey(object? data, string innerExpr)
    {
        if (data is IDictionary dict)
        {
            string key = UnquoteJqString(innerExpr);
            foreach (var k in dict.Keys) if (string.Equals(k?.ToString(), key, StringComparison.Ordinal)) return true;
            return false;
        }
        if (data is IList list && int.TryParse(innerExpr, out var idx)) return idx >= 0 && idx < list.Count;
        return false;
    }

    private static object? StringBuiltin(string name, object? data, string arg)
    {
        switch (name)
        {
            case "join":
            {
                if (data is not IList list) return null;
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(arg);
                    sb.Append(list[i] is string s ? s : JqToString(list[i]));
                }
                return sb.ToString();
            }
            case "split":
                return data is string ss ? ss.Split(new[] { arg }, StringSplitOptions.None).Cast<object?>().ToArray() : null;
            case "startswith":
                return data is string sw && sw.StartsWith(arg, StringComparison.Ordinal);
            case "endswith":
                return data is string ew && ew.EndsWith(arg, StringComparison.Ordinal);
            case "ltrimstr":
                return data is string ls && ls.StartsWith(arg, StringComparison.Ordinal) ? ls.Substring(arg.Length) : data;
            case "rtrimstr":
                return data is string rs && rs.EndsWith(arg, StringComparison.Ordinal) ? rs.Substring(0, rs.Length - arg.Length) : data;
            default: return data;
        }
    }

    private static string JqSerialize(object? data) => ToJson(data, compact: true, sortKeys: false, rawOutput: false);

    /// <summary>jq <c>@csv</c> / <c>@tsv</c>: render an array as one delimited row.</summary>
    private static string RowToDelimited(object? data, char sep, bool quote)
    {
        if (data is not IList list) return string.Empty;
        var parts = new List<string>(list.Count);
        foreach (var v in list)
        {
            if (v is string s)
            {
                parts.Add(quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
            }
            else if (v == null)
            {
                parts.Add(string.Empty);
            }
            else
            {
                parts.Add(JqToString(v));
            }
        }
        return string.Join(sep.ToString(), parts);
    }

    private static string UnquoteJqString(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
        return s;
    }

    /// <summary>
    /// Find the LAST top-level whitespace-surrounded arithmetic operator (for
    /// left-associative evaluation). Mandatory spaces around the operator keep
    /// dot-paths (<c>.a-b</c>), negative literals, and array indices safe.
    /// Returns (index of the leading space, operator char) or null.
    /// </summary>
    private static (int idx, char op)? FindLastSpacedArithOp(string f, bool additive)
    {
        string ops = additive ? "+-" : "*/%";
        int depth = 0; bool inStr = false; int found = -1; char foundOp = '\0';
        for (int i = 0; i < f.Length; i++)
        {
            char c = f[i];
            if (c == '"' && (i == 0 || f[i - 1] != '\\')) { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == ' ' && i + 2 < f.Length && f[i + 2] == ' ' && ops.IndexOf(f[i + 1]) >= 0)
            {
                found = i; foundOp = f[i + 1];
            }
        }
        return found >= 0 ? (found, foundOp) : null;
    }

    private static bool IsNum(object? v) => v is int or long or double or decimal or float;
    private static double ToD(object? v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);

    /// <summary>jq binary arithmetic: number math, plus the string/array/object
    /// overloads (<c>+</c> concat/merge, <c>-</c> array difference, <c>*</c> string
    /// repeat, <c>/</c> string split).</summary>
    private static object? ApplyArith(object? l, char op, object? r)
    {
        switch (op)
        {
            case '+':
                if (l == null) return r;
                if (r == null) return l;
                if (IsNum(l) && IsNum(r)) return ToD(l) + ToD(r);
                if (l is string ls && r is string rs) return ls + rs;
                if (l is IList la && r is IList lb)
                {
                    var c = new List<object?>();
                    foreach (var x in la) c.Add(x);
                    foreach (var x in lb) c.Add(x);
                    return c.ToArray();
                }
                if (l is IDictionary da && r is IDictionary db)
                {
                    var m = new Dictionary<string, object?>();
                    foreach (DictionaryEntry e in da) m[e.Key?.ToString() ?? ""] = e.Value;
                    foreach (DictionaryEntry e in db) m[e.Key?.ToString() ?? ""] = e.Value;
                    return m;
                }
                throw new JqException($"jq: {TypeOf(l)} and {TypeOf(r)} cannot be added");
            case '-':
                if (IsNum(l) && IsNum(r)) return ToD(l) - ToD(r);
                if (l is IList la2 && r is IList lb2)
                {
                    var c = new List<object?>();
                    foreach (var x in la2)
                    {
                        bool inR = false;
                        foreach (var y in lb2) if (JqCompare(x, y) == 0) { inR = true; break; }
                        if (!inR) c.Add(x);
                    }
                    return c.ToArray();
                }
                throw new JqException($"jq: {TypeOf(l)} and {TypeOf(r)} cannot be subtracted");
            case '*':
                if (IsNum(l) && IsNum(r)) return ToD(l) * ToD(r);
                if (l is string sl && IsNum(r)) { int n = (int)ToD(r); return n <= 0 ? null : string.Concat(Enumerable.Repeat(sl, n)); }
                if (r is string sr && IsNum(l)) { int n = (int)ToD(l); return n <= 0 ? null : string.Concat(Enumerable.Repeat(sr, n)); }
                throw new JqException($"jq: {TypeOf(l)} and {TypeOf(r)} cannot be multiplied");
            case '/':
                if (IsNum(l) && IsNum(r)) { double d = ToD(r); if (d == 0) throw new JqException("jq: number divided by zero"); return ToD(l) / d; }
                if (l is string sls && r is string srs) return sls.Split(new[] { srs }, StringSplitOptions.None).Cast<object?>().ToArray();
                throw new JqException($"jq: {TypeOf(l)} and {TypeOf(r)} cannot be divided");
            case '%':
                if (IsNum(l) && IsNum(r)) { long rr = (long)ToD(r); if (rr == 0) throw new JqException("jq: number divided by zero"); return (double)((long)ToD(l) % rr); }
                throw new JqException($"jq: {TypeOf(l)} and {TypeOf(r)} cannot be divided (remainder)");
            default:
                return null;
        }
    }

    private static List<object?> Recurse(object? data)
    {
        var result = new List<object?>();
        result.Add(data);
        if (data is IList list)
        {
            foreach (var elem in list) result.AddRange(Recurse(elem));
        }
        else if (data is IDictionary dict)
        {
            foreach (var v in dict.Values) result.AddRange(Recurse(v));
        }
        return result;
    }

    // ── select ────────────────────────────────────────────────────────────

    private static bool EvalSelect(object? data, string expr, Dictionary<string, object?> variables)
    {
        // ops in oracle order: >=, <=, !=, ==, >, <
        string[] ops = { ">=", "<=", "!=", "==", ">", "<" };
        foreach (var op in ops)
        {
            int idx = FindTopLevelStr(expr, op);
            if (idx >= 0)
            {
                string leftExpr = expr.Substring(0, idx).Trim();
                string rightExpr = expr.Substring(idx + op.Length).Trim();
                var leftVals = EvalFilter(data, leftExpr, variables);
                var rightVals = EvalFilter(data, rightExpr, variables);
                object? left = leftVals.Count > 0 ? leftVals[0] : null;
                object? right = rightVals.Count > 0 ? rightVals[0] : null;
                return CompareValues(left, right, op);
            }
        }
        // Truthy check
        var vals = EvalFilter(data, expr, variables);
        if (vals.Count == 0) return false;
        return !IsFalsy(vals[0]);
    }

    private static bool CompareValues(object? left, object? right, string op)
    {
        if (op == "==") return Equals(left, right) || NumericEquals(left, right);
        if (op == "!=") return !(Equals(left, right) || NumericEquals(left, right));

        // Ordering: convert to comparable. Numbers via double; strings ordinal.
        if (TryAsDouble(left, out double l) && TryAsDouble(right, out double r))
        {
            switch (op)
            {
                case ">": return l > r;
                case "<": return l < r;
                case ">=": return l >= r;
                case "<=": return l <= r;
            }
        }
        string ls = left?.ToString() ?? string.Empty;
        string rs = right?.ToString() ?? string.Empty;
        int cmp = string.CompareOrdinal(ls, rs);
        switch (op)
        {
            case ">": return cmp > 0;
            case "<": return cmp < 0;
            case ">=": return cmp >= 0;
            case "<=": return cmp <= 0;
        }
        return false;
    }

    private static bool NumericEquals(object? a, object? b)
    {
        if (TryAsDouble(a, out double da) && TryAsDouble(b, out double db))
        {
            return da == db;
        }
        return false;
    }

    private static bool TryAsDouble(object? v, out double d)
    {
        switch (v)
        {
            case int i: d = i; return true;
            case long l: d = l; return true;
            case double dd: d = dd; return true;
            case decimal m: d = (double)m; return true;
            case float f: d = f; return true;
        }
        d = 0;
        return false;
    }

    // ── if-then-elif-else-end ─────────────────────────────────────────────

    private static List<object?> EvalIf(object? data, string filter, Dictionary<string, object?> variables)
    {
        string rest = filter;
        while (rest.StartsWith("if ", StringComparison.Ordinal))
        {
            int thenIdx = FindKeyword(rest, "then");
            if (thenIdx < 0) throw new JqException("jq: expected 'then' in if expression");
            string condExpr = rest.Substring(3, thenIdx - 3).Trim();
            rest = rest.Substring(thenIdx + 4).TrimStart();

            var nextKw = FindBranchKeyword(rest);
            string bodyExpr = rest.Substring(0, nextKw.Index).Trim();
            rest = rest.Substring(nextKw.Index).TrimStart();

            var condVals = EvalFilter(data, condExpr, variables);
            bool condTrue = condVals.Count > 0 && !IsFalsy(condVals[0]);
            if (condTrue)
            {
                return EvalFilter(data, bodyExpr, variables);
            }
            if (nextKw.Keyword == "elif")
            {
                rest = "if " + rest.Substring(4).TrimStart();
                continue;
            }
            if (nextKw.Keyword == "else")
            {
                int endIdx = FindKeyword(rest, "end");
                if (endIdx < 0) throw new JqException("jq: expected 'end' in if expression");
                string elseBody = rest.Substring(4, endIdx - 4).Trim();
                return EvalFilter(data, elseBody, variables);
            }
            // end with no match
            return new List<object?>();
        }
        return new List<object?>();
    }

    // ── String interpolation ──────────────────────────────────────────────

    private static string ResolveStringInterpolation(string s, object? data, Dictionary<string, object?> variables)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char nc = s[i + 1];
                if (nc == '(')
                {
                    int depth = 1;
                    int start = i + 2;
                    int j = start;
                    while (j < s.Length && depth > 0)
                    {
                        if (s[j] == '(') depth++;
                        else if (s[j] == ')') depth--;
                        if (depth > 0) j++;
                    }
                    string expr = s.Substring(start, j - start);
                    var vals = EvalFilter(data, expr, variables);
                    object? val = vals.Count > 0 ? vals[0] : string.Empty;
                    sb.Append(val);
                    i = j + 1;
                    continue;
                }
                if (nc == 'n') { sb.Append('\n'); i += 2; continue; }
                if (nc == 't') { sb.Append('\t'); i += 2; continue; }
                if (nc == '\\') { sb.Append('\\'); i += 2; continue; }
                if (nc == '"') { sb.Append('"'); i += 2; continue; }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    // ── Dot-path resolution ───────────────────────────────────────────────

    private static List<object?> ResolveDotPath(object? data, string path)
    {
        int pos = 1; // skip leading '.'
        var current = new List<object?> { data };

        while (pos < path.Length)
        {
            char ch = path[pos];
            if (ch == '[')
            {
                int closeIdx = MatchingBracket(path, '[', ']', pos);
                if (closeIdx < 0) throw new JqException("jq: unmatched [ in path");
                string inner = path.Substring(pos + 1, closeIdx - pos - 1).Trim();
                pos = closeIdx + 1;
                var next = new List<object?>();
                if (inner.Length == 0)
                {
                    foreach (var item in current)
                    {
                        if (item is IList list)
                        {
                            foreach (var elem in list) next.Add(elem);
                        }
                        else if (item is IDictionary dict)
                        {
                            foreach (var v in dict.Values) next.Add(v);
                        }
                    }
                }
                else
                {
                    if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    {
                        throw new JqException($"jq: index must be integer: {inner}");
                    }
                    foreach (var item in current)
                    {
                        if (item is IList list)
                        {
                            int actual = idx;
                            if (actual < 0) actual = list.Count + actual;
                            if (actual >= 0 && actual < list.Count) next.Add(list[actual]);
                            else next.Add(null);
                        }
                    }
                }
                current = next;
                continue;
            }
            if (ch == '.')
            {
                pos++;
                continue;
            }
            int nameStart = pos;
            while (pos < path.Length && path[pos] != '.' && path[pos] != '[') pos++;
            string fieldName = path.Substring(nameStart, pos - nameStart);
            if (fieldName.Length == 0) continue;
            var newNext = new List<object?>();
            foreach (var item in current)
            {
                object? val = null;
                if (item is IDictionary dict)
                {
                    if (dict.Contains(fieldName)) val = dict[fieldName];
                }
                newNext.Add(val);
            }
            current = newNext;
        }
        return current;
    }

    // ── Tokenizing helpers ────────────────────────────────────────────────

    private static List<string> SplitTopLevel(string filter, char splitChar)
    {
        var segments = new List<string>();
        int depth = 0;
        bool inStr = false;
        var current = new StringBuilder();
        for (int i = 0; i < filter.Length; i++)
        {
            char c = filter[i];
            if (inStr)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < filter.Length)
                {
                    i++;
                    current.Append(filter[i]);
                }
                else if (c == '"')
                {
                    inStr = false;
                }
                continue;
            }
            if (c == '"') { inStr = true; current.Append(c); continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            if (c == splitChar && depth == 0)
            {
                segments.Add(current.ToString().Trim());
                current = new StringBuilder();
                continue;
            }
            current.Append(c);
        }
        string last = current.ToString().Trim();
        if (last.Length > 0) segments.Add(last);
        return segments;
    }

    private static int MatchingBracket(string s, char open, char close, int start)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindTopLevelChar(string s, char ch)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            if (c == ch && depth == 0) return i;
        }
        return -1;
    }

    private static int FindTopLevelStr(string s, string sub)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = 0; i <= s.Length - sub.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            if (depth == 0 && s.Substring(i, sub.Length) == sub) return i;
        }
        return -1;
    }

    private static int FindKeyword(string s, string keyword)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = 0; i <= s.Length - keyword.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            if (depth == 0 && s.Substring(i, keyword.Length) == keyword)
            {
                bool beforeOk = i == 0 || IsBoundaryBefore(s[i - 1]);
                int afterIdx = i + keyword.Length;
                bool afterOk = afterIdx >= s.Length || IsBoundaryAfter(s[afterIdx]);
                if (beforeOk && afterOk) return i;
            }
        }
        return -1;
    }

    private readonly struct BranchKw
    {
        public BranchKw(int idx, string kw) { Index = idx; Keyword = kw; }
        public int Index { get; }
        public string Keyword { get; }
    }

    private static BranchKw FindBranchKeyword(string s)
    {
        int bestIdx = s.Length;
        string bestKw = "end";
        foreach (var kw in new[] { "elif", "else", "end" })
        {
            int idx = FindKeyword(s, kw);
            if (idx >= 0 && idx < bestIdx)
            {
                bestIdx = idx;
                bestKw = kw;
            }
        }
        return new BranchKw(bestIdx, bestKw);
    }

    private static bool IsBoundaryBefore(char c)
        => char.IsWhiteSpace(c) || c == '(' || c == '[' || c == '{' || c == ',' || c == ';';

    private static bool IsBoundaryAfter(char c)
        => char.IsWhiteSpace(c) || c == ')' || c == ']' || c == '}' || c == ',' || c == ';';
}
