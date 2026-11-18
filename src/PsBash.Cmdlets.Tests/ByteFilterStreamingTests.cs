using System.Text.RegularExpressions;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A byte filter (base64, od) reads a file operand a chunk at a time and writes its output as it goes
/// (<see cref="ByteFilterStreaming"/>, docs/specs/runtime-functions.md "Byte filters stream"), so a downstream
/// <c>head</c> stops it. REGRESSION: both read the whole operand before writing a byte - <c>base64 /dev/urandom | head
/// -c 32</c> never ended, <c>od -v /dev/zero</c> refused ("give -N BYTES") and <c>cat /dev/zero | od -v | head -1</c>
/// never ended. Oracle note (qa-rubric Directive 1): the bash-visible results are pinned by
/// ByteFilterStreamingDifferentialTests; these bound the same pipelines by the clock so a hang fails one test.
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class ByteFilterStreamingTests : IClassFixture<SharedPwshFixture>
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private readonly SharedPwshFixture _fixture;

    public ByteFilterStreamingTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string[] RunBash(string bash, string what)
    {
        var pwsh = _fixture.AcquireFresh();
        var output = BoundedPipeline.Invoke(pwsh, BashTranspiler.Transpile(bash)!, Bound, what);
        Assert.True(pwsh.Streams.Error.Count == 0, string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString())));
        return output.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Base64_OfAnEndlessDevice_IntoHead_Ends()
    {
        var lines = RunBash("base64 /dev/zero | head -2", "`base64 /dev/zero | head -2` (head must stop base64)");
        Assert.Equal([new string('A', 76), new string('A', 76)], lines);
    }

    [Fact]
    public void Base64Unwrapped_OfAnEndlessDevice_IntoHeadC_Ends()
    {
        var lines = RunBash("base64 -w0 /dev/zero | head -c 10", "`base64 -w0 /dev/zero | head -c 10` (one endless line, written in pieces)");
        Assert.Equal("AAAAAAAAAA", string.Concat(lines));
    }

    [Fact]
    public void Od_OfAnEndlessDevice_IntoHead_Ends()
    {
        var lines = RunBash("od -v /dev/zero | head -1", "`od -v /dev/zero | head -1`");
        Assert.Equal(["0000000 000000 000000 000000 000000 000000 000000 000000 000000"], lines);
    }

    [Fact]
    public void Od_OfAnEndlessPipe_IntoHead_Ends()
    {
        var lines = RunBash("cat /dev/zero | od -v | head -1", "`cat /dev/zero | od -v | head -1` (od formats stdin as it arrives)");
        Assert.Equal(["0000000 000000 000000 000000 000000 000000 000000 000000 000000"], lines);
    }

    [Fact]
    public void Od_WithALimit_OfAnEndlessDevice_StopsReading()
    {
        var lines = RunBash("od -An -tx1 -N 4 /dev/zero", "`od -N 4 /dev/zero` (stops reading after 4 bytes)");
        Assert.Equal([" 00 00 00 00"], lines);
    }
}

/// <summary>
/// The incremental formatters give the same text for any chunking of the input: what a byte filter writes cannot depend
/// on where <see cref="ByteFilterStreaming.Pump"/> happened to split a read. Oracle note (qa-rubric Directive 1): a
/// pure-function invariant; the whole-input output is the reference.
/// </summary>
public class ByteFilterChunkingTests
{
    private static string Render(List<object> records)
        => string.Concat(records.Select(r => BashRuntime.GetBashText(r) + (BashRuntime.IsUnterminated(r) ? "" : "\n")));

    [Fact]
    public void Base64Unwrapped_LongInput_IsWrittenInPieces_ThatJoinToTheEncoding()
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data);
        var records = new List<object>();
        var enc = new Base64LineEncoder(0, records.Add);
        for (int at = 0; at < data.Length; at += 4096) enc.Append(data.AsSpan(at, Math.Min(4096, data.Length - at)));
        Assert.True(records.Count >= 3, "-w0 must write while the input arrives, not hold one line of the whole input");
        enc.Finish();
        Assert.Equal(Convert.ToBase64String(data), Render(records));
        Assert.All(records, r => Assert.True(BashRuntime.IsUnterminated(r)));
        Assert.All(records.Take(records.Count - 1), r => Assert.True(BashRuntime.GetBashText(r).Length >= Base64LineEncoder.UnwrappedPiece));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-v")]
    [InlineData("-c")]
    [InlineData("-An -tx4")]
    [InlineData("-Ad -tx1 -w10")]
    [InlineData("-j 5 -N 1000 -tu2")]
    [InlineData("-to1 -tc")]
    public void OdLineWriter_AnyChunking_GivesTheWholeInputsLines(string args)
    {
        var plan = OdFormat.Plan(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Null(plan.Error);
        // Runs of equal lines (folded to `*` without -v), a short last line, bytes of every value.
        var data = new byte[3000];
        new Random(11).NextBytes(data.AsSpan(1000, 900));
        for (int i = 2500; i < data.Length; i++) data[i] = (byte)i;

        List<string> Run(int chunk)
        {
            var lines = new List<string>();
            var writer = new OdLineWriter(plan, lines.Add);
            for (int at = 0; at < data.Length && !writer.Done; at += chunk)
                writer.Append(data.AsSpan(at, Math.Min(chunk, data.Length - at)));
            writer.Finish();
            return lines;
        }

        var whole = Run(data.Length);
        Assert.NotEmpty(whole);
        foreach (var chunk in new[] { 1, 3, 16, 17, 1000 })
            Assert.Equal(whole, Run(chunk));
    }
}

/// <summary>
/// The byte filters that read a file operand read it through <see cref="ByteFilterStreaming.Pump"/>, never whole: this
/// scan fails on a whole-input read (<c>ReadAllBytes</c>, a copy into a <see cref="MemoryStream"/>, <c>ReadToEnd</c>) in
/// their cmdlets, the shape of the bug that made <c>base64 /dev/urandom | head</c> and <c>od -v /dev/zero</c> never end.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ByteFilterStreamingGuardTests
{
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

    private static readonly Regex WholeRead = new(
        @"ReadAllBytes\s*\(|ReadAllText\s*\(|\.ReadToEnd\s*\(|\.CopyTo\s*\(|new\s+MemoryStream\s*\(\s*\)\s*;[^\n]*CopyTo",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("InvokeBashBase64Command.cs")]
    [InlineData("InvokeBashOdCommand.cs")]
    public void ByteFilter_ReadsItsFileOperandsAsAStream(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", file));
        Assert.Contains("ByteFilterStreaming.Pump(", text, StringComparison.Ordinal);
        var hits = WholeRead.Matches(text).Select(m => m.Value).ToList();
        Assert.True(hits.Count == 0, $"{file} reads an operand whole ({string.Join(", ", hits)}): use ByteFilterStreaming.Pump");
    }
}
