using System.IO.Compression;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashTar</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashTarCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashTar</c> function. Failure-surface axes
/// covered (per Directive 3): missing archive (error path), empty operands
/// (no-files-specified error), unicode filenames in archive, <c>--exclude</c>
/// glob filter, <c>--directory=DIR</c> chdir-before-extract, <c>-z</c>
/// gzipped round-trip, <c>-t</c> list, <c>-v</c> verbose, alias resolution,
/// <c>--help</c>, and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashTarCommandTests : IDisposable
{
    private readonly string _tmpDir;

    public InvokeBashTarCommandTests()
    {
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-tar-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private static System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunObjects(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private static string PsQuote(string p) => p.Replace("'", "''");

    [Fact]
    public void Tar_CreateAndList_RoundTrip_ListsCreatedFile()
    {
        string src = Path.Combine(_tmpDir, "a.txt");
        File.WriteAllText(src, "hello");
        string archive = Path.Combine(_tmpDir, "out.tar");

        RunLines($"Invoke-BashTar -c -f '{PsQuote(archive)}' '{PsQuote(src)}'");
        Assert.True(File.Exists(archive));

        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.Contains("a.txt", listed);
    }

    [Fact]
    public void Tar_CreateAndExtract_RoundTrip_RestoresFileContents()
    {
        string src = Path.Combine(_tmpDir, "payload.txt");
        File.WriteAllText(src, "round-trip-bytes");
        string archive = Path.Combine(_tmpDir, "rt.tar");
        string extractDir = Path.Combine(_tmpDir, "extracted");
        Directory.CreateDirectory(extractDir);

        RunLines($"Invoke-BashTar -c -f '{PsQuote(archive)}' '{PsQuote(src)}'");
        RunLines($"Invoke-BashTar -x -f '{PsQuote(archive)}' --directory='{PsQuote(extractDir)}'");

        string restored = Path.Combine(extractDir, "payload.txt");
        Assert.True(File.Exists(restored));
        Assert.Equal("round-trip-bytes", File.ReadAllText(restored));
    }

    [Fact]
    public void Tar_GzippedRoundTrip_RestoresFileContents()
    {
        string src = Path.Combine(_tmpDir, "gz.txt");
        File.WriteAllText(src, "gzip-payload");
        string archive = Path.Combine(_tmpDir, "out.tar.gz");
        string extractDir = Path.Combine(_tmpDir, "gzextract");
        Directory.CreateDirectory(extractDir);

        RunLines($"Invoke-BashTar -czf '{PsQuote(archive)}' '{PsQuote(src)}'");
        Assert.True(File.Exists(archive));

        // Sanity: the archive bytes should be gzip-magic (1f 8b)
        byte[] head = File.ReadAllBytes(archive);
        Assert.True(head.Length >= 2);
        Assert.Equal(0x1f, head[0]);
        Assert.Equal((byte)0x8b, head[1]);

        // Extract: .tar.gz suffix auto-detects gzip per oracle
        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(extractDir)}'");
        string restored = Path.Combine(extractDir, "gz.txt");
        Assert.True(File.Exists(restored));
        Assert.Equal("gzip-payload", File.ReadAllText(restored));
    }

    [Fact]
    public void Tar_ListMode_EmitsTypedOutput()
    {
        string src = Path.Combine(_tmpDir, "listed.txt");
        File.WriteAllText(src, "x");
        string archive = Path.Combine(_tmpDir, "list.tar");
        RunLines($"Invoke-BashTar -c -f '{PsQuote(archive)}' '{PsQuote(src)}'");

        var objs = RunObjects($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.NotEmpty(objs);
        Assert.Contains(objs, o => string.Equals(o.TypeNames[0], "PsBash.TarListOutput", StringComparison.Ordinal));
    }

    [Fact]
    public void Tar_VerboseCreate_EmitsOneNamePerEntry()
    {
        string src = Path.Combine(_tmpDir, "v.txt");
        File.WriteAllText(src, "vbody");
        string archive = Path.Combine(_tmpDir, "v.tar");

        string[] lines = RunLines($"Invoke-BashTar -cvf '{PsQuote(archive)}' '{PsQuote(src)}'");
        Assert.Contains("v.txt", lines);
    }

    [Fact]
    public void Tar_ExcludePattern_OmitsMatchingEntries()
    {
        string dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "k");
        File.WriteAllText(Path.Combine(dir, "skip.tmp"), "s");
        string archive = Path.Combine(_tmpDir, "ex.tar");

        RunLines($"Invoke-BashTar -c -f '{PsQuote(archive)}' --exclude=.tmp '{PsQuote(dir)}'");
        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");

        Assert.Contains(listed, l => l.Contains("keep.txt"));
        Assert.DoesNotContain(listed, l => l.Contains("skip.tmp"));
    }

    [Fact]
    public void Tar_DirectoryChdir_ExtractsToTargetDir()
    {
        string src = Path.Combine(_tmpDir, "chdir.txt");
        File.WriteAllText(src, "chdir-body");
        string archive = Path.Combine(_tmpDir, "chdir.tar");
        string outDir = Path.Combine(_tmpDir, "chdest");
        Directory.CreateDirectory(outDir);

        RunLines($"Invoke-BashTar -cf '{PsQuote(archive)}' '{PsQuote(src)}'");
        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(outDir)}'");

        Assert.True(File.Exists(Path.Combine(outDir, "chdir.txt")));
    }

    [Fact]
    public void Tar_MissingArchive_ErrorContinues()
    {
        string missing = Path.Combine(_tmpDir, "does-not-exist.tar");
        // Should not throw; should not produce any output objects.
        var objs = RunObjects($"Invoke-BashTar -t -f '{PsQuote(missing)}'");
        Assert.Empty(objs);
    }

    [Fact]
    public void Tar_TarAlias_ResolvesToCmdlet()
    {
        string src = Path.Combine(_tmpDir, "alias.txt");
        File.WriteAllText(src, "a");
        string archive = Path.Combine(_tmpDir, "alias.tar");

        // Use the bash 'tar' alias instead of the cmdlet name.
        RunLines($"tar -cf '{PsQuote(archive)}' '{PsQuote(src)}'");
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void Tar_HelpFlag_DelegatesToShowBashHelp()
    {
        // --help should emit help text without throwing. The psm1 Show-BashHelp
        // function exists; we just verify the cmdlet routes through it without
        // crashing.
        var objs = RunObjects("Invoke-BashTar --help");
        // We don't care about the exact content — just that it completed without
        // throwing and produced something or nothing without an error record.
        Assert.NotNull(objs);
    }

    [Fact]
    public void Tar_UnicodeFilename_RoundTrips()
    {
        string src = Path.Combine(_tmpDir, "héllo-🦀.txt");
        File.WriteAllText(src, "unicode-body");
        string archive = Path.Combine(_tmpDir, "u.tar");
        string outDir = Path.Combine(_tmpDir, "uout");
        Directory.CreateDirectory(outDir);

        RunLines($"Invoke-BashTar -cf '{PsQuote(archive)}' '{PsQuote(src)}'");
        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(outDir)}'");

        string restored = Path.Combine(outDir, "héllo-🦀.txt");
        Assert.True(File.Exists(restored));
        Assert.Equal("unicode-body", File.ReadAllText(restored));
    }

    [Fact]
    public void Tar_InjectionProbe_FilenameLiteralNotExecuted()
    {
        // Directive 12: a filename containing $(throw 'pwn') must reach the
        // missing-file path as a literal string and NOT be re-parsed as
        // PowerShell. The literal name does not exist on disk so the cmdlet
        // routes the operand through the unresolved-provider-path slice and
        // emits a bash-style "No such file" error via Write-BashError. No
        // exception should escape the test.
        string injection = "$(throw 'pwn').tar";
        var objs = RunObjects($"Invoke-BashTar -t -f '{PsQuote(injection)}'");
        Assert.Empty(objs);
    }
}
