using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>A malformed JSON text; the message is jq's wording including <c>at line L, column C</c> (or <c>at EOF at line ...</c>).</summary>
internal sealed class JqJsonException : Exception
{
    public JqJsonException(string message) : base(message) { }
}

/// <summary>
/// A port of jq 1.7's incremental JSON parser (<c>jv_parse.c</c>): the same character classes, the same token/stack machine and therefore
/// the same error messages and line/column positions (a position counts the character that exposed the error). It reads a stream of
/// whitespace-separated top-level values; numbers keep their literal text (<see cref="JqNumber"/>); <c>nan</c>/<c>NaN</c>/<c>Infinity</c> are
/// numbers; a raw control character inside a string is an error; a lone high-surrogate escape is an error, a lone low one becomes U+FFFD.
/// </summary>
internal sealed class JqJsonReader
{
    private sealed class KeyMark { public KeyMark(string key) { Key = key; } public string Key { get; } }

    private readonly string _s;
    private int _i;
    private int _line = 1, _col;
    private readonly List<object> _stack = new();
    private bool _hasNext;
    private object? _next;
    private readonly StringBuilder _tok = new();
    private bool _inString, _escape;
    private bool _eof;

    public JqJsonReader(string text)
    {
        _s = text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
    }

    /// <summary>The line count jq would report (<c>input_line_number</c>, "(at &lt;stdin&gt;:N)"): jq feeds its parser one line at a time, so this is
    /// the number of newlines up to and including the end of the line that completed the last value.</summary>
    public int LinesConsumedThroughLine
    {
        get
        {
            int end = _i - 1;
            if (end < 0) return 0;
            int stop = _s.IndexOf('\n', Math.Min(end, _s.Length - 1) < 0 ? 0 : Math.Min(end, _s.Length - 1));
            int limit = stop < 0 ? _s.Length - 1 : stop;
            int n = 0;
            for (int k = 0; k <= limit && k < _s.Length; k++) if (_s[k] == '\n') n++;
            return n;
        }
    }

    private JqJsonException Fail(string message) => new($"{message} at line {_line}, column {_col}");
    private JqJsonException FailEof(string message) => new($"{message} at EOF at line {_line}, column {_col}");

    private enum Class { Whitespace, Structure, Quote, Literal }

    private static Class Classify(char c) => c switch
    {
        ' ' or '\t' or '\r' or '\n' => Class.Whitespace,
        '"' => Class.Quote,
        '[' or ',' or ']' or '{' or ':' or '}' => Class.Structure,
        _ => Class.Literal,
    };

    /// <summary>Reads the next top-level value; false at the end of the text. Throws <see cref="JqJsonException"/> on malformed input.</summary>
    public bool TryRead(out object? value)
    {
        value = null;
        if (_eof) return false;
        while (_i < _s.Length)
        {
            char ch = _s[_i++];
            if (Scan(ch))
            {
                value = _next;
                _next = null;
                _hasNext = false;
                return true;
            }
        }

        _eof = true;
        if (_inString) throw FailEof("Unfinished string");
        var literalError = CheckLiteral();
        if (literalError != null) throw FailEof(literalError);
        if (_stack.Count != 0) throw FailEof("Unfinished JSON term");
        if (_hasNext)
        {
            value = _next;
            _next = null;
            _hasNext = false;
            return true;
        }
        return false;
    }

    private bool Scan(char ch)
    {
        _col++;
        if (ch == '\n') { _line++; _col = 0; }

        if (!_inString)
        {
            var cls = Classify(ch);
            if (cls != Class.Literal)
            {
                var error = CheckLiteral();
                if (error != null) throw Fail(error);
                if (cls == Class.Structure)
                {
                    error = Token(ch);
                    if (error != null) throw Fail(error);
                }
            }
            if (cls == Class.Literal) _tok.Append(ch);
            if (cls == Class.Quote) { _inString = true; _escape = false; }
            return _stack.Count == 0 && _hasNext;
        }

        if (ch == '"' && !_escape)
        {
            var error = FoundString();
            if (error != null) throw Fail(error);
            _inString = false;
            return _stack.Count == 0 && _hasNext;
        }
        _tok.Append(ch);
        _escape = ch == '\\' && !_escape;
        return false;
    }

    private string? Value(object? v)
    {
        if (_hasNext) return "Expected separator between values";
        _next = v;
        _hasNext = true;
        return null;
    }

    private string? CheckLiteral()
    {
        if (_tok.Length == 0) return null;
        string t = _tok.ToString();
        _tok.Clear();
        string? pattern = null;
        object? v = null;
        switch (t[0])
        {
            case 't': pattern = "true"; v = true; break;
            case 'f': pattern = "false"; v = false; break;
            case 'n':
                if (t.Length == 3) { pattern = "nan"; v = double.NaN; }
                else { pattern = "null"; v = null; }
                break;
        }
        if (pattern != null)
        {
            if (t != pattern) return "Invalid literal";
            return Value(v);
        }
        if (!TryNumber(t, out var number)) return "Invalid numeric literal";
        return Value(number);
    }

