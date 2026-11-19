using PsBash.Core;
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
/// <c>--stdout</c> (EXACT bytes: the stream is carried as escaped-byte markers, see RawBytes, so
/// <c>gzip -c f | gzip -dc</c> round-trips; with no operand the pipeline is the input, like stdin), preserves the original file under <c>-k</c> /
/// <c>--keep</c>, requires <c>-f</c> / <c>--force</c> to replace existing output, emits
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
/// mode over the operand's bytes. Decompress and compress stream into a sibling
/// staging file and promote only after completion; under <c>-c</c> they emit the exact output
/// bytes as text records (the old base64 form for compress is gone: the escaped-byte model
/// carries raw bytes through the string pipeline). Under verbose mode, the cmdlet emits
/// one extra <c>PsBash.TextOutput</c> line per file with the ratio.
///
/// Identity: <c>gunzip</c> means "<c>gzip -d</c>" and <c>zcat</c> "<c>gzip -dc</c>". They are their own cmdlets
/// (<see cref="InvokeBashGunzipCommand"/>, <see cref="InvokeBashZcatCommand"/>): the alias name used to be read from
/// InvocationName, which is <c>&amp;</c> under an indirect call (<c>xargs gunzip</c>, <c>env zcat f</c>), and gunzip
/// then COMPRESSED its operand (guard <c>InvocationIdentityGuardTests</c>).
///
/// Status (GNU): an error is 1, a warning 2 unless an error happened (<see cref="Warn"/>); a name in a message or a
/// verbose / list line is the operand as typed (<c>r/f.gz</c>), never the resolved Windows path.
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
public class InvokeBashGzipCommand : BashCmdlet
{
    /// <summary><c>gunzip</c> / <c>zcat</c>: decompress whatever the options say.</summary>
    private protected virtual bool ImpliedDecompress => false;

    /// <summary><c>zcat</c>: write to stdout.</summary>
    private protected virtual bool ImpliedStdout => false;

    // GNU exit status: 0, 1 = error, 2 = warning (only while no error happened).
    private int _status;

    private void Error(string message)
    {
        FileSystemHelpers.WriteStderr(this, message);
        _status = 1;
    }

    /// <summary>GNU's WARN: the message unless suppressed (<c>-q</c>), and status 2 unless an error already made it 1.</summary>
    private void Warn(string message, bool print = true)
    {
        if (print) FileSystemHelpers.WriteStderr(this, message);
        if (_status == 0) _status = 2;
    }

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
            new("no-name", 'n', "no-name"),   // distinct ids: glibc only collapses an abbreviation onto ONE option, so `--n` must stay ambiguous
            new("name", 'N', "name"),
            new(OptQuiet, 'q', "quiet"),
            new(OptQuiet, '\0', "silent"),
            new(OptRecursive, 'r', "recursive"),
            new("rsyncable", '\0', "rsyncable"),
            new("synchronous", '\0', "synchronous"),
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

    /// <summary>Pipeline input: the byte stream gzip compresses / decompresses when no file operand is given.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // ---- stdin pipeline (no file operand), consumed record by record. COMPRESS streams: each
    // record's bytes go through the stateful RawBytes encoder into a GZipStream whose output is
    // emitted as records while it is produced (nothing but the compressor's own window is held).
    // DECOMPRESS / -t must see the whole gzip member before it can inflate, so the COMPRESSED
    // input bytes are kept (a MemoryStream of the real size, not the 3-5x string copies it used
    // to take) and the inflated output streams out in chunks.
    private bool _planned, _stdinMode, _stdinDecompress, _stdinTest;
    private GzipArgs? _stdinPlan;
    private RecordByteEncoder? _recordBytes;
    private GZipStream? _compressor;
    private ByteRecordEmitter? _outRecords;
    private MemoryStream? _compressedInput;
    private ByteSink? _stdinSink;

    /// <summary>Records fed to the stdin stream (test seam).</summary>
    internal int StdinRecords { get; private set; }

    /// <summary>Bytes of compressed input retained for inflating (0 when compressing; test seam).</summary>
    internal long RetainedInputBytes => _compressedInput?.Length ?? 0;

    private string[] EffectiveArgs()
        => BashRuntime.PrependDecoys(Arguments ?? Array.Empty<string>(), (D.IsPresent, "-d"), (C.IsPresent, "-c"), (V.IsPresent, "-v"), (F.IsPresent, "-f"));

