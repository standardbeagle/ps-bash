using System.Security.Cryptography;
using System.Text;
using PsBash.Testing;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A native program sees EXACTLY the bytes bash would hand it, and its output reaches the next stage / a file /
/// <c>$(…)</c> unchanged. Windows <c>ssh.exe host 'bash -s' &lt; script</c> failed with <c>$'hostname\r'</c>
/// because PowerShell's native pipe appended <c>\r\n</c> to every record it fed the program (the file, heredoc,
/// here-string and pipe forms alike), added a newline to an unterminated last line, and re-encoded the bytes.
/// <para>
/// Oracle: bash — a pipe, a redirect and a heredoc are byte streams. Both directions are measured by a neutral
/// native (<c>pwsh</c> running a three-line helper script), so no ssh and no ps-bash cmdlet is in the way:
/// the DUMPER reads its stdin and prints <c>LEN= SHA= BYTES=</c>; the WRITER emits exact bytes from hex.
/// Expected values are written as Latin-1 strings (one char per byte).
/// </para>
/// </summary>
public class NativeStdioBytesTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "psb_nsb_" + Environment.ProcessId);

    private static readonly string DumperPath = Write("dump.ps1", """
        $s=[Console]::OpenStandardInput();$m=New-Object IO.MemoryStream;$s.CopyTo($m);$a=$m.ToArray()
        $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($a)).Replace('-','')
        $t='LEN='+$a.Length+' SHA='+$h; if($a.Length -le 600){$t+=' BYTES='+($a -join ',')}
        [Console]::Out.Write($t+"`n")
        """);

    private static readonly string WriterPath = Write("write.ps1", """
        param([string]$hex)
        $b=[byte[]]@($hex -split '(?<=\G..)(?!$)' | % { [Convert]::ToByte($_,16) })
        $o=[Console]::OpenStandardOutput();$o.Write($b,0,$b.Length);$o.Flush()
        """);

    private static string Write(string name, string text)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, text);
        return path.Replace('\\', '/');
    }

    // The native under test: reads its stdin / writes exact bytes.
    private static readonly string R = $"pwsh -NoProfile -File '{DumperPath}'";
    private static readonly string W = $"pwsh -NoProfile -File '{WriterPath}'";

    // The OUTPUT side. PowerShell decodes a native program's stdout into text lines before ps-bash sees it, so a
    // CR before an LF is dropped and a missing final newline is added, in every pipe / `>` / `$(…)`. Byte-exact
    // output needs ps-bash to run the program itself (raw stdout) — a separate, larger change than the stdin feed.
    // Each skipped case below is the exact bash behaviour; remove Skip when that lands. Cases that already hold
    // (invalid UTF-8, UTF-8, NUL, LF-only data with a final newline) stay live.
    private const string KnownGap = "known gap: PowerShell line-decodes a native program's stdout (drops CR, adds a final newline)";

    private static string F(string name) => $"/tmp/psb_nsb_{Environment.ProcessId}_{name}";

    private static string Dump(string latin1)
    {
        var a = Encoding.Latin1.GetBytes(latin1);
        var t = $"LEN={a.Length} SHA={Convert.ToHexString(SHA256.HashData(a))}";
        return a.Length <= 600 ? t + " BYTES=" + string.Join(',', a) : t;
    }

    private static async Task<string> RunAsync(string script, int seconds = 90)
    {
        var runner = PsBashRunner.Create().WithTimeout(seconds);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        Skip.IfNot(PwshOnPath(), "pwsh not on PATH");
        var r = await runner.RunScriptAsync(script);
        return r.Stdout.ReplaceLineEndings("\n").TrimEnd('\n');
    }

    private static bool PwshOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")));

    // ───────────── input: bytes a native program reads ─────────────

    [SkippableTheory]
    // pipes
    [InlineData("printf 'a\\nb\\n' | {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb' | {R}", "a\nb")]                          // no final newline: none added
    [InlineData("printf 'a\\r\\nb\\r\\n' | {R}", "a\r\nb\r\n")]           // CR in the DATA survives
    [InlineData("printf '\\n\\na\\n\\n' | {R}", "\n\na\n\n")]
    [InlineData("printf 'one line' | {R}", "one line")]
    [InlineData("echo hi | {R}", "hi\n")]
    [InlineData("echo -n hi | {R}", "hi")]
    [InlineData("printf '' | {R}", "")]
    [InlineData(": | {R}", "")]
    [InlineData("printf '\\377\\376\\n' | {R}", "ÿþ\n")]          // bytes that are not UTF-8
    [InlineData("printf 'a\\0b\\n' | {R}", "a\0b\n")]                      // NUL
    [InlineData("printf 'a\\nb\\n' | cat | {R}", "a\nb\n")]               // after a mapped stage
    [InlineData("printf 'b\\na\\n' | sort | {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb\\n' | X=1 {R}", "a\nb\n")]                 // env-prefixed stage
    [InlineData("printf 'a\\nb\\n' | {R} 2>&1", "a\nb\n")]
    // here-string / heredoc
    [InlineData("{R} <<< hi", "hi\n")]
    [InlineData("{R} <<< ''", "\n")]
    [InlineData("x='a b'; {R} <<< \"$x\"", "a b\n")]
    [InlineData("{R} <<EOF\nx\ny\nEOF", "x\ny\n")]
    [InlineData("{R} <<'EOF'\n$x\nb\\c\nEOF", "$x\nb\\c\n")]
    [InlineData("x=5; {R} <<EOF\nv=$x\nEOF", "v=5\n")]
    [InlineData("{R} <<-EOF\n\tx\n\ty\nEOF", "x\ny\n")]
    [InlineData("{R} <<EOF\n\nEOF", "\n")]
    [InlineData("X=1 {R} <<< hi", "hi\n")]
    // process substitution, stdout/stderr forms
    [InlineData("{R} < <(printf 'a\\nb\\n')", "a\nb\n")]
    [InlineData("{R} < <(printf 'a\\nb')", "a\nb")]
    [InlineData("x=$(printf 'a\\nb\\n' | {R}); echo \"$x\"", "a\nb\n")]
    // compounds (already bridged; must stay exact)
    [InlineData("printf 'a\\nb\\n' | { {R}; }", "a\nb\n")]
    [InlineData("printf 'a\\nb' | { {R}; }", "a\nb")]
    [InlineData("{R} < /dev/null", "")]
    public async Task NativeStdin_PipeHeredocHereString_IsExactBytes(string template, string expectedLatin1)
    {
        var script = template.Replace("{R}", R);
        var expected = Dump(expectedLatin1);
        Assert.Equal(expected, await RunAsync(script));
    }

    [SkippableTheory]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\n\nc")]                       // CR, blank line, no final newline
    [InlineData("a\n")]
    [InlineData("\n")]
    [InlineData("x")]
    [InlineData("\r\n\r\n")]
    [InlineData("ÿþ\n\u0080")]              // non-UTF-8 bytes
    public async Task NativeStdin_FromFileRedirect_IsExactBytes(string contentLatin1)
    {
        var path = F("in");
        var oct = string.Concat(Encoding.Latin1.GetBytes(contentLatin1).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
        // `cat FILE` reads a text file CRLF-transparently on purpose (BashFileSystem policy), so a CR is only compared through the redirect forms.
        // (and decodes non-UTF-8 text lossily unless the file looks binary), so only plain ASCII also goes through `cat |`.
        var catStage = contentLatin1.Any(c => c == '\r' || c > 0x7e) ? $"{R} < {path}" : $"cat {path} | {R}";
        var script = $"printf '{oct}' > {path}; {R} < {path}; echo ---; {catStage}; echo ---; {{ {R}; }} < {path}; rm -f {path}";
        var one = Dump(contentLatin1);
        Assert.Equal($"{one}\n---\n{one}\n---\n{one}", await RunAsync(script));
    }

    [SkippableFact]
    public async Task NativeStdin_AllByteValues_RoundTrip()
    {
        var all = string.Concat(Enumerable.Range(0, 256).Select(i => (char)i));
        var oct = string.Concat(Enumerable.Range(0, 256).Select(i => "\\" + Convert.ToString(i, 8).PadLeft(3, '0')));
        Assert.Equal(Dump(all), await RunAsync($"printf '{oct}' | {R}"));
    }

    [SkippableFact]
    public async Task NativeStdin_OneMegabytePipe_IsExactAndStreams()
    {
        var expected = Dump(string.Concat(Enumerable.Repeat("abcdefghi\n", 100000)));
        Assert.Equal(expected, await RunAsync($"yes abcdefghi | head -n 100000 | {R}"));
    }

    [SkippableFact]
    public async Task NativeStdin_ExitStatusAndStdout_StillReachTheShell()
    {
        Assert.Equal("3", await RunAsync("printf a | pwsh -NoProfile -c 'exit 3'; echo $?"));
    }

    [SkippableFact]
    public async Task NativeStdin_ProgramThatIgnoresStdin_DoesNotWaitForAnEndlessProducer()
    {
        // Intermittent on the Ubuntu CI runner only (empty stdout after ~1 s; never reproduced locally or in WSL
        // under a 4-core pin), so a failure must carry what the plain stdout comparison hides.
        Skip.IfNot(OperatingSystem.IsWindows(), "known gap: PowerShell reports Broken pipe and drops the child output on Unix; docs/bugs/native-stdin-endless-producer-broken-pipe.md");
        var runner = PsBashRunner.Create().WithTimeout(20);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        Skip.IfNot(PwshOnPath(), "pwsh not on PATH");
        var r = await runner.RunScriptAsync("yes | pwsh -NoProfile -c 'Write-Output ok'");
        var stdout = r.Stdout.ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.True(stdout == "ok",
            $"stdout='{stdout}' exit={r.ExitCode} wallMs={r.WallMs}\n--- stderr ---\n{r.Stderr}");
    }

    // Not the stdin bytes: each of these wrappers is broken in ps-bash for its own reason today (env/nohup do not run
    // a command, `time` and `bash -c` reject piped input, timeout resolves to Windows timeout.exe, `eval` runs its
    // text without the converter). Kept as an executable list of the gaps; remove Skip as each is fixed.
    [SkippableTheory(Skip = "known gap: wrapper commands do not forward stdin to a native program")]
    [InlineData("printf 'a\\nb\\n' | env {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb\\n' | command {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb\\n' | timeout 60 {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb\\n' | nohup {R}", "a\nb\n")]
    [InlineData("printf 'a\\nb\\n' | time {R}", "a\nb\n")]
    [InlineData("eval '{R}' <<< hi", "hi\n")]
    [InlineData("bash -c '{R}' <<< hi", "hi\n")]
    public async Task NativeStdin_ThroughWrapperCommands_IsExactBytes(string template, string expectedLatin1)
    {
        var got = await RunAsync(template.Replace("{R}", R));
        Assert.Contains(Dump(expectedLatin1), got);
    }

    // ───────────── output: bytes a native program writes ─────────────

    private static async Task AssertStdoutExactAsync(string hex, string expectedLatin1)
    {
        var b64 = Convert.ToBase64String(Encoding.Latin1.GetBytes(expectedLatin1));
        var f = F("out");
        // a pipe into a mapped command, through a mapped stage, into a file, appended, and teed
        var script =
            $"{W} {hex} | base64 -w0; echo; " +
            $"{W} {hex} | cat | base64 -w0; echo; " +
            $"{W} {hex} > {f}; base64 -w0 {f}; echo; " +
            $"{W} {hex} >> {f}.2; base64 -w0 {f}.2; echo; " +
            $"{W} {hex} | tee {f}.3 > /dev/null; base64 -w0 {f}.3; echo; " +
            $"{W} {hex} 2>&1 | base64 -w0; echo; " +
            $"rm -f {f} {f}.2 {f}.3";
        var line = string.Join("\n", Enumerable.Repeat(b64, 6));
        Assert.Equal(line, await RunAsync(script));
    }

    [SkippableTheory]
    [InlineData("0a0a", "\n\n")]
    [InlineData("ff0a", "ÿ\n")]                     // not UTF-8
    [InlineData("e282ac0a", "â\u0082¬\n")]     // UTF-8
    [InlineData("00610a", "\0a\n")]                      // NUL
    public Task NativeStdout_ToPipeFileTeeAndRedirect_IsExactBytes(string hex, string expectedLatin1) =>
        AssertStdoutExactAsync(hex, expectedLatin1);

    [SkippableTheory(Skip = KnownGap)]
    [InlineData("610a62", "a\nb")]                       // no final newline: none added
    [InlineData("610d0a62", "a\r\nb")]                   // CR survives
    [InlineData("610d0a", "a\r\n")]
    [InlineData("6162", "ab")]
    public Task NativeStdout_ToPipeFileTeeAndRedirect_KeepsCarriageReturnAndMissingNewline(string hex, string expectedLatin1) =>
        AssertStdoutExactAsync(hex, expectedLatin1);

    [SkippableTheory]
    [InlineData("610a62", "a\nb")]
    [InlineData("6162", "ab")]
    public async Task NativeStdout_CommandSubstitution_IsExactBytes(string hex, string expectedLatin1)
    {
        var b64 = Convert.ToBase64String(Encoding.Latin1.GetBytes(expectedLatin1));
        Assert.Equal(b64, await RunAsync($"x=$({W} {hex}); printf %s \"$x\" | base64 -w0"));
    }

    [SkippableTheory(Skip = KnownGap)]
    [InlineData("610d0a62", "a\r\nb")]                   // $(…) strips only trailing LFs: CR stays
    [InlineData("610d0a0a0a", "a\r")]
    public async Task NativeStdout_CommandSubstitution_KeepsCarriageReturns(string hex, string expectedLatin1)
    {
        var b64 = Convert.ToBase64String(Encoding.Latin1.GetBytes(expectedLatin1));
        Assert.Equal(b64, await RunAsync($"x=$({W} {hex}); printf %s \"$x\" | base64 -w0"));
    }

    [SkippableTheory]
    [InlineData("ff0a", "ÿ\n")]
    public async Task NativeStdout_IntoAnotherNative_NonUtf8_IsExactBytes(string hex, string expectedLatin1) =>
        Assert.Equal(Dump(expectedLatin1), await RunAsync($"{W} {hex} | {R}"));

    [SkippableTheory(Skip = KnownGap)]
    [InlineData("610a62", "a\nb")]
    [InlineData("610d0a62", "a\r\nb")]
    [InlineData("6162", "ab")]
    public async Task NativeStdout_IntoAnotherNative_IsExactBytes(string hex, string expectedLatin1) =>
        Assert.Equal(Dump(expectedLatin1), await RunAsync($"{W} {hex} | {R}"));

    [SkippableFact(Skip = KnownGap)]
    public async Task NativeStdout_IntoReadLoop_KeepsCarriageReturn()
    {
        // bash: `read -r` keeps the CR that precedes the newline.
        var got = await RunAsync($"{W} 610d0a62 | while read -r l; do printf '[%s]' \"$l\"; done | base64 -w0");
        var expected = Convert.ToBase64String(Encoding.Latin1.GetBytes("[a\r][b]"));
        Assert.Equal(expected, got);
    }
}
