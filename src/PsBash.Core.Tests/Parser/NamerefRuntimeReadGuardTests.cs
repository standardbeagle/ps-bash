using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// GUARD: a nameref whose target is known only at run time (<c>declare -n r; r=$v</c>, <c>declare -n r=$v</c>,
/// <c>local -n r=$1</c>) is read through the run time in EVERY expansion form, never from r's own storage. The scalar
/// forms apply their operator to <c>PsBuild.ReadThroughNameref</c>; the array forms read <c>$r</c>, which follows the
/// reference (<c>BashVariableLookup.ViewNameref</c>, made by the declaration, dot-sourced so the block holding it keeps
/// no local slot for <c>$r</c>). REGRESSION: <c>declare -n r=$v</c> / <c>local -n r=$1</c> were plain variables holding
/// the value; <c>${r:=x}</c> emitted an assignment to a <c>$( )</c> (a parse error that lost the whole script),
/// <c>${!r}</c> expanded r's value, <c>${r?}</c> named nobody, <c>${r@a}</c> read r's attributes; in a subshell or function
/// that also assigned <c>$r</c> (<c>r+=(z)</c>), <c>${r[@]}</c> read a copy taken when r was declared. A new form, or a
/// new way to declare one, that reads r's own variable fails here.
/// </summary>
public class NamerefRuntimeReadGuardTests
{
    // Every way the emitter meets such a name; {0} is the code that uses it.
    private static readonly string[] Declarations =
    [
        "declare -n r; v=tg; r=$v; {0}",   // named by its first write
        "v=tg; declare -n r=$v; {0}",      // declared with a target the emitter cannot see
        "f() {{ local -n r=$1; {0}; }}",   // a function's reference from a parameter
    ];

    private const string ReadThrough = "[PsBash.Cmdlets.BashVariableLookup]::ReadThroughNameref($ExecutionContext.SessionState, 'r')";
    private const string View = ". { [PsBash.Cmdlets.BashVariableLookup]::ViewNameref($ExecutionContext.SessionState, 'r') }";

    // r's own scalar storage, under any spelling the emitter has for it.
    private static readonly Regex OwnScalar = new(@"\$\{?env:r\b|BashVariableStore\]::(Get|Set)\('r'", RegexOptions.Compiled);

    private static string T(int declaration, string bash) => PsEmitter.Transpile(string.Format(Declarations[declaration], bash))!;

    // The code reads r's own storage nowhere the declaration itself does not (a function's local saves and restores r).
    private static void AssertNoOwnRead(int declaration, string ps)
        => Assert.Equal(OwnScalar.Matches(T(declaration, ":")).Count, OwnScalar.Matches(ps).Count);

    private static TheoryData<int, string> Cross(params string[] bodies)
    {
        var data = new TheoryData<int, string>();
        for (int d = 0; d < Declarations.Length; d++)
            foreach (var body in bodies) data.Add(d, body);
        return data;
    }

    public static TheoryData<int, string> ScalarForms => Cross(
        "echo $r",
        "echo \"${r}\"",
        "echo ${r:-x} ${r-x} ${r:+y} ${r+y}",
        "echo ${#r}",
        "echo ${r#h} ${r##*o} ${r%d} ${r%%o*}",
        "echo ${r/o/0} ${r//o/0} ${r/#h/H} ${r/%d/D}",
        "echo ${r^^} ${r^} ${r,,} ${r,}",
        "echo ${r@Q} ${r@U} ${r@L} ${r@E}",
        "echo ${r:1:3} ${r: -2}",
        "echo \"${r:-$v}\"");

    public static TheoryData<int, string> ArrayForms => Cross(
        "echo \"${r[@]}\"",
        "echo ${r[1]}",
        "echo ${#r[@]}",
        "echo ${!r[@]}",
        "echo ${r[@]:1}",
        "echo \"${r[@]/b/B}\"",
        "for e in \"${r[@]}\"; do :; done",
        "x=(\"${r[@]}\")",
        "r+=(z); r[0]=A; echo \"${r[@]}\"");

    public static TheoryData<int, string> AssignDefaultForms => Cross("echo ${r:=d}", "echo ${r=d}");
    public static TheoryData<int, string> ErrorForms => Cross("echo ${r?}", "echo ${r:?boom}");
    public static TheoryData<int, string> IndirectForm => Cross("echo ${!r}");
    public static TheoryData<int, string> AttributesForm => Cross("echo ${r@a}");

    [Theory]
    [MemberData(nameof(ScalarForms))]
    public void Transpile_ScalarFormOfARuntimeNameref_ReadsThroughTheRunTime(int declaration, string bash)
    {
        string ps = T(declaration, bash);
        Assert.Contains(ReadThrough, ps);
        AssertNoOwnRead(declaration, ps);
    }

    [Theory]
    [MemberData(nameof(ArrayForms))]
    public void Transpile_ArrayFormOfARuntimeNameref_ReadsTheVariableThatFollowsIt(int declaration, string bash)
    {
        string ps = T(declaration, bash);
        Assert.Contains("$r", ps);
        Assert.Contains(View, ps);
        AssertNoOwnRead(declaration, ps); // a scalar fallback (`${r[0]}` of a scalar target) reads through the run time too
    }

    [Theory]
    [MemberData(nameof(IndirectForm))]
    public void Transpile_IndirectOfARuntimeNameref_IsTheNameItStandsFor(int declaration, string bash)
    {
        string ps = T(declaration, bash);
        Assert.Contains("[PsBash.Cmdlets.BashVariableLookup]::NamerefName('r')", ps);
        Assert.Contains("invalid indirect expansion", ps);
        Assert.DoesNotContain("Get-BashIndirectValue", ps);
    }

    [Theory]
    [MemberData(nameof(AssignDefaultForms))]
    public void Transpile_AssignDefaultOfARuntimeNameref_WritesThroughIt(int declaration, string bash)
    {
        var assign = new Regex(Regex.Escape("[PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'r', "));
        string ps = T(declaration, bash);
        Assert.Equal(1, assign.Matches(ps).Count - assign.Matches(T(declaration, ":")).Count);
        AssertNoOwnRead(declaration, ps);
        Assert.DoesNotContain(ReadThrough + " = ", ps);
    }

    [Theory]
    [MemberData(nameof(ErrorForms))]
    public void Transpile_ErrorFormOfARuntimeNameref_NamesTheReference(int declaration, string bash)
        => Assert.Contains("'r: '", T(declaration, bash));

    [Theory]
    [MemberData(nameof(AttributesForm))]
    public void Transpile_AttributesOfARuntimeNameref_AreThoseOfItsTarget(int declaration, string bash)
        => Assert.Contains("Attributes(([PsBash.Cmdlets.BashShellState]::ResolveNameref('r'))", T(declaration, bash));
}
