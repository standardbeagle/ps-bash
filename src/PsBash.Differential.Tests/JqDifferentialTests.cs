using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// jq 1.7 against ps-bash: stdout, stderr and exit status of the same command line in real jq and in the jq cmdlet (the parser /
/// evaluator / number model / option handling). Each row is <c>(input, args)</c>: a null input runs <c>jq -n ARGS</c>, otherwise the
/// input text is piped to <c>jq ARGS</c>. Rows pin the language (paths, assignment, reduce/foreach, destructuring, closures, string
/// interpolation, formats, regex), the builtins the task lists, number printing (a literal keeps its text until arithmetic touches it),
/// error wording and exit statuses (3 compile, 5 runtime, 4 / 1 with -e), and the command line.
/// </summary>
public class JqDifferentialTests
{
    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    [SkippableTheory]
    [MemberData(nameof(Rows))]
    public Task SameAsJq(string? input, string args) =>
        AssertOracle.EqualAsync(
            input is null ? $"jq -n {args}" : $"printf '%s' {Quote(input)} | jq {args}",
            timeout: TimeSpan.FromSeconds(20));

    private static object?[] R(string? input, string args) => new object?[] { input, args };

    public static IEnumerable<object?[]> Rows()
    {
        // ───────────── paths, indexing, slicing ─────────────
        yield return R("{\"a\":{\"b\":[1,2,{\"c\":3}]}}", "-c '.a.b[2].c, .a.b[-1], .a[\"b\"][0], .a.b[1:], .a.b[:1], .a.b[5], .x.y.z'");
        yield return R("[0,1,2,3,4,5]", "-c '.[2:4], .[-2:], .[:2], .[4:2], .[1:-1], .[10:]'");
        yield return R("\"abcdef\"", "-c '.[2:4], .[-2:], .[:1]'");
        yield return R("\"é😀x\"", "-c '.[1:2], .[2:], length, utf8bytelength'");
        yield return R("{\"a\":1}", "-c '.a?, .b?, (.a.x)?, [.[]?], [..]'");
        yield return R("[1,[2,[3]]]", "-c '[..], [.. | numbers], [recurse(.[]?; type == \"array\")]'");
        yield return R("null", "-c '.a, .[0], .[1:2], [.[]?]'");
        yield return R("{\"a\":[1,2]}", "-c '.a as [$x,$y] | {x:$x, y:$y}'");
        yield return R("{\"a b\":1,\"c\":2}", "-c '.\"a b\", .[\"c\"], .\"c\"'");
        yield return R("[[1,2],[3,4]]", "-c '.[][0], .[1][1], [.[][]]'");
        yield return R("{\"a\":1}", "-c 'try (.a.b) catch ., try (.[0]) catch ., try (.a[0]) catch .'");
        yield return R("[1,2]", "-c 'try (.a) catch ., try (.[\"a\"]) catch ., try (.[1.7]) catch ., try (.[null]) catch .'");
        yield return R("[1,2,3]", "-c '.[1:] | .[0], first, last, nth(1)'");
        yield return R("{\"a\":[{\"b\":5}]}", "-c 'getpath([\"a\",0,\"b\"]), getpath([\"x\",\"y\"]), [paths], [paths(type==\"number\")]'");
        yield return R("{\"a\":1}", "-c 'try getpath([\"a\",\"b\"]) catch .'");

        // ───────────── arithmetic, comparison, order of evaluation ─────────────
        yield return R(null, "-c '[1+2, 5-7, 3*4, 7/2, 7%3, -5%3, 5%-3, 5.9%3.1, -(1), 1e3+1]'");
        yield return R(null, "-c '[\"a\"+\"b\", null+1, 1+null, [1]+[2], {a:1}+{b:2}, {a:1}+{a:2}, [1,2,1]-[1], \"ab\"*3, \"ab\"*0, \"ab\"*0.5, 2*\"x\", \"a,b\"/\",\"]'");
        yield return R(null, "-c '{\"a\":{\"b\":1}} * {\"a\":{\"c\":2}}, ({} * {a:1})'");
        yield return R(null, "-c '[(1,2)+(10,20)]'");
        yield return R(null, "-c '[1,2] | [.[] as $x | $x * 2]'");
        yield return R(null, "-c '[1 < 2, 1 == 1.0, \"a\" < \"b\", [1] < [1,2], {} < [], null < false, false < true, true < 0, 0 < \"a\", \"a\" < [], ({a:1} < {a:2}), ({a:2} < {b:1})]'");
        yield return R(null, "-c '[1,2,3] | [.[] | select(. > 1 and . < 3), select(. == 1 or . == 3)]'");
        yield return R(null, "-c '[true and false, true or false, (null | not), (false | not), (0 | not), (\"\" | not)]'");
        yield return R(null, "-c '[1 // 2, null // 2, false // 3, empty // 4, (null,false,1,2) // 9, ([] | .[0] // \"d\")]'");
        yield return R(null, "-c 'try (1 / 0) catch ., try (1 % 0) catch ., try (\"a\" - 1) catch ., try ({} * 2) catch ., try ([] / 2) catch ., try (-\"a\") catch .'");
        yield return R(null, "-c '[1,2] | [.[] | . as $x | [$x, $x * 10]]'");
        yield return R(null, "-c '[range(5)] | [.[] | if . % 2 == 0 then \"e\" elif . == 3 then \"t\" else \"o\" end]'");
        yield return R(null, "-c 'if empty then 1 else 2 end, [if (true,false) then 1 else 2 end], (if false then 1 end), (if null then 1 elif true then 2 end)'");

        // ───────────── numbers (literal text preserved until arithmetic) ─────────────
        yield return R("[1.0, 1.50, 100000000000000000000, 1e2, 1E-2, 0.1e1, -0, 3.14159265358979323846264338327950288, 0.0001, 0.00001, 123456789012345678, 1.0e+2, 5e0]", "-c '.'");
        yield return R("[1.0, 1.50, 100000000000000000000, 1e2, 1E-2, 0.1e1, -0, 3.14159265358979323846264338327950288, 0.0001, 0.00001, 123456789012345678]", "-c 'map(.+0)'");
        yield return R("[1.0, 1.50, 100000000000000000000, 1e2, 0.10]", "-c 'map(tostring), map(tojson), sort, unique, (.[0:2]|add)'");
        yield return R(null, "-c '[1.0, 1e1000, 1e-5, 0.0001, 123456789012345678, 1e17, 12345678901234567890, 0.1+0.2, 1/3, 3.0, -0, 100000, 1e5, 1.5e300, 5e-324]'");
        yield return R(null, "-c '[infinite, -infinite, nan] | ., map(isinfinite), map(isnan), map(isnormal), tojson, (.[2] < 1), (.[2] == .[2])'");
        yield return R(null, "-c '[9007199254740993, 1e15, 1e16, 12345678901234567, 0.1e-6, 1e-7, 1.5e-10, 255] | map(. * 1)'");
        yield return R("9007199254740993", "-c '., .+0, [.], tojson'");
        yield return R("[1,2.5,-3]", "-c 'map(floor), map(ceil), map(round), map(fabs), map(sqrt|floor), map(abs)'");
        yield return R("[1.5,-1.5,2.5]", "-c 'map(trunc), map(rint), map(round)'");
        yield return R(null, "-c '[pow(2;10), log2(8), exp10(2), (16|sqrt), (2|exp2), (100|log10), (8|cbrt), (1|atan*4|.*1000|floor), fmin(1;2), fmax(1;2)]'");
        yield return R("[0.1, 0.30000000000000004, 1e-7, 1.5e300, 123456789.123456789, -1.5e-10, 3e0]", "-c 'tojson, (.|map(.*1)|tojson)'");
        yield return R("[1.10]", "-c '[.[0], (.[0]+0), (.[0]*1), (.[0]|floor), (.[0]|tostring), (.[0]==1.1), (.|add), (.[0]|-.)]'");
        yield return R("[1,2,3]", "-c 'add, (map(.*2)|add), (map(tostring)|join(\",\")), length'");
        yield return R(null, "-c '[\"1\",\"1.5\",\" 2\",\"-0\",\"1e2\",\"0x1\",\"abc\",\"\",\"[1]\",\"nan\"] | map(try tonumber catch .)'");
        yield return R(null, "-c '[1,\"a\",[1],{\"a\":1},null,true] | map(tostring), map(tojson)'");
        yield return R(null, "-c '[\"[1,2]\", \"{\\\"a\\\":1}\", \"1 2\", \"\", \"{a\", \"nan\", \"\\\"x\\\"\"] | map(try fromjson catch .)'");
        yield return R(null, "-c '[limit(3; range(10))], [range(0,1;3,4)], [range(5;0;-2)], [range(0;1;0.3)], [range(3;0)], [range(2.5)]'");

        // ───────────── constructors, interpolation, formats ─────────────
        yield return R("{\"x\":1,\"y\":\"s\"}", "-c '{a: .x, b: .y, \"c\": 3, (.y): 4, \"d\\(.x)\": 5, @base64 \"k\": 6}'");
        yield return R("{\"a\":1,\"b\":2}", "-c '{a, b}, {\"a\"}, ({a:(1,2)}), {a:(1,2),b:(3,4)}, {(\"x\",\"y\"):(1,2)}'");
        yield return R("{\"a\":1}", "-c '1 as $x | {$x, a}, ({a:1} | {a: .a} + {b: 2})'");
        yield return R("{\"a\":\"x\"}", "-c '\"v=\\(.a) n=\\(1+2) j=\\([1,\"a\"]) \\(1,2)-\\(3,4)\"'");
        yield return R(null, "-r '\"\\(\"a\") \\(null) \\(true) \\({a:1}) \\(1.50)\"'");
        yield return R(null, "-r '[1,\"a b\",null,true,\"x\\\"y\"] | @csv, @tsv, @sh, @json, @html, @uri, @text, @base64'");
        yield return R(null, "-r '[\"a\\tb\",\"c\\\\d\",\"e\\nf\"] | @tsv, @csv'");
        yield return R(null, "-r '\"a-b_c.d~e/f ?&=é\" | @uri, @html, @base64, (@base64|@base64d)'");
        yield return R(null, "-r '\"<a href=\\\"x\\\">&\\u0027</a>\" | @html'");
        yield return R(null, "-r '@sh \"echo \\(\"a b\" , \"it'\\''s\")\", @base64 \"x=\\(\"hi\")\", @json \"v=\\([1,\"a\"])\", @uri \"q=\\(\"a b\")\"'");
        yield return R(null, "-r '\"YWJj\" | @base64d, (\"YWJ\" | @base64d)'");
        yield return R(null, "-c 'try ({} | @csv) catch ., try ([[1]] | @csv) catch ., try ({} | @sh) catch ., try (1 | @tsv) catch ., try (\"!!\" | @base64d) catch .'");
        yield return R(null, "-c '[1,2] | @json, @text, (.|tostring)'");

        // ───────────── builtins: keys, entries, collections ─────────────
        yield return R("{\"b\":1,\"a\":2}", "-c 'keys, keys_unsorted, [.[]], to_entries, (to_entries|from_entries), with_entries(.value += 1), map_values(.+1), (map(.+1)), length, tojson, has(\"a\"), has(\"z\"), (add), ([.[]]|add)'");
        yield return R("[5,6]", "-c 'keys, has(0), has(2), to_entries, length, (with_entries(.))?'");
        yield return R(null, "-c '[{\"key\":\"a\",\"value\":1},{\"key\":\"a\",\"value\":2}] | from_entries'");
        yield return R(null, "-c '[[{\"key\":\"a\"}], [{\"name\":\"n\",\"value\":2}], [{\"Name\":\"d\",\"Value\":3}], [{\"key\":\"a\",\"value\":false}], [{\"key\":\"a\",\"value\":null,\"Value\":5}], [{\"key\":null,\"name\":\"n\"}], [{\"key\":false,\"name\":\"n\"}], [{\"key\":\"a\",\"Value\":4}]] | map(from_entries)'");
        yield return R(null, "-c '[[{\"key\":1,\"value\":2}], [{\"k\":\"b\",\"v\":2}], [{\"key\":true}], [null], [1]] | map(try from_entries catch .)'");
        yield return R("{\"a\":{\"b\":1}}", "-c '[to_entries[] | .key], with_entries(select(.key == \"a\")), with_entries(.key |= ascii_upcase)'");
        yield return R("[3,1,2,1]", "-c 'sort, unique, reverse, min, max, add, length, sort_by(-.), group_by(. % 2), unique_by(. % 2), min_by(-.), max_by(-.), (map(tostring)|sort), index(1), rindex(1), indices(1), (.[1:]|first), any, all, (map(. > 0)|all), any(. > 2), all(. > 2), contains([1]), inside([1,2,3,4])'");
        yield return R("[{\"a\":2,\"b\":1},{\"a\":1,\"b\":2},{\"a\":2,\"b\":3}]", "-c 'sort_by(.a), sort_by(.a, .b), sort_by(-.b), group_by(.a), unique_by(.a), min_by(.a), max_by(.a), (map(.a)|unique), (sort_by(.a)|map(.b))'");
        yield return R("[]", "-c 'min, max, add, first, last, (.[0]), sort, unique, reverse, group_by(.), min_by(.a), any, all, length'");
        yield return R(null, "-c '[1,[2,[3,[4]]]] | flatten, flatten(1), flatten(0), (try flatten(-1) catch .)'");
        yield return R(null, "-c '[[1,2],[3]] | transpose, add, map(add), ([.[][]]|length)'");
        yield return R(null, "-c '[[1,2],[3,4]] | [combinations], ([[1,2]]|[combinations(2)])'");
        yield return R(null, "-c '[1,2,null,\"a\",[1],{\"a\":1}] | map(type), [.[] | numbers], [.[] | strings], [.[] | nulls], [.[] | arrays], [.[] | objects], [.[] | iterables], [.[] | scalars], [.[] | booleans], [.[]|values]'");
        yield return R(null, "-c '[1,[2],{\"a\":3}] | contains([1]), (\"foobar\" | contains(\"bar\")), ({\"a\":[1,2],\"b\":3} | contains({a:[1]})), (try (1|contains(\"a\")) catch .), (\"bar\"|inside(\"foobar\")), (\"a\"|in({\"a\":1})), (0|in([1]))'");
        yield return R(null, "-c '[limit(3; repeat(1))], [limit(0; 1,2)], [limit(-1; 1,2)], [first(range(10))], [first(empty)], [last(range(10))], [last(empty)], [nth(2; range(10))], [nth(5; 1,2)], (try nth(-1; 1,2) catch .), [until(. > 100; . * 2)?], (1 | until(. > 100; . * 2)), [1 | while(. < 20; . * 2)], [isempty(empty), isempty(1, error(\"x\"))]'");
        yield return R(null, "-c '[range(3)] | first, last, nth(1), (first(.[])), [.[] | select(. > 0)], [.[] | select(. > 0)] == .[1:]'");
        yield return R(null, "-c '[any(1,2; . == 2), all(1,2; . == 2), any(empty; .), all(empty; .), ([true,false]|any,all), ([]|any,all), ([1,2]|any(. > 1), all(. > 1))]'");
        yield return R(null, "-c '{\"a\":[1,{\"b\":2}]} | [tostream], fromstream(tostream), [leaf_paths?], ([1|truncate_stream([[0],1],[[1,0],2],[[1,0]],[[1]])])'");
        yield return R(null, "-c '[[1,[2]]|walk(if type == \"number\" then . + 1 else . end)], [{\"a\":\"b\",\"c\":[{\"a\":1}]} | walk(if type == \"object\" then del(.a) else . end)], [[[3,1],[2]] | walk(if type == \"array\" then sort else . end)]'");
        yield return R(null, "-c '[1,2,3] | IN(2), IN([1,2,3],[3]), ([{\"id\":1},{\"id\":2}] | INDEX(.id)), ({\"a\":{\"b\":1}} | pick(.a.b)), ([1,2,3]|pick(.[1]))'");
        yield return R(null, "-c '[1,null,2] | map(values), map(. // \"x\"), (.[1] | values), [.[] | numbers]'");
        yield return R(null, "-c 'env | type, ($ENV | type), ($ENV | has(\"PATH\") or has(\"Path\"))'");
        yield return R(null, "-c 'input_filename, input_line_number, ([.[]?]), ($__loc__), (null|ltrimstr(\"a\"))'");
        yield return R(null, "-c '{} | .a.b.c = 1, (.a[2] = 1), (.[\"x\"] = 1), (.a += 1), (.a -= 1), (.a *= 2), (.a //= 7), (try (.a /= 0) catch .)'");

        // ───────────── paths and assignment ─────────────
        yield return R("{\"a\":[1,2,3],\"b\":{\"c\":4}}", "-c '[paths], [paths(type == \"number\")], [path(..)], [path(.a[])], [path(.a[1:])], path(.b.c), (path(.a | first)), [paths(..)]'");
        yield return R("{\"a\":[1,2,3]}", "-c '.a[1] = 9, (.a[] |= . + 1), (.a |= map(. * 2)), (.a[1:] = [7]), (.a[-1] = 0), (del(.a[0])), (del(.a[0,2])), (del(.a[1:])), (delpaths([[\"a\",0],[\"a\",1]])), (.a | del(.[] | select(. == 2)))'");
        yield return R("{\"a\":{\"b\":1}}", "-c 'setpath([\"a\",\"c\"]; 2), setpath([\"x\",\"y\",\"z\"]; 1), (setpath([]; 5)), getpath([\"a\",\"b\"]), (try setpath([0]; 1) catch .), (del(.a.b)), (del(.x)), (del(.a, .a)), del(.)'");
        yield return R(null, "-c '[1,2,3,4] | (.[] | select(. > 2)) |= empty, (.[1] |= empty), map_values(empty), (map_values(select(. > 2))), ((.[] | select(. > 2)) |= . * 10), (.[2:] |= map(. + 1))'");
        yield return R(null, "-c '{\"a\":1,\"b\":2} | (.a, .b) = 5, ((.a, .b) |= . + 1), (.[] += 1), (.c = .a + .b), (.a = (1,2)), (.a |= (.,.)), (.c //= 3), (.a //= 3)'");
        yield return R(null, "-c '[1,[2]] | (.. | numbers) |= . + 1, ([..] | length), (first(.[] | select(. == 1)) |= 10), (.[1][0] = 9), (.[5] = 1 | length), (try (.[-5] = 1) catch .)'");
        yield return R(null, "-c 'null | .a.b = 1, (.[2] = 1), (.a[1].b = 2), (.[\"k\"] += 1), ([.[]?] | length)'");
        yield return R(null, "-c '{\"a\":[{\"b\":1},{\"b\":2}]} | [.a[].b], (.a[].b |= . * 10), (.a |= map(select(.b > 1))), (.a | map(.b) | add), ([.a[] | select(.b == 2)] | length), (del(.a[] | select(.b == 1)))'");
        yield return R(null, "-c 'try (path(1)) catch ., try ([1] | path(.[0] + 1)) catch ., ({\"a\":1} | path(.a)), ([[1]] | path(.[0][0])), ({} | [paths])'");
        yield return R(null, "-c '{\"a\":1} | to_entries, (to_entries | map(.key)), ([.[]] | length), (. as $o | keys | map($o[.]))'");
        yield return R(null, "-c '[[0,1],[1,2]] | (.[0][1], .[1][0]) |= . * 10, (.[][] |= . + 1), (map(.[0]) | add), (map(.[1]) | max)'");

        // ───────────── reduce / foreach / variables / functions / control ─────────────
        yield return R(null, "-c 'reduce range(5) as $i (0; . + $i), [foreach range(5) as $i (0; . + $i)], [foreach range(5) as $i (0; . + $i; [$i, .])], (reduce empty as $x (7; . + 1)), (reduce range(3) as $i (0; empty)), [foreach range(3) as $i (0; empty)], [reduce range(3) as $i ((0,10); . + $i)], [foreach (1,2) as $x ((0,10); . + $x; [$x, .])]'");
        yield return R(null, "-c 'def f(x): x * 2; def g: f(3); def fac: if . <= 1 then 1 else . * (. - 1 | fac) end; [g, f(1,2), (10 | fac)]'");
        yield return R(null, "-c 'def f($a; $b): $a + $b; def h(g): [g, g]; def k($x): $x + x; [f(1;2), h(1,2), k(2), (1 | def m: . + 1; m | m)]'");
        yield return R(null, "-c 'def f: 1; def f: 2; def f(a): a; def f(a;b): a + b; [f, f(10), f(1;2)], (def f: def g: 3; g * 2; f), (def f(g): 10 | g; 1 | f(. + 1))'");
        yield return R(null, "-c '[1,2] as [$a,$b] | {$a, $b}, ({\"x\":1,\"y\":[2,3]} as {x:$x, y:[$y,$z]} | [$x,$y,$z]), ([[1,2],[3,4]] | .[] as [$a,$b] | $a + $b), ({\"a\":{\"b\":5}} as {a:{b:$v}} | $v), ({\"k\":\"a\",\"a\":1} as {k:$k} | $k)'");
        yield return R(null, "-c '{\"a\":1,\"b\":[2]} as {$a, b:[$c]} | [$a, $c], ([[1,2],{\"a\":3}][] as [$a] ?// {a:$a} | $a), ([[1]] | .[] as [$a] ?// $a | [$a]), ({\"a\":1} as {(\"a\"):$x} | $x)'");
        yield return R(null, "-c '[label $f | range(10) | ., (select(. == 3) | break $f)], [label $a | label $b | 1, break $a, 2], [range(3) | label $l | if . == 1 then break $l else . end]'");
        yield return R(null, "-c '[.[]?], [(1,null) | .a?], (try error(\"x\") catch .), ([1,\"a\"] | map(try tonumber catch \"bad\")), [(1,2) | (if . == 1 then error(\"x\") else . end)?], (try error catch .), (try error(null) catch .), ({} | try error catch .), (try error({\"a\":1}) catch .a), [.[] | try error(\"x\")]'");
        yield return R(null, "-c 'try (error(\"a\") | 1) catch (. + \"!\"), (try (1, error(\"x\"), 3) catch \"c\"), [(1,2,3) | try (if . == 2 then error(\"x\") else . end) catch \"E\"], (try (try error(\"in\") catch error(\"out\")) catch .)'");
        yield return R("[1,2,3]", "-c '[.[] | tostring] | join(\"-\"), (map(tostring) | add), ([.[]|.*2] | @csv)'");
        yield return R(null, "-c '[1,2,3] | [limit(2; .[])], [first(.[] | select(. > 1))], [.[] | select(. > 1)] | length'");
        yield return R(null, "-c '\"x\" | [limit(3; repeat(. + \"b\"))], ([1,2] | [limit(3; repeat(.[0] += 1))]), (2 | [limit(4; repeat(. * .))]), (1 | [limit(5; recurse(. + 1))]), ([2 | recurse(if . < 20 then . * . else empty end)]), ([1,[2]] | [recurse], [recurse(.[]?; . != 2)]), ([[1,2],[3]] | [recurse(.[]?)] | length)'");
        yield return R("[1,[2]]", "-c '[recurse | numbers], [..] == [recurse], ([recurse_down] | length)'");
        yield return R(null, "-c '$__loc__, ($ENV | type), ([splits(\"a\")?]), ([\"a\",\"b\"] | .[] as $x | $x), (1 as $x | 2 as $y | [$x,$y,$x+$y]), (. as $d | $d)'");
        yield return R(null, "-c '[.[]?] | length, (empty | 1), ([empty]), [empty, 1, empty], ([range(0)] | length)'");
        yield return R(null, "-c 'def f: reduce .[] as $x (0; . + $x); [[1,2],[3]] | map(f)'");

        // ───────────── strings and regular expressions ─────────────
        yield return R(null, "-c '[\"abc\" | ltrimstr(\"a\"), rtrimstr(\"c\"), ltrimstr(\"x\"), startswith(\"ab\"), endswith(\"bc\"), ascii_downcase, ascii_upcase, length, explode, (explode | implode), reverse?, ([.[]?]|length)]'");
        yield return R(null, "-c '[\"a,b,c\" | split(\",\"), split(\", *\"; null), split(\",\"; \"g\"), (split(\"\") | length), [splits(\",\")], ascii_downcase]'");
        yield return R(null, "-c '[[1,null,\"a\",true] | join(\"-\")], [[] | join(\",\")], [[\"a\"] | join(\",\")], [[1.0,2] | join(\"-\")], (try ([[1]] | join(\",\")) catch .), ([\"a\",\"b\"] | join(\", \"))'");
        yield return R(null, "-c '[\"foo bar\" | test(\"BAR\"; \"i\"), test(\"a.*b\"), test(\"o+\"), test(\"^foo\"), test(\"x\"), ([match(\"o\"; \"g\") | .offset]), (match(\"(?<n>o+)\") | .captures), ([match(\"\"; \"g\") | .offset])]'");
        yield return R(null, "-c '[\"foo bar\" | capture(\"(?<a>\\\\w+) (?<b>\\\\w+)\"), [scan(\"o\")], [scan(\"(o)(.)\")], [scan(\"\\\\w+\")], (match(\"(b)(x)?\") | .captures)]'");
        yield return R(null, "-c '[\"aXbXc\" | sub(\"X\"; \"-\"), gsub(\"X\"; \"-\"), gsub(\"x\"; \"-\"; \"i\"), gsub(\"(?<l>[a-c])\"; \"<\\(.l)>\"), gsub(\"\"; \"-\"), gsub(\"^\"; \">\"), sub(\"$\"; \"<\"), gsub(\"X\"; \"1\", \"2\"), [sub(\"b\"; \"1\", \"2\")], gsub(\"[abc]\"; empty)]'");
        yield return R(null, "-c '\"a1b2c\" | [gsub(\"(?<d>[0-9])\"; if .d == \"1\" then \"x\", \"y\", \"z\" else \"p\", \"q\" end)], [gsub(\"(?<d>[0-9])\"; if .d == \"1\" then \"x\" else \"p\", \"q\" end)], [gsub(\"(?<d>[0-9])\"; \"x\")], [sub(\"(?<d>[0-9])\"; \"x\", \"y\")]'");
        yield return R(null, "-c '[\"a b\" | test(\"a b\"; \"x\"), test(\"A\"; \"i\"), test([\"B\", \"i\"]), test(\"a.b\"; \"s\"), ([match(\"[[:alpha:]]+\"; \"g\") | .string]), ([match(\"\\\\d\"; \"g\")] | length)]'");
        yield return R(null, "-c '[\"a,b, cd,efg\" | indices(\", \"), index(\",\"), rindex(\",\"), indices(\"\"), (index(\"zz\"))], [[0,1,2,1,3] | indices(1), indices([1,2]), index(1), rindex(1)], [\"abcb\" | indices(\"b\")]'");
        yield return R(null, "-c '[\"a1b22c\" | [splits(\"[0-9]+\")], ([match(\"[0-9]+\"; \"g\") | .length]), (split(\"[0-9]+\"; null)), (split(\"b\"; \"g\"))]'");
        yield return R(null, "-c '[\"é😀\" | [match(\".\"; \"g\") | .offset], (explode), (length), (utf8bytelength), (.[0:1]), (@json), (tojson), ([.] | tojson), ascii_downcase]'");
        yield return R(null, "-c '[\"\\u00e9\\u0000\\u001f\\u007f\\ud83d\\ude00\\\"\\\\\\/\\b\\f\\n\\r\\t\"] | tojson, .[0], (.[0] | length), (.[0] | explode)'");
        yield return R(null, "-c '[\"ÀB\"|ascii_downcase], [\"àb\"|ascii_upcase], (try (1|ascii_downcase) catch .), (try (1|explode) catch .), (try ([\"a\"]|implode) catch .), ([65,66,128512]|implode)'");
        yield return R(null, "-c '[\"abc\" | try ltrimstr(1) catch ., try test(1) catch ., try (1|test(\"a\")) catch ., try (1|split(\",\")) catch ., try (\"a\"|split(1)) catch ., try test(\"(\") catch ., try startswith(1) catch ., try (1|startswith(\"a\")) catch .]'");
        yield return R(null, "-c '[\"a\\tb\"|@json, tojson, (. | length)], (\"x\" * 3), (\"abc\" | .[1:]), (\"a-b\" | split(\"-\") | join(\"+\")), (\"a\" | . + \"b\" | . * 2)'");
        yield return R(null, "-c '[\"test\" | ascii_downcase, (ltrimstr(\"te\") | rtrimstr(\"t\")), (. / \"e\"), (split(\"e\") | length), (.[1:3]), (. + \" \" + .)]'");
        yield return R(null, "-c '\"abc\" | [match(\"(?<x>b)\") | .captures[0] | keys], [match(\"b\") | keys], [match(\"(b)\") | .captures[0].name], capture(\"(?<x>b)(?<y>z)?\")'");

        // ───────────── errors and exit status ─────────────
        yield return R("{\"a\":1}", "'.a.b'");
        yield return R("{\"a\":1}", "-e '.a == 2'");
        yield return R("{\"a\":1}", "'error(\"boom\")'");
        yield return R("{\"a\":1}", "'error({\"a\":1})'");
        yield return R("{\"a\":1}", "'error(null)'");
        yield return R("{\"a\":1}", "'{a:1} | error'");
        yield return R("1", "'\"abcdefghijklmnopqrstuvwxyz\" | error'");
        yield return R("1", "'{\"aaaaaaaaaaaaaaaaaaaa\":1} | .[0]'");
        yield return R("1", "'[{\"aaaaaaaaaaaaaaaaaaaa\":1}] | .[0] + 1'");
        yield return R("1", "'[1,2,3,4,5,6,7,8,9,10,11,12] | .a'");
        yield return R("{\"a\":1}\n{\"a\":\"x\"}\n{\"a\":3}\n", "'.a + 1'");
        yield return R("1\n\n\n\"x\"\n3\n", "'. + 1'");
        yield return R("{\n\"a\":\n\"x\"\n}\n", "'.a + 1'");
        yield return R("1", "-c 'input_filename, input_line_number'");
        yield return R("1 2 3", "-c '[., input]'");
        yield return R("1 2 3", "-c '[inputs]'");
        yield return R("1 2 3", "-nc '[inputs]'");
        yield return R("1 2 3", "-nc 'input, input'");
        yield return R("1 2 3", "-nc '[., input]'");
        yield return R("1", "'.a'");
        yield return R("1", "'{'");
        yield return R("1", "'.a |'");
        yield return R("1", "'foo'");
        yield return R("1", "'foo(1)'");
        yield return R("1", "'$x'");
        yield return R("1", "'.a as $x | $y'");
        yield return R("1", "'break $x'");
        yield return R("1", "'1 +'");
        yield return R("1", "'[1,'");
        yield return R("1", "'{(1):2}'");
        yield return R("1", "'{(null):2}'");
        yield return R("1", "'.[] as [$a, {b: $c}] | $a'");
        yield return R("1", "'def f: 1;'");
        yield return R("1", "'1 | foo | bar'");
        yield return R(null, "'1, null' -e");
        yield return R(null, "-e 'null, 1'");
        yield return R(null, "-e 'false'");
        yield return R(null, "-e 'empty'");
        yield return R(null, "-e '1, 2'");
        yield return R("[1,null]", "-e '.[]'");
        yield return R("", "-e '.'");
        yield return R("", "'.'");
        yield return R("  \n", "'.'");
        yield return R("1", "-e 'error(\"x\")'");
        yield return R(null, "'\"x\\n\" | halt_error'");
        yield return R(null, "'{\"a\":1} | halt_error(2)'");
        yield return R(null, "'halt'");
        yield return R(null, "'1, (2 | halt), 3'");
        yield return R(null, "'null | halt_error'");
        yield return R(null, "'[1] | debug | length'");
        yield return R(null, "'1 | debug(\"m\") | . + 1'");
        yield return R(null, "'[1,2] | debug(\"v: \\(.)\") | length'");

        // ───────────── input parsing ─────────────
        yield return R("{\"a\":", ".");
        yield return R("1 2 x 3", "-c .");
        yield return R("[1,2\n3]\n", "-c .");
        yield return R("{\"a\":1}\n{\"b\":\n", "-c .");
        yield return R("1\n2\n}\n3", "-c .");
        yield return R("{'a':1}", "-c .");
        yield return R("tru", "-c .");
        yield return R("nul", "-c .");
        yield return R("{\"a\":1,}", "-c .");
        yield return R("[1,]", "-c .");
        yield return R("\"abc", "-c .");
        yield return R("{\"a\" 1}", "-c .");
        yield return R("[1 2]", "-c .");
        yield return R("{1:2}", "-c .");
        yield return R("{\"a\"::1}", "-c .");
        yield return R("[,1]", "-c .");
        yield return R("{,}", "-c .");
        yield return R("]", "-c .");
        yield return R("{\"a\":1]", "-c .");
        yield return R("1 [", "-c .");
        yield return R("[-]", "-c .");
        yield return R("[1e]", "-c .");
        yield return R("[1a]", "-c .");
        yield return R("# c\n1", "-c .");
        yield return R("\"a\tb\"", "-c .");
        yield return R("\"\\q\"", "-c .");
        yield return R("\"\\u12\"", "-c .");
        yield return R("\"\\u00e9\\ud83d\\ude00\\ud800\"", "-c .");
        yield return R("\"\\udc00\"", "-c .");
        yield return R("\"\\u00e9\\ud83d\\ude00\"", "-c .");
        yield return R("[0, -0, 0.0, 1., .5, 01, +1]", "-c .");
        yield return R("[NaN,Infinity,-Infinity,nan]", "-c '., map(.+0)'");
        yield return R("[1E400,-1E400]", "-c '., map(.+0)'");
        yield return R("1\r\n2\r\n", "-c .");
        yield return R("\"x\"\n", "-c .");
        yield return R("1 2\n[3]\n\"x\"", "-c '[., input_line_number]'");
        yield return R("{\"a\":[1,{\"b\":2}]}", "-c --stream .");
        yield return R("[1,[2,3]] {\"a\":{}} []", "-c --stream .");
        yield return R("1 2\n3", "-c -s .");
        yield return R("1 x", "-sc .");
        yield return R("a\nb\n", "-R .");
        yield return R("a\nb", "-R .");
        yield return R("a\nb", "-Rs .");
        yield return R("a\nb\n", "-nR '[inputs]' -c");
        yield return R("a\nb\n", "-nR 'input'");
        yield return R("1 2 3", "-ns '[inputs]' -c");
        yield return R("1 2 3", "-n '[inputs | . * 2]' -c");
        yield return R("1 2 3", "-n 'reduce inputs as $x (0; . + $x)'");
        yield return R("1 2 3", "-n 'input | ., input'");
        yield return R("{\"a\":1}", "-s .");
        yield return R("", "-s -c .");
        yield return R("", "-n -s -c .");
        yield return R("", "-R .");
        yield return R("", "-Rs .");

        // ───────────── output options ─────────────
        yield return R("{\"a\":[1,{\"b\":null}],\"c\":\"é\",\"d\":{}}", ".");
        yield return R("{\"a\":[1,{\"b\":null}],\"c\":\"é\",\"d\":{}, \"e\":[]}", "--tab .");
        yield return R("{\"a\":[1,{\"b\":null}]}", "--indent 1 .");
        yield return R("{\"a\":[1,{\"b\":null}]}", "--indent 0 .");
        yield return R("{\"a\":[1,{\"b\":null}]}", "--indent 7 .");
        yield return R("{\"a\":[1]}", "--indent 8 .");
        yield return R("{\"a\":[1]}", "--indent");
        yield return R("{\"a\":[1]}", "--indent x .");
        yield return R("{\"a\":1}", "--tab -c .");
        yield return R("{\"b\":1,\"a\":{\"d\":1,\"c\":2}}", "-S .");
        yield return R("{\"b\":1,\"a\":{\"d\":1,\"c\":2}}", "-S -c '., keys_unsorted, tojson, (to_entries|map(.key))'");
        yield return R("{\"b\":1,\"a\":2}", "-c --sort-keys '., tojson'");
        yield return R("[\"a\",\"b\"]", "-r '.[]'");
        yield return R("[\"a\",\"b\"]", "-j '.[]'");
        yield return R("[\"a\",1,null]", "-j '.[]'");
        yield return R("[\"a\",\"b\"]", "--join-output '.[]'");
        yield return R("[\"a\",\"b\"]", "--raw-output '.[]'");
        yield return R("\"é😀\"", "-a .");
        yield return R("\"é😀\"", "-ar .");
        yield return R("[\"é\"]", "-a -c .");
        yield return R("\"a\\u0000b\"", "-r .");
        yield return R("[\"x\"]", "-r .");
        yield return R("{\"a\":\"x\"}", "-r '.a, .'");
        yield return R("{\"a\":1}", "-nc '$a' --arg a 1");
        yield return R("{\"a\":1}", "-c --arg a 1 --arg b 2 '[$a, $b, .a]'");
        yield return R(null, "-c --argjson a '{\"x\":[1,2]}' '$a, $a.x[1]'");
        yield return R(null, "-c '$ARGS' --arg a 1 --argjson b '[1]'");
        yield return R(null, "-c '$ARGS.positional' --args x y");
        yield return R(null, "-c '$ARGS' --args x y --arg k v");
        yield return R(null, "-c '$ARGS.positional' --jsonargs 1 '\"x\"' '{\"a\":2}' null");
        yield return R(null, "-c '$ARGS.positional' --jsonargs 1 notjson");
        yield return R(null, "--arg a");
        yield return R(null, "--arg a 1");
        yield return R(null, "-n --argjson a '{' '$a'");
        yield return R(null, "-n --slurpfile a nosuchfile '$a'");
        yield return R(null, "-n --rawfile a nosuchfile '$a'");
        yield return R(null, "-nc '$a' --arg a 1 --arg a 2");
        yield return R(null, "-n '$undefined'");
        yield return R(null, "-n '$ENV.PATH | type'");
        yield return R(null, "-c '[$__prog_args]'");
        yield return R(null, "--bogus .");
        yield return R(null, "-z .");
        yield return R(null, "");
        yield return R(null, "--version");
        yield return R(null, "-V");
        yield return R("{\"a\":1}", "-nr '\"a b\" | @sh'");
        yield return R("1", "-nc 1 -n -n");
        yield return R("1", "-c -c .");
        yield return R("{\"a\":1}", "-ncr '[.a?, \"x\"]'");
        yield return R("{\"a\":1}", "-rn '\"x\", 1, null, [\"y\"]'");
        yield return R("{\"a\":1}", "-e -c '.a, null'");
        yield return R("{\"a\":1}", "-ec '.a'");
        yield return R("{\"a\":1}", "--exit-status '.b'");
        yield return R("{\"a\":1}", "--compact-output --null-input '[1,2]'");
        yield return R("{\"a\":1}", "--slurp --compact-output '.'");
        yield return R("{\"a\":1}", "--raw-input --slurp '.'");
        yield return R("{\"a\":1}", ". --compact-output");
        yield return R("{\"a\":1}", "-c -- '.a'");
        yield return R("{\"a\":1}", "-nc '[1]' --");
    }
}
