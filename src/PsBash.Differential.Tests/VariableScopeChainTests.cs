using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A bash scalar has ONE storage, the shell variable store (docs/specs/environment-model.md §8). A loop that binds
/// its variable as a PowerShell variable as well made the name resolve through two chains: a function called from
/// the loop body read the CALLER's loop variable through PowerShell's dynamic scope instead of its own `local`
/// (`for q in one; do f; done` with `f() { local q=9; echo $((q+1)); }` printed 1), and a write the loop body could
/// not see (a callee's `q=5`, `read q`, `printf -v q`) left the body reading a stale copy.
/// Matrix: loop kind x callee access x shadowing x the loop variable after the call.
/// </summary>
public class VariableScopeChainTests
{
    // Each callee shadows q and reads it one way; the loop prints its own q after every call.
    private const string Callees = """
        q=glob
        ar() { local q; q=$(echo 9); echo "ar $((q + 1))"; }
        cm() { local q=9; (( q > 5 )) && echo "cm yes" || echo "cm no"; (( q++ )); echo "cm $q"; }
        lt() { local q=9; let 'r = q + 1'; echo "lt $r"; }
        br() { local q=9; echo "br ${q} $q"; }
        tt() { local q=9; [[ $q == 9 ]] && echo "tt yes" || echo "tt no"; [[ q -eq 9 ]] && echo "tt eq"; }
        ap() { local q=9; q+=x; echo "ap $q"; }
        pv() { local q; printf -v q '%s' 9; echo "pv $((q+1)) $q"; }
        rd() { local q; read q <<< 9; echo "rd $((q+1)) $q"; }
        dc() { declare q=9; echo "dc $((q+1)) $q"; }
        vv() { local q=9; [[ -v q ]] && echo "vv set"; local n=q; echo "ind ${!n}"; }
        dp() { local q=9; declare -p q; }
        un() { local q; echo "un [${q-unset}] $((q+1))"; }
        nest() { local q=9; inner; }
        inner() { echo "inner $((q+1)) $q"; }
        rec() { local q=$1; if (( q > 0 )); then rec $((q - 1)); fi; echo "rec $q $((q*2))"; }
        all() { for fn in ar cm lt br tt ap pv rd dc vv dp un nest; do $fn; echo "  after $fn q=$q"; done; rec 2; }

        """;

    public static TheoryData<string> Loops() => new()
    {
        "for q in one; do all; done; echo \"end q=$q\"",
        "for q in 'two words'; do all; done; echo \"end q=$q\"",
        "for ((q = 100; q < 101; q++)); do all; done; echo \"end q=$q\"",
        "while read q; do all; done <<< rv; echo \"end q=[$q]\"",
        "while read -r q rest; do all; done <<< 'rv tail'; echo \"end q=[$q]\"",
        "for q in outer; do for q in inner; do all; done; echo \"mid q=$q\"; done; echo \"end q=$q\"",
        "all; echo \"end q=$q\"",
    };

    [SkippableTheory]
    [MemberData(nameof(Loops))]
    public Task ACalleesLocal_HidesTheCallersLoopVariable(string loop) =>
        AssertOracle.EqualAsync(Callees + loop, timeout: TimeSpan.FromSeconds(120));

    // No `local`: the callee writes the shell's q, and the loop body must read that write back.
    private const string Writers = """
        gs() { q=5; }
        ga() { (( q = 6 )); }
        gr() { read q <<< 7; }
        gp() { printf -v q '%s' 8; }
        gl() { let q=9; }
        gx() { q+=x; }
        gu() { unset q; }
        show() { echo "$1 [$q] $((q+0)) ${q:-empty}"; }

        """;

    public static TheoryData<string> WriterLoops() => new()
    {
        "for q in a; do for w in gs ga gr gp gl gx gu; do q=a; $w; show $w; done; done; echo \"end [$q]\"",
        "for ((q = 0; q < 1; q++)); do for w in gs ga gr gp gl gx; do q=0; $w; show $w; done; q=0; done; echo \"end [$q]\"",
        "while read q; do for w in gs ga gr gp gl gx gu; do q=a; $w; show $w; done; done <<< a; echo \"end [$q]\"",
        "for q in a; do q=b; show body; read q <<< c; show read; printf -v q d; show printf; (( q = 4 )); show arith; done",
    };

    [SkippableTheory]
    [MemberData(nameof(WriterLoops))]
    public Task AWriteTheLoopBodyCannotSee_IsWhatTheBodyReadsNext(string loop) =>
        AssertOracle.EqualAsync(Writers + loop, timeout: TimeSpan.FromSeconds(120));

    // A loop variable a callee reads without declaring it is the caller's current value.
    [SkippableFact]
    public Task ACalleeWithoutLocal_SeesTheLoopValue() => AssertOracle.EqualAsync("""
        see() { echo "see $q $((q*2)) ${#q}"; [[ $q -gt 1 ]] && echo big; }
        for q in 1 2; do see; done
        for ((q = 3; q < 5; q++)); do see; done
        printf '%s\n' 5 6 | while read q; do see; done
        while read q; do see; done <<< 7
        """);

    // The same shadow outside any loop: a builtin that once kept a PowerShell copy of the variable (read, printf -v,
    // getopts) and a name PowerShell itself uses (input, host, error, args) must not hide the shell's value. `read`
    // into a function's local left a GLOBAL copy behind: every later `(( q > 0 ))` read it, and a recursion on a
    // `local q=$1` never reached its base case.
    [SkippableFact]
    public Task AStaleOrPowerShellOwnedCopy_NeverShadowsTheShellVariable() => AssertOracle.EqualAsync("""
        rl() { local rq; read rq <<< 9; }; rl; echo "rl [${rq-unset}] $((rq + 0))"
        down() { local rq=$1; if (( rq > 0 )); then down $((rq - 1)); fi; echo "down $rq"; }; down 2
        read rr <<< 5; rr=7; echo "rr $((rr)) $rr"
        printf -v pp 5; pp=7; echo "pp $((pp)) $pp"
        set -- -a; getopts a opt; opt=3; echo "opt $((opt + 1))"
        input=4; echo "input $((input + 1))"
        host=3; echo "host $((host * 2))"
        Error=2; echo "Error $((Error + 1))"
        f() { args=6; input=1; echo "fn $((args + input))"; }; f
        """);

    // A background job started in a loop still sees the value the loop had when it started the job.
    [SkippableFact]
    public Task ABackgroundJobInALoop_KeepsTheValueAtLaunch() => AssertOracle.EqualAsync(
        "for j in 1 2 3; do echo \"job $j $((j * 10))\" & done | sort; wait\n"
        + "for ((k = 0; k < 2; k++)); do echo \"k $k $((k + 5))\" & done | sort; wait\n"
        + "printf '7\\n8\\n' | while read r; do echo \"r $r $((r * 2))\" & done | sort; wait");
}
