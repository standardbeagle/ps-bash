using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>sed</c> must not collect its input: pipeline records flow through the engine as they arrive (a quit stops the
/// upstream), file operands are read through a streaming reader (no whole-file cap), and <c>-i</c> streams into a
/// temp file that replaces the original. Goes through <c>Invoke-BashSed</c> as the module exposes it (the psm1 proxy
/// in front of the cmdlet).
/// </summary>
public class SedStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public SedStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var task = Task.Run(() => pwsh.AddScript(script).Invoke());
        Assert.True(task.Wait(TimeSpan.FromSeconds(120)), "sed did not terminate");
        pwsh.Commands.Clear();
        return task.Result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), "psb_sedstream_" + Guid.NewGuid().ToString("N").Substring(0, 8) + name);

    [Fact]
    public void Pipeline_EmitsWhileProducerIsStillRunning()
    {
        // P<n> = producer emitted n, T<x> = sed output reached the consumer. A collect-then-run sed logs
        // P1..P4 before any T; the streaming one is one record behind (the last output record is held back
        // until its terminator state is known).
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..4 | ForEach-Object { $log.Add(\"P$_\"); \"a$_\" } } | Invoke-BashSed 's/a/X/' | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        Assert.Equal(new[] { "P1 P2 TX1 P3 TX2 P4 TX3 TX4" }, log);
    }

    [Fact]
    public void Quit_StopsTheUpstream()
    {
        var r = Run(
            "$s = Get-Command Invoke-BashSed -CommandType Cmdlet; $global:gn = 0; " +
            "$o = @(& { while ($true) { $global:gn++; \"x$($global:gn)\" } } | & $s 2q | ForEach-Object { $_.ToString().Trim() }); " +
            "($o -join ',') + ' ' + ($global:gn -lt 50)");
        Assert.Equal(new[] { "x1,x2 True" }, r);
    }

    [Fact]
    public void Proxy_Quit_DrainsInsteadOfKillingTheStatement()
    {
        var r = Run("$o = @(1..50 | ForEach-Object { \"x$_\" } | Invoke-BashSed 3q | ForEach-Object { $_.ToString().Trim() }); $o -join ','");
        Assert.Equal(new[] { "x1,x2,x3" }, r);
    }

    [Fact]
    public void LastLineAddress_And_N_WorkWithoutKnowingTheEndInAdvance()
    {
        var r = Run("'a','b','c','d','e' | Invoke-BashSed '$!N;s/\\n/-/' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "a-b", "c-d", "e" }, r);
    }

    [Fact]
    public void RangeRegex_StreamsAcrossRecords()
    {
        var r = Run("1..9 | ForEach-Object { \"l$_\" } | Invoke-BashSed -n '/l3/,/l5/p' | ForEach-Object { $_.ToString().Trim() }");
        Assert.Equal(new[] { "l3", "l4", "l5" }, r);
    }

    [Fact]
    public void BigFile_AboveTheWholeDocumentCap_Streams()
    {
        // 20 MB of text: the old ReadAllText path threw "Whole-document text read exceeds 16777216 characters".
        string path = TempFile("big.txt");
        try
        {
            using (var w = new StreamWriter(path))
            {
                for (int i = 1; i <= 500_000; i++) w.Write("line-" + i + "-padding-padding-padding-pad\n");
            }
            Assert.True(new FileInfo(path).Length > 16 * 1024 * 1024);
            var r = Run($"Invoke-BashSed -n '$p' '{path}' | ForEach-Object {{ $_.ToString().Trim() }}");
            Assert.Equal(new[] { "line-500000-padding-padding-padding-pad" }, r);
            var r2 = Run($"Invoke-BashSed -n '7p' '{path}' | ForEach-Object {{ $_.ToString().Trim() }}");
            Assert.Equal(new[] { "line-7-padding-padding-padding-pad" }, r2);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FilesAreOneContinuousStream_AndTheMissingNewlineIsSupplied()
    {
        string a = TempFile("a.txt"), b = TempFile("b.txt");
        try
        {
            File.WriteAllText(a, "a1\na2");        // no final newline
            File.WriteAllText(b, "b1\nb2\n");
            var r = Run($"Invoke-BashSed -n '$p;3p' '{a}' '{b}' | ForEach-Object {{ $_.ToString().Trim() }}");
            Assert.Equal(new[] { "b1", "b2" }, r);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void InPlace_StreamsIntoATempFile_AndReplacesTheOriginal()
    {
        string f = TempFile("ip.txt");
        string dir = Path.GetDirectoryName(f)!;
        try
        {
            File.WriteAllText(f, string.Concat(Enumerable.Range(1, 300).Select(i => $"row{i} a\n")));
            var r = Run($"Invoke-BashSed '-i.bak' 's/a/b/;2d' '{f}'; $LASTEXITCODE");
            Assert.Equal(new[] { "0" }, r);
            var lines = File.ReadAllLines(f);
            Assert.Equal(299, lines.Length);
            Assert.Equal("row1 b", lines[0]);
            Assert.Equal("row3 b", lines[1]);
            Assert.Equal(300, File.ReadAllLines(f + ".bak").Length);
            // no temp file is left beside the target
            Assert.DoesNotContain(Directory.GetFiles(dir, "sed????????"), p => new FileInfo(p).LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-2));
        }
        finally
        {
            File.Delete(f);
            File.Delete(f + ".bak");
        }
    }

    [Fact]
    public void InPlace_AllLinesVanish_LeavesAnEmptyFile()
    {
        string f = TempFile("empty.txt");
        try
        {
            File.WriteAllText(f, "a\nb\n");
            Run($"Invoke-BashSed -i d '{f}'");
            Assert.Equal(0, new FileInfo(f).Length);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void NulSeparated_FileMode_StreamsRecords()
    {
        string f = TempFile("z.bin");
        try
        {
            File.WriteAllBytes(f, new byte[] { (byte)'a', 0, (byte)'b', 0, (byte)'c' });
            var r = Run($"Invoke-BashSed -z 's/b/X/' '{f}' | ForEach-Object {{ ($_.ToString() -replace [char]0, '|') }}");
            Assert.Equal(new[] { "a|", "X|", "c" }, r);
        }
        finally { File.Delete(f); }
    }
}
