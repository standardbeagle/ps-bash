using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>${!ref}</c> when ref itself is unset or empty. bash 5 reports "ref: invalid indirect expansion" (unset) or
/// ": invalid variable name" (empty) and drops the command, status 1. Before, ps-bash printed an empty value with
/// status 0, read an unset ref under <c>set -u</c> as "unbound variable", and <c>${!undef:=x}</c> reported twice and
/// then threw assigning to an empty name.
/// </summary>
public class IndirectExpansionErrorTests
{
    // bash prefixes its message with "line N:" and ps-bash does not, so each failing command writes stderr to a file
    // and the script prints the message from the name on.
    private const string Clear = "rm -f indirect-err.txt\n";
    private const string Msg = "\ngrep -o \x27[^:]*: invalid [a-z ]*\x27 indirect-err.txt\nrm -f indirect-err.txt";

    [SkippableTheory]
    [InlineData("{ echo \"a[${!undef}]\"; } 2>>indirect-err.txt\necho st=$?")]
    [InlineData("{ echo \"b[${!undef-dflt}]\"; } 2>>indirect-err.txt\necho st=$?")]
    [InlineData("ref=\n{ echo \"c[${!ref}]\"; } 2>>indirect-err.txt\necho st=$?")]
    [InlineData("{ echo \"d[${!undef:=x}]\"; } 2>>indirect-err.txt\necho st=$?")]
    [InlineData("x=$({ echo \"e[${!undef}]\"; } 2>>indirect-err.txt)\necho \"st=$? x=$x\"")]
    [InlineData("{ echo \"i[${!undef:+alt}]\"; } 2>>indirect-err.txt\necho st=$?")]
    [InlineData("{ echo undef=${!undef-'default'}; } 2>>indirect-err.txt\nset -u\necho NOUNSET\n{ echo undef=${!undef}; } 2>>indirect-err.txt\necho st=$?")]
    public Task Indirect_ThroughAnUnsetOrEmptyRef_IsAnError(string script) => AssertOracle.EqualAsync(Clear + script + Msg);

    /// <summary>An empty array, an unset target or a missing positional is an empty value, not an error.</summary>
    [SkippableTheory]
    [InlineData("arr=()\necho \"f[${!arr}]\"\necho st=$?")]
    [InlineData("ref=nope\necho \"g[${!ref}]\"\necho st=$?")]
    [InlineData("f() { echo \"h[${!1}]\"; }\nf\necho st=$?")]
    [InlineData("ref=tgt\necho \"k[${!ref:=val}]\"\necho \"st=$? tgt=$tgt\"")]
    // An indexed subscript is arithmetic; before, anything but a literal number read as unset. A subscript that does
    // not parse abandons the command (the message wording differs: ps-bash's arithmetic parser words it its own way).
    [InlineData("a=(x y z); i=1\nr1='a[i]'; r2='a[i+1]'; r3='a[-1]'; r4='a[5]'; r5='a[ 0 ]'\necho \"1=${!r1} 2=${!r2} 3=${!r3} 4=${!r4} 5=${!r5}\"\nPWD=1; r6='a[~+]'\n( echo \"6=${!r6}\" ) 2>/dev/null\necho st=$?")]
    // An assoc subscript's quotes are removed wherever they are; before, only the outer ones were (`"al"e` -> `al"e`).
    [InlineData("declare -A assoc=([ale]=bean [corn]=dip ['a b']=sp)\nr1='assoc[\"ale\"]'; r2='assoc[\"al\"e]'; r3=\"assoc['a b']\"; r4='assoc[\"bad\"]'; r5='assoc[corn]'\necho \"1=${!r1} 2=${!r2} 3=${!r3} 4=${!r4} 5=${!r5}\"")]
    public Task Indirect_ThroughASetRef_IsItsTargetsValue(string script) => AssertOracle.EqualAsync(script);
}
