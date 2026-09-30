using System.IO.Compression;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashGzip</c> function
/// (REFACTOR-2 follow-on). Reproduces GNU coreutils <c>gzip</c> /
/// <c>gunzip</c> / <c>zcat</c>: compresses each operand file in place
/// (replacing it with <c>{PATH}.gz</c>) by default, decompresses with
/// <c>-d</c> / <c>--decompress</c> (stripping the <c>.gz</c> suffix),
/// writes the (de)compressed bytes to stdout under <c>-c</c> /
/// <c>--stdout</c>, preserves the original file under <c>-k</c> /
/// <c>--keep</c>, accepts <c>-f</c> / <c>--force</c> for arg-compat, emits
/// a per-file ratio line under <c>-v</c> / <c>--verbose</c>, and emits a
/// fixed-width listing under <c>-l</c> / <c>--list</c>. Compression level
/// follows the <c>-1</c>..<c>-9</c> flags (1 = Fastest, 9 = SmallestSize,
/// else Optimal — same .NET <see cref="CompressionLevel"/> ladder as the
/// psm1 oracle).
///
/// Behavioral parity oracle: the psm1 <c>Invoke-BashGzip</c> function.
/// Each operand is resolved via the unresolved-provider-path slice (matching
/// the oracle's <c>Resolve-BashGlob</c>); a missing path emits a bash-style
/// "No such file or directory" error via
/// <see cref="FileSystemHelpers.WriteBashError"/> and the cmdlet continues
/// with subsequent operands. The <c>-l</c> path computes a percentage
/// ratio using <see cref="GZipStream"/> in <see cref="CompressionMode.Decompress"/>
/// mode over the operand's bytes. Decompress and compress both write back to
/// disk via <see cref="File.WriteAllBytes"/>; under <c>-c</c> they emit one
/// <c>PsBash.TextOutput</c> object (a UTF-8 string for decompress, a base64
/// string for compress — the oracle did this on purpose because raw bytes
/// don't survive a string pipeline). Under verbose mode, the cmdlet emits
/// one extra <c>PsBash.TextOutput</c> line per file with the ratio.
///
/// Alias detection: the bash <c>gunzip</c> alias means "<c>gzip -d</c>" and
/// <c>zcat</c> means "<c>gzip -dc</c>". The psm1 oracle reads
/// <c>$MyInvocation.InvocationName</c>; the cmdlet reads
/// <see cref="System.Management.Automation.PSCmdlet.MyInvocation"/>.<c>InvocationName</c>
/// and applies the same default-flag boost.
///
/// Flag binding: <c>-d</c> / <c>-c</c> / <c>-v</c> prefix-collide with
/// <c>-Debug</c> / <c>-Confirm</c> / <c>-Verbose</c> common parameters
/// (binder routes the bare token by exact parameter name — beats a
/// common-parameter prefix match). They are therefore declared as
/// <see cref="SwitchParameter"/>s literally named <c>D</c> / <c>C</c> /
/// <c>V</c>. <c>-f</c> has no PowerShell common-parameter prefix overlap
/// but is declared as a <see cref="SwitchParameter"/> <c>F</c> for symmetry
/// (it keeps the bare token from sliding into <see cref="Arguments"/>'s
/// catch-all and is recovered post-parse via the same flag flag).
/// <c>-k</c> / <c>-l</c> / <c>-1</c>..<c>-9</c> have no common-parameter
/// prefix collision and stay in <see cref="Arguments"/>; bundled forms
/// (<c>-dk</c>, <c>-cv</c>, <c>-9v</c>, etc.) are recovered by the manual
/// post-parse scan, exactly as the oracle's <c>foreach ($ch in
/// $arg.Substring(1).ToCharArray())</c> loop did.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c>
/// delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// File-read / -write failures route through
/// <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGzip")]
[OutputType(typeof(string))]
public sealed class InvokeBashGzipCommand : PSCmdlet
{
    private const string OptDecompress = "decompress", OptStdout = "stdout", OptKeep = "keep", OptForce = "force",
        OptVerbose = "verbose", OptList = "list", OptTest = "test", OptRecursive = "recursive", OptQuiet = "quiet",
        OptSuffix = "suffix", OptNoOp = "noop", OptLevelPrefix = "level";

