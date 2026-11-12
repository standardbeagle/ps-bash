using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`a[1 + 2]=x`: a subscript with blanks is still ONE assignment word. Oracle (bash 5.2): it assigns element 3.</summary>
public class SpacedSubscriptAssignTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_SpacedSubscript_IsElementAssignment() => Assert.Contains("Set-BashElement", T("a[1 + 2]=x"));
    [Fact] public void Transpile_SpacedSubscriptAppend_IsElementAssignment() => Assert.Contains("-Append", T("a[ i + 1 ]+=x"));
    // Not an assignment: no `=` after the `]`, so `a[1` stays a command word (bash runs it and fails with 127).
    [Fact] public void Transpile_SpacedSubscriptWithoutEquals_IsNotAssignment() => Assert.DoesNotContain("Set-BashElement", T("a[1 + 2] x"));
    [Fact] public void Transpile_ArgumentWordIsNotAnAssignment() => Assert.DoesNotContain("Set-BashElement", T("echo a[1 + 2]=x"));
}

/// <summary>`a[1]=v cmd`: an element prefix is not an environment binding; the command still runs and keeps its own status.</summary>
public class ElementPrefixAssignTests
{
    [Fact] public void Transpile_ElementPrefix_RunsTheCommandWithoutEnvWrap()
    {
        var ps = PsEmitter.Transpile("a[1]=v nosuchcmd")!;
        Assert.DoesNotContain("__psbash_ea_", ps);
        Assert.Contains("nosuchcmd", ps);
    }
}

/// <summary>`a[a[0]=1]=X` / `b[i++]=p`: a subscript that assigns runs before the array is read, so its write survives.
/// Oracle (bash 5.2): `a[a[0]=1]=X` leaves a=([0]="1" [1]="X"); ps-bash lost a[0]=1.</summary>
public class AssigningSubscriptTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("a[a[0]=1]=X")]
    [InlineData("b[i++]=p")]
    [InlineData("a[ a[0]+=1 ]+=X")]
    public void Transpile_AssigningSubscript_IsEvaluatedBeforeTheArrayIsRead(string bash)
    {
        var ps = T(bash);
        int eval = ps.IndexOf("Invoke-BashArith $__psbK", StringComparison.Ordinal);
        Assert.True(eval >= 0, ps);
        Assert.True(eval < ps.IndexOf("Set-BashElement", StringComparison.Ordinal), ps);
    }

    [Theory]
    [InlineData("a[1]=X")]
    [InlineData("a[i+1]=X")]
    [InlineData("a[i==1]=X")]
    public void Transpile_PlainSubscript_KeepsTheSingleCall(string bash) => Assert.DoesNotContain("$__psbK", T(bash));
}

/// <summary>`declare b[2]=y` declares b and assigns the element (the word was globbed and handed to declare: "not found").</summary>
public class ElementDeclarationTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("declare b[2]=y")]
    [InlineData("typeset -a t[i+1]=$v")]
    [InlineData("declare -A m[k]+=v")]
    [InlineData("declare a[a[0]=1]=X")]
    public void Transpile_DeclareElementInitializer_AssignsTheElement(string bash)
    {
        var ps = T(bash);
        Assert.Contains("Set-BashElement", ps);
        Assert.DoesNotContain("ConvertTo-BashGlob", ps);
    }

    // A declare operand is split at blanks before it is read as an assignment: `a[` and `]=Y` are invalid identifiers.
    [Fact] public void Transpile_DeclareSpacedSubscript_IsSplitAtBlanks()
    {
        var ps = T("declare a[ a[2]=3 ]=Y");
        Assert.Contains("`a['", ps);
        Assert.Contains("`]=Y'", ps);
    }

    [Fact] public void Transpile_DeclareReadonlyElement_RefusesWithoutDroppingTheLine()
    {
        var ps = T("declare -r ro[0]=1; echo next");
        Assert.Contains("ro: readonly variable", ps);
        Assert.Contains("Invoke-BashEcho", ps);
    }
}

/// <summary>A redirected compound runs in the current scope (dot-sourced): an array element assigned inside survives.</summary>
public class RedirectedCompoundScopeTests
{
    [Theory]
    [InlineData("{ b[1]=y; } 2>/dev/null")]
    [InlineData("if true; then a[1]=x; fi >out.txt")]
    public void Transpile_RedirectedCompound_IsDotSourced(string bash)
        => Assert.StartsWith(". {", PsEmitter.Transpile(bash)!);
}
