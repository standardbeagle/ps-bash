using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

// The cache is an optimization layered over the pure transpiler. Its ONE non-negotiable property is
// parity: a cached transpile must be byte-identical to a direct transpile for the same (content,
// context, path-mode, build). These tests pin that, plus the LRU bound and error pass-through.
public class TranspileCacheTests
{
    public static readonly TheoryData<string> Inputs = new()
    {
        "echo hello",
        "ls -la | grep .txt",
        "for i in 1 2 3; do echo $i; done",
        "cat a.txt > b.txt 2>&1",
        "x=1; y=2; echo $((x + y))",
        // A longer, multi-line script (exercises the in-memory tier's length gate, too).
        "function greet() {\n  local name=$1\n  echo \"hi $name\"\n}\ngreet world\ngreet again\n",
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void GetOrTranspileFile_MatchesDirectTranspile(string bash)
    {
        var expected = BashTranspiler.Transpile(bash);
        // Call twice: first populates the on-disk entry, second is a cache hit — both must match.
        Assert.Equal(expected, TranspileCache.GetOrTranspileFile(bash));
        Assert.Equal(expected, TranspileCache.GetOrTranspileFile(bash));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void GetOrTranspileMemory_MatchesDirectTranspile(string bash)
    {
        var expected = BashTranspiler.Transpile(bash);
        Assert.Equal(expected, TranspileCache.GetOrTranspileMemory(bash)); // miss → transpile + store
        Assert.Equal(expected, TranspileCache.GetOrTranspileMemory(bash)); // hit (if over the length gate)
    }

    [Fact]
    public void GetOrTranspileFile_ContextIsPartOfTheKey_NeverCrossServed()
    {
        // Find an input that genuinely emits differently per context in this build (Eval disables the
        // PS-builtin-alias short-circuit). If the cache key omitted the context, warming Default then
        // requesting Eval would wrongly return the Default PowerShell.
        string[] candidates =
        {
            "ls", "pwd", "echo hi", "cat f", "sort data.txt", "diff a b", "cp a b", "rm x", "sleep 1",
        };
        foreach (var bash in candidates)
        {
            var def = BashTranspiler.Transpile(bash, TranspileContext.Default);
            var eval = BashTranspiler.Transpile(bash, TranspileContext.Eval);
            if (def == eval) continue; // not context-sensitive in this build — keep looking

            // Warm the Default entry first, then ask for Eval — must get the Eval emission, not Default.
            Assert.Equal(def, TranspileCache.GetOrTranspileFile(bash, TranspileContext.Default));
            Assert.Equal(eval, TranspileCache.GetOrTranspileFile(bash, TranspileContext.Eval));
            return; // proved it on a genuinely context-sensitive input
        }

        // No candidate differs by context in this build — the cross-serve check isn't exercisable, but
        // parity must still hold for both contexts on the same input.
        Assert.Equal(BashTranspiler.Transpile("echo hi", TranspileContext.Eval),
            TranspileCache.GetOrTranspileFile("echo hi", TranspileContext.Eval));
    }

    [Fact]
    public void GetOrTranspileMemory_OverCapacity_EvictsButStaysCorrect()
    {
        // Push well past the 64-entry cap with distinct long scripts; every lookup must still match a
        // direct transpile (a stale/evicted entry must never return another script's PowerShell).
        for (int i = 0; i < 200; i++)
        {
            var bash = $"# script {i}\n" + string.Concat(System.Linq.Enumerable.Repeat($"echo line{i}\n", 30));
            Assert.Equal(BashTranspiler.Transpile(bash), TranspileCache.GetOrTranspileMemory(bash));
        }
    }

    [Fact]
    public void GetOrTranspileFile_ParseError_PropagatesAndIsNotCached()
    {
        const string broken = "for i in"; // incomplete — the parser rejects it
        Assert.Throws<ParseException>(() => TranspileCache.GetOrTranspileFile(broken));
        // A second attempt must still throw (the failure was never stored as a cache hit).
        Assert.Throws<ParseException>(() => TranspileCache.GetOrTranspileFile(broken));
    }
}
