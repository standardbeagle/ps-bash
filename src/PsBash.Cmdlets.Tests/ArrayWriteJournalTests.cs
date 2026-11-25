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
        // `sp=([5]=x); g() { sp+=(y); }; ( g )`: BashIndexedArray.Append appends to the sparse array itself.
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

    // `a=(0); { a+=(u); echo 1; } | while read x; do a+=(d); done; echo "${a[@]}"` prints 0 in bash: both stages are
    // subshells that started with a=(0). REGRESSION: the loop's journal kept (0 u) as its "before" and put it back last.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterleavedSiblingStages_PutBackTheValueFromBeforeThePipeline(bool upstreamEndsFirst)
    {
        var frames = new VarFrameStack();
        var upstream = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        var downstream = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        var a = new PSVariable("a", new object[] { "0" });
        upstream.Note(a, made: null);
        a.Value = new object[] { "0", "u" };
        downstream.Note(a, made: null);
        a.Value = new object[] { "0", "u", "d" };
        if (upstreamEndsFirst) { upstream.Undo(null); downstream.Undo(null); }
        else { downstream.Undo(null); upstream.Undo(null); }
        Assert.Equal(new object[] { "0" }, (object[])a.Value!);
        Assert.Empty(frames.OpenJournals);
    }

    [Fact]
    public void InterleavedSiblingStages_AnAssociativeArrayChangedInPlace_IsPutBackAsItWas()
    {
        // `declare -A m=([k]=0); { m[u]=1; echo 1; } | while read x; do m[d]=2; done`: bash leaves m with k only.
        var frames = new VarFrameStack();
        var upstream = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        var downstream = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        var m = new PSVariable("m", new BashAssoc { ["k"] = "0" });
        upstream.Note(m, made: null);
        ((BashAssoc)m.Value!)["u"] = "1";
        downstream.Note(m, made: null);
        ((BashAssoc)m.Value!)["d"] = "2";
        upstream.Undo(null);
        ((BashAssoc)m.Value!)["late"] = "3";   // the loop goes on after the upstream ended, writing what is there now
        downstream.Undo(null);
        var back = Assert.IsType<BashAssoc>(m.Value);
        Assert.Single(back);
        Assert.Equal("0", back["k"]);
    }

    [Fact]
    public void ANestedSubshell_PutsBackTheValueItStartedWith()
    {
        // `a=(0); ( a+=(o); ( a+=(i) ); echo "${a[@]}" )` prints "0 o": the inner subshell started after the outer's write.
        var frames = new VarFrameStack();
        var outer = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        var a = new PSVariable("a", new object[] { "0" });
        outer.Note(a, made: null);
        a.Value = new object[] { "0", "o" };
        var inner = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        inner.Note(a, made: null);
        a.Value = new object[] { "0", "o", "i" };
        inner.Undo(null);
        Assert.Equal(new object[] { "0", "o" }, (object[])a.Value!);
        outer.Undo(null);
        Assert.Equal(new object[] { "0" }, (object[])a.Value!);
    }

    [Fact]
    public void Clear_EndsTheLocalArraysAndTheOpenJournals()
    {
        var frames = new VarFrameStack();
        frames.Enter();
        frames.AddLocalArrays(["p"]);
        _ = new BashVariableLookup.ArrayWriteJournal(frames, 0);
        frames.Clear();
        Assert.False(frames.IsLocalArray("p"));
        Assert.Empty(frames.OpenJournals);
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
