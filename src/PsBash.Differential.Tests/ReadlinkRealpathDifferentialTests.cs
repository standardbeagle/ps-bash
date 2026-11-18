using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for <c>readlink</c> / <c>realpath</c>: every mode against every operand shape (existing, missing
/// last / intermediate component, trailing slash, <c>.</c>/<c>..</c> after a file or a missing name, absolute, empty).
/// REGRESSION: both commands canonicalized through PowerShell's Resolve-Path, which folds <c>..</c> by text, follows
/// no link and knows no "all but the last component must exist": plain <c>readlink f</c> printed f's full path (GNU:
/// nothing, status 1), <c>readlink -m</c> was <c>-f</c>, <c>realpath nosuch/x</c> printed a path, and options other
/// than <c>-e</c> were skipped. Fixed at the class: one gnulib-ported walk (<c>CanonicalPath</c>) behind both, GNU name
/// quoting in messages (<c>GnuQuote</c>), and the shared argv parser. Links are covered by the cmdlet and unit tests:
/// Windows hosts may not create them. The canonical answers are physical, so <c>D</c> is <c>pwd -P</c>: on macOS the
/// temp dir is reached as <c>/var/…</c> but is <c>/private/var/…</c> (REGRESSION: a cassette recorded where /tmp is no link
/// failed there while the commands were right).
/// </summary>
public class ReadlinkRealpathDifferentialTests
{
    [SkippableFact]
    public Task EveryMode_EveryOperandShape()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; d=$(pwd -P); mkdir sub; touch f\n"
            + "for c in 'readlink' 'readlink -f' 'readlink -e' 'readlink -m' 'readlink -fv' 'readlink -ev' 'readlink -mv' "
            + "'readlink -v' 'realpath' 'realpath -e' 'realpath -m' 'realpath -q' 'realpath -s' 'realpath -L' 'realpath -sm'; do\n"
            + "  echo \"## $c\"\n"
            + "  for op in f sub sub/ f/ nosuch sub/nosuch sub/nosuch/ nosuch/x sub/../f ./sub/./f sub/nosuch/../f f/x f/.. "
            + "\"$d/sub/../f\" '' 'a b' \"it's\"; do $c \"$op\"; echo \"  rc=$? [$op]\"; done\n"
            + "done 2>&1 | sed \"s|$d|D|g\"\n"
            + "cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task Options_OrderOutputFormsAndUsageErrors()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; d=$(pwd -P); mkdir sub; touch f\n"
            + "readlink -n f sub 2>&1; echo \"rc=$?\"\n"
            + "readlink -fn f | sed \"s|$d|D|g\"; echo \"|rc=$?\"\n"
            + "readlink -fz f sub | tr '\\0' '\\n' | sed \"s|$d|D|g\"; echo \"|\"\n"
            + "realpath -z f nosuch/x 2>/dev/null | tr '\\0' '\\n' | sed \"s|$d|D|g\"; echo \"|\"\n"
            + "readlink -fq nosuch/x; echo \"rc=$?\"; readlink -fvq nosuch/x; echo \"rc=$?\"; readlink -fqv nosuch/x 2>&1; echo \"rc=$?\"\n"
            + "readlink -mfe sub/nosuch 2>&1; echo \"rc=$?\"; readlink -efm nosuch/x | sed \"s|$d|D|g\"; echo \"rc=$?\"\n"
            + "readlink 2>&1; echo \"rc=$?\"; realpath 2>&1; echo \"rc=$?\"; readlink -x f 2>&1; echo \"rc=$?\"\n"
            + "readlink --canon f 2>&1; echo \"rc=$?\"; realpath --canonicalize-m nosuch/x | sed \"s|$d|D|g\"\n"
            + "cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(30));
}