    /// <summary>GNU gzip 1.12 options ps-bash refuses (exit 2): the LZW (<c>compress</c>) format and its bit width.</summary>
    private static readonly string[] GzipValidButUnsupported = { "-Z", "--lzw", "-b", "--bits" };

    private static OptSpec[] BuildGzipSpecs()
    {
        var specs = new List<OptSpec>
        {
            new(OptNoOp, 'a', "ascii"),
            new(OptStdout, 'c', "to-stdout"),
            new(OptStdout, '\0', "stdout"),
            new(OptDecompress, 'd', "decompress"),
            new(OptDecompress, '\0', "uncompress"),
            new(OptForce, 'f', "force"),
            new(OptSpecSet.HelpId, 'h', "help"),
            new(OptSpecSet.HelpId, 'H', null),
            new(OptKeep, 'k', "keep"),
            new(OptList, 'l', "list"),
            new(OptSpecSet.VersionId, 'L', "license"),  // GNU prints the license; ps-bash prints its version line
            new(OptNoOp, 'm', null),
            new(OptNoOp, 'M', null),
            new(OptNoOp, 'n', "no-name"),
            new(OptNoOp, 'N', "name"),
            new(OptQuiet, 'q', "quiet"),
            new(OptQuiet, '\0', "silent"),
            new(OptRecursive, 'r', "recursive"),
            new(OptNoOp, '\0', "rsyncable"),
            new(OptNoOp, '\0', "synchronous"),
            new(OptSuffix, 'S', "suffix", OptKind.Value),
            new(OptTest, 't', "test"),
            new(OptVerbose, 'v', "verbose"),
            new(OptSpecSet.VersionId, 'V', "version"),
            new(OptLevelPrefix + "1", '\0', "fast"),
            new(OptLevelPrefix + "9", '\0', "best"),
        };
        for (char d = '1'; d <= '9'; d++) specs.Add(new OptSpec(OptLevelPrefix + d, d, null));
        return specs.ToArray();
    }

    /// <summary>gzip's option surface (GNU 1.12). Usage errors exit 1 (gzip ERROR status); ambiguity lists
    /// follow gzip's <c>long_options[]</c> order (<c>--s</c> = '--stdout' '--silent' '--synchronous' '--suffix').
    /// <c>-a -m -M -n -N --rsyncable --synchronous</c> are accepted no-ops: the .NET GZipStream header has no
    /// name/time/text-mode controls, and there is no fsync-flush or rsync-friendly mode.</summary>
    private static readonly OptSpecSet GzipSpec = new(
        BuildGzipSpecs(),
        GzipValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "ascii", "to-stdout", "stdout", "decompress", "uncompress", "force", "help", "keep", "list", "license",
            "no-name", "name", "quiet", "silent", "recursive", "rsyncable", "synchronous", "suffix", "test",
            "verbose", "version", "fast", "best", "lzw", "bits",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, GzipSpec);

