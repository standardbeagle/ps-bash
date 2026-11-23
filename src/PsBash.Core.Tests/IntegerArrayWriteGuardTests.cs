using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// GUARD: the integer attribute on an ARRAY (<c>declare -ai</c>, <c>local -ai</c>, <c>declare -ia</c>, <c>declare -i</c> on
/// an existing array) applies to every element write, and the write consults it in ONE place:
/// <c>PsEmitter.ElementValues</c> (a list, a literal, <c>read -a</c>, <c>mapfile</c>: <c>ConvertTo-BashIntegerArray</c>) and
/// <c>PsBuild.SetElement</c>'s integer flag (<c>a[i]=v</c>, <c>a[i]+=v</c>, <c>a=v</c>: <c>Set-BashElement -Integer</c>).
/// REGRESSION: each write form decided for itself and none looked, so <c>declare -ai q=([1]=010); q[2]+=010</c> stored
/// 010 and 010010 (bash 5.2: <c>8 8</c>). The values are pinned against bash by
/// <c>Differential.Tests/ArithOneEvaluatorDifferentialTests.IntegerArray_EveryElementWriteEvaluates</c>.
/// </summary>
public class IntegerArrayWriteGuardTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    /// <summary>Every write form, each after a declaration that gives the array the attribute.</summary>
    public static TheoryData<string, string> WriteForms()
    {
        var writes = new[]
        {
            "a=(1+1 2*3)", "a+=(9*9)", "a=([2]=1+1 [5]=x)", "a+=(3 4*4)", "a[1]=x*2", "a[i+1]+=010", "a=3+3", "a+=1",
            "read -a a <<< '1+1'", "read -ra a <<< '1+1'", "mapfile -t a < /dev/null", "readarray -t a < /dev/null",
            "mapfile -O 2 -t a < /dev/null", "echo ${a[3]:=2+2}",
        };
        var declarations = new[] { "declare -ai a", "declare -ia a=(1)", "a=(1); declare -i a" };
        var data = new TheoryData<string, string>();
        foreach (var d in declarations)
            foreach (var w in writes)
                data.Add(d, w);
        // In a function: `local -ai` and a function-scoped `declare -ai` (a local).
        data.Add("f() { local -ai a", "a=(1+1); a[1]=2*2; read -a a <<< 3; }");
        data.Add("f() { declare -ai a=(1+1)", "a+=(2*2); }");
        return data;
    }

    private static bool Evaluates(string ps) =>
        ps.Contains(PsBuild.IntegerElementsFunction, StringComparison.Ordinal)
        || ps.Contains("Set-BashElement", StringComparison.Ordinal) && ps.Contains(" " + PsBuild.IntegerElementSwitch, StringComparison.Ordinal)
        || ps.Contains("$(Invoke-BashArith ([string](", StringComparison.Ordinal)
        || ps.Contains("$__psbash_av = Invoke-BashArith ([string](", StringComparison.Ordinal);

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [MemberData(nameof(WriteForms))]
    public void Transpile_EveryElementWriteToAnIntegerArray_Evaluates(string declaration, string write)
    {
        string ps = T(declaration + "; " + write);
        Assert.True(Evaluates(ps), $"`{declaration}; {write}` stores text in an integer array:\n{ps}");
    }

    [Theory]
    [MemberData(nameof(WriteForms))]
    public void Transpile_EveryElementWriteToAPlainArray_StoresTheText(string declaration, string write)
    {
        string plain = declaration.Replace("-ai", "-a").Replace("-ia", "-a").Replace("; declare -i a", "");
        string ps = T(plain + "; " + write);
        Assert.False(Evaluates(ps), $"`{plain}; {write}` evaluates the elements of an array with no integer attribute:\n{ps}");
    }

    /// <summary>
    /// <c>unset NAME</c> and <c>declare +i NAME</c> take the attribute away: later writes store the text (bash:
    /// <c>declare -ai u=(1); unset u; u=(2+2)</c> is <c>2+2</c>).
    /// </summary>
    [Theory]
    [InlineData("declare -ai a=(1); unset a; a=(2+2)")]
    [InlineData("declare -ai a=(1); unset -v a; a[1]=2+2")]
    [InlineData("declare -ai a=(1); declare +i a; a+=(2+2)")]
    public void Transpile_IntegerArrayAfterUnsetOrPlusI_StoresTheText(string bash)
    {
        string ps = T(bash);
        // The last statement, the write (each statement opens its own line-discard scope).
        string after = ps[ps.LastIndexOf("BashLineDiscard]::Enter()", StringComparison.Ordinal)..];
        Assert.False(Evaluates(after), $"`{bash}` still evaluates after the attribute is gone:\n{ps}");
    }

    /// <summary>
    /// Whether the WRITE between <c>prefix</c> and <c>suffix</c> evaluates its value: it adds arithmetic to the text the
    /// script has without it.
    /// </summary>
    private static bool WriteEvaluates(string prefix, string write, string suffix)
        => Evaluations(T(prefix + write + suffix)) > Evaluations(T(prefix + suffix));

    private static int Evaluations(string ps)
        => Count(ps, "Invoke-BashArith") + Count(ps, " " + PsBuild.IntegerElementSwitch) + Count(ps, PsBuild.IntegerElementsFunction);

    /// <summary>
    /// Only an UNCONDITIONAL <c>unset</c> / <c>declare +i</c> (a command of the top-level list or of a function body's)
    /// takes the attribute away, scalar or array. REGRESSION: one in an if, a case arm, a loop or after <c>||</c> did
    /// too, so <c>declare -i n=5; [ -z "$KEEP" ] || unset n; n=n*2</c> stored n*2 (bash 5.2: 10).
    /// </summary>
    [Theory]
    [InlineData("declare -i n=5; [ -z \"$KEEP\" ] || unset n; ", "n=n*2", "")]
    [InlineData("declare -i n=0; for k in 1 2 3; do if [ $k = 9 ]; then unset n; fi; ", "n+=k; ", "done")]
    [InlineData("declare -i n=1; if false; then unset n; fi; ", "n=n+1", "")]
    [InlineData("declare -i n=1; case x in y) unset n;; esac; ", "n=n+1", "")]
    [InlineData("declare -ai a=(1); if false; then unset a; fi; ", "a+=(2+2)", "")]
    [InlineData("declare -ai a=(1); [ -n \"$K\" ] && declare +i a; ", "a[1]=2+2", "")]
    [InlineData("declare -ai a=(1); while false; do unset a; done; ", "a=(2+2)", "")]
    // A function body is emitted more than once: each emission starts from the attributes it was entered with.
    [InlineData("f() { declare -i v; ", "v=1+1; ", "unset v; }")]
    [InlineData("f() { local -ai l; ", "l=(1+1); ", "unset l; }")]
    public void Transpile_ConditionalUnsetOrPlusI_KeepsTheAttribute(string prefix, string write, string suffix)
        => Assert.True(WriteEvaluates(prefix, write, suffix), $"`{prefix}{write}{suffix}` stores the text:\n{T(prefix + write + suffix)}");

    /// <summary>An unconditional one in a function body holds for the rest of the body (bash: <c>8 2+2</c>).</summary>
    [Theory]
    [InlineData("f() { declare -i v=1; unset v; ", "v=2+2; ", "}")]
    [InlineData("f() { local -ai v=(1); unset v; ", "v=(2+2); ", "}")]
    [InlineData("f() { declare -i v=1; declare +i v; ", "v=2+2; ", "}")]
    [InlineData("declare -i n=1; unset n; ", "n=2+2", "")]
    public void Transpile_UnconditionalUnsetOrPlusI_DropsTheAttribute(string prefix, string write, string suffix)
        => Assert.False(WriteEvaluates(prefix, write, suffix), $"`{prefix}{write}{suffix}` still evaluates:\n{T(prefix + write + suffix)}");

    /// <summary>
    /// <c>declare -Ai</c> / <c>-iA</c> / <c>-gAi</c> with no value is associative. REGRESSION: only <c>-A</c> and <c>-gA</c>
    /// were, so <c>declare -Ai c; c[$w]+=1</c> summed every word into <c>c[0]</c> (bash 5.2: per key).
    /// </summary>
    [Theory]
    [InlineData("declare -Ai c")]
    [InlineData("declare -iA c")]
    [InlineData("declare -gAi c")]
    [InlineData("typeset -A -i c")]
    public void Transpile_BareAssociativeIntegerDeclaration_MakesATable(string bash)
        => Assert.Contains("[PsBash.Cmdlets.BashAssoc]::Unset()", T(bash));

    [Fact]
    public void Transpile_AssociativePrintForm_DeclaresNothing()
        => Assert.DoesNotContain("[PsBash.Cmdlets.BashAssoc]::Unset()", T("declare -Ap c"));

    /// <summary>
    /// Under <c>shopt -s lastpipe</c> the last stage runs in the shell: its <c>read -a</c> / <c>mapfile</c> into an integer
    /// array converts after the pipeline. REGRESSION: <c>echo "1+1 2*2" | read -a r</c> stored the text (bash 5.2: 2 4).
    /// </summary>
    [Theory]
    [InlineData("shopt -s lastpipe; declare -ai r; echo '1+1 2*2' | read -a r", "Invoke-BashRead")]
    [InlineData("shopt -s lastpipe; declare -ai r; printf '%s\\n' 1+1 | mapfile -t r", "Invoke-BashMapfile")]
    public void Transpile_LastpipeStageIntoAnIntegerArray_Converts(string bash, string builtin)
    {
        string ps = T(bash);
        int at = ps.IndexOf(builtin, StringComparison.Ordinal);
        Assert.True(at >= 0 && ps.IndexOf(PsBuild.IntegerElementsFunction, at, StringComparison.Ordinal) > at, ps);
    }

    /// <summary>
    /// A list write stores its elements one at a time, in written order, so each sees the ones before it (bash 5.2:
    /// <c>a=(1); a+=(a[0]+1 a[1]+1)</c> is <c>1 2 3</c>, <c>i=5; a=([2]=i++ [0]=i++)</c> is a[2]=5 a[0]=6). REGRESSION:
    /// the whole list was converted after it was built (<c>ConvertTo-BashIntegerArray</c>), in subscript order.
    /// </summary>
    [Theory]
    [InlineData("declare -ai a; a=(1 a[0]+1)")]
    [InlineData("declare -ai a=(1); a+=(a[0]+1 a[1]+1)")]
    [InlineData("declare -ai a; a=([2]=i++ [0]=i++)")]
    [InlineData("f() { local -ai a=(1 a[0]+1); }")]
    public void Transpile_IntegerListWrite_StoresElementByElement(string bash)
    {
        string ps = T(bash);
        Assert.True(Evaluates(ps), ps);
        Assert.DoesNotContain(PsBuild.IntegerElementsFunction, ps);
    }

    /// <summary>
    /// <c>mapfile -O N</c> converts only what it wrote: the array is kept before it, and the conversion is given the copy.
    /// REGRESSION: <c>a=(x y); declare -i a; mapfile -O 2 a &lt;&lt;&lt; 5+5</c> converted x and y too (bash 5.2: x y 10).
    /// </summary>
    [Fact]
    public void Transpile_MapfileWithAnOrigin_ConvertsOnlyWhatItWrote()
    {
        string ps = T("a=(x y); declare -i a; mapfile -t -O 2 a <<< 5+5");
        int before = ps.IndexOf(PsBuild.IntegerBeforeVariable + " = ", StringComparison.Ordinal);
        int mapfile = ps.IndexOf("Invoke-BashMapfile", StringComparison.Ordinal);
        Assert.True(before >= 0 && mapfile > before, ps);
        Assert.Contains("-Before " + PsBuild.IntegerBeforeVariable, ps[mapfile..]);
        Assert.DoesNotContain(PsBuild.IntegerBeforeVariable, T("declare -ai a; mapfile -t a <<< 5+5"));
    }

    /// <summary>
    /// Under <c>set -u</c> an unbound name in an integer write ends the command, element or scalar (bash 5.2: the error,
    /// then the shell exits 1). REGRESSION: it printed the error and the script went on.
    /// </summary>
    [Theory]
    [InlineData("set -u; declare -ai a; a=(1 y+1)", "$__psbash_av = Invoke-BashArith")]
    [InlineData("set -u; declare -ai a; a=([1]=y+1)", "$__psbash_av = Invoke-BashArith")]
    [InlineData("set -u; declare -i n; n=y+1", "$__psbash_ac = Invoke-BashArith")]
    [InlineData("set -u; declare -i n; n+=y", "$__psbash_ac = Invoke-BashArith")]
    [InlineData("set -u; declare -i n=y", "$__psbash_ac = Invoke-BashArith")]
    public void Transpile_IntegerWriteUnderNounset_EndsOnAnUnboundName(string bash, string guarded)
        => Assert.Contains(guarded, T(bash));

    /// <summary>
    /// REGRESSION: the conversion after <c>mapfile</c> / <c>read -a</c> was part of the command's own text, so a stdin
    /// redirect put it inside the redirect's <c>| &amp; { … }</c> block, where it converted a copy and left the array
    /// empty. It follows the whole statement.
    /// </summary>
    [Theory]
    [InlineData("declare -ai a; mapfile -t a < f", "Invoke-BashMapfile")]
    [InlineData("declare -ai a; mapfile -t a < <(echo 1+1)", "Invoke-BashMapfile")]
    [InlineData("declare -ai a; readarray a < f", "Invoke-BashMapfile")]
    [InlineData("declare -ai a; read -a a < f", "Invoke-BashRead")]
    public void Transpile_IntegerArrayBuiltinWithARedirect_ConvertsAfterTheRedirectBlock(string bash, string builtin)
    {
        string ps = T(bash);
        int at = ps.IndexOf(builtin, StringComparison.Ordinal);
        int conversion = ps.IndexOf(PsBuild.IntegerElementsFunction, at, StringComparison.Ordinal);
        Assert.True(at >= 0 && conversion > at, ps);
        // The builtin runs in the redirect's script block (`. { … }`, `| & { … }`); it closes before the conversion starts.
        Assert.True(ps[at..conversion].Contains('}'),
            $"The conversion runs inside the redirect block of `{bash}`:\n{ps}");
    }

    /// <summary>
    /// One emitted element write: <c>= Set-BashElement</c> appears only in <c>PsBuild.SetElement</c>, whose
    /// <c>integer</c> parameter has no default, so a new write site cannot forget the attribute.
    /// </summary>
    [Fact]
    public void ElementWrite_IsEmittedOnlyByPsBuildSetElement()
    {
        string dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllText(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || !line.Contains("= Set-BashElement", StringComparison.Ordinal)) continue;
                bool isPrimitive = Path.GetFileName(file) == "PsBuild.cs" && line.Contains("\" = Set-BashElement \" + TargetArg(target)", StringComparison.Ordinal);
                if (!isPrimitive) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "An element write is emitted outside PsBuild.SetElement, so it does not consult the integer attribute. "
            + "Build it with PsBuild.SetElement(…, integer: IsIntegerArray(name)). Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
