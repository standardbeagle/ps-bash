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
        || ps.Contains("$(Invoke-BashArith ([string](", StringComparison.Ordinal);

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
