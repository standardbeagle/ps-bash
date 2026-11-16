using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A prefix assignment (<c>v=t cmd</c>) is a temp binding over the variable, in every mode: <c>v=t unset v</c> takes
/// the binding and reveals the value under it. Under <c>set -o posix</c> a binding before a special builtin persists
/// when the builtin ends, but it is still a binding while the builtin runs. ps-bash emitted the POSIX form as a plain
/// assignment, so <c>x=42; x=tmp unset x</c> left x unset there (oils temp-binding "Temp bindings with unset": the
/// second call of the function ran after <c>set -o posix</c>). Each body below is the same list of forms, run at top
/// level, in a function called twice, and over a <c>local</c>, with and without POSIX mode.
/// </summary>
public class TempBindingDifferentialTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private const string Setup = """
        cd "$(mktemp -d)" || exit 9
        echo 'echo src=$x' > _src.sh
        echo 'unset x' > _srcunset.sh
        g() { echo g=$x; }
        gu() { unset x; echo gu=${x-unset}; }
        body() {
          x=42; x=tmp unset x; echo "unset ${x-unset}"
          x=42; x=tmp eval 'unset x'; echo "eval-unset ${x-unset}"
          x=42; x=tmp eval 'unset x; echo in=${x-unset}'; echo "eval-unset-in ${x-unset}"
          x=42; x=tmp eval 'unset x; unset x'; echo "eval-unset2 ${x-unset}"
          x=42; x=tmp eval 'unset x; x=new'; echo "eval-unset-assign ${x-unset}"
          x=42; x=tmp eval 'x=new'; echo "eval-assign ${x-unset}"
          x=42; x=tmp eval 'echo in=$x'; echo "eval ${x-unset}"
          x=42; x=tmp unset y; echo "unset-other ${x-unset}"
          x=42; x=tmp y=t2 unset x; echo "unset-two ${x-unset} ${y-unset}"
          unset y
          x=42; x=tmp :; echo "colon ${x-unset}"
          x=42; x=tmp export x; echo "export ${x-unset}"
          x=42; x=tmp export y=1; echo "export-other ${x-unset}"
          x=42; x=tmp declare z=1; echo "declare ${x-unset}"
          x=42; x=tmp g; echo "fn ${x-unset}"
          x=42; x=tmp gu; echo "fn-unset ${x-unset}"
          x=42; x=tmp . ./_src.sh; echo "dot ${x-unset}"
          x=42; x=tmp source ./_src.sh; echo "source ${x-unset}"
          x=42; x=tmp . ./_srcunset.sh; echo "dot-unset ${x-unset}"
          unset x; x=tmp unset x; echo "noprior-unset ${x-unset}"
          unset x; x=tmp :; echo "noprior-colon ${x-unset}"
          unset x; x=tmp eval 'unset x'; echo "noprior-eval-unset ${x-unset}"
          x=42; x=tmp true; echo "true ${x-unset}"
          x=42; x=tmp set -- a; echo "set ${x-unset}"
          x=42; x=tmp shift; echo "shift ${x-unset}"
          x=42; x=tmp trap - USR1; echo "trap ${x-unset}"
        }
        fbody() { body; }
        lbody() {
          local x=L
          x=tmp unset x; echo "local-unset ${x-unset}"
          local x=L
          x=tmp eval 'unset x'; echo "local-eval-unset ${x-unset}"
          local x=L
          x=tmp :; echo "local-colon ${x-unset}"
          local x=L
          x=tmp local w=1; echo "local-local ${x-unset}"
          x=L
          x=tmp local x; echo "local-local-same ${x-unset}"
        }

        """;

    [SkippableTheory]
    [InlineData("body")]
    [InlineData("fbody; echo again; fbody")]
    [InlineData("x=G; lbody; echo \"after ${x-unset}\"; x=G; lbody; echo \"after ${x-unset}\"")]
    public Task PrefixAssignment_BeforeEachBuiltin_LeavesWhatBashLeaves(string run)
        => AssertOracle.EqualAsync(Setup + run, timeout: Limit);

    [SkippableTheory]
    [InlineData("body")]
    [InlineData("fbody; echo again; fbody")]
    [InlineData("x=G; lbody; echo \"after ${x-unset}\"; x=G; lbody; echo \"after ${x-unset}\"")]
    public Task PrefixAssignment_UnderPosixMode_LeavesWhatBashLeaves(string run)
        => AssertOracle.EqualAsync(Setup + "set -o posix\n" + run, timeout: Limit);

    /// <summary>The oils case: the same function before and after <c>set -o posix</c>, then with it off again.</summary>
    [SkippableFact]
    public Task PrefixAssignment_SameFunctionBeforeAndAfterPosixMode_IsTheSame()
        => AssertOracle.EqualAsync(Setup + "fbody; set -o posix; fbody; set +o posix; fbody", timeout: Limit);

    [SkippableTheory]
    [InlineData("a=42; a=tmp readonly a; echo \"readonly ${a-unset}\"")]
    [InlineData("b=42; b=tmp readonly y=1; echo \"readonly-other ${b-unset}\"")]
    [InlineData("set -o posix; c=42; c=tmp readonly c; echo \"posix readonly ${c-unset}\"")]
    [InlineData("set -o posix; d=42; d=tmp readonly y=1; echo \"posix readonly-other ${d-unset}\"")]
    [InlineData("e=42; e=tmp export e; echo \"export ${e-unset}\"; sh -c 'echo child=$e'")]
    [InlineData("k() { local h=L; h=tmp readonly h; echo \"fn-readonly $h\"; }; k; echo \"after ${h-unset}\"")]
    public Task PrefixAssignment_BeforeReadonlyOrExport_LeavesWhatBashLeaves(string script)
        => AssertOracle.EqualAsync(script, timeout: Limit);
}
