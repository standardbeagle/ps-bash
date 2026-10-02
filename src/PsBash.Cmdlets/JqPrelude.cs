namespace PsBash.Cmdlets;

/// <summary>
/// jq's library functions that are defined in jq itself (jq 1.7 <c>builtin.jq</c>, behaviour checked against the real 1.7 binary):
/// parsed once on first use, looked up by <c>name/arity</c> after the user's own definitions and the native builtins.
/// </summary>
internal static class JqPrelude
{
    private const string Source = @"
def values: select(. != null);
def nulls: select(. == null);
def booleans: select(type == ""boolean"");
def numbers: select(type == ""number"");
def strings: select(type == ""string"");
def arrays: select(type == ""array"");
def objects: select(type == ""object"");
def iterables: select(type | . == ""array"" or . == ""object"");
def scalars: select(type | . != ""array"" and . != ""object"");
def finites: select(isinfinite or isnan | not);
def normals: select(isnormal);
def select(f): if f then . else empty end;
def map(f): [.[] | f];
def map_values(f): .[] |= f;
def recurse(f): def r: ., (f | r); r;
def recurse(f; cond): def r: ., (f | select(cond) | r); r;
def recurse: recurse(.[]?);
def from_entries: reduce .[] as $x ({}; . + { ($x | .key as $k | ($k // .Key // .name // .Name)): ($x | if has(""value"") then .value else .Value end) });
def reverse: [.[length - 1 - range(0; length)]];
def with_entries(f): to_entries | map(f) | from_entries;
def paths: path(..) | select(length > 0);
def paths(node_filter): . as $dot | paths | select(. as $p | $dot | getpath($p) | node_filter);
def pick(pathexps): . as $top | reduce path(pathexps) as $p (null; setpath($p; $top | getpath($p)));
def abs: if . < 0 then - . else . end;
def any: reduce .[] as $x (false; . or $x);
def all: reduce .[] as $x (true; . and $x);
def any(f): any(.[]; f);
def all(f): all(.[]; f);
def any(generator; condition): isempty(first(generator | condition or empty)) | not;
def all(generator; condition): isempty(first(generator | condition and empty));
def _flatten($x): reduce .[] as $i ([]; if $i | type == ""array"" and $x != 0 then . + ($i | _flatten($x - 1)) else . + [$i] end);
def flatten($x): if $x < 0 then error(""flatten depth must not be negative"") else _flatten($x) end;
def flatten: _flatten(-1);
def range($x): range(0; $x);
def range($from; $upto; $by): if $by > 0 then $from | while(. < $upto; . + $by) elif $by < 0 then $from | while(. > $upto; . + $by) else empty end;
def first: .[0];
def last: .[-1];
def nth($n): .[$n];
def until(cond; update): def _until: if cond then . else (update | _until) end; _until;
def while(cond; update): def _while: if cond then ., (update | _while) else empty end; _while;
def in(xs): . as $x | xs | has($x);
def inside(xs): . as $x | xs | contains($x);
def combinations: if length == 0 then [] else .[0][] as $x | (.[1:] | combinations) as $w | [$x] + $w end;
def combinations(n): . as $dot | [range(n)] | map($dot) | combinations;
def walk(f): def w: if type == ""object"" then map_values(w) elif type == ""array"" then map(w) else . end | f; w;
def transpose: if . == [] then [] else . as $in | (map(length) | max) as $max | [range(0; $max) as $j | [range(0; $in | length) as $i | $in[$i][$j]]] end;
def env: $ENV;
def tostream: path(def r: (.[]? | r), .; r) as $p | getpath($p) | reduce path(.[]?) as $q ([$p, .]; [$p + $q]);
def fromstream(f): { x: null, e: false } as $init | foreach f as $i ($init; if .e then $init else . end | if $i | length == 2 then setpath([""e""]; $i[0] | length == 0) | setpath([""x""] + $i[0]; $i[1]) else setpath([""e""]; $i[0] | length == 1) end; if .e then .x else empty end);
def truncate_stream(stream): . as $n | null | stream | . as $input | if (.[0] | length) > $n then setpath([0]; .[0][$n:]) else empty end;
def index($i): indices($i) | .[0];
def rindex($i): indices($i) | .[-1:][0];
def indices($i): if type == ""array"" and ($i | type) == ""array"" then .[$i] elif type == ""array"" then .[[$i]] elif type == ""string"" and ($i | type) == ""string"" then _strindices($i) else .[[$i]] end;
def IN(s): any(s == .; .);
def IN(src; s): any(src == s; .);
def INDEX(stream; idx_expr): reduce stream as $row ({}; .[$row | idx_expr | tostring] |= $row);
def INDEX(idx_expr): INDEX(.[]; idx_expr);
def debug(msg): (msg | debug | empty), .;
";

    private static Dictionary<string, JEnv>? _table;
    private static readonly object Gate = new();

    private static Dictionary<string, JEnv> Table
    {
        get
        {
            if (_table != null) return _table;
            lock (Gate)
            {
                if (_table != null) return _table;
                var table = new Dictionary<string, JEnv>(StringComparer.Ordinal);
                foreach (var def in JqParser.ParseDefinitions(Source))
                {
                    var entry = new JEnv { Kind = JEnv.EntryKind.Func, Name = def.Name, Arity = def.Params.Length, Def = def };
                    entry.DefEnv = entry;
                    table[def.Name + "/" + def.Params.Length] = entry;
                }
                _table = table;
                return table;
            }
        }
    }

    public static JEnv? Lookup(string name, int arity) => Table.TryGetValue(name + "/" + arity, out var e) ? e : null;

    public static IEnumerable<string> Names => Table.Keys;
}
