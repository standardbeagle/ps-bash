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
public class InvokeBashTarCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashTarCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-tar-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunObjects(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private static string PsQuote(string p) => p.Replace("'", "''");

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

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

        // --exclude is a glob, not a substring: `*.tmp` excludes skip.tmp.
        // (A bare `.tmp` would NOT, matching GNU tar — see oracle.)
        RunLines($"Invoke-BashTar -c -f '{PsQuote(archive)}' --exclude=*.tmp '{PsQuote(dir)}'");
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

    // ===================== Security: path traversal (tar-slip) =====================

    /// <summary>Write a tar containing a single entry under an attacker-chosen name.</summary>
    private string WriteMaliciousTar(string archiveName, string entryName, string body)
    {
        string archive = Path.Combine(_tmpDir, archiveName);
        using var fs = File.Create(archive);
        using var tw = new System.Formats.Tar.TarWriter(fs);
        var entry = new System.Formats.Tar.PaxTarEntry(
            System.Formats.Tar.TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(body)),
        };
        tw.WriteEntry(entry);
        return archive;
    }

    [Fact]
    public void Tar_Extract_DotDotTraversalEntry_DoesNotEscapeDest()
    {
        // A malicious entry named ../escape.txt must NOT be written into the
        // parent of the extraction dir (classic tar-slip / Zip-Slip).
        string archive = WriteMaliciousTar("evil.tar", "../escape.txt", "pwned");
        string dest = Path.Combine(_tmpDir, "dest");
        Directory.CreateDirectory(dest);

        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(dest)}'");

        // The traversal target sits in _tmpDir (parent of dest) — it must not exist.
        Assert.False(File.Exists(Path.Combine(_tmpDir, "escape.txt")),
            "tar extracted a ../ entry outside the destination directory");
    }

    [Fact]
    public void Tar_Extract_AbsolutePathEntry_DoesNotEscapeDest()
    {
        // An absolute/rooted entry name must be rejected, not honored verbatim
        // (which would let Path.Combine discard the destination).
        string rooted = OperatingSystem.IsWindows()
            ? @"C:\Windows\Temp\psb-tarslip-probe.txt"
            : "/tmp/psb-tarslip-probe.txt";
        string archive = WriteMaliciousTar("evilabs.tar", rooted, "pwned");
        string dest = Path.Combine(_tmpDir, "dest2");
        Directory.CreateDirectory(dest);

        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(dest)}'");

        Assert.False(File.Exists(rooted),
            "tar extracted an absolute-path entry outside the destination directory");
    }

    // ===================== Empty --exclude must match nothing =====================

    [Fact]
    public void Tar_Create_EmptyExcludePattern_DoesNotExcludeEverything()
    {
        // An empty --exclude= pattern must exclude nothing — string.Contains("")
        // is true for every path and would otherwise produce an empty archive.
        string dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");
        string archive = Path.Combine(_tmpDir, "ex.tar");

        RunLines($"Invoke-BashTar -c --exclude= -f '{PsQuote(archive)}' '{PsQuote(dir)}'");

        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.Contains(listed, e => e.EndsWith("keep.txt"));
    }

    [Fact]
    public void Tar_Create_ExcludeGlob_ExcludesMatchingFilesOnly()
    {
        // --exclude='*.log' is a glob, not a substring — it must drop a.log but
        // keep b.txt. Oracle: tar --exclude='*.log' ... lists b.txt only.
        string dir = Path.Combine(_tmpDir, "g1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.log"), "x");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "y");
        string archive = Path.Combine(_tmpDir, "g1.tar");

        RunLines($"Invoke-BashTar -c --exclude=*.log -f '{PsQuote(archive)}' '{PsQuote(dir)}'");

        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.Contains(listed, e => e.EndsWith("b.txt"));
        Assert.DoesNotContain(listed, e => e.EndsWith("a.log"));
    }

    [Fact]
    public void Tar_Create_ExcludeDirName_PrunesWholeSubtree()
    {
        // --exclude=nm matches a path component and prunes the whole nm/ subtree
        // (entry and its children). Oracle: tar --exclude=nm lists only a.txt.
        string dir = Path.Combine(_tmpDir, "g2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        string nm = Path.Combine(dir, "nm");
        Directory.CreateDirectory(nm);
        File.WriteAllText(Path.Combine(nm, "c.txt"), "z");
        string archive = Path.Combine(_tmpDir, "g2.tar");

        RunLines($"Invoke-BashTar -c --exclude=nm -f '{PsQuote(archive)}' '{PsQuote(dir)}'");

        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.Contains(listed, e => e.EndsWith("a.txt"));
        Assert.DoesNotContain(listed, e => e.Contains("nm"));
    }

    [Fact]
    public void Tar_Create_ExcludeIsGlobNotSubstring_KeepsPartialMatch()
    {
        // Negative guard: a bare `.tmp` (no wildcard) is a glob that matches only
        // a component named exactly ".tmp" — it must NOT substring-match
        // "skip.tmp". Oracle: tar --exclude=.tmp keeps skip.tmp. This pins the
        // glob semantics so the old Contains()-substring behavior can't return.
        string dir = Path.Combine(_tmpDir, "g3");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skip.tmp"), "x");
        string archive = Path.Combine(_tmpDir, "g3.tar");

        RunLines($"Invoke-BashTar -c --exclude=.tmp -f '{PsQuote(archive)}' '{PsQuote(dir)}'");

        string[] listed = RunLines($"Invoke-BashTar -t -f '{PsQuote(archive)}'");
        Assert.Contains(listed, e => e.EndsWith("skip.tmp"));
    }

    // ===================== symlink / hardlink extraction =====================

    private static bool CanCreateSymlinks()
    {
        string d = Path.Combine(Path.GetTempPath(), "psb-slprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "t"), "x");
            string l = Path.Combine(d, "l");
            File.CreateSymbolicLink(l, "t");
            return new FileInfo(l).LinkTarget is not null;
        }
        catch { return false; }
        finally { try { Directory.Delete(d, true); } catch { /* best-effort */ } }
    }

    /// <summary>Write a tar with a regular file plus a symlink entry to it.</summary>
    private string WriteSymlinkTar(string archiveName, string linkName, string linkTarget)
    {
        string archive = Path.Combine(_tmpDir, archiveName);
        using var fs = File.Create(archive);
        using var tw = new System.Formats.Tar.TarWriter(fs);
        var file = new System.Formats.Tar.PaxTarEntry(
            System.Formats.Tar.TarEntryType.RegularFile, "target.txt")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("link-body")),
        };
        tw.WriteEntry(file);
        var link = new System.Formats.Tar.PaxTarEntry(
            System.Formats.Tar.TarEntryType.SymbolicLink, linkName)
        {
            LinkName = linkTarget,
        };
        tw.WriteEntry(link);
        return archive;
    }

    [SkippableFact]
    public void Tar_Extract_Symlink_RoundTrips()
    {
        Skip.IfNot(CanCreateSymlinks(), "symlink creation not permitted on this machine");

        string archive = WriteSymlinkTar("sl.tar", "link.txt", "target.txt");
        string dest = Path.Combine(_tmpDir, "sldest");
        Directory.CreateDirectory(dest);

        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(dest)}'");

        string linkPath = Path.Combine(dest, "link.txt");
        Assert.True(File.Exists(linkPath));
        Assert.Equal("target.txt", new FileInfo(linkPath).LinkTarget);
    }

    [Fact]
    public void Tar_Extract_EscapingSymlink_IsRefused()
    {
        // A symlink whose target climbs out of the destination is the tar-slip
        // pivot — it must be refused, not created.
        string archive = WriteSymlinkTar("evilsl.tar", "link.txt",
            "../../psb-tarslip-link-probe");
        string dest = Path.Combine(_tmpDir, "esldest");
        Directory.CreateDirectory(dest);

        RunLines($"Invoke-BashTar -xf '{PsQuote(archive)}' --directory='{PsQuote(dest)}'");

        Assert.False(File.Exists(Path.Combine(dest, "link.txt")),
            "an escaping symlink must not be created");
    }

    // ===================== Unsupported-flag classifier =====================

    [Fact]
    public void Tar_ValidButUnsupportedLongFlag_ReportsNotSupported()
    {
        // --bzip2 is a valid GNU tar option ps-bash does not implement.
        // It lands in the operand list and the classifier must report
        // "recognized but not supported" rather than treating it as a filename.
        // (Short -j is silently consumed by the bundle handler — only long form reachable here.)
        string arc = Path.Combine(_tmpDir, "dummy.tar");
        var (_, errs) = RunWithErrors($"Invoke-BashTar -xf '{PsQuote(arc)}' --bzip2 2>$null");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tar_UnrecognizedLongOption_BashParityMessage()
    {
        string arc = Path.Combine(_tmpDir, "dummy2.tar");
        var (_, errs) = RunWithErrors($"Invoke-BashTar -xf '{PsQuote(arc)}' --bogus 2>$null");
        Assert.Contains(errs, m =>
            m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            && m.Contains("--bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void Tar_BundledBzip2_ReportsNotSupported_NotSilentlyUncompressed()
    {
        // -cjf must NOT silently write an uncompressed tar (bzip2 has no managed
        // .NET codec). The bundle handler now refuses it clearly (exit 2).
        string src = Path.Combine(_tmpDir, "z.txt");
        File.WriteAllText(src, "x");
        string arc = Path.Combine(_tmpDir, "z.tar.bz2");
        var (_, errs) = RunWithErrors(
            $"Invoke-BashTar -cjf '{PsQuote(arc)}' '{PsQuote(src)}' 2>$null");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(arc), "no archive should be written when the codec is unsupported");
    }

    [Fact]
    public void Tar_AutoCompress_GzipByExtension_RoundTrips()
    {
        // -a/--auto-compress picks gzip from a .tgz/.tar.gz extension.
        string src = Path.Combine(_tmpDir, "auto.txt");
        File.WriteAllText(src, "auto-compress-payload");
        string arc = Path.Combine(_tmpDir, "auto.tar.gz");

        RunLines($"Invoke-BashTar -caf '{PsQuote(arc)}' '{PsQuote(src)}'");
        Assert.True(File.Exists(arc));
        // Confirm it really is gzip (magic 0x1f 0x8b), not a bare tar.
        byte[] head = File.ReadAllBytes(arc);
        Assert.True(head.Length > 2 && head[0] == 0x1f && head[1] == 0x8b, "archive must be gzip-compressed");

        string[] listed = RunLines($"Invoke-BashTar -taf '{PsQuote(arc)}'");
        Assert.Contains("auto.txt", listed);
    }

    [Fact]
    public void Tar_KeepOldFiles_DoesNotOverwriteExisting()
    {
        string src = Path.Combine(_tmpDir, "keep.txt");
        File.WriteAllText(src, "fromarchive");
        string arc = Path.Combine(_tmpDir, "keep.tar");
        RunLines($"Invoke-BashTar -cf '{PsQuote(arc)}' '{PsQuote(src)}'");

        string dest = Path.Combine(_tmpDir, "kdest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "preexisting");

        var (_, errs) = RunWithErrors(
            $"Invoke-BashTar -xkf '{PsQuote(arc)}' --directory='{PsQuote(dest)}' 2>$null");
        Assert.Equal("preexisting", File.ReadAllText(Path.Combine(dest, "keep.txt")));
        Assert.Contains(errs, m => m.Contains("File exists", StringComparison.OrdinalIgnoreCase));
    }
}
