using System.Management.Automation;
using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A subshell's <see cref="BashVariableLookup.ArrayWriteJournal"/>: the arrays outside it that it wrote, put back when
/// it ends. Its size is bounded by the distinct outside arrays, not the writes (a cost invariant; the behavior is pinned
/// against bash by <c>Differential.Tests/ShellLanguageGapDifferentialTests</c>' subshell array tests).
/// </summary>
public class ArrayWriteJournalTests
{
    [Fact]
    public void ALocalArrayOfAFunctionCalledInside_IsNeverNoted()
    {
        // `seq N | while read n; do parse "$n"; done` with `parse() { local -a p; read -ra p <<< "$1"; }`: each call's
        // local is a new variable. REGRESSION: each was noted, and each note scanned the earlier ones (quadratic).
        var frames = new VarFrameStack();
        var journal = new BashVariableLookup.ArrayWriteJournal(frames, frames.Depth);
        for (int i = 0; i < 10_000; i++)
        {
            frames.Enter();
            frames.AddLocalArrays(["p"]);
            journal.Note(new PSVariable("p", new object[] { i.ToString() }), made: null);
            frames.Exit();
        }
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void ALocalOfAFunctionTheSubshellRunsIn_IsNotedOnce()
    {
        // `f() { local -a p=(o); ( p+=(i) ); echo "${p[@]}"; }`: the caller's local outlives the subshell, so it is put back.
        var frames = new VarFrameStack();
        frames.Enter();
        frames.AddLocalArrays(["p"]);
        var journal = new BashVariableLookup.ArrayWriteJournal(frames, frames.Depth);
        var p = new PSVariable("p", new object[] { "o" });
        for (int i = 0; i < 1_000; i++)
        {
            journal.Note(p, made: null);
            p.Value = new object[] { "o", i.ToString() };
        }
        Assert.Equal(1, journal.Count);
        journal.Undo(null);
        Assert.Equal(new object[] { "o" }, (object[])p.Value!);
    }

    [Fact]
    public void ManyOutsideArrays_EachNotedOnce_AndEachPutBack()
    {
        var frames = new VarFrameStack();
        var journal = new BashVariableLookup.ArrayWriteJournal(frames, frames.Depth);
        var vars = new PSVariable[500];
        for (int i = 0; i < vars.Length; i++) vars[i] = new PSVariable("a" + i, new object[] { "before" });
        for (int round = 0; round < 3; round++)
            foreach (var v in vars)
            {
                journal.Note(v, made: null);
                v.Value = new object[] { "after" + round };
            }
        Assert.Equal(vars.Length, journal.Count);
        journal.Undo(null);
        Assert.All(vars, v => Assert.Equal(new object[] { "before" }, (object[])v.Value!));
    }

    [Fact]
    public void AnAssociativeArrayChangedInPlace_IsPutBackAsItWas()
    {
        // `declare -A mm=([a]=1); fa() { mm[b]=2; }; ( fa )`: Set-BashElement writes the table itself.
        var table = new BashAssoc { ["a"] = "1" };
        var mm = new PSVariable("mm", table);
        var journal = new BashVariableLookup.ArrayWriteJournal(new VarFrameStack(), 0);
        journal.Note(mm, made: null);
        table["b"] = "2";
        journal.Undo(null);
        var back = Assert.IsType<BashAssoc>(mm.Value);
        Assert.Single(back);
        Assert.Equal("1", back["a"]);
    }

    [Fact]
    public void ASparseArrayAppendedInPlace_IsPutBackAsItWas()
    {
        // `sp=([5]=x); g() { sp+=(y); }; ( g )`: Add-BashArray appends to the sparse array itself.
        var sparse = new BashSparseArray();
        sparse[5L] = "x";
        var sp = new PSVariable("sp", sparse);
        var journal = new BashVariableLookup.ArrayWriteJournal(new VarFrameStack(), 0);
        journal.Note(sp, made: null);
        sparse[6L] = "y";
        journal.Undo(null);
        var back = Assert.IsType<BashSparseArray>(sp.Value);
        Assert.Single(back);
        Assert.Equal("x", back[5L]);
    }

    [Fact]
    public void AVariableThatCannotBePutBack_LeavesTheOthersPutBack()
    {
        var journal = new BashVariableLookup.ArrayWriteJournal(new VarFrameStack(), 0);
        var first = new PSVariable("first", new object[] { "1" });
        journal.Note(first, made: null);
        first.Value = new object[] { "changed" };
        var locked = new PSVariable("locked", new object[] { "x" });
        journal.Note(locked, made: null);
        locked.Options = ScopedItemOptions.ReadOnly;
        journal.Undo(null);
        Assert.Equal(new object[] { "1" }, (object[])first.Value!);
    }
}
