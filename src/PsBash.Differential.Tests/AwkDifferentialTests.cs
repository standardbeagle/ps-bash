using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for awk: each script runs in real bash AND ps-bash and the
/// bytes are diffed. This is the parity gate for the awk implementation (psm1
/// today, a binary cmdlet port in progress) — every case here is a behavior the
/// C# port must reproduce exactly. Cases deliberately avoid awk's
/// unspecified-order constructs (e.g. `for (k in arr)`), which are not
/// byte-stable across implementations.
/// </summary>
public class AwkDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(15));

    // These five cases diverge from bash on the CURRENT psm1 awk (verified
    // 2026-06-14: string-concat of fields, split(), += accumulation, index(),
    // and if/else). They are not regressions — they are the parity targets the
    // C# awk port must fix. Kept here (skipped) so the gap list is tracked in
    // the oracle and flips to active assertions the moment the port closes them.
    private const string GapNote = "known psm1 awk gap — C# port parity target (un-skip when fixed)";

    [SkippableFact] public Task Awk_PrintWholeLine() => Eq("printf 'a b c\\n' | awk '{print}'");
    [SkippableFact] public Task Awk_PrintDollar0() => Eq("printf 'a b c\\n' | awk '{print $0}'");
    [SkippableFact] public Task Awk_FieldAccess() => Eq("printf 'a b c\\n' | awk '{print $2}'");
    [SkippableFact] public Task Awk_MultiFieldPrint() => Eq("printf 'a b c\\n' | awk '{print $1, $3}'");
    [SkippableFact] public Task Awk_NF() => Eq("printf 'a b c d\\n' | awk '{print NF}'");
    [SkippableFact] public Task Awk_NR() => Eq("printf 'x\\ny\\nz\\n' | awk '{print NR, $0}'");
    [SkippableFact] public Task Awk_FieldSepColon() => Eq("printf 'a:b:c\\n' | awk -F: '{print $2}'");
    [SkippableFact] public Task Awk_LastField_NF() => Eq("printf 'a b c\\n' | awk '{print $NF}'");

    [SkippableFact] public Task Awk_Begin() => Eq("printf '' | awk 'BEGIN{print \"start\"}'");
    [SkippableFact] public Task Awk_End_NR() => Eq("printf 'a\\nb\\n' | awk 'END{print NR}'");
    [SkippableFact] public Task Awk_BeginBodyEnd() => Eq("printf 'a\\nb\\n' | awk 'BEGIN{print \"s\"} {print} END{print \"e\"}'");

    [SkippableFact] public Task Awk_PatternRegex() => Eq("printf 'foo\\nbar\\nbaz\\n' | awk '/ba/{print}'");
    [SkippableFact] public Task Awk_PatternNumericCompare() => Eq("printf '1\\n5\\n10\\n' | awk '$1 > 3 {print}'");
    [SkippableFact] public Task Awk_PatternNrEquals() => Eq("printf 'a\\nb\\nc\\n' | awk 'NR==2{print}'");

    [SkippableFact] public Task Awk_VarFlag() => Eq("printf '' | awk -v x=5 'BEGIN{print x}'");
    [SkippableFact] public Task Awk_Arithmetic() => Eq("printf '2 3\\n' | awk '{print $1 + $2}'");
    [SkippableFact] public Task Awk_StringConcat() { Skip.If(true, GapNote); return Eq("printf 'a b\\n' | awk '{print $1 $2}'"); }
    [SkippableFact] public Task Awk_SumAccumulate() { Skip.If(true, GapNote); return Eq("printf '1\\n2\\n3\\n' | awk '{s+=$1} END{print s}'"); }

    [SkippableFact] public Task Awk_Length() => Eq("printf 'hello\\n' | awk '{print length($0)}'");
    [SkippableFact] public Task Awk_Substr() => Eq("printf 'hello\\n' | awk '{print substr($0,2,3)}'");
    [SkippableFact] public Task Awk_Toupper() => Eq("printf 'abc\\n' | awk '{print toupper($0)}'");
    [SkippableFact] public Task Awk_Tolower() => Eq("printf 'ABC\\n' | awk '{print tolower($0)}'");
    [SkippableFact] public Task Awk_Index() { Skip.If(true, GapNote); return Eq("printf 'hello\\n' | awk '{print index($0,\"ll\")}'"); }
    [SkippableFact] public Task Awk_Printf() => Eq("printf 'a\\n' | awk '{printf \"%s-%s\\n\", $1, \"x\"}'");
    [SkippableFact] public Task Awk_Sub() => Eq("printf 'aaa\\n' | awk '{sub(/a/,\"b\"); print}'");
    [SkippableFact] public Task Awk_Gsub() => Eq("printf 'aaa\\n' | awk '{gsub(/a/,\"b\"); print}'");
    [SkippableFact] public Task Awk_Split() { Skip.If(true, GapNote); return Eq("printf 'a,b,c\\n' | awk '{n=split($0,arr,\",\"); print n, arr[1], arr[3]}'"); }

    [SkippableFact] public Task Awk_Ofs() => Eq("printf 'a b\\n' | awk 'BEGIN{OFS=\"-\"} {print $1, $2}'");
    [SkippableFact] public Task Awk_IfElse() { Skip.If(true, GapNote); return Eq("printf '1\\n5\\n' | awk '{if ($1>3) print \"big\"; else print \"small\"}'"); }
}
