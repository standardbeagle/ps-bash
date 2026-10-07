using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for everyday shell idioms that were silently wrong or destructive.
///
/// Covered regressions:
///   - `$f.txt` / `${f}x` / `"${f}_bak"` in a word: PowerShell folded the text into the variable
///     token (`.txt` member access, `fx` another name) — "" printed, `touch $f.bak` crashed
///   - an EMPTY operand resolved to the current directory: `rm -rf "$unset"` wiped the cwd
///   - `break` / `continue` / `return` inside `while read … done &lt; file` ended the whole script,
///     and `break 2` in plain nested loops did too (a PS break takes a label, not a count)
///   - a sourced file's output was discarded, and `source` cleared the caller's $1..
///   - `case` / `[[ == ]]` / `[ = ]` compared case-INSENSITIVELY (PowerShell -like / -eq)
/// </summary>
public class CommonPatternDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task VarFollowedByText_StaysOneWord()
        => Eq("f=report; echo $f.txt $f:x ${f}x ${f}.txt $f.$f pre$f.txt; "
            + "echo \"[${f}x] [${f}_bak] [${f}1] [$f?]\"; x=\"${f}suffix\"; echo \"$x\"");

    [SkippableFact]
    public Task VarFollowedByText_FileOperands()
        => Eq("cd \"$(mktemp -d)\" || exit 1; f=report; touch $f.bak && cp $f.bak $f.copy && ls");

    [SkippableFact]
    public Task EmptyOperand_RmNeverTouchesCwd()
        => Eq("cd \"$(mktemp -d)\" || exit 1; touch canary; mkdir sub; "
            + "rm -rf ''; echo rc=$?; rm -rf \"$nope\"; echo rc=$?; rm ''; echo rc=$?; ls");

    [SkippableFact]
    public Task BreakContinueReturn_InRedirectedReadLoop()
        => Eq("cd \"$(mktemp -d)\" || exit 1; printf 'a\\nb\\nc\\n' > in.txt\n"
            + "while read -r l; do [ \"$l\" = b ] && break; echo \"1 $l\"; done < in.txt; echo 1-after\n"
            + "while read -r l; do [ \"$l\" = b ] && continue; echo \"2 $l\"; done < in.txt; echo 2-after\n"
            + "printf 'a\\nb\\n' | while read -r l; do [ \"$l\" = b ] && break; echo \"3 $l\"; done; echo 3-after\n"
            + "s=$'x\\ny\\nz'; while read -r l; do [ \"$l\" = y ] && break; echo \"4 $l\"; done <<< \"$s\"; echo 4-after\n"
            + "f() { while read -r l; do [ \"$l\" = b ] && return 7; echo \"5 $l\"; done < in.txt; echo never; }; f; echo \"5 rc=$?\"\n"
            + "for i in 1 2; do while read -r l; do [ \"$l\" = b ] && break 2; echo \"6 $i $l\"; done < in.txt; done; echo 6-after");

    [SkippableFact]
    public Task BreakContinueN_InNestedLoops()
        => Eq("for i in 1 2; do for j in a b; do echo \"$i$j\"; break 2; done; done; echo after\n"
            + "for i in 1 2 3; do for j in a b; do [ $j = a ] && continue 2; echo no; done; echo never; done; echo after2\n"
            + "for i in 1 2; do case $i in 1) continue ;; esac; echo \"case-continue $i\"; done\n"
            + "for i in 1 2 3; do case $i in 2) break ;; esac; echo \"case-break $i\"; done; echo after3");

    [SkippableFact]
    public Task Source_OutputAndPositionalParameters()
        => Eq("cd \"$(mktemp -d)\" || exit 1; printf 'echo \"lib says [$1]\"\\nX=1\\n' > lib.sh\n"
            + "set -- a b; . ./lib.sh; echo \"X=$X 1=$1\"; source ./lib.sh p q; echo \"after-args 1=$1 2=$2\"");

    [SkippableFact]
    public Task CaseAndTest_AreCaseSensitive_NocasematchToggles()
        => Eq("x=abc\n"
            + "case $x in A*) echo 1-yes ;; *) echo 1-no ;; esac\n"
            + "case ABC in abc) echo 2-yes ;; *) echo 2-no ;; esac\n"
            + "[[ $x == A* ]] && echo 3-yes || echo 3-no\n"
            + "[[ abc == ABC ]] && echo 4-yes || echo 4-no\n"
            + "[[ $x != A* ]] && echo 5-ne-true || echo 5-ne-false\n"
            + "[ abc = ABC ] && echo 6-yes || echo 6-no\n"
            + "[ abc = 'a*' ] && echo 7-yes || echo 7-no\n"
            + "case $x in [A-Z]*) echo 8-class ;; *) echo 8-no ;; esac\n"
            + "case abc in *) echo 9-star-first ;; abc) echo 9-literal ;; esac\n"
            + "shopt -s nocasematch\n"
            + "case $x in A*) echo 10-nocase ;; *) echo 10-no ;; esac\n"
            + "[[ $x == ABC ]] && echo 11-nocase || echo 11-no\n"
            + "[ abc = ABC ] && echo 12-yes || echo 12-no\n"
            + "shopt -u nocasematch\n"
            + "[[ $x == A* ]] && echo 13-yes || echo 13-no\n"
            + "case 'a[b' in 'a[b') echo 14-bracket ;; *) echo 14-no ;; esac");
}
