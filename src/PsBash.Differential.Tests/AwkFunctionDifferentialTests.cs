using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for awk user-defined functions: each idiom runs in real bash (gawk) AND ps-bash
/// and stdout + exit status (+ the resulting tree for the file-writing cases) are diffed. Static
/// errors (duplicate function, undefined function, ...) are NOT here: gawk exits 1 for them and
/// ps-bash 2 like every other awk syntax error — they are hand-asserted in AwkFunctionTests.
/// </summary>
public class AwkFunctionDifferentialTests
{
    private const string Data = "a 1\nb 2\na 3\n";

    // stdout + exit status (+ final tree) of one scenario; stderr is not compared.
    private static Task Eq(string command, string setup = "") => EqualAsync(setup, command);

    [SkippableFact] public Task Max_TernaryReturn() =>
        Eq("awk 'function max(a,b){return a>b?a:b} BEGIN{print max(3,7), max(9,2), max(\"a\",\"b\")}'");

    [SkippableFact] public Task Factorial_Recursive() =>
        Eq("awk 'function f(n){return n<=1?1:n*f(n-1)} BEGIN{print f(1), f(5), f(10), f(20)}'");

    [SkippableFact] public Task Fibonacci_Recursive() =>
        Eq("awk 'function fib(n){return n<2?n:fib(n-1)+fib(n-2)} BEGIN{print fib(20)}'");

    [SkippableFact] public Task MutualRecursion() =>
        Eq("awk 'function ev(n){return n==0?1:od(n-1)} function od(n){return n==0?0:ev(n-1)} BEGIN{print ev(10), od(10), ev(7)}'");

    [SkippableFact] public Task ArrayByReference_Fill() =>
        Eq("awk 'function fill(a,n,  i){for(i=1;i<=n;i++)a[i]=i*i} BEGIN{fill(sq,4); for(i=1;i<=4;i++) printf \"%s \", sq[i]; print \"\"}'");

    [SkippableFact] public Task ScalarByValue_CallerKeepsItsValue() =>
        Eq("awk 'function inc(x){x++; return x} BEGIN{y=5; print inc(y), y}'");

    [SkippableFact] public Task LocalArray_FreshOnEveryCall() =>
        Eq("awk 'function g(  loc,k,n){loc[\"a\"]=1; loc[\"b\"]=2; for(k in loc) n++; return n} BEGIN{print g(), g()}'");

    [SkippableFact] public Task LocalScalar_UninitializedAndFresh() =>
        Eq("awk 'function h(  z){return z+1 \"|\" length(z)} function c(  x){x=x+1; return x} BEGIN{print h(), c(), c(), c()}'");

    [SkippableFact] public Task FewerArguments_MissingAreLocals() =>
        Eq("awk 'function f(a,b,c){return a \"-\" b \"-\" c \"-\"} BEGIN{print f(1); print f(1,2); print f()}'");

    [SkippableFact] public Task DefinedAfterUse_AndFuncKeyword() =>
        Eq("awk 'BEGIN{print later(4), sq(5)} function later(x){return x*2} func sq(x){return x*x}'");

    [SkippableFact] public Task NoReturn_BareReturn() =>
        Eq("awk 'function f(x){x=1} function g(){return} BEGIN{v=f(2); w=g(); print \"[\" v \"]\", length(v), v+0, \"[\" w \"]\"}'");

    [SkippableFact] public Task UntypedArgument_BecomesCallersArray() =>
        Eq("awk 'function mk(a){a[\"k\"]=\"v\"} function inner(b){b[1]=\"deep\"} function outer(a){inner(a)} BEGIN{mk(arr); print arr[\"k\"], length(arr); outer(z); print z[1]}'");

    [SkippableFact] public Task RecursiveArrayFill() =>
        Eq("awk 'function r(a,n){if(n==0)return; a[n]=n; r(a,n-1)} BEGIN{r(q,3); print length(q), q[1], q[3]}'");

    [SkippableFact] public Task ReturnFromNestedLoops() =>
        Eq("awk 'function find(  i,j){for(i=1;i<9;i++){for(j=1;j<9;j++){if(i*j==12 && i==3) return i \",\" j}} return \"none\"} BEGIN{print find()}'");

