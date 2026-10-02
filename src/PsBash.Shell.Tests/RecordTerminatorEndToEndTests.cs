using System.Text;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// The launcher's stdout, when it is a file or pipe (every capture: the Bash tool, `> f`, `| cmd`), carries
/// records separated by LF on every platform, as bash and Git Bash write them. Oracle: bash 5.2
/// (`echo hi | od -An -tx1` = 68 69 0a). A console renders LF as a newline itself, so nothing needs CRLF.
/// </summary>
[Trait("Category", "Integration")]
public class RecordTerminatorEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static async Task<byte[]> StdoutBytes(string script)
    {
        var psi = PsBashTestProcess.Create(["-c", script], ipcEndpoint: IpcEndpoint);
        psi.StandardOutputEncoding = Encoding.Latin1;
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(0, exit);
        return Encoding.Latin1.GetBytes(stdout);
    }

    [SkippableFact]
    public async Task Echo_RedirectedStdout_EndsInLfOnly() =>
        Assert.Equal(new byte[] { 0x68, 0x69, 0x0A }, await StdoutBytes("echo hi"));

    [SkippableFact]
    public async Task MultipleRecords_AreLfSeparated() =>
        Assert.Equal("a\nb\nc\n", Encoding.Latin1.GetString(await StdoutBytes("printf 'a\\nb\\n'; echo c")));

    [SkippableFact]
    public async Task RecordsWithTheirOwnCr_KeepThemExactly() =>
        // a CR that is part of the data is data: only the record boundary is LF
        Assert.Equal(new byte[] { 0x61, 0x0D, 0x0A, 0x62, 0x0A }, await StdoutBytes("printf 'a\\r\\n'; echo b"));
}