    internal sealed class GzipArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Decompress, ToStdout, Keep, Force, Verbose, List, Test, Recursive, Quiet;
        public string Suffix = ".gz";
        public int Level = 6;
        public List<string> Operands = new();
    }

    /// <summary>
    /// Scan in GNU's order (last of <c>-1..-9/--fast/--best</c> wins, last <c>-S</c> wins). Fixes over the
    /// old hand scan: a dangling <c>-S</c> was ignored, <c>--</c>/abbreviations (<c>--dec</c>, <c>--std</c>)
    /// were not honoured (an unknown long option fell through as a file name), unknown long options exit 1
    /// (was 2), <c>-h</c>/<c>-V</c>/<c>-L</c> were not options, <c>-10</c> = <c>-1</c> then invalid <c>-0</c>.
    /// </summary>
    internal static GzipArgs Plan(string[] args)
    {
        var p = new GzipArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptDecompress: p.Decompress = true; break;
                case OptStdout: p.ToStdout = true; break;
                case OptKeep: p.Keep = true; break;
                case OptForce: p.Force = true; break;
                case OptVerbose: p.Verbose = true; break;
                case OptList: p.List = true; break;
                case OptTest: p.Test = true; break;
                case OptRecursive: p.Recursive = true; break;
                case OptQuiet: p.Quiet = true; break;
                case OptSuffix: p.Suffix = tok.Value!; break;
                default:
                    if (tok.OptId is { } id && id.StartsWith(OptLevelPrefix, StringComparison.Ordinal))
                        p.Level = id[OptLevelPrefix.Length] - '0';
                    break;
            }
        }
        return p;
    }
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The bash <c>-d</c> (decompress) switch — explicit because the bare token
    /// <c>-d</c> prefix-collides with <c>-Debug</c>.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>The bash <c>-c</c> (to stdout) switch — explicit because the bare token
    /// <c>-c</c> prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    /// <summary>The bash <c>-v</c> (verbose) switch — explicit because the bare token
    /// <c>-v</c> prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    /// <summary>The bash <c>-f</c> (force) switch — declared for symmetry. No
    /// common-parameter prefix collision; oracle parity is accept-and-ignore beyond
    /// allowing overwrite (which <see cref="File.WriteAllBytes"/> already does).</summary>
    [Parameter]
    public SwitchParameter F { get; set; }

    protected override void EndProcessing()
    {
        // Decoy-bound switches (bare -d/-c/-v/-f never reach Arguments) are re-injected before the scan.
        var raw = Arguments ?? Array.Empty<string>();
        var args = BashRuntime.PrependDecoys(raw, (D.IsPresent, "-d"), (C.IsPresent, "-c"), (V.IsPresent, "-v"), (F.IsPresent, "-f"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "gzip", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "gzip", plan.Parsed)) return;

        bool decompress = plan.Decompress;
        bool toStdout = plan.ToStdout;
        bool keep = plan.Keep;
        bool force = plan.Force;
        bool verbose = plan.Verbose;
        bool list = plan.List;
        bool test = plan.Test;
        bool recursive = plan.Recursive;
        bool quiet = plan.Quiet;
        string suffix = plan.Suffix;
        int level = plan.Level;

        // Detect gunzip / zcat invocation via alias name. Matches the psm1
        // oracle's `$MyInvocation.InvocationName -eq 'gunzip'` branch.
        string invokedAs = MyInvocation?.InvocationName ?? string.Empty;
        if (string.Equals(invokedAs, "gunzip", StringComparison.OrdinalIgnoreCase))
        {
            decompress = true;
        }
        if (string.Equals(invokedAs, "zcat", StringComparison.OrdinalIgnoreCase))
        {
            decompress = true;
            toStdout = true;
        }

        var operands = plan.Operands;
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "gzip: missing file operand");
            return;
        }

        // Suppress unused warnings — `force` and `keep` are read above through
        // the manual scan into the same variables; touch both so the build
        // doesn't strip the local under nullability/clean analyzers.
        _ = force;

        // Flatten operands into the concrete file list. A directory operand is
        // expanded into its contained files under -r/--recursive; without -r it
        // is skipped with the GNU "is a directory -- ignored" warning (suppressed
        // by -q). A missing path errors and is skipped.
        var files = new List<string>();
        foreach (string operand in operands)
        {
            foreach (string resolved in FileSystemHelpers.ResolveOperandPaths(this, operand))
            {
                if (File.Exists(resolved))
                {
                    files.Add(resolved);
                }
                else if (Directory.Exists(resolved))
                {
                    if (recursive)
                    {
                        try
                        {
                            foreach (var child in Directory.EnumerateFiles(
                                         resolved, "*", SearchOption.AllDirectories))
                            {
                                files.Add(child);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            FileSystemHelpers.WriteBashError(this, $"gzip: {resolved.Replace('\\', '/')}: {ex.Message}");
                        }
                    }
                    else if (!quiet)
                    {
                        FileSystemHelpers.WriteBashError(this, $"gzip: {resolved.Replace('\\', '/')} is a directory -- ignored");
                    }
                }
                else
                {
                    // GNU gzip -d retries NAME as NAME<suffix> and reports the name it tried last.
                    string tried = decompress && !resolved.EndsWith(suffix, StringComparison.Ordinal)
                        ? resolved + suffix : resolved;
                    FileSystemHelpers.WriteBashError(this, $"gzip: {tried.Replace('\\', '/')}: No such file or directory");
                }
            }
        }

        {
            foreach (string filePath in files)
            {
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                {
                    string normalized = filePath.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: No such file or directory");
                    continue;
                }

                long inputSize;
                try
                {
                    inputSize = new FileInfo(filePath).Length;
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    string normalized = filePath.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                    continue;
                }

                // -t / --test: decompress and discard to verify integrity. Silent
                // on success (exit 0); a corrupt stream errors and sets exit 1.
                if (test)
                {
                    try
                    {
                        using var input = BashFileSystem.OpenRead(filePath);
                        using var gs = new GZipStream(input, CompressionMode.Decompress);
                        var tbuf = new byte[81920];
                        while (gs.Read(tbuf, 0, tbuf.Length) > 0) { }
                        if (verbose) WriteObject(BashRuntime.NewBashObject($"{filePath}: OK"));
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        FileSystemHelpers.WriteBashError(this, $"gzip: {filePath.Replace('\\', '/')}: not in gzip format");
                        FileSystemHelpers.SetLastExitCode(this, 1);
                    }
                    continue;
                }

                if (list)
                {
                    long compressedSize = inputSize;
                    long uncompressedSize;
                    try
                    {
                        // Stream-count the decompressed size — never materialize it.
                        // Keep it 64-bit: a >2 GB member overflows an int cast and
                        // prints a negative/wrapped size and bogus ratio.
                        using var input = BashFileSystem.OpenRead(filePath);
                        using var gs = new GZipStream(input, CompressionMode.Decompress);
                        var cbuf = new byte[81920];
                        long total = 0; int cn;
                        while ((cn = gs.Read(cbuf, 0, cbuf.Length)) > 0) total += cn;
                        uncompressedSize = total;
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        string normalized = filePath.Replace('\\', '/');
                        FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                        continue;
                    }
                    string ratio = uncompressedSize > 0
                        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0:F1}%", (1.0 - ((double)compressedSize / uncompressedSize)) * 100)
                        : "0.0%";
                    string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0,10} {1,10} {2,6} {3}", compressedSize, uncompressedSize, ratio, filePath);
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.GzipListOutput");
                    obj.Properties.Add(new PSNoteProperty("BashText", line));
                    obj.Properties.Add(new PSNoteProperty("CompressedSize", compressedSize));
                    obj.Properties.Add(new PSNoteProperty("UncompressedSize", uncompressedSize));
                    obj.Properties.Add(new PSNoteProperty("Ratio", ratio));
                    obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                    WriteObject(obj);
                    continue;
                }

                if (decompress)
                {
                    if (toStdout)
                    {
                        string text;
                        try
                        {
                            using var input = BashFileSystem.OpenRead(filePath);
                            using var gs = new GZipStream(input, CompressionMode.Decompress);
                            using var buf = new MemoryStream();
                            gs.CopyTo(buf);
                            text = Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            string normalized = filePath.Replace('\\', '/');
                            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                            continue;
                        }
                        WriteObject(BashRuntime.NewBashObject(text));
                    }
                    else
                    {
                        // GNU gzip refuses to decompress a file whose name lacks the
                        // expected suffix (it cannot derive the output name) — warn
                        // and skip rather than overwrite the input in place.
                        if (!filePath.EndsWith(suffix, StringComparison.Ordinal))
                        {
                            if (!quiet)
                            {
                                FileSystemHelpers.WriteBashError(this,
                                    $"gzip: {filePath.Replace('\\', '/')}: unknown suffix -- ignored");
                            }
                            continue;
                        }
                        string outPath = filePath.Substring(0, filePath.Length - suffix.Length);
                        long outSize;
                        try
                        {
                            // Stream decompress straight into the output file — the
                            // decompressed content is never held in memory.
                            using (var input = BashFileSystem.OpenRead(filePath))
                            using (var gs = new GZipStream(input, CompressionMode.Decompress))
                            using (var outFs = File.Create(outPath))
                            {
                                gs.CopyTo(outFs);
                            }
                            outSize = new FileInfo(outPath).Length;
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            string normalized = filePath.Replace('\\', '/');
                            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                            continue;
                        }
                        if (!keep)
                        {
                            // Force-delete: the source may be read-only (e.g. a
                            // file inside a .git pack dir); bare File.Delete throws
                            // UnauthorizedAccess on Windows and the original lingers.
                            try { FileSystemHelpers.DeleteFileForce(filePath); } catch { /* best effort, oracle parity */ }
                        }
                        if (verbose)
                        {
                            string ratio = outSize > 0
                                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "{0:F1}%", (1.0 - ((double)inputSize / outSize)) * 100)
                                : "0.0%";
                            WriteObject(BashRuntime.NewBashObject($"{filePath}: {ratio}"));
                        }
                    }
                }
                else
                {
                    // GNU gzip refuses to compress a file that already ends in the
                    // active suffix ("already has .gz suffix -- unchanged"). Without
                    // this guard `gzip -r` over a tree containing *.gz members would
                    // re-compress each into *.gz.gz and delete the original. The
                    // -c/stdout path is exempt (it never renames/deletes, matching GNU).
                    if (!toStdout && filePath.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        if (!quiet)
                        {
                            FileSystemHelpers.WriteBashError(this,
                                $"gzip: {filePath.Replace('\\', '/')} already has {suffix} suffix -- unchanged");
                        }
                        continue;
                    }

                    CompressionLevel compLevel = level switch
                    {
                        <= 1 => CompressionLevel.Fastest,
                        >= 9 => CompressionLevel.SmallestSize,
                        _ => CompressionLevel.Optimal,
                    };

                    if (toStdout)
                    {
                        // The psm1 oracle base64-emits compressed bytes because a
                        // string pipeline cannot carry arbitrary bytes — keep that
                        // so a `gzip -c FILE | base64 -d` chain round-trips. base64
                        // needs the whole blob, so the compressed output is
                        // materialized here (the input is still streamed in).
                        byte[] compressedBytes;
                        try
                        {
                            using var ms = new MemoryStream();
                            using (var gs = new GZipStream(ms, compLevel, leaveOpen: true))
                            using (var input = BashFileSystem.OpenRead(filePath))
                            {
                                input.CopyTo(gs);
                            }
                            compressedBytes = ms.ToArray();
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            string normalized = filePath.Replace('\\', '/');
                            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                            continue;
                        }
                        string b64 = Convert.ToBase64String(compressedBytes);
                        WriteObject(BashRuntime.NewBashObject(b64));
                    }
                    else
                    {
                        string outPath = filePath + suffix;
                        long compSize;
                        try
                        {
                            // Stream the input straight through the compressor to
                            // disk — neither the raw nor the compressed bytes are
                            // ever fully held in memory.
                            using (var input = BashFileSystem.OpenRead(filePath))
                            using (var outFs = File.Create(outPath))
                            using (var gs = new GZipStream(outFs, compLevel))
                            {
                                input.CopyTo(gs);
                            }
                            compSize = new FileInfo(outPath).Length;
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            string normalized = filePath.Replace('\\', '/');
                            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                            continue;
                        }
                        if (!keep)
                        {
                            try { FileSystemHelpers.DeleteFileForce(filePath); } catch { /* best effort, oracle parity */ }
                        }
                        if (verbose)
                        {
                            string ratio = inputSize > 0
                                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "{0:F1}%", (1.0 - ((double)compSize / inputSize)) * 100)
                                : "0.0%";
                            WriteObject(BashRuntime.NewBashObject($"{filePath}: {ratio}"));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// File.WriteAllBytes with the oracle's bash-style error contract:
    /// emit "gzip: PATH: MESSAGE" via FileSystemHelpers.WriteBashError on
    /// failure and signal the caller (return false) so the per-operand loop
    /// continues with subsequent operands.
    /// </summary>
    private bool TryWriteAllBytes(string path, byte[] data)
    {
        try
        {
            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
            return false;
        }
    }
}
