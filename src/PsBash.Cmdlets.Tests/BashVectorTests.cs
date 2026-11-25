using PsBash.Cmdlets;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashVector"/>, the dense array a script grows or writes to. It must read back what was written at every
/// size (the tail, one trie level, deeper levels), leave every older copy as it was, and cost O(1) amortized per append
/// and O(log32 n) per write. The cost tests count allocated bytes, not time, so they hold on a loaded machine.
/// REGRESSION: `a+=(x)` copied the whole object[] per append and `a[i]=v` copied it through a List per write, so both
/// loops were O(n²): 40k appends took 17 s where bash takes well under 0.1 s.
/// </summary>
public class BashVectorTests
{
    private static BashVector Build(int n)
    {
        var v = BashVector.Empty;
        for (int i = 0; i < n; i++) v = v.Append(i);
        return v;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(1056)]
    [InlineData(32 * 32 * 32 + 33)]   // a third trie level
    public void Append_ReadsBackEveryElement_InOrder(int n)
    {
        var v = Build(n);
        Assert.Equal(n, v.Count);
        for (int i = 0; i < n; i++) Assert.Equal(i, v[i]);
        Assert.Equal(Enumerable.Range(0, n).Cast<object?>(), v);
        Assert.Equal(Enumerable.Range(0, n).Cast<object?>(), v.ToArray());
    }

    [Fact]
    public void AppendRange_EqualsOneAppendPerItem_FromAnySize()
    {
        foreach (int start in new[] { 0, 5, 32, 40, 1030 })
        {
            var a = Build(start).AppendRange(Enumerable.Range(start, 2000).Cast<object>().ToArray());
            Assert.Equal(Enumerable.Range(0, start + 2000).Cast<object?>(), a);
        }
    }

    [Fact]
    public void Index_Negative_CountsBackFromTheEnd_AndOutOfRange_IsNull()
    {
        var v = Build(40);
        Assert.Equal(39, v[-1]);
        Assert.Equal(0, v[-40]);
        Assert.Null(v[-41]);
        Assert.Null(v[40]);
    }

    [Fact]
    public void AnOlderCopy_DoesNotSeeAppendsToANewerOne_NorTheOtherWay()
    {
        // `( a+=(x) )` / a saved scope: both copies hold the same vector, and each append must stay in its own copy.
        foreach (int n in new[] { 3, 31, 32, 64, 1000 })
        {
            var shared = Build(n);
            var child = shared.Append("child");
            var parent = shared.Append("parent");
            var grand = child.Append("grand");
            Assert.Equal(n, shared.Count);
            Assert.Equal("child", child[n]);
            Assert.Equal("parent", parent[n]);
            Assert.Equal(new object?[] { "child", "grand" }, grand.Skip(n));
            Assert.Equal(Enumerable.Range(0, n).Cast<object?>(), shared);
        }
    }

    [Fact]
    public void SetItem_ChangesOnlyTheNewCopy_InTheTailAndInTheTrie()
    {
        var v = Build(2000);
        var tailWrite = v.SetItem(1999, "t");
        var trieWrite = v.SetItem(7, "r");
        Assert.Equal(1999, v[1999]);
        Assert.Equal(7, v[7]);
        Assert.Equal("t", tailWrite[1999]);
        Assert.Equal("r", trieWrite[7]);
        Assert.Equal(7, tailWrite[7]);
        // Appending to a written copy keeps the write and the shared original.
        var after = trieWrite.Append("z");
        Assert.Equal("r", after[7]);
        Assert.Equal("z", after[2000]);
        Assert.Equal(2000, v.Count);
    }

    [Fact]
    public void RandomWritesAndAppends_MatchAPlainList()
    {
        var rng = new Random(11);
        var reference = new List<object?>();
        var v = BashVector.Empty;
        var snapshots = new List<(BashVector V, object?[] Expected)>();
        for (int step = 0; step < 20_000; step++)
        {
            if (reference.Count == 0 || rng.Next(3) > 0) { v = v.Append(step); reference.Add(step); }
            else { int i = rng.Next(reference.Count); v = v.SetItem(i, -step); reference[i] = -step; }
            if (step % 997 == 0) snapshots.Add((v, reference.ToArray()));
        }
        Assert.Equal(reference, v);
        foreach (var (snap, expected) in snapshots) Assert.Equal(expected, snap);
    }