    private bool IsDecompress(GzipArgs plan) => plan.Decompress || ImpliedDecompress;

    private static CompressionLevel LevelFor(int level) => level switch
    {
        <= 1 => CompressionLevel.Fastest,
        >= 9 => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        if (!_planned)
        {
            _planned = true;
            var plan = Plan(EffectiveArgs());
            _stdinPlan = plan;
            // Only a plain no-operand run reads the pipeline; usage errors / --help / --version / a
            // file operand leave it untouched (EndProcessing reports them exactly as before).
            _stdinMode = !plan.Parsed.HasError && plan.Operands.Count == 0
                         && !plan.Parsed.Has(OptSpecSet.HelpId) && !plan.Parsed.Has(OptSpecSet.VersionId);
            if (_stdinMode)
            {
                _stdinDecompress = IsDecompress(plan);
                _stdinTest = plan.Test;
                _recordBytes = new RecordByteEncoder();
                _outRecords = new ByteRecordEmitter(WriteObject);
                if (!_stdinDecompress && !_stdinTest)
                {
                    _compressor = new GZipStream(new ByteRecordStream(_outRecords), LevelFor(plan.Level), leaveOpen: false);
                    _stdinSink = bytes => _compressor.Write(bytes);
                }
                else
                {
                    _compressedInput = new MemoryStream();
                    _stdinSink = bytes => _compressedInput.Write(bytes);
                }
            }
        }
        if (!_stdinMode) return;
        StdinRecords++;
        _recordBytes!.Encode(InputObject, _stdinSink!);
    }
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
        try { Run(); }
        finally { if (_status != 0) FileSystemHelpers.SetLastExitCode(this, _status); }
    }

    private void Run()
    {
        // Decoy-bound switches (bare -d/-c/-v/-f never reach Arguments) are re-injected before the scan.
        var raw = Arguments ?? Array.Empty<string>();
        var args = BashRuntime.PrependDecoys(raw, (D.IsPresent, "-d"), (C.IsPresent, "-c"), (V.IsPresent, "-v"), (F.IsPresent, "-f"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "gzip", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "gzip", plan.Parsed)) return;

        bool decompress = plan.Decompress || ImpliedDecompress;
        bool toStdout = plan.ToStdout || ImpliedStdout;
        bool keep = plan.Keep;
        bool force = plan.Force;
        bool verbose = plan.Verbose;
        bool list = plan.List;
        bool test = plan.Test;
        bool recursive = plan.Recursive;
        bool quiet = plan.Quiet;
        string suffix = plan.Suffix;
        int level = plan.Level;

        var operands = plan.Operands;
        if (operands.Count == 0 && StdinRecords > 0)
        {
            FinishStdin();
            return;
        }
        if (operands.Count == 0)
        {
            Error("gzip: missing file operand");
            return;
        }

        // Flatten operands into the concrete file list, each with the NAME GNU shows for it: the operand as typed,
        // a recursive member as OPERAND/rel. A directory operand is expanded under -r; without -r it is skipped with
        // the "is a directory -- ignored" warning (status 2, message suppressed by -q). A missing NAME under -d is
        // (and -l / -t, which read members) retried as NAME<suffix> (`gunzip f` decompresses f.gz), reported under the name tried last.
        var files = new List<(string Path, string Name)>();
        int named = 0;
        foreach (string operand in operands)
        {
            var resolvedAll = FileSystemHelpers.ResolveOperandPaths(this, operand).ToList();
            foreach (string resolved in resolvedAll)
            {
                named++;
                string name = resolvedAll.Count == 1 ? operand : ShellRelative(resolved);
                if (File.Exists(resolved))
                {
                    files.Add((resolved, name));
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
                                files.Add((child, name.TrimEnd('/') + "/" + BashPath.ToShell(Path.GetRelativePath(resolved, child))));
                            }
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            Error($"gzip: {name}: {Failure(ex, resolved)}");
                        }
                    }
                    else
                    {
                        Warn($"gzip: {name} is a directory -- ignored", print: !quiet);
                    }
                }
                else if ((decompress || list || test) && !resolved.EndsWith(suffix, StringComparison.Ordinal))
                {
                    if (File.Exists(resolved + suffix)) files.Add((resolved + suffix, name + suffix));
                    else Error($"gzip: {name}{suffix}: No such file or directory");
                }
                else
                {
                    Error($"gzip: {name}: No such file or directory");
                }
            }
        }

        // -l: GNU's header once, a row per member, and a totals row when more than one name was given. GNU's totals
        // ratio subtracts only the LAST member's header_bytes (0 after a member that was not gzip): kept as is.
        long totalC = 0, totalU = 0, lastHdr = 0;
        bool listed = false;
        void ListRow(long c, long u, long hdr, string label)
        {
            if (!listed)
            {
                WriteObject(BashRuntime.NewBashObject(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0,19} {1,19}  ratio uncompressed_name", "compressed", "uncompressed")));
                listed = true;
            }
            string ratio = Ratio(u - (c - hdr), u);
            string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,19} {1,19} {2} {3}", c, u, ratio, label);
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.GzipListOutput");
            obj.Properties.Add(new PSNoteProperty("BashText", line));
            obj.Properties.Add(new PSNoteProperty("CompressedSize", c));
            obj.Properties.Add(new PSNoteProperty("UncompressedSize", u));
            obj.Properties.Add(new PSNoteProperty("Ratio", ratio.Trim()));
            obj.Properties.Add(new PSNoteProperty("FileName", label));
            WriteObject(obj);
        }

        foreach (var (filePath, name) in files)
        {
            long inputSize;
            try
            {
                inputSize = new FileInfo(filePath).Length;
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                Error($"gzip: {name}: {Failure(ex, filePath)}");
                continue;
            }

            // An empty member: GNU writes a blank line, then the error.
            if (inputSize == 0 && (decompress || test || list))
            {
                Error($"\ngzip: {name}: unexpected end of file");
                continue;
            }
            // -t / --test: decompress and discard to verify integrity. Silent on success.
            if (test)
            {
                try
                {
                    using var input = BashFileSystem.OpenRead(filePath);
                    using var gs = GzipIntegrity.OpenDecoder(input);
                    var tbuf = new byte[81920];
                    while (gs.Read(tbuf, 0, tbuf.Length) > 0) { }
                    if (verbose) FileSystemHelpers.WriteStderr(this, $"{name}:\t OK");   // GNU: verbose lines are stderr
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    Error($"gzip: {name}: not in gzip format");
                }
                continue;
            }

            if (list)
            {
                long uncompressedSize, hdr;
                try
                {
                    // Stream-count the decompressed size — never materialize it. 64-bit: a >2 GB member.
                    using var input = BashFileSystem.OpenRead(filePath);
                    hdr = MemberOverhead(input);
                    input.Position = 0;
                    using var gs = GzipIntegrity.OpenDecoder(input);
                    var cbuf = new byte[81920];
                    long total = 0; int cn;
                    while ((cn = gs.Read(cbuf, 0, cbuf.Length)) > 0) total += cn;
                    uncompressedSize = total;
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    if (ex is InvalidDataException) lastHdr = 0;
                    Error($"gzip: {name}: {Failure(ex, filePath)}");
                    continue;
                }
                ListRow(inputSize, uncompressedSize, hdr, StripSuffix(name, suffix));
                totalC += inputSize; totalU += uncompressedSize; lastHdr = hdr;
                continue;
            }

            if (decompress)
            {
                if (toStdout)
                {
                    // Inflate in chunks straight into records: the decompressed bytes are never
                    // materialised (a bomb used to fill a MemoryStream, then a string, then a split).
                    try
                    {
                        using var input = BashFileSystem.OpenRead(filePath);
                        using var gs = GzipIntegrity.OpenDecoder(input);
                        var records = new ByteRecordEmitter(WriteObject);
                        var chunk = new byte[81920];
                        int got;
                        try
                        {
                            while ((got = gs.Read(chunk, 0, chunk.Length)) > 0) records.Append(chunk.AsSpan(0, got));
                        }
                        finally { records.Finish(); }
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        Error($"gzip: {name}: {Failure(ex, filePath)}");
                    }
                    continue;
                }

                // GNU refuses a name without the suffix (it cannot derive the output name): a warning, which -q
                // (and -r without -v) suppresses together with its status.
                if (!filePath.EndsWith(suffix, StringComparison.Ordinal))
                {
                    if (verbose || (!recursive && !quiet)) Warn($"gzip: {name}: unknown suffix -- ignored");
                    continue;
                }
                string outPath = filePath.Substring(0, filePath.Length - suffix.Length);
                string outName = StripSuffix(name, suffix);
                if (RefuseExistingOutput(outPath, outName, force)) continue;
                long outSize, overhead;
                try
                {
                    using (var probe = BashFileSystem.OpenRead(filePath)) overhead = MemberOverhead(probe);
                    outSize = WriteFileOutput(outPath, force, outFs =>
                    {
                        using var input = BashFileSystem.OpenRead(filePath);
                        using var gs = GzipIntegrity.OpenDecoder(input);
                        gs.CopyTo(outFs);
                    });
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    Error($"gzip: {name}: {Failure(ex, filePath)}");
                    continue;
                }
                if (!keep)
                {
                    // Force-delete: the source may be read-only (e.g. a file inside a .git pack dir).
                    try { FileSystemHelpers.DeleteFileForce(filePath); } catch { /* best effort, oracle parity */ }
                }
                if (verbose)
                    FileSystemHelpers.WriteStderr(this, $"{name}:\t{Ratio(outSize - (inputSize - overhead), outSize)} -- replaced with {outName}");
                continue;
            }

            // GNU refuses to compress a file that already ends in the active suffix: a notice, no status. Without
            // this guard `gzip -r` over a tree with *.gz members re-compressed each into *.gz.gz. -c is exempt.
            if (!toStdout && filePath.EndsWith(suffix, StringComparison.Ordinal))
            {
                if (verbose || (!recursive && !quiet))
                    FileSystemHelpers.WriteStderr(this, $"gzip: {name} already has {suffix} suffix -- unchanged");
                continue;
            }

            CompressionLevel compLevel = LevelFor(level);

            if (toStdout)
            {
                // The compressed stream is binary: it travels the string pipeline as escaped-byte markers
                // (RawBytes), so `gzip -c FILE | gzip -dc` and `gzip -c FILE > f.gz` carry exactly GNU's bytes.
                try
                {
                    var records = new ByteRecordEmitter(WriteObject);
                    try
                    {
                        using var sink = new ByteRecordStream(records);
                        CompressFile(filePath, sink, compLevel);
                    }
                    finally { records.Finish(); }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    Error($"gzip: {name}: {Failure(ex, filePath)}");
                }
                continue;
            }

            string gzPath = filePath + suffix;
            if (RefuseExistingOutput(gzPath, name + suffix, force)) continue;
            long compSize;
            try
            {
                compSize = WriteFileOutput(gzPath, force, outFs => CompressFile(filePath, outFs, compLevel));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                Error($"gzip: {name}: {Failure(ex, filePath)}");
                continue;
            }
            if (!keep)
            {
                try { FileSystemHelpers.DeleteFileForce(filePath); } catch { /* best effort, oracle parity */ }
            }
            if (verbose)
            {
                // Our member has a bare 10-byte header (no stored name) and the 8-byte trailer.
                FileSystemHelpers.WriteStderr(this,
                    $"{name}:\t{Ratio(inputSize - (compSize - 18), inputSize)} -- replaced with {name}{suffix}");
            }
        }

        if (listed && named > 1) ListRow(totalC, totalU, lastHdr, "(totals)");
    }

    private string ShellRelative(string resolved)
    {
        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        string rel = Path.GetRelativePath(cwd, resolved);
        return BashPath.ToShell(rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? resolved : rel);
    }

    /// <summary>GNU's words for a failed member: a bad stream is "not in gzip format", an I/O error strerror.</summary>
    private static string Failure(Exception ex, string path)
        => ex is InvalidDataException ? "not in gzip format" : FileSystemHelpers.ReadErrorMessage(ex, path);

    private static string StripSuffix(string name, string suffix)
        => name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length ? name[..^suffix.Length] : name;

    /// <summary>
    /// GNU's <c>display_ratio</c>: per-mille, truncated, a sign column (<c>-33.3%</c>, <c> 50.0%</c>,
    /// <c>-100.0%</c>). Pure (unit-test seam).
    /// </summary>
    internal static string Ratio(long num, long den)
    {
        // gzip 1.12: fprintf("%5.1f%%", den == 0 ? 0 : 100.0 * num / den) — rounded, the sign beside the digits
        // (" -0.2%", "  0.0%", "-100.0%"). The pre-1.10 integer form truncated and put the sign in column 1.
        double r = den == 0 ? 0 : 100.0 * num / den;
        return r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) + "%";
    }

    /// <summary>
    /// A gzip member's bytes that are not deflate data: the header (10 bytes, plus FEXTRA / FNAME / FCOMMENT /
    /// FHCRC when flagged) and the 8-byte trailer — GNU's <c>header_bytes</c>, which its ratios leave out. Reads from
    /// the stream's start; a stream too short for a header counts the fixed 18. Pure (unit-test seam).
    /// </summary>
    internal static long MemberOverhead(Stream s)
    {
        Span<byte> head = stackalloc byte[10];
        if (s.ReadAtLeast(head, 10, throwOnEndOfStream: false) < 10) return 18;
        long len = 10;
        byte flags = head[3];
        if ((flags & 4) != 0)
        {
            int lo = s.ReadByte(), hi = s.ReadByte();
            if (lo < 0 || hi < 0) return len + 8;
            int xlen = lo | (hi << 8);
            len += 2 + xlen;
            s.Seek(xlen, SeekOrigin.Current);
        }
        foreach (int bit in new[] { 8, 16 })
        {
            if ((flags & bit) == 0) continue;
            int b;
            do { b = s.ReadByte(); len++; } while (b > 0);
        }
        if ((flags & 2) != 0) len += 2;
        return len + 8;
    }
    /// <summary>
    /// gzip with no file operand: the pipeline (the exact byte stream, consumed in
    /// <see cref="ProcessRecord"/>) is compressed — or, under <c>-d</c> / <c>zcat</c>, decompressed —
    /// to stdout, GNU's stdin-to-stdout filter mode. Compress has already streamed its output; this
    /// closes the gzip trailer, or inflates the retained compressed input chunk by chunk.
    /// </summary>
    private void FinishStdin()
    {
        try
        {
            if (_compressor is not null)
            {
                _recordBytes!.Finish(_stdinSink!);
                _compressor.Dispose();   // writes the trailer through the record stream
                _outRecords!.Finish();
                return;
            }

            _recordBytes!.Finish(_stdinSink!);
            _compressedInput!.Position = 0;
            using var gs = GzipIntegrity.OpenDecoder(_compressedInput);
            if (_stdinTest)
            {
                gs.CopyTo(Stream.Null);
                return;
            }
            var chunk = new byte[81920];
            int got;
            try
            {
                while ((got = gs.Read(chunk, 0, chunk.Length)) > 0) _outRecords!.Append(chunk.AsSpan(0, got));
            }
            finally { _outRecords!.Finish(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Error("gzip: stdin: not in gzip format");
        }
    }
    // RFC 1952 member: header, an empty final deflate block, CRC32=0, ISIZE=0.
    // On .NET 8/10 GZipStream can emit no bytes at all when no input is written.
    private static readonly byte[] EmptyMember =
        [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 255, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static void CompressFile(string path, Stream output, CompressionLevel level)
    {
        using var input = BashFileSystem.OpenRead(path);
        int first = input.ReadByte();
        if (first < 0)
        {
            output.Write(EmptyMember);
            return;
        }
        using var gzip = new GZipStream(output, level, leaveOpen: true);
        gzip.WriteByte((byte)first);
        input.CopyTo(gzip);
    }

    /// <summary>GNU (stdin not a terminal): <c>NAME already exists;\tnot overwritten</c>, a warning even under -q.</summary>
    private bool RefuseExistingOutput(string path, string name, bool force)
    {
        if (force || !File.Exists(path)) return false;
        Warn($"gzip: {name} already exists;\tnot overwritten");
        return true;
    }

    // Complete and close the stream before replacing any destination. A corrupt input,
    // full disk or failed promotion must leave both the input and any old output intact.
    private static long WriteFileOutput(string path, bool force, Action<FileStream> write)
    {
        string staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            ".psbash-gzip-" + Guid.NewGuid().ToString("N"));
        try
        {
            long length;
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(output);
                length = output.Length;
            }
            // overwrite:false also protects outputs created after the existence check.
            File.Move(staging, path, overwrite: force);
            return length;
        }
        finally
        {
            try { FileSystemHelpers.DeleteFileForce(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary><c>gunzip</c> = <c>gzip -d</c>, its own cmdlet so the identity survives an indirect call.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGunzip")]
[OutputType(typeof(string))]
public sealed class InvokeBashGunzipCommand : InvokeBashGzipCommand
{
    private protected override bool ImpliedDecompress => true;
}

/// <summary><c>zcat</c> = <c>gzip -dc</c>, its own cmdlet so the identity survives an indirect call.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashZcat")]
[OutputType(typeof(string))]
public sealed class InvokeBashZcatCommand : InvokeBashGzipCommand
{
    private protected override bool ImpliedDecompress => true;
    private protected override bool ImpliedStdout => true;
}