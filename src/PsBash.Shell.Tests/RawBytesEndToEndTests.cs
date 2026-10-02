using System.Diagnostics;
using System.Text;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// The launcher writes BYTES to its stdout/stderr (docs/specs/runtime-functions.md "Raw bytes"): a byte that
/// is not valid UTF-8 (<c>printf '\351'</c>) reaches the pipe as that single byte, not as U+FFFD (EF BF BD)
/// or the two bytes C3 A9. Output is captured as Latin-1 so each char IS one byte and the assertions can
/// compare exact byte sequences. Oracle: bash 5.2 (`printf '\351' | od -An -tx1` = e9).
/// </summary>
[Trait("Category", "Integration")]
public class RawBytesEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static async Task<(int ExitCode, byte[] Stdout, byte[] Stderr)> RunBytesAsync(
        string script, string? workingDirectory = null)
    {
        var psi = PsBashTestProcess.Create(["-c", script], workingDirectory, ipcEndpoint: IpcEndpoint);
        psi.StandardOutputEncoding = Encoding.Latin1;
        psi.StandardErrorEncoding = Encoding.Latin1;
        var (exit, stdout, stderr) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(90));
        return (exit, Encoding.Latin1.GetBytes(stdout), Encoding.Latin1.GetBytes(stderr));
    }

    /// <summary>Strip the trailing line terminator the host serializer adds (\n, or \r\n on Windows).</summary>
    private static byte[] TrimEol(byte[] b)
    {
        int n = b.Length;
        if (n > 0 && b[n - 1] == 0x0A) n--;
        if (n > 0 && b[n - 1] == 0x0D) n--;
        return b[..n];
    }

    [SkippableFact]
    public async Task Printf_LoneInvalidByte_ReachesStdoutAsOneByte()
    {
        var (exit, stdout, _) = await RunBytesAsync(@"printf '\351'");
        Assert.Equal(0, exit);
        Assert.Equal(new byte[] { 0xE9 }, stdout);
    }

    [SkippableFact]
    public async Task PrintfHexAndOctal_InvalidBytes_ReachStdoutExactly()
    {
        var (_, stdout, _) = await RunBytesAsync(@"printf 'a\xff\xfe\200b'");
        Assert.Equal(new byte[] { 0x61, 0xFF, 0xFE, 0x80, 0x62 }, stdout);
    }

    [SkippableFact]
    public async Task Printf_ValidUtf8ByteRun_ReachesStdoutAsTheSameThreeBytes()
    {
        var (_, stdout, _) = await RunBytesAsync(@"printf '\xe2\x82\xac'");
        Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }, stdout);
    }

    [SkippableFact]
    public async Task Printf_InvalidByte_PipedToWcC_CountsOneByte()
    {
        var (_, stdout, _) = await RunBytesAsync(@"printf '\351' | wc -c");
        Assert.Equal("1", Encoding.ASCII.GetString(TrimEol(stdout)).Trim());
    }

    [SkippableFact]
    public async Task Echo_E_InvalidByte_AndAnsiCQuoting_ReachStdout()
    {
        var (_, stdout, _) = await RunBytesAsync(@"printf '%s' $'\xe9\xff'; printf '%s' ""$(printf '\xe9')""");
        // $(...) capture strips trailing newlines only; the byte survives the command substitution.
        Assert.Equal(new byte[] { 0xE9, 0xFF, 0xE9 }, stdout);
    }

    [SkippableFact]
    public async Task Stderr_InvalidByte_ReachesStderrAsOneByte()
    {
        var (_, _, stderr) = await RunBytesAsync(@"printf '\351' >&2");
        Assert.Contains((byte)0xE9, stderr);
        Assert.DoesNotContain((byte)0xC3, stderr);
    }

    [SkippableFact]
    public async Task EveryByte_FileRoundTrip_CatToRedirect_IsByteIdentical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-rbe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var rnd = new byte[5000];
            new Random(11).NextBytes(rnd);
            File.WriteAllBytes(Path.Combine(dir, "all"), all);
            File.WriteAllBytes(Path.Combine(dir, "rnd"), rnd);

            var (exit, _, stderr) = await RunBytesAsync(
                "cat all > all.copy; cat rnd > rnd.copy; cat all rnd > both; head -c 100 rnd > h; tail -c 10 rnd > t; " +
                "cat rnd | tee tee.copy > /dev/null; gzip -c rnd | gzip -dc > gz.copy; wc -c < rnd > count; md5sum < rnd > sum",
                dir);

            Assert.True(exit == 0, Encoding.Latin1.GetString(stderr));
            Assert.Equal(all, File.ReadAllBytes(Path.Combine(dir, "all.copy")));
            Assert.Equal(rnd, File.ReadAllBytes(Path.Combine(dir, "rnd.copy")));
            Assert.Equal(all.Concat(rnd).ToArray(), File.ReadAllBytes(Path.Combine(dir, "both")));
            Assert.Equal(rnd.Take(100).ToArray(), File.ReadAllBytes(Path.Combine(dir, "h")));
            Assert.Equal(rnd.Skip(rnd.Length - 10).ToArray(), File.ReadAllBytes(Path.Combine(dir, "t")));
            Assert.Equal(rnd, File.ReadAllBytes(Path.Combine(dir, "tee.copy")));
            Assert.Equal(rnd, File.ReadAllBytes(Path.Combine(dir, "gz.copy")));
            Assert.Equal(rnd.Length.ToString(), File.ReadAllText(Path.Combine(dir, "count")).Trim());
            var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(rnd)).ToLowerInvariant();
            Assert.StartsWith(md5, File.ReadAllText(Path.Combine(dir, "sum")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task Printf_InvalidByte_Redirected_WritesOneByteFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-rbe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var (exit, _, _) = await RunBytesAsync(@"printf '\xe9' > f", dir);
            Assert.Equal(0, exit);
            Assert.Equal(new byte[] { 0xE9 }, File.ReadAllBytes(Path.Combine(dir, "f")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // -- a script that itself holds a non-UTF-8 byte (script file, piped stdin) ---------------------------------

    [SkippableFact]
    public async Task ScriptFile_WithARawInvalidByteInAString_PassesItThrough()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-rbe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // printf 'a<E9>b' — a Latin-1 script: the byte is in the SOURCE, not an escape.
            var script = Path.Combine(dir, "s.sh");
            File.WriteAllBytes(script, new byte[] { 0x70, 0x72, 0x69, 0x6E, 0x74, 0x66, 0x20, 0x27, 0x61, 0xE9, 0x62, 0x27, 0x0A });
            var psi = PsBashTestProcess.Create([script], dir, ipcEndpoint: IpcEndpoint);
            psi.StandardOutputEncoding = Encoding.Latin1;
            var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(90));
            Assert.Equal(0, exit);
            Assert.Equal(new byte[] { 0x61, 0xE9, 0x62 }, Encoding.Latin1.GetBytes(stdout));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task PipedStdinScript_WithARawInvalidByteInAString_PassesItThrough()
    {
        var psi = PsBashTestProcess.Create(["-s"], ipcEndpoint: IpcEndpoint);
        psi.StandardOutputEncoding = Encoding.Latin1;
        psi.StandardInputEncoding = Encoding.Latin1;
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(
            psi, stdinContent: "printf '%s' 'xéy'\n", timeout: TimeSpan.FromSeconds(90));
        Assert.Equal(0, exit);
        Assert.Equal(new byte[] { 0x78, 0xE9, 0x79 }, Encoding.Latin1.GetBytes(stdout));
    }

    // -- native children: stdout captured into the pipeline, pipeline piped into native stdin ---------------

    [SkippableFact]
    public async Task NativeChild_OutputWithInvalidBytes_IsCapturedByteFaithfully()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "uses findstr.exe; the POSIX leg is NativeChild_Posix_*");
        var dir = Path.Combine(Path.GetTempPath(), "psb-rbe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "bin"), new byte[] { 0x61, 0xE9, 0xFF, 0x62, 0x0A });
            // native stdout -> PowerShell capture -> wc -c: the child wrote a E9 FF b \n = 5 bytes.
            var (exit, stdout, stderr) = await RunBytesAsync("findstr . bin | wc -c", dir);
            Assert.True(exit == 0, Encoding.Latin1.GetString(stderr));
            Assert.Equal("5", Encoding.ASCII.GetString(TrimEol(stdout)).Trim());

            // ... and straight to our stdout: the bytes come out as they went in.
            var (_, raw, _) = await RunBytesAsync("findstr . bin", dir);
            Assert.Equal(new byte[] { 0x61, 0xE9, 0xFF, 0x62 }, TrimEol(raw));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task NativeChild_StdinFromThePipeline_ReceivesTheOriginalBytes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "uses findstr.exe; the POSIX leg is NativeChild_Posix_*");
        // printf -> pipeline -> findstr stdin -> findstr stdout -> our stdout.
        var (_, stdout, _) = await RunBytesAsync(@"printf 'x\351y\n' | findstr .");
        Assert.Equal(new byte[] { 0x78, 0xE9, 0x79 }, TrimEol(stdout));
    }

    [SkippableFact]
    public async Task NativeChild_Posix_BinaryRoundTripThroughACat()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX leg: /bin/cat is the native child");
        var (_, stdout, _) = await RunBytesAsync(@"printf 'a\351\377b\n' | /bin/cat");
        // The invalid bytes are the subject. Piping a record into a native stdin makes PowerShell append
        // its own line terminator after the record's embedded "\n", so a faithful /bin/cat shows a doubled
        // newline (findstr hid it on Windows). Compare modulo trailing line terminators.
        var trimmed = stdout.AsSpan();
        while (trimmed.Length > 0 && (trimmed[^1] == 0x0A || trimmed[^1] == 0x0D)) trimmed = trimmed[..^1];
        Assert.Equal(new byte[] { 0x61, 0xE9, 0xFF, 0x62 }, trimmed.ToArray());
    }
}
