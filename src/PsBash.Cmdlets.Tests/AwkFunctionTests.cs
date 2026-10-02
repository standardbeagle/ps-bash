using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk user-defined functions: <c>function name(params, locals) { ... }</c> (also <c>func</c>), calls,
/// <c>return</c>, recursion, scalars by value, arrays by reference, extra parameters as locals, an
/// untyped parameter that becomes an array in the callee, and gawk's errors. Expected values are gawk
/// 5.2.1's (WSL Ubuntu-24.04); byte-level parity of the common idioms is in AwkFunctionDifferentialTests.
/// </summary>
public class AwkFunctionTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public AwkFunctionTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private CmdResult AwkR(string program) => Run("Invoke-BashAwk " + Q(program));

    private string[] Awk(string program) =>
        AwkR(program).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    private string[] AwkStdin(string input, string program) =>
        Run(string.Join(",", input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Q)) +
            " | Invoke-BashAwk " + Q(program)).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    // ── calls and return ─────────────────────────────────────────────────────

    [Fact]
    public void Function_Max_ReturnsLarger() =>
        Assert.Equal(new[] { "7 9" }, Awk("function max(a,b){return a>b?a:b} BEGIN{print max(3,7), max(9,2)}"));

    [Fact]
    public void Function_RecursiveFactorial() =>
        Assert.Equal(new[] { "3628800" }, Awk("function f(n){return n<=1?1:n*f(n-1)} BEGIN{print f(10)}"));

    [Fact]
    public void Number_IntegralValuesUpTo64Bits_PrintAsIntegers() =>
        // gawk: 20! and 2^62 print in full; only values past the 64-bit range fall back to %.6g.
        Assert.Equal(new[] { "2432902008176640000 4611686018427387904 1e+30" },
            Awk("function f(n){return n<=1?1:n*f(n-1)} BEGIN{print f(20), 2^62, 10^30}"));

    [Fact]
    public void Function_RecursiveFibonacci() =>
        Assert.Equal(new[] { "610" }, Awk("function fib(n){return n<2?n:fib(n-1)+fib(n-2)} BEGIN{print fib(15)}"));

    [Fact]
    public void Function_MutualRecursion() =>
        Assert.Equal(new[] { "1 0" },
            Awk("function ev(n){return n==0?1:od(n-1)} function od(n){return n==0?0:ev(n-1)} BEGIN{print ev(10), od(10)}"));

    [Fact]
    public void Function_DefinedAfterUse() =>
        Assert.Equal(new[] { "8" }, Awk("BEGIN{print later(4)} function later(x){return x*2}"));

    [Fact]
    public void Function_FuncKeyword() =>
        Assert.Equal(new[] { "25" }, Awk("func sq(x){return x*x} BEGIN{print sq(5)}"));

    [Fact]
    public void Function_NoReturn_YieldsUninitialized() =>
        Assert.Equal(new[] { "[] 0 0" }, Awk("function f(x){x=1} BEGIN{v=f(2); print \"[\" v \"]\", length(v), v+0}"));

    [Fact]
    public void Function_BareReturn_YieldsUninitialized() =>
        Assert.Equal(new[] { "[]" }, Awk("function f(){return} BEGIN{print \"[\" f() \"]\"}"));

    [Fact]
    public void Function_ReturnFromInsideNestedLoops()
    {
        Assert.Equal(new[] { "3,4" },
            Awk("function find(  i,j){for(i=1;i<9;i++){for(j=1;j<9;j++){if(i*j==12 && i==3) return i \",\" j}} return \"none\"} BEGIN{print find()}"));
    }

    [Fact]
    public void Function_ReturnFromWhileAndForIn()
    {
        Assert.Equal(new[] { "5", "k2" },
            Awk("function w(  n){while(1){n++; if(n==5) return n}} function fi(a,  k){for(k in a) if(a[k]==2) return k} BEGIN{ print w(); z[\"k2\"]=2; print fi(z) }"));
    }

    [Fact]
    public void Function_NestedCallArguments() =>
        Assert.Equal(new[] { "10" }, Awk("function add(a,b){return a+b} BEGIN{print add(add(1,2), add(3,4))}"));

    [Fact]
    public void Function_CallInPatternPosition() =>
        Assert.Equal(new[] { "1", "3" }, AwkStdin("a\nb\nc\n", "function odd(n){return n%2} odd(NR){print NR}"));

    [Fact]
    public void Function_PrintsInsideBody() =>
        Assert.Equal(new[] { "in:1", "in:z" }, Awk("function p(x){print \"in:\" x} BEGIN{p(1); p(\"z\")}"));

    [Fact]
    public void Function_StringAndNumberParameters() =>
        Assert.Equal(new[] { "3 0 2.5" }, Awk("function f(s){return s+0} BEGIN{print f(\"3x\"), f(\"abc\"), f(2.5)}"));

    [Fact]
    public void Function_SyntaxVariants_NewlinesAndSemicolon()
    {
        Assert.Equal(new[] { "3", "2", "2" }, Awk(
            "function f(a,\n b){return a+b}\nfunction g(a)\n{return a+1}\nfunction h(a){return a+1};\nBEGIN{print f(1,2); print g(1); print h(1)}"));
    }

    // ── parameters and locals ────────────────────────────────────────────────

    [Fact]
    public void Function_ScalarsPassByValue() =>
        Assert.Equal(new[] { "6 5" }, Awk("function inc(x){x++; return x} BEGIN{y=5; print inc(y), y}"));

    [Fact]
    public void Function_ArraysPassByReference() =>
        Assert.Equal(new[] { "1 4 9 16 " },
            Awk("function fill(a,n,  i){for(i=1;i<=n;i++)a[i]=i*i} BEGIN{fill(sq,4); for(i=1;i<=4;i++) printf \"%s \", sq[i]; print \"\"}"));

    [Fact]
    public void Function_UntypedArgumentBecomesCallersArray()
    {
        Assert.Equal(new[] { "v 1" }, Awk("function mk(a){a[\"k\"]=\"v\"} BEGIN{mk(arr); print arr[\"k\"], length(arr)}"));
    }

    [Fact]
    public void Function_UntypedArgumentPassedThroughTwoLevels() =>
        Assert.Equal(new[] { "deep" }, Awk("function inner(b){b[1]=\"deep\"} function outer(a){inner(a)} BEGIN{outer(z); print z[1]}"));

    [Fact]
    public void Function_ArrayParameterFilledRecursively() =>
        Assert.Equal(new[] { "3 1 3" }, Awk("function r(a,n){if(n==0)return; a[n]=n; r(a,n-1)} BEGIN{r(q,3); print length(q), q[1], q[3]}"));

    [Fact]
    public void Function_ExtraParametersAreFreshLocalsEachCall()
    {
        Assert.Equal(new[] { "1 1 1" }, Awk("function f(  x){x=x+1; return x} BEGIN{print f(), f(), f()}"));
        Assert.Equal(new[] { "12345", "123" },
            Awk("function f(n,  i,s){for(i=1;i<=n;i++)s=s i; return s} BEGIN{print f(5); print f(3)}"));
    }

    [Fact]
    public void Function_UninitializedLocalUsableAsArray_FreshEachCall() =>
        Assert.Equal(new[] { "2 2" },
            Awk("function g(  loc,k,n){loc[\"a\"]=1; loc[\"b\"]=2; for(k in loc) n++; return n} BEGIN{print g(), g()}"));

    [Fact]
    public void Function_UninitializedLocalScalar() =>
        Assert.Equal(new[] { "1|0" }, Awk("function h(  z){return z+1 \"|\" length(z)} BEGIN{print h()}"));

    [Fact]
    public void Function_CalledWithFewerArguments() =>
        Assert.Equal(new[] { "1---" }, Awk("function f(a,b,c){return a \"-\" b \"-\" c \"-\"} BEGIN{print f(1)}"));

    [Fact]
    public void Function_ExtraArgumentsAreEvaluatedAndIgnored() =>
        Assert.Equal(new[] { "1 2" }, Awk("function f(a){return a} BEGIN{print f(1, x = 2), x}"));

    [Fact]
    public void Function_ParameterShadowsGlobal() =>
        Assert.Equal(new[] { "9" }, Awk("function f(G){G=1} BEGIN{G=9; f(); print G}"));

    [Fact]
    public void Function_AssignsGlobal() =>
        Assert.Equal(new[] { "42" }, Awk("function setg(){G=42} BEGIN{setg(); print G}"));

    [Fact]
    public void Function_GlobalArrayPassedAndSummed() =>
        Assert.Equal(new[] { "5" }, Awk("function sum(a,  k,s){for(k in a)s+=a[k]; return s} BEGIN{a[1]=2;a[2]=3; print sum(a)}"));

    [Fact]
    public void Function_DeleteAndSplitOnArrayParameters()
    {
        Assert.Equal(new[] { "0" }, Awk("function clr(a,  k){for(k in a)delete a[k]} BEGIN{x[1];x[2]; clr(x); print length(x)}"));
        Assert.Equal(new[] { "3 c" }, Awk("function sp(s,a){return split(s,a,\",\")} BEGIN{n=sp(\"a,b,c\",arr); print n, arr[3]}"));
    }

    [Fact]
    public void Function_ReturnsArrayElement() =>
        Assert.Equal(new[] { "e" }, Awk("function f(a){return a[1]} BEGIN{x[1]=\"e\"; print f(x)}"));

    [Fact]
    public void Function_UntypedArgumentLengthIsZero() =>
        Assert.Equal(new[] { "0" }, Awk("function f(x){return length(x)} BEGIN{print f(u)}"));

    [Fact]
    public void Function_MultiSubscriptKeysInsideBody() =>
        Assert.Equal(new[] { "1:2" }, Awk("function k(a,  s,p){a[1,2]=\"v\"; for(s in a){split(s,p,SUBSEP); return p[1] \":\" p[2]}} BEGIN{print k(m)}"));

    [Fact]
    public void Function_ModifiesFieldsOfTheCurrentRecord() =>
        Assert.Equal(new[] { "3 a Z c" }, AwkStdin("a b c\n", "function f(){$2=\"Z\"; return NF} {print f(), $0}"));

    // ── control flow out of a function ───────────────────────────────────────

    [Fact]
    public void Function_ExitInsideFunction_StopsWithStatus()
    {
        var r = AwkR("function f(){exit 3} BEGIN{f(); print \"no\"}");
        r.AssertFailed(3);
        Assert.DoesNotContain("no", r.Stdout);
    }

    [Fact]
    public void Function_NextInsideFunction_SkipsTheRule() =>
        Assert.Equal(new[] { "end" }, AwkStdin("x\n", "function f(){next} {f(); print \"no\"} END{print \"end\"}"));

    [Fact]
    public void Function_RecursionDepthOneThousand() =>
        Assert.Equal(new[] { "1000" }, Awk("function d(n){return n==0?0:1+d(n-1)} BEGIN{print d(1000)}"));

    [Fact]
    public void Function_RunawayRecursion_IsACleanFatalError_NotACrash()
    {
        var r = AwkR("function d(n){return 1+d(n+1)} BEGIN{print d(0)}");
        r.AssertFailed(2, "nesting");
    }

    [Fact]
    public void Function_WithGetlineAndRedirectInside()
    {
        Assert.Equal(new[] { "line1" },
            AwkStdin("line1\n", "function rd(  l){getline l; return l} BEGIN{print rd()}"));
    }

    // ── errors (gawk 5.2.1: static `error:` / syntax errors exit 1, `fatal:` ones exit 2) ──

    [Theory]
    [InlineData("function f(){return 1} function f(){return 2} BEGIN{print f()}", "previously defined", 1)]
    [InlineData("function f(){return 1} BEGIN{f=3; print f}", "function `f'", 1)]
    [InlineData("function f(f){return 1} BEGIN{print f(1)}", "cannot use function name as parameter name", 1)]
    [InlineData("function f(a,a){return 1} BEGIN{print f(1,2)}", "duplicates parameter", 1)]
    [InlineData("BEGIN{print nosuch(1)}", "function `nosuch' not defined", 2)]
    [InlineData("function f(a){return a} BEGIN{print f (1)}", "called with space", 1)]
    [InlineData("BEGIN{return 1}", "return", 1)]
    [InlineData("function length(x){return 1} BEGIN{print 1}", "built-in", 1)]
    public void Function_StaticErrors_ExitLikeGawk(string program, string stderrFragment, int exit) =>
        AwkR(program).AssertFailed(exit, stderrFragment);

    [Fact]
    public void Function_ScalarArgumentUsedAsArray_IsFatal() =>
        AwkR("function f(a){a[1]=5} BEGIN{x=1; f(x)}").AssertFailed(2, "scalar parameter `a' as an array");

    [Fact]
    public void Function_ArrayUsedAsScalar_IsFatal()
    {
        AwkR("function f(a){a[1]=1} BEGIN{f(x); x=2; print x}").AssertFailed(2, "array `x' in a scalar context");
        AwkR("function f(a){return a} BEGIN{x[1]=1; print f(x)}").AssertFailed(2, "in a scalar context");
    }
}
