using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>An array assigned by <c>eval</c> text inside a function is the shell's own, unless that function declared it <c>local</c>.</summary>
public class EvalArrayScopeTests
{
    [Fact]
    public Task Eval_AssignsCallersArrayByDynamicName() => AssertOracle.EqualAsync(
        "a=(1 2 3); k() { eval \"$1=(m n)\"; }; k a; echo \"${a[@]}\"; ev() { eval \"$1+=(added)\"; }; ev a; echo \"${a[@]}\"");

    [Fact]
    public Task Eval_AssignsFunctionLocalArray_NotTheGlobal() => AssertOracle.EqualAsync(
        "f() { local -a b; b=(z); eval \"b=(y w)\"; echo \"${b[@]}\"; }; f; echo \"[${b[@]}]\"");

    [Fact]
    public Task Eval_ArrayHelpers_ShiftUnshiftReverse() => AssertOracle.EqualAsync(
        "function unshift { builtin eval -- \"$1=(\\\"\\${@:2}\\\" \\\"\\${$1[@]}\\\")\"; }\nfunction shift_ { builtin eval -- \"$1=(\\\"\\${$1[@]:$((${2:-1}))}\\\")\"; }\na=(1 2 3 4 5 6)\nshift_ a 2\necho \"${a[@]}\"\nunshift a 99\necho \"${a[@]}\"");

    [Fact]
    public Task QuotedArraySlice_IsOneWordPerElement_InLiteralsListsAndArgs() => AssertOracle.EqualAsync(
        "a=(1 2 3 4); a=(\"${a[@]:1}\"); echo ${#a[@]}; b=(\"${a[@]:1:1}\" x); echo ${#b[@]} \"${b[@]}\"; set -- \"${a[@]:1}\"; echo $#; printf '[%s]' \"${a[@]:1}\"; echo; for i in \"${a[@]:0:2}\"; do echo \"i=$i\"; done; for i in \"${a[@]:1}\" z; do echo \"j=$i\"; done");

    [Fact]
    public Task Eval_LocalDeclaredInEvalText_IsTheFunctionsLocal() => AssertOracle.EqualAsync(
        "ib=outer\ng() { eval \"set -- p q r; local ia=\\$#; echo ia=\\$ia n=\\$#\"; }; g\nh() { eval \"local ib=5; echo ib=\\$ib\"; }; h; echo \"[$ib]\"\nu() { builtin eval -- \"local v=2\"; echo v=$v; }; u");

    [Fact]
    public Task Eval_ArrayReverseHelper_WithRunTimeNames() => AssertOracle.EqualAsync(
        "function rev { builtin eval \"\n  set -- \\\"\\${$1[@]}\\\"; $1=()\n  local e$1 i$1=\\$#\n  for e$1; do $1[--i$1]=\\\"\\$e$1\\\"; done\"; }\na=(99 4 5 6)\nrev a\necho \"${a[@]}\"");

    [Fact]
    public Task Eval_SyntaxError_IsStatus2() => AssertOracle.EqualAsync(
        "{ eval 'echo >'; } 2>/dev/null\necho status=$?\nx='echo >'\n{ eval \"$x\"; } 2>/dev/null\necho s2=$?\neval 'echo ok'\necho s3=$?");

    [Fact]
    public Task Source_WithoutArguments_IsStatus2() => AssertOracle.EqualAsync(
        "{ source; } 2>/dev/null\necho status=$?\n{ . ; } 2>/dev/null\necho s=$?");

    [Fact]
    public Task Eval_InvalidOption_IsStatus2() => AssertOracle.EqualAsync(
        "{ eval -z; } 2>/dev/null\necho $?\n{ eval -- 'echo hi'; } 2>/dev/null\necho $?\n{ eval -; } 2>/dev/null\necho $?");

    [Fact]
    public Task NumericCommandWord_IsCommandNotFound() => AssertOracle.EqualAsync(
        "{ 127; } 2>/dev/null\necho s=$?\n{ 0; } 2>/dev/null\necho s=$?");
}
