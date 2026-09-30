using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A reader's "no such file" diagnostic quotes the operand AS TYPED, in each tool's own GNU wording
/// (<c>cat: nosuchfile: No such file or directory</c>, <c>head: cannot open 'nosuchfile' for
/// reading: …</c>), never the resolved full path. Every template below was captured from GNU
/// coreutils 9.4 / grep / sed / gawk / util-linux in WSL Ubuntu 24.04 with <c>CMD nosuchfile</c> and
/// <c>CMD sub/nosuchfile</c> from a scratch directory. <c>{0}</c> is the operand as typed.
/// Not covered here: sort / cut / stat / file (their cmdlets are owned by another change).
/// </summary>
public class ReaderOperandDisplayTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public ReaderOperandDisplayTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psb-rd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "f"), "x\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private CmdResult Run(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        return CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location '{_dir}'; {ps}");
    }

    public static IEnumerable<object[]> Cases() => new (string Cmd, string Template)[]
    {
        ("cat", "cat: {0}: No such file or directory"),
        ("head", "head: cannot open '{0}' for reading: No such file or directory"),
        ("tail", "tail: cannot open '{0}' for reading: No such file or directory"),
        ("wc", "wc: {0}: No such file or directory"),
        ("grep x", "grep: {0}: No such file or directory"),
        ("uniq", "uniq: {0}: No such file or directory"),
        ("tac", "tac: failed to open '{0}' for reading: No such file or directory"),
        ("nl", "nl: {0}: No such file or directory"),
        ("fold", "fold: {0}: No such file or directory"),
        ("expand", "expand: {0}: No such file or directory"),
        ("unexpand", "unexpand: {0}: No such file or directory"),
        ("paste", "paste: {0}: No such file or directory"),
        ("strings", "strings: '{0}': No such file"),
        ("base64", "base64: {0}: No such file or directory"),
        ("md5sum", "md5sum: {0}: No such file or directory"),
        ("sha1sum", "sha1sum: {0}: No such file or directory"),
        ("sha256sum", "sha256sum: {0}: No such file or directory"),
        ("ls", "ls: cannot access '{0}': No such file or directory"),
        ("du", "du: cannot access '{0}': No such file or directory"),
        ("realpath -e", "realpath: {0}: No such file or directory"),
        ("gzip", "gzip: {0}: No such file or directory"),
        ("tar -tf", "tar: {0}: Cannot open: No such file or directory"),
        ("sed p", "sed: can't read {0}: No such file or directory"),
        ("awk 1", "awk: fatal: cannot open file `{0}' for reading: No such file or directory"),
        ("split", "split: cannot open '{0}' for reading: No such file or directory"),
        ("rev", "rev: cannot open {0}: No such file or directory"),
        ("diff f", "diff: {0}: No such file or directory"),
        ("comm f", "comm: {0}: No such file or directory"),
        ("join f", "join: {0}: No such file or directory"),
    }.SelectMany(c => new[] { "nosuchfile", "sub/nosuchfile" }
        .Select(operand => new object[] { c.Cmd, operand, string.Format(c.Template, operand) }));

    [Theory]
    [MemberData(nameof(Cases))]
    public void MissingOperand_IsQuotedAsTyped(string cmd, string operand, string expected)
    {
        var r = Run($"{cmd} {operand}");
        Assert.Contains(expected, r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), r.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gzip_Decompress_NamesTheTypedOperandWithSuffix()
    {
        var r = Run("gzip -d nosuchfile");
        Assert.Contains("gzip: nosuchfile.gz: No such file or directory", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), r.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wildcard_NoMatch_IsNoSuchFileNotAWindowsSyntaxError()
    {
        // GNU also shell-quotes special names ('*.zz'); that quotearg rule is a separate gap. What must
        // hold here is the strerror text, not Windows' "filename ... syntax is incorrect".
        var r = Run("cat '*.zz'");
        Assert.Contains("*.zz: No such file or directory", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tee_UnwritablePath_QuotesOperandAsTyped()
    {
        var r = Run("echo hi | tee nodir/x");
        Assert.Contains("tee: nodir/x: No such file or directory", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), r.Stderr, StringComparison.OrdinalIgnoreCase);
    }
}
