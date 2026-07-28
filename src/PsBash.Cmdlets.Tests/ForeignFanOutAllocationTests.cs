using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// S4 of the fan-out epic: a FOREIGN producer (an unmapped PowerShell command —
/// <c>Get-ChildItem</c>, <c>Invoke-SqlCmd</c>, a database query) fanning into one
/// of our consumer cmdlets used to pay the full PowerShell extended-type-system
/// string-conversion machinery for every line, inside
/// <see cref="BashRuntime.GetBashText"/>.
///
/// Measured on <c>Get-ChildItem -Recurse -File src | grep .cs</c> (the S0
/// foreign-producer shape): the ETS <c>PSObject.ToString()</c> fallback cost
/// ~385 B per item, while the wrapped object's OWN <c>ToString()</c> cost ~1 B.
/// The BashText probe itself was only ~41 B — the stringification, not the
/// property lookup, was the redundant work.
///
/// These tests pin BOTH halves of the trade: the allocation budget (Directive 2 —
/// numbers or it did not happen) AND the byte-for-byte equivalence of the fast
/// path with the ETS path it replaces, including the shapes that must KEEP the
/// ETS path (empty base, enumerable base, an instance/ETS ToString override).
/// </summary>
public class ForeignFanOutAllocationTests
{
    private static PSObject[] ForeignItems(int count)
    {
        var items = new PSObject[count];
        for (int i = 0; i < count; i++)
        {
            // A FileInfo needs no disk access to construct or to ToString() —
            // this is exactly the object Get-ChildItem fans out, with none of
            // its I/O.
            items[i] = PSObject.AsPSObject(
                new FileInfo(Path.Combine(Path.GetTempPath(), $"psb-s4-{i}.cs")));
        }
        return items;
    }

    /// <summary>
    /// The S4 acceptance bar. Budget is 200 B/item: the pre-S4 cost measured on
    /// this exact shape was ~430 B/item and the post-S4 cost ~105 B/item, so the
    /// bar sits clear of both. Uses the per-THREAD allocation counter, not the
    /// process-wide one — xunit runs test classes in parallel and the
    /// process-wide counter would be polluted by every other test.
    /// </summary>
    [Fact]
    public void GetBashText_ForeignProducerObject_StaysUnderPerItemAllocationBudget()
    {
        var items = ForeignItems(2000);

        foreach (var item in items) BashRuntime.GetBashText(item);   // warm: JIT + ETS caches

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var item in items) BashRuntime.GetBashText(item);
        long perItem = (GC.GetAllocatedBytesForCurrentThread() - before) / items.Length;

        Assert.True(perItem < 200,
            $"GetBashText allocated {perItem} B per foreign pipeline object (budget 200 B). " +
            "A foreign producer fanning into a consumer cmdlet must not pay the ETS " +
            "string-conversion machinery per line.");
    }

    /// <summary>
    /// Oracle for the fast path: whatever <c>GetBashText</c> returns for an
    /// object with no BashText must be byte-identical to what the ETS
    /// <c>PSObject.ToString()</c> returned before S4. This covers the three
    /// shapes that must KEEP the ETS path — an empty base (renders
    /// <c>@{a=1}</c>), an enumerable base (ETS unravels to space-joined
    /// elements), and a string base — alongside the plain CLR ones.
    /// </summary>
    public static TheoryData<object> ForeignShapes() => new()
    {
        new FileInfo(Path.Combine(Path.GetTempPath(), "psb-s4-shape.cs")),
        new DirectoryInfo(Path.GetTempPath()),
        42,
        3.5,
        true,
        new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new Version(1, 2, 3),
        "plain string",
        new object[] { 1, 2, 3 },
        new List<string> { "a", "b" },
        new System.Collections.Hashtable { { "k", "v" } },
    };

    [Theory]
    [MemberData(nameof(ForeignShapes))]
    public void GetBashText_ForeignShape_MatchesEtsStringConversion(object value)
    {
        var pso = PSObject.AsPSObject(value);
        Assert.Equal(pso.ToString(), BashRuntime.GetBashText(pso));
    }

    [Fact]
    public void GetBashText_EmptyBasePSObject_MatchesEtsStringConversion()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("a", 1));
        pso.Properties.Add(new PSNoteProperty("b", "two"));

        Assert.Equal(pso.ToString(), BashRuntime.GetBashText(pso));
    }

    // The instance-ToString-override case (Add-Member / Set-BashDisplayProperty)
    // lives in ForeignFanOutPipelineTests: a PSScriptMethod cannot be invoked
    // outside a runspace (ScriptBlock.GetContextFromTLS throws), so it can only
    // be exercised through the fixture.

    [Fact]
    public void GetBashText_BashTextProperty_StillWinsOverToString()
    {
        var obj = BashRuntime.NewBashObject("payload", "PsBash.CatLine");
        Assert.Equal("payload", BashRuntime.GetBashText(obj));
    }

    /// <summary>
    /// A raw CLR object (parameter binding unwrapped its PSObject before the
    /// call) still stringifies the same way.
    /// </summary>
    [Fact]
    public void GetBashText_UnwrappedClrObject_Stringifies()
    {
        var fi = new FileInfo(Path.Combine(Path.GetTempPath(), "psb-s4-raw.cs"));
        Assert.Equal(fi.ToString(), BashRuntime.GetBashText(fi));
    }
}
