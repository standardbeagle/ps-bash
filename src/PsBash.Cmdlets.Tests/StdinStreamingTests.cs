using System.Security.Cryptography;
using System.Text;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// gzip / base64 / md5sum / sha*sum on stdin consume the pipeline RECORD BY RECORD
/// (<see cref="RecordByteEncoder"/> into an incremental hasher / compressor / base64 encoder) instead of
/// joining the whole stream into a string and a byte copy. Streaming is asserted by ORDER (output
/// observed while the producer still runs) and by retained-count seams, never by a memory number; the
/// incremental pieces are pinned byte-for-byte against the joined-string forms they replace.
/// </summary>
public class StdinStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public StdinStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    // ------------------------------------------------------------------ pure pieces

    private static byte[] OldBytes(IEnumerable<object> items) => RawBytes.GetBytes(BashRuntime.RecordStreamText(items));

    private static byte[] NewBytes(IEnumerable<object> items, out int maxChunk)
    {
        var enc = new RecordByteEncoder();
        var ms = new MemoryStream();
        int max = 0;
        foreach (var item in items) enc.Encode(item, b => { max = Math.Max(max, b.Length); ms.Write(b); });
        enc.Finish(b => ms.Write(b));
        maxChunk = max;
        return ms.ToArray();
    }

    [Fact]
    public void RecordByteEncoder_MatchesJoinedRecordStream()
    {
        var items = new List<object>
        {
            "plain", "ends\n", "", BashRuntime.TextRecord("no newline", unterminated: true),
            BashRuntime.TextRecord("tail\n", unterminated: true), "café €",
            RawBytes.GetString(new byte[] { 0xE9, 0x41, 0xFF, 0xC0, 0x80 }),
            BashRuntime.TextRecord("\ud83d", unterminated: true),   // a surrogate pair split ACROSS records
            BashRuntime.TextRecord("\ude00", unterminated: true),
            "after",
        };
        Assert.Equal(OldBytes(items), NewBytes(items, out _));
    }

    [Fact]
    public void RecordByteEncoder_RandomRecords_MatchJoinedRecordStream()
    {
        var rng = new Random(7);
        for (int round = 0; round < 50; round++)
        {
            var items = new List<object>();
            for (int i = rng.Next(0, 40); i > 0; i--)
            {
                var sb = new StringBuilder();
                for (int c = rng.Next(0, 12); c > 0; c--)
                    sb.Append(rng.Next(6) switch
                    {
                        0 => '\n', 1 => 'é', 2 => RawBytes.ToMarker((byte)rng.Next(0x80, 0x100)),
                        3 => '€', _ => (char)('a' + rng.Next(26)),
                    });
                items.Add(BashRuntime.TextRecord(sb.ToString(), unterminated: rng.Next(3) == 0));
            }
            Assert.Equal(OldBytes(items), NewBytes(items, out _));
        }
    }

    [Fact]
    public void RecordByteEncoder_HoldsOneRecord_NotTheStream()
    {
        // 20000 records of ~100 bytes: no single sink call exceeds one record (+ its boundary).
        var items = Enumerable.Range(0, 20000).Select(i => (object)(new string('x', 100) + i));
        var enc = new RecordByteEncoder();
        int max = 0;
        foreach (var item in items) enc.Encode(item, b => max = Math.Max(max, b.Length));
        Assert.True(max <= 200, $"largest chunk {max}");
    }

    private static List<(string Text, bool Unterminated)> Records(Action<Action<object>> produce)
    {
        var list = new List<(string, bool)>();
        produce(o => list.Add((BashRuntime.GetBashText(o), BashRuntime.IsUnterminated(o))));
        return list;
    }

    [Fact]
    public void ByteRecordEmitter_MatchesEmitBashLines_AtEveryChunking()
    {
        var rng = new Random(11);
        for (int round = 0; round < 80; round++)
        {
            var data = new byte[rng.Next(0, 300)];
            for (int i = 0; i < data.Length; i++)
                data[i] = rng.Next(5) switch { 0 => (byte)'\n', 1 => (byte)rng.Next(0x80, 0x100), _ => (byte)rng.Next('a', 'z') };

            var expected = Records(add => { foreach (var r in BashRuntime.EmitBashLines(RawBytes.GetString(data))) add(r); });
            var got = Records(add =>
            {
                var em = new ByteRecordEmitter(add);
                for (int at = 0; at < data.Length;)
                {
                    int take = Math.Min(rng.Next(1, 40), data.Length - at);
                    em.Append(data.AsSpan(at, take));
                    at += take;
                }
                em.Finish();
            });
            Assert.Equal(expected, got);
        }
    }

    [Fact]
    public void ByteRecordEmitter_EmitsLinesAsTheyComplete_AndRetainsOnlyTheOpenLine()
    {
        int emitted = 0;
        var em = new ByteRecordEmitter(_ => emitted++);
        var chunk = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0123456789\n", 1000)) + "open");
        for (int i = 0; i < 100; i++)
        {
            em.Append(chunk);
            // every complete line is out the moment its newline arrives ...
            Assert.Equal((i + 1) * 1000, emitted);
            // ... and only the open line ("open", no newline yet) is held.
            Assert.Equal(4, em.PendingChars);
        }
        em.Finish();
        Assert.Equal(100 * 1000 + 1, emitted);   // the dangling "open" is the final unterminated record
        Assert.Equal(emitted, em.Emitted);
    }

    private static string Render(List<object> records)
        => string.Concat(records.Select(r => BashRuntime.GetBashText(r) + (BashRuntime.IsUnterminated(r) ? "" : "\n")));

    [Theory]
    [InlineData(76)] [InlineData(20)] [InlineData(4)] [InlineData(1)] [InlineData(0)]
    public void Base64LineEncoder_MatchesConvert_AnyChunkingAndWrap(int wrap)
    {
        var rng = new Random(wrap + 3);
        for (int round = 0; round < 60; round++)
        {
            var data = new byte[rng.Next(0, 400)];
            rng.NextBytes(data);
            var records = new List<object>();
            var enc = new Base64LineEncoder(wrap, records.Add);
            for (int at = 0; at < data.Length;)
            {
                int take = Math.Min(rng.Next(1, 17), data.Length - at);
                enc.Append(data.AsSpan(at, take));
                at += take;
            }
            enc.Finish();

            string all = Convert.ToBase64String(data);
            string expected = wrap <= 0 || all.Length == 0
                ? all
                : string.Join("\n", Enumerable.Range(0, (all.Length + wrap - 1) / wrap).Select(i => all.Substring(i * wrap, Math.Min(wrap, all.Length - i * wrap)))) + "\n";
            Assert.Equal(expected, Render(records));
            if (wrap > 0) Assert.All(records, r => Assert.False(BashRuntime.IsUnterminated(r)));
        }
    }

    [Fact]
    public void Base64LineEncoder_EmitsWrappedLinesAsTheyFill()
    {
        var lines = new List<object>();
        var enc = new Base64LineEncoder(8, lines.Add);
        enc.Append(new byte[6]);                // 8 chars -> one full line already
        Assert.Single(lines);
        enc.Append(new byte[300]);
        Assert.True(lines.Count > 40);          // emitted while the input is still arriving
    }

    private static (byte[]? Bytes, bool Failed) OldDecode(string text, bool ignoreGarbage)
    {
        try
        {
            text = text.Trim();
            if (ignoreGarbage) text = new string(text.Where(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=').ToArray());
            return (Convert.FromBase64String(text), false);
        }
        catch (FormatException) { return (null, true); }
    }

    private static (byte[]? Bytes, bool Failed) NewDecode(string text, bool ignoreGarbage, int chunk)
    {
        var ms = new MemoryStream();
        var dec = new Base64StreamDecoder(ignoreGarbage, b => ms.Write(b));
        try
        {
            for (int at = 0; at < text.Length; at += chunk) dec.Append(text.AsSpan(at, Math.Min(chunk, text.Length - at)));
            dec.Finish();
            return (ms.ToArray(), false);
        }
        catch (FormatException) { return (null, true); }
    }

    [Theory]
    [InlineData("aGVsbG8K")] [InlineData("aGVsbG8K\n")] [InlineData("  aGVs\nbG8K \r\n")] [InlineData("QQ==")] [InlineData("QQ==\n")]
    [InlineData("")] [InlineData("\n\n")] [InlineData("QQ")] [InlineData("QQ=")] [InlineData("Q")] [InlineData("QQ==QQ==")]
    [InlineData("QUJD\u000bQUJD")] [InlineData("\u000bQUJD\u000b")] [InlineData("QU JD")] [InlineData("QUJDé")] [InlineData("====")]
    [InlineData("QUJDRA==\n\n")] [InlineData("QUJDRA== x")] [InlineData("QUJD\tRUZH")]
    public void Base64StreamDecoder_AgreesWithFromBase64StringOfTheTrimmedText(string text)
    {
        foreach (bool ig in new[] { false, true })
            foreach (int chunk in new[] { 1, 2, 3, 5, 1000 })
            {
                var (ob, of) = OldDecode(text, ig);
                var (nb, nf) = NewDecode(text, ig, chunk);
                Assert.Equal(of, nf);
                if (!of) Assert.Equal(ob, nb);
            }
    }

    [Fact]
    public void ChecksumStdin_HashMode_RetainsNoRecords_AndMatchesTheJoinedStream()
    {
        var items = Enumerable.Range(0, 50000).Select(i => PSObjectOf("line " + i)).ToList();
        var stdin = new ChecksumStdin(HashAlgorithmName.SHA256, "sha256sum");
        foreach (var item in items) stdin.Add(item, Array.Empty<string>());
        Assert.Equal(50000, stdin.Streamed);
        Assert.Equal(0, stdin.Retained);
        Assert.True(stdin.HasInput);
        string expected = Convert.ToHexString(SHA256.HashData(OldBytes(items.Cast<object>()))).ToLowerInvariant();
        Assert.Equal(expected, stdin.Hex());
        Assert.Equal(expected, stdin.Hex()); // idempotent: `-` named twice reuses the digest
    }

    [Fact]
    public void ChecksumStdin_CheckMode_KeepsTheList_FileOperandIgnoresThePipeline()
    {
        var check = new ChecksumStdin(HashAlgorithmName.MD5, "md5sum");
        check.Add(PSObjectOf("d41d8cd98f00b204e9800998ecf8427e  f"), new[] { "-c" });
        Assert.Equal(1, check.Retained);
        Assert.Equal(0, check.Streamed);

        var file = new ChecksumStdin(HashAlgorithmName.MD5, "md5sum");
        file.Add(PSObjectOf("ignored"), new[] { "somefile" });
        Assert.Equal(0, file.Retained);
        Assert.Equal(0, file.Streamed);
        Assert.False(file.HasInput);
    }

    private static System.Management.Automation.PSObject PSObjectOf(string text)
        => System.Management.Automation.PSObject.AsPSObject(text);

    // ------------------------------------------------------------------ the cmdlets, by ORDER

    [Fact]
    public void Base64Encode_EmitsLinesWhileTheProducerIsStillRunning()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..6 | ForEach-Object { $log.Add(\"P$_\"); 'abcdefg' } } | Invoke-BashBase64 -w 4 | " +
            "ForEach-Object { $log.Add('T') }; $log -join ' '");
        // A joined-string implementation logs all six P before the first T.
        Assert.StartsWith("P1 T", log[0]);
    }

    [Fact]
    public void Base64Decode_EmitsRecordsWhileTheProducerIsStillRunning()
    {
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..5 | ForEach-Object { $log.Add(\"P$_\"); 'YWIK' } } | Invoke-BashBase64 -d | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        Assert.Equal("P1 Tab P2 Tab P3 Tab P4 Tab P5 Tab", log[0]);
    }

    [Fact]
    public void Base64_PipelineBytes_AreUnchanged_ForTerminatedAndUnterminatedRecords()
    {
        // printf 'b\na' | base64  is  Ygph (no final newline in the byte stream), `echo hi` adds one.
        var got = Run(
            "$a = (Invoke-BashPrintf 'b\\na' | Invoke-BashBase64 | ForEach-Object { $_.ToString() }) -join '|'; " +
            "$b = ('hi' | Invoke-BashBase64 | ForEach-Object { $_.ToString() }) -join '|'; " +
            "$c = ('hi' | Invoke-BashBase64 -w 0 | ForEach-Object { $_.ToString() }) -join '|'; \"$a $b $c\"");
        Assert.Equal("Ygph aGkK aGkK", got[0]);
    }

    [Fact]
    public void Base64Decode_Garbage_StillFailsWithExit1_AfterDecodingThePrefix()
    {
        var got = Run(
            "$out = @('YWIK', 'YWIK', '!!!!') | Invoke-BashBase64 -d 2>&1 | ForEach-Object { $_.ToString().Trim() }; " +
            "($out -join '|') + ' ' + $global:LASTEXITCODE");
        Assert.StartsWith("ab|ab|", got[0]);
        Assert.Contains("base64: invalid input", got[0]);
        Assert.EndsWith(" 1", got[0]);
    }

    [Fact]
    public void GzipCompress_EmitsOutputWhileTheProducerIsStillRunning()
    {
        // Incompressible-ish lines so deflate flushes blocks long before the input ends.
        var log = Run(
            "$log = [System.Collections.Generic.List[string]]::new(); $rng = [System.Random]::new(5); " +
            "& { 1..60000 | ForEach-Object { $log.Add('P'); [guid]::NewGuid().ToString('N') + $rng.Next() } } | Invoke-BashGzip | " +
            "ForEach-Object { $log.Add('T') }; " +
            "$firstT = $log.IndexOf('T'); $lastP = $log.LastIndexOf('P'); \"$($firstT -ge 0 -and $firstT -lt $lastP)\"");
        Assert.Equal("True", log[0]);
    }

    [Fact]
    public void Gzip_RoundTrip_ThroughThePipeline_IsExact()
    {
        var got = Run(
            "$s = 1..2000 | ForEach-Object { \"line $_ café\" }; " +
            "$back = $s | Invoke-BashGzip | Invoke-BashGzip -d | ForEach-Object { $_.ToString() }; " +
            "(($back -join \"`n\") -ceq ($s -join \"`n\")).ToString() + ' ' + $back.Count");
        Assert.Equal("True 2000", got[0]);
    }

    [Fact]
    public void GzipDecompress_File_EmitsTheDecodedRows_BeforeReportingACorruptTrailer()
    {
        // A member with a bad CRC footer inflates all its data and THEN reports the damage; the old
        // buffer-everything path produced nothing before failing.
        var got = Run(
            "$d = Join-Path ([System.IO.Path]::GetTempPath()) ('psb-gz-' + [guid]::NewGuid().ToString('N')); " +
            "New-Item -ItemType Directory $d | Out-Null; $f = Join-Path $d 'a.gz'; " +
            "$ms = [System.IO.MemoryStream]::new(); $gz = [System.IO.Compression.GZipStream]::new($ms, [System.IO.Compression.CompressionLevel]::NoCompression); " +
            "$text = [System.Text.Encoding]::ASCII.GetBytes((1..30000 | ForEach-Object { \"row $_`n\" }) -join ''); $gz.Write($text, 0, $text.Length); $gz.Dispose(); " +
            "$all = $ms.ToArray(); $all[$all.Length - 6] = $all[$all.Length - 6] -bxor 0xFF; [System.IO.File]::WriteAllBytes($f, [byte[]]$all); " +
            "$fileOut = @(Invoke-BashGzip -d -c $f 2>&1 | ForEach-Object { $_.ToString() }); " +
            "$rows = @($fileOut | Where-Object { $_ -like 'row *' }).Count; " +
            "Remove-Item -Recurse -Force $d; \"$($rows -gt 1000) $((($fileOut -join '|') -match 'gzip:'))\"");
        Assert.Equal("True True", got[0]);
    }

    [Fact]
    public void Md5Sum_Stdin_HashesTheExactByteStream()
    {
        var got = Run(
            "$a = (Invoke-BashPrintf 'x' | Invoke-BashMd5sum).Hash; " +
            "$b = ('x' | Invoke-BashMd5sum).Hash; " +
            "$c = (1..3 | ForEach-Object { \"$_\" } | Invoke-BashSha256sum).Hash; \"$a $b $c\"");
        string x = Convert.ToHexString(MD5.HashData(new[] { (byte)'x' })).ToLowerInvariant();
        string xn = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("x\n"))).ToLowerInvariant();
        string c = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("1\n2\n3\n"))).ToLowerInvariant();
        Assert.Equal($"{x} {xn} {c}", got[0]);
    }
}
