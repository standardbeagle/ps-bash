using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// rg's file types: the built-in ripgrep 14.1 table (<see cref="BuiltinTable"/>, all 203 types), <c>--type-add</c>
/// (<c>name:glob</c> and <c>name:include:a,b</c>), <c>--type-clear</c>, <c>-t</c> / <c>-T</c> selection and
/// <c>--type-list</c>. Globs match the file's BASE name, case-sensitively, with ripgrep's glob dialect
/// (<c>*</c>, <c>?</c>, <c>[abc]</c>, <c>[!a]</c>).
/// </summary>
internal static partial class RgTypes
{
    internal sealed class TypeSet
    {
        private readonly SortedDictionary<string, List<string>> _defs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
        private readonly HashSet<string> _negated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Regex> _globs = new(StringComparer.Ordinal);
        private bool _selectAll, _negateAll;
        private List<Regex>? _selRx, _negRx;

        internal TypeSet()
        {
            foreach (var line in BuiltinTable.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                _defs[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            }
        }

        /// <summary>True when any <c>-t</c> / <c>-T</c> was given (so walked files are filtered).</summary>
        internal bool Active => _selected.Count > 0 || _negated.Count > 0 || _selectAll || _negateAll;

        internal bool HasType(string name) => name == "all" || _defs.ContainsKey(name);

        /// <summary><c>--type-add</c> SPEC; null on success, else the error text after "error parsing flag --type-add: ".</summary>
        internal string? Add(string spec)
        {
            var parts = spec.Split(':', 3);
            if (parts.Length < 2 || parts[0].Length == 0)
                return $"invalid definition (format is type:glob, e.g., html:*.html): '{spec}'";
            string name = parts[0];
            if (parts[1] == "include")
            {
                if (parts.Length < 3) return $"invalid definition (format is type:glob, e.g., html:*.html): '{spec}'";
                foreach (var inc in parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_defs.TryGetValue(inc, out var globs)) return $"unrecognized file type: {inc}";
                    AddGlobs(name, globs.ToArray());
                }
                return null;
            }
            string glob = parts.Length == 3 ? parts[1] + ":" + parts[2] : parts[1];
            AddGlobs(name, new[] { glob });
            return null;
        }

        private void AddGlobs(string name, string[] globs)
        {
            if (!_defs.TryGetValue(name, out var list)) _defs[name] = list = new List<string>();
            foreach (var g in globs) if (!list.Contains(g)) list.Add(g);
            list.Sort(StringComparer.Ordinal);
        }

        internal void Clear(string name) { _defs.Remove(name); }

        internal bool Select(string name) { if (name == "all") { _selectAll = true; return true; } if (!_defs.ContainsKey(name)) return false; _selected.Add(name); _selRx = null; return true; }

        internal bool Negate(string name) { if (name == "all") { _negateAll = true; return true; } if (!_defs.ContainsKey(name)) return false; _negated.Add(name); _negRx = null; return true; }

        /// <summary><c>--type-list</c> lines: <c>name: glob, glob</c>, sorted by name.</summary>
        internal IEnumerable<string> ListLines()
        {
            foreach (var (name, globs) in _defs)
                yield return name + ": " + string.Join(", ", globs);
        }

        private List<Regex> Compile(HashSet<string> names, bool all)
        {
            var rx = new List<Regex>();
            foreach (var (name, globs) in _defs)
            {
                if (!all && !names.Contains(name)) continue;
                foreach (var g in globs) rx.Add(GlobRegex(g));
            }
            return rx;
        }

        private Regex GlobRegex(string glob)
        {
            if (_globs.TryGetValue(glob, out var rx)) return rx;
            var sb = new StringBuilder("^");
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else if (c == '[')
                {
                    int close = glob.IndexOf(']', i + 2 > glob.Length ? glob.Length - 1 : i + 2);
                    if (close < 0) { sb.Append("\\["); continue; }
                    string body = glob.Substring(i + 1, close - i - 1);
                    if (body.StartsWith('!')) body = "^" + body.Substring(1);
                    sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                    i = close;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            rx = new Regex(sb.ToString(), RegexOptions.CultureInvariant);
            _globs[glob] = rx;
            return rx;
        }

        /// <summary>Would a walked file with this base name be searched under the active -t / -T selection?</summary>
        internal bool Accepts(string baseName)
        {
            if (!Active) return true;
            if (_negated.Count > 0 || _negateAll)
            {
                _negRx ??= Compile(_negated, _negateAll);
                foreach (var rx in _negRx) if (rx.IsMatch(baseName)) return false;
            }
            if (_selected.Count > 0 || _selectAll)
            {
                _selRx ??= Compile(_selected, _selectAll);
                foreach (var rx in _selRx) if (rx.IsMatch(baseName)) return true;
                return false;
            }
            return true;
        }
    }
}