    [SkippableFact] public Task SumOfArrayParameter_AndDeleteAndSplit() =>
        Eq("awk 'function sum(a,  k,s){for(k in a)s+=a[k]; return s} function clr(a,  k){for(k in a)delete a[k]} function sp(s,a){return split(s,a,\",\")} BEGIN{a[1]=2;a[2]=3; print sum(a); clr(a); print length(a); n=sp(\"x,y,z\",arr); print n, arr[3]}'");

    [SkippableFact] public Task GlobalVisibleAndShadowedByParameter() =>
        Eq("awk 'function setg(){G=42} function f(G){G=1} BEGIN{setg(); print G; H=9; f(H); print H}'");

    [SkippableFact] public Task StringNumberParameters() =>
        Eq("awk 'function f(s){return s+0} BEGIN{print f(\"3x\"), f(\"abc\"), f(2.5), f(\"0x1A\")}'");

    [SkippableFact] public Task CallAsPattern_OnStdin() =>
        Eq("printf 'a\\nb\\nc\\nd\\ne\\n' | awk 'function odd(n){return n%2} odd(NR){print NR \":\" $0}'");

    [SkippableFact] public Task FieldsModifiedInsideFunction() =>
        Eq("echo 'a b c' | awk 'function f(){$2=\"Z\"; return NF} {print f(), $0}'");

    [SkippableFact] public Task ExitInsideFunction_KeepsStatus() =>
        Eq("awk 'function f(){exit 3} BEGIN{f(); print \"no\"} END{print \"end\"}'");

    [SkippableFact] public Task NextInsideFunction() =>
        Eq("printf 'x\\ny\\n' | awk 'function f(){if ($0==\"x\") next} {f(); print \"kept:\" $0} END{print NR}'");

    [SkippableFact] public Task GetlineInsideFunction() =>
        Eq("printf 'l1\\nl2\\n' | awk 'function nxt(  l){getline l; return l} {print $0 \"/\" nxt()}'");

    [SkippableFact] public Task SubGsubAndMatchInsideFunction() =>
        Eq("awk 'function clean(s){gsub(/[aeiou]/,\"_\",s); return s} function pos(s){return match(s,/b+/) \":\" RLENGTH} BEGIN{print clean(\"education\"), pos(\"aabbbc\")}'");

    [SkippableFact] public Task SprintfAndRecursionBuildString() =>
        Eq("awk 'function rev(s){return length(s)<=1?s:rev(substr(s,2)) substr(s,1,1)} function hex(n){return sprintf(\"%x\", n)} BEGIN{print rev(\"abcdef\"), hex(255)}'");

    [SkippableFact] public Task RecursionDepthOneThousand() =>
        Eq("awk 'function d(n){return n==0?0:1+d(n-1)} BEGIN{print d(1000)}'");

    [SkippableFact] public Task SplitIntoFilesThroughAFunction() =>
        Eq("awk 'function out(n, s){ print s > (n \".txt\") } { out($1, $2) }' data", Tree(("data", Data)));

    [SkippableFact] public Task WordCountReport_WithHelper() =>
        Eq("printf 'b a b\\nc b\\n' | awk 'function bump(w){cnt[w]++} {for(i=1;i<=NF;i++) bump($i)} END{n=0; for(w in cnt) keys[++n]=w; for(i=1;i<=n;i++) for(j=i+1;j<=n;j++) if(keys[j]<keys[i]){t=keys[i];keys[i]=keys[j];keys[j]=t} for(i=1;i<=n;i++) print keys[i], cnt[keys[i]]}'");

    [SkippableFact] public Task SortViaFunctionAndPipe() =>
        Eq("printf '3\\n1\\n2\\n' | awk 'function emit(x){print x | \"sort -n\"} {emit($1)}'");

    [SkippableFact] public Task ScalarArgumentUsedAsArray_FatalExit2() =>
        Eq("awk 'function f(a){a[1]=5} BEGIN{x=1; f(x); print \"no\"}'");

    [SkippableFact] public Task ArrayUsedAsScalar_FatalExit2() =>
        Eq("awk 'function f(a){a[1]=1} BEGIN{f(x); x=2; print \"no\"}'");

    [SkippableFact] public Task UndefinedFunction_Fatal() =>
        Eq("awk 'BEGIN{print nosuch(1)}'");
}