    [Fact]
    public void Append_ToALargeVector_AllocatesNoCopyOfIt()
    {
        var v = Build(100_000);
        v = v.Append(-1);   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) v = v.Append(i);
        long perAppend = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        // A vector object per append plus a node per 32: a few dozen bytes. A copy would be 800 KB.
        Assert.True(perAppend < 200, $"an append allocated {perAppend} bytes");
    }

    [Fact]
    public void SetElement_OnALargeVector_CopiesOnePath()
    {
        var v = Build(100_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) v = BashIndexedArray.SetElement(v, i * 37 % 99_000, "x", append: false);
        long perWrite = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        // At most four 32-slot nodes (~280 bytes each) and a vector object.
        Assert.True(perWrite < 1400, $"an element write allocated {perWrite} bytes");
    }

    [Fact]
    public void Append_FromAnObjectArray_OrScalar_OrNull_FlattensLikeConcat()
    {
        Assert.Equal(new object?[] { "a", "b", "c" }, (BashVector)BashIndexedArray.Append(new object?[] { "a", "b" }, new object?[] { "c" }));
        Assert.Equal(new object?[] { "s", "x" }, (BashVector)BashIndexedArray.Append("s", new object?[] { "x" }));
        Assert.Equal(new object?[] { "x" }, (BashVector)BashIndexedArray.Append(null, new object?[] { "x" }));
    }

    [Fact]
    public void Append_ToASparseArray_GoesAfterItsHighestSubscript()
    {
        var sparse = BashSparseArray.FromList(new object?[] { "a" });
        sparse[(object)5L] = "f";
        Assert.Same(sparse, BashIndexedArray.Append(sparse, new object?[] { "g" }));
        Assert.Equal("g", sparse[(object)6L]);
    }

    [Fact]
    public void SetElement_PlusEqual_AppendsToTheElementsText()
    {
        var v = BashIndexedArray.ToVector(new object?[] { "ab" });
        Assert.Equal("abc", BashIndexedArray.SetElement(v, 0, "c", append: true)[0]);
        Assert.Equal("z", BashIndexedArray.SetElement(v, 1, "z", append: true)[1]);
        Assert.Equal("ab", v[0]);
    }
}

/// <summary>The vector through the emitted script: what a bash script sees. Each expected output is bash 5.3's for the same script.</summary>
[Collection(WallClockBoundCollection.Name)]
public class BashVectorScriptTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public BashVectorScriptTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string Run(string bash)
    {
        var pwsh = _fixture.AcquireFresh();
        var output = BoundedPipeline.Invoke(pwsh, BashTranspiler.Transpile(bash)!, TimeSpan.FromSeconds(60), bash);
        Assert.True(pwsh.Streams.Error.Count == 0, string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString())));
        return string.Join("\n", output.Select(o => o?.ToString() ?? ""));
    }

    [Theory]
    [InlineData("a=(); for ((i=0;i<100;i++)); do a+=($i); done; echo ${#a[@]} ${a[0]} ${a[99]} ${a[-1]} \"${a[@]:97}\"", "100 0 99 99 97 98 99")]
    [InlineData("a=(x); ( a+=(y); echo ${#a[@]} ); echo ${#a[@]} \"${a[*]}\"", "2\n1 x")]
    [InlineData("a=(); for ((i=0;i<40;i++)); do a+=($i); done; b=(\"${a[@]}\"); a[3]=q; echo ${a[3]} ${b[3]} ${#b[@]}", "q 3 40")]
    [InlineData("a=(1 2); a[1]+=z; a[2]=n; echo \"${a[@]}\" ${#a[@]}", "1 2z n 3")]
    [InlineData("a=(); a+=(p); a[5]=q; echo ${#a[@]} \"${!a[@]}\"", "2 0 5")]
    [InlineData("a=(); for i in 1 2 3; do a+=(\"$i x\"); done; for e in \"${a[@]}\"; do echo \"[$e]\"; done", "[1 x]\n[2 x]\n[3 x]")]
    [InlineData("f() { local -a l=(o); l+=(i); echo ${#l[@]}; }; f; a=(m); a+=(n); a=z; echo \"${a[@]}\"", "2\nz n")]
    [InlineData("s=world; s+=(x); echo \"${s[@]}\" ${#s[@]}", "world x 2")]
    public void AGrownArray_ReadsAsBash(string bash, string expected) => Assert.Equal(expected, Run(bash));
}
