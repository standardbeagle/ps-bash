using System.Management.Automation;
using System.Management.Automation.Runspaces;
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
public class ForeignFanOutAllocationTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ForeignFanOutAllocationTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The measured items must come from the REAL producer, not from
    /// <c>PSObject.AsPSObject(new FileInfo(...))</c>. A provider-emitted object
    /// already carries its instance note properties (<c>PSPath</c>,
    /// <c>PSProvider</c>, …), so its member collection is materialized; a
    /// synthetic wrapper's is not, and every probe against it pays to build one.
    /// Measured on the same code: 280 B per synthetic item vs 41 B per
    /// Get-ChildItem item for the identical BashText probe. Benchmarking the
    /// synthetic shape would have measured the wrapper, not the fan-out.
    /// </summary>
    internal static PSObject[] ForeignItems(PowerShell pwsh)
    {
        pwsh.Commands.Clear();
        var items = pwsh
            .AddScript($"Get-ChildItem -Recurse -File -LiteralPath '{AppContext.BaseDirectory.Replace("'", "''")}'")
            .Invoke()
            .ToArray();
        pwsh.Commands.Clear();
        Assert.True(items.Length > 100, $"expected a real fan-out to measure, got {items.Length} items");
        return items;
    }

    /// <summary>Per-item allocation of <paramref name="work"/>, per-THREAD (xunit
    /// runs classes in parallel, so the process-wide counter is polluted).</summary>
    private static long PerItemBytes(PSObject[] items, Action<PSObject> work)
    {
        foreach (var item in items) work(item);          // warm: JIT + ETS caches
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var item in items) work(item);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / items.Length;
    }

    /// <summary>
    /// The S4 acceptance bar, stated as a RATIO against the path it replaces
    /// rather than an absolute byte budget, and measured in the same session so
    /// both halves see the same JIT tier and the same warm ETS caches.
    ///
    /// An absolute budget was tried and rejected: the same code on the same
    /// objects measures 105 B/item in a warm long-running pwsh loop (the shape
    /// the host actually runs) and ~528 B/item in this short xunit loop, so any
    /// constant would either be unreachable here or vacuous there. The
    /// invariant that holds in both is the one worth pinning — probing for an
    /// override and calling the base object's own ToString must cost materially
    /// less than PowerShell's ETS string conversion.
    ///
    /// MUST run with a runspace attached to this thread: without one the cost
    /// profile inverts (no runspace: probe 232 B / ETS ToString 144 B; runspace:
    /// 232 B / 480 B), and the product only ever runs inside a runspace.
    /// </summary>
    [Fact]
    public void GetBashText_ForeignProducerObject_CostsMateriallyLessThanEtsStringConversion()
    {
        var pwsh = _fixture.AcquireFresh();
        var items = ForeignItems(pwsh);
        var previous = Runspace.DefaultRunspace;
        Runspace.DefaultRunspace = pwsh.Runspace;
        try
        {
            // The pre-S4 implementation, verbatim: probe for BashText, then hand
            // the whole PSObject to the ETS string converter.
            long ets = PerItemBytes(items, o =>
            {
                var p = o.Properties["BashText"];
                var _ = p != null ? p.Value?.ToString() : o.ToString();
            });
            long now = PerItemBytes(items, o => { var _ = BashRuntime.GetBashText(o); });

            // Bar is 85%, not the 74% actually measured here: a revert scores
            // 1.0 by construction (it would BE the `ets` lambda), so the bar only
            // has to sit below 1.0 with room for another environment's ratio to
            // differ the way pwsh's 0.25 and xunit's 0.74 already do.
            Assert.True(now <= ets * 0.85,
                $"GetBashText allocated {now} B per foreign pipeline object vs {ets} B for the " +
                "ETS path it replaces (bar: at most 85%). A foreign producer fanning into a " +
                "consumer cmdlet must not pay the ETS string-conversion machinery per line.");
        }
        finally
        {
            Runspace.DefaultRunspace = previous;
        }
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