    private static bool TryNumber(string t, out object? number)
    {
        number = null;
        string body = t;
        bool negative = false;
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+')) { negative = body[0] == '-'; body = body.Substring(1); }
        string lower = body.ToLowerInvariant();
        if (lower is "infinity" or "inf") { number = negative ? double.NegativeInfinity : double.PositiveInfinity; return true; }
        if (lower.StartsWith("nan", StringComparison.Ordinal) && lower.Skip(3).All(char.IsDigit)) { number = double.NaN; return true; }

        int i = 0, digits = 0;
        while (i < body.Length && body[i] >= '0' && body[i] <= '9') { i++; digits++; }
        if (i < body.Length && body[i] == '.')
        {
            i++;
            while (i < body.Length && body[i] >= '0' && body[i] <= '9') { i++; digits++; }
        }
        if (digits == 0) return false;
        if (i < body.Length && (body[i] == 'e' || body[i] == 'E'))
        {
            i++;
            if (i < body.Length && (body[i] == '+' || body[i] == '-')) i++;
            int expDigits = 0;
            while (i < body.Length && body[i] >= '0' && body[i] <= '9') { i++; expDigits++; }
            if (expDigits == 0) return false;
        }
        if (i != body.Length) return false;
        double d = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        number = new JqNumber(d, JqValue.CanonicalLiteral(t));
        return true;
    }

    private string? FoundString()
    {
        string raw = _tok.ToString();
        _tok.Clear();
        var sb = new StringBuilder(raw.Length);
        for (int k = 0; k < raw.Length; k++)
        {
            char c = raw[k];
            if (c < 0x20) return "Invalid string: control characters from U+0000 through U+001F must be escaped";
            if (c != '\\') { sb.Append(c); continue; }
            if (k + 1 >= raw.Length) return "Invalid escape";
            char e = raw[++k];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    {
                        if (!TryHex4(raw, k + 1, out int cp)) return "Invalid \\uXXXX escape";
                        k += 4;
                        if (cp >= 0xD800 && cp <= 0xDBFF)
                        {
                            if (k + 6 >= raw.Length || raw[k + 1] != '\\' || raw[k + 2] != 'u') return "Invalid \\uXXXX\\uXXXX surrogate pair escape";
                            if (!TryHex4(raw, k + 3, out int lo) || lo < 0xDC00 || lo > 0xDFFF) return "Invalid \\uXXXX\\uXXXX surrogate pair escape";
                            sb.Append((char)cp).Append((char)lo);
                            k += 6;
                            break;
                        }
                        if (cp >= 0xDC00 && cp <= 0xDFFF) { sb.Append('�'); break; }
                        sb.Append((char)cp);
                        break;
                    }
                default: return "Invalid escape";
            }
        }
        return Value(sb.ToString());
    }

    private static bool TryHex4(string s, int at, out int value)
    {
        value = 0;
        if (at + 4 > s.Length) return false;
        return int.TryParse(s.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private string? Token(char ch)
    {
        switch (ch)
        {
            case '[':
                if (_hasNext) return "Expected separator between values";
                _stack.Add(new List<object?>());
                return null;
            case '{':
                if (_hasNext) return "Expected separator between values";
                _stack.Add(new OrderedDictionary());
                return null;
            case ':':
                if (!_hasNext) return "Expected string key before ':'";
                if (_stack.Count == 0 || _stack[^1] is not OrderedDictionary) return "':' not as part of an object";
                if (_next is not string key) return "Object keys must be strings";
                _stack.Add(new KeyMark(key));
                _hasNext = false;
                _next = null;
                return null;
            case ',':
                if (!_hasNext) return "Expected value before ','";
                if (_stack.Count == 0) return "',' not as part of an object or array";
                if (_stack[^1] is List<object?> list) { list.Add(_next); }
                else if (_stack[^1] is KeyMark mark && _stack.Count >= 2 && _stack[^2] is OrderedDictionary obj)
                {
                    _stack.RemoveAt(_stack.Count - 1);
                    obj[mark.Key] = _next;
                }
                else return "Objects must consist of key:value pairs";
                _hasNext = false;
                _next = null;
                return null;
            case ']':
                {
                    if (_stack.Count == 0 || _stack[^1] is not List<object?> arr) return "Unmatched ']'";
                    if (_hasNext) arr.Add(_next);
                    else if (arr.Count != 0) return "Expected another array element";
                    _stack.RemoveAt(_stack.Count - 1);
                    _next = arr.ToArray();
                    _hasNext = true;
                    return null;
                }
            case '}':
                {
                    if (_stack.Count == 0) return "Unmatched '}'";
                    if (_hasNext)
                    {
                        if (_stack[^1] is not KeyMark mark || _stack.Count < 2 || _stack[^2] is not OrderedDictionary target) return "Objects must consist of key:value pairs";
                        _stack.RemoveAt(_stack.Count - 1);
                        target[mark.Key] = _next;
                    }
                    else
                    {
                        if (_stack[^1] is not OrderedDictionary empty) return "Unmatched '}'";
                        if (empty.Count != 0) return "Expected another key-value pair";
                    }
                    var done = (OrderedDictionary)_stack[^1];
                    _stack.RemoveAt(_stack.Count - 1);
                    _next = done;
                    _hasNext = true;
                    return null;
                }
        }
        return null;
    }

    // ───────────── convenience ─────────────

    /// <summary>Parses text holding exactly one JSON value (<c>fromjson</c>, <c>tonumber</c>, <c>--argjson</c>); the message carries jq's
    /// <c>(while parsing '...')</c> suffix.</summary>
    public static object? ParseSingle(string text)
    {
        try
        {
            var reader = new JqJsonReader(text);
            if (!reader.TryRead(out var value)) throw new JqJsonException("Expected JSON value");
            if (reader.TryRead(out _)) throw new JqJsonException("Unexpected extra JSON values");
            return value;
        }
        catch (JqJsonException ex)
        {
            throw new JqJsonException($"{ex.Message} (while parsing '{text}')");
        }
    }

    public static IEnumerable<object?> ParseAll(string text)
    {
        var reader = new JqJsonReader(text);
        while (reader.TryRead(out var value)) yield return value;
    }
}
