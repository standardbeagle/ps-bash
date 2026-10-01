using PsBash.Core;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared engine for the md5sum / sha1sum / sha256sum binary cmdlets (GNU coreutils 9.4 semantics):
/// <list type="bullet">
/// <item>Hash mode (<c>FILE...</c>, or stdin with no operand / <c>-</c>): one record per input,
/// <c>HASH  NAME</c> (text), <c>HASH *NAME</c> (<c>-b</c>), <c>ALGO (NAME) = HASH</c> (<c>--tag</c>); a name
/// containing <c>\</c> or a newline is printed escaped with a leading <c>\</c>; <c>-z</c> ends every record with
/// NUL instead of a newline and never escapes. The NAME is the operand AS TYPED (it used to be the resolved
/// absolute path, so a checksum file written by ps-bash named files by absolute path). stdin hashes the exact
/// byte stream (<see cref="BashRuntime.RecordStreamText"/>: <c>printf x | md5sum</c> hashes <c>x</c>, not <c>x\n</c>).</item>
/// <item>Check mode (<c>-c</c>): verifies each line of the listed checksum file(s) — text, binary and BSD
/// <c>--tag</c> formats, hash length per algorithm, blank and <c>#</c> lines skipped — printing
/// <c>NAME: OK|FAILED|FAILED open or read</c> (<c>--quiet</c> hides OK, <c>--status</c> hides everything),
/// GNU's <c>WARNING:</c> summaries on stderr, <c>--warn</c> per-line format diagnostics, <c>--strict</c> (an
/// improper line fails), <c>--ignore-missing</c>. Exit 1 on any failure, unreadable file, or a list with no
/// properly formatted line.</item>
/// </list>
/// Options go through the shared ordered parser (<see cref="ChecksumSpec"/>; usage errors exit 1 like coreutils).
/// Option-combination errors follow GNU ("the --warn option is meaningful only when verifying checksums", ...).
/// </summary>
internal static class ChecksumEngine
{
    private const string OptBinary = "binary", OptCheck = "check", OptIgnoreMissing = "ignore-missing",
        OptQuiet = "quiet", OptStatus = "status", OptStrict = "strict", OptTag = "tag", OptText = "text",
        OptWarn = "warn", OptZero = "zero";

    /// <summary>coreutils <c>md5sum.c</c> option table (<c>--t</c> = '--tag' '--text', <c>--s</c> = '--status' '--strict').</summary>
    private static readonly OptSpecSet ChecksumSpec = new(
        new[]
        {
            new OptSpec(OptBinary, 'b', "binary"),
            new OptSpec(OptCheck, 'c', "check"),
            new OptSpec(OptIgnoreMissing, '\0', "ignore-missing"),
            new OptSpec(OptQuiet, '\0', "quiet"),
            new OptSpec(OptStatus, '\0', "status"),
            new OptSpec(OptStrict, '\0', "strict"),
            new OptSpec(OptTag, '\0', "tag"),
            new OptSpec(OptText, 't', "text"),
            new OptSpec(OptWarn, 'w', "warn"),
            new OptSpec(OptZero, 'z', "zero"),
        },
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "binary", "check", "ignore-missing", "quiet", "status", "strict", "tag", "text", "warn", "zero",
            "help", "version",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, ChecksumSpec);

    internal sealed class ChecksumArgs
    {
        public ParsedArgs Parsed = null!;
        public bool? Binary;          // null = neither -b nor -t given; the last of -b/-t wins
        public bool Check, Warn, Quiet, Status, Strict, IgnoreMissing, Tag, Zero;
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>Scan + GNU's option-combination checks (all exit 1).</summary>
    internal static ChecksumArgs Plan(string command, string[] args)
    {
        var p = new ChecksumArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptBinary: p.Binary = true; break;
                case OptText: p.Binary = false; break;
                case OptCheck: p.Check = true; break;
                case OptWarn: p.Warn = true; break;
                case OptQuiet: p.Quiet = true; break;
                case OptStatus: p.Status = true; break;
                case OptStrict: p.Strict = true; break;
                case OptIgnoreMissing: p.IgnoreMissing = true; break;
                case OptTag: p.Tag = true; break;
                case OptZero: p.Zero = true; break;
            }
        }

        if (!p.Check)
        {
            if (p.IgnoreMissing) p.Error = $"{command}: the --ignore-missing option is meaningful only when verifying checksums";
            else if (p.Status) p.Error = $"{command}: the --status option is meaningful only when verifying checksums";
            else if (p.Warn) p.Error = $"{command}: the --warn option is meaningful only when verifying checksums";
            else if (p.Quiet) p.Error = $"{command}: the --quiet option is meaningful only when verifying checksums";
            else if (p.Strict) p.Error = $"{command}: the --strict option is meaningful only when verifying checksums";
        }
        else
        {
            if (p.Tag) p.Error = $"{command}: the --tag option is meaningless when verifying checksums";
            else if (p.Binary is not null) p.Error = $"{command}: the --binary and --text options are meaningless when verifying checksums";
            else if (p.Zero) p.Error = $"{command}: the --zero option is not supported when verifying checksums";
        }
        return p;
    }

    public static void Run(
        PSCmdlet cmdlet,
        HashAlgorithmName algorithmName,
        string algorithmLabel,
        string commandName,
        string[] arguments,
        ChecksumStdin stdin)
    {
        FileSystemHelpers.SetLastExitCode(cmdlet, 0);
        var plan = Plan(commandName, arguments);
        if (FileSystemHelpers.TryWriteParseError(cmdlet, commandName, plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(cmdlet, commandName, plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(cmdlet, planError);
            FileSystemHelpers.SetLastExitCode(cmdlet, 1);
            return;
        }

        if (plan.Check)
        {
            RunCheck(cmdlet, algorithmName, algorithmLabel, commandName, plan, stdin.Buffered);
            return;
        }

        RunHash(cmdlet, algorithmName, algorithmLabel, commandName, plan, stdin);
    }

    // ---------------------------------------------------------------- hash mode

    private static void RunHash(
        PSCmdlet cmdlet, HashAlgorithmName algorithmName, string algorithmLabel, string commandName,
        ChecksumArgs plan, ChecksumStdin stdin)
    {
        var operands = plan.Operands;
        bool hadError = false;

        // No operand, or a lone `-`: hash stdin. (Only when something was piped: a bare call prints nothing.)
        if (operands.Count == 0 || (operands.Count == 1 && operands[0] == "-" ))
        {
            if (stdin.HasInput || operands.Count == 1)
                cmdlet.WriteObject(MakeOutput(stdin.Hex(), "-", algorithmLabel, plan));
            return;
        }

        foreach (var rawPath in operands)
        {
            if (rawPath == "-")
            {
                cmdlet.WriteObject(MakeOutput(stdin.Hex(), "-", algorithmLabel, plan));
                continue;
            }

            foreach (var op0 in FileSystemHelpers.ResolveOperands(cmdlet, rawPath))
            {
                var op = op0 with { Display = Shown(op0.Display) };
                string filePath = op.Path;
                if (Directory.Exists(filePath))
                {
                    FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: Is a directory");
                    hadError = true;
                    continue;
                }
                if (!File.Exists(filePath))
                {
                    FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: No such file or directory");
                    hadError = true;
                    continue;
                }

                string hex;
                try
                {
                    // Stream-hash in chunks — never load the whole file. A
                    // checksum of a multi-GB file runs in ~80 KB of memory.
                    using var s = BashFileSystem.OpenRead(filePath);
                    hex = ComputeHexFromStream(algorithmName, s);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: {ex.Message}");
                    hadError = true;
                    continue;
                }

                cmdlet.WriteObject(MakeOutput(hex, op.Display, algorithmLabel, plan));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(cmdlet, 1);
    }

    /// <summary>The name to print. On Windows a backslash is a path separator, not a filename character, so it is shown as `/`
    /// (otherwise every Windows path would come out with GNU's `\` escaping); on POSIX the name is kept verbatim.</summary>
    private static string Shown(string display) => OperatingSystem.IsWindows() ? FileSystemHelpers.ToBashPath(display) : display;

    /// <summary>The output line for one input (pure; <c>-z</c> never escapes, like GNU).</summary>
    internal static string FormatLine(string hex, string name, string algorithmLabel, bool binary, bool tag, bool zero)
    {
        bool escape = !zero && (name.Contains('\\') || name.Contains('\n'));
        string shown = escape ? name.Replace("\\", "\\\\").Replace("\n", "\\n") : name;
        string prefix = escape ? "\\" : string.Empty;
        string body = tag
            ? $"{algorithmLabel} ({shown}) = {hex}"
            : $"{hex} {(binary ? '*' : ' ')}{shown}";
        return prefix + body;
    }

    private static PSObject MakeOutput(string hex, string fileName, string algorithmLabel, ChecksumArgs plan)
    {
        string line = FormatLine(hex, fileName, algorithmLabel, plan.Binary == true, plan.Tag, plan.Zero);
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.TextOutput");
        obj.Properties.Add(new PSNoteProperty("BashText", plan.Zero ? line + "\0" : line));
        obj.Properties.Add(new PSNoteProperty("Hash", hex));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("Algorithm", algorithmLabel));
        // -z: the NUL is the terminator; the serializer must not add a newline after it.
        if (plan.Zero) obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        return obj;
    }

    // ---------------------------------------------------------------- check mode

    /// <summary>One parsed checksum-list line (<see cref="TryParseCheckLine"/>).</summary>
    internal readonly record struct CheckLine(string Hash, string Name);

    /// <summary>
    /// Parse a checksum-list line: <c>HASH  NAME</c> / <c>HASH *NAME</c> (hex of exactly
    /// <paramref name="hexLength"/> digits, then a space, then an optional space or <c>*</c>) or the BSD tag
    /// <c>ALGO (NAME) = HASH</c> with this command's <paramref name="algorithmLabel"/>. A leading <c>\</c>
    /// marks an escaped name (<c>\\</c> and <c>\n</c>). Returns false for an improperly formatted line.
    /// </summary>
    internal static bool TryParseCheckLine(string line, string algorithmLabel, int hexLength, out CheckLine parsed)
    {
        parsed = default;
        bool escaped = line.StartsWith('\\');
        string body = escaped ? line.Substring(1) : line;

        string hash, name;
        var tag = Regex.Match(body, @"^" + Regex.Escape(algorithmLabel) + @" \((.+)\) = ([0-9A-Fa-f]+)$");
        if (tag.Success)
        {
            name = tag.Groups[1].Value;
            hash = tag.Groups[2].Value;
        }
        else
        {
            var m = Regex.Match(body, @"^([0-9A-Fa-f]+) (?:[ *](.+)|([^ *].*))$");
            if (!m.Success) return false;
            hash = m.Groups[1].Value;
            name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }
        if (hash.Length != hexLength) return false;

        if (escaped) name = Regex.Replace(name, @"\\(.)", mm => mm.Groups[1].Value == "n" ? "\n" : mm.Groups[1].Value);
        parsed = new CheckLine(hash, name);
        return true;
    }

    private static void RunCheck(
        PSCmdlet cmdlet, HashAlgorithmName algorithmName, string algorithmLabel, string commandName,
        ChecksumArgs plan, IList<PSObject>? pipelineInput)
    {
        int hexLength = ComputeHex(algorithmName, Array.Empty<byte>()).Length;
        bool anyFailure = false;

        // Sources of checksum lines: each operand file, or stdin (no operand / `-`).
        var sources = new List<(string Display, Func<List<string>?> Read)>();
        if (plan.Operands.Count == 0)
        {
            sources.Add(("standard input", () => PipelineLines(pipelineInput)));
        }
        foreach (var raw in plan.Operands)
        {
            if (raw == "-")
            {
                sources.Add(("standard input", () => PipelineLines(pipelineInput)));
                continue;
            }
            foreach (var op0 in FileSystemHelpers.ResolveOperands(cmdlet, raw))
            {
                var captured = op0 with { Display = Shown(op0.Display) };
                sources.Add((captured.Display, () => ReadListFile(cmdlet, commandName, captured)));
            }
        }

        foreach (var (display, read) in sources)
        {
            var lines = read();
            if (lines is null) { anyFailure = true; continue; }
            if (!CheckOneList(cmdlet, algorithmName, algorithmLabel, commandName, plan, display, lines, hexLength))
                anyFailure = true;
        }

        FileSystemHelpers.SetLastExitCode(cmdlet, anyFailure ? 1 : 0);
    }

    private static List<string>? PipelineLines(IList<PSObject>? pipelineInput)
    {
        var lines = new List<string>();
        if (pipelineInput is null) return lines;
        foreach (var item in pipelineInput)
            foreach (var (text, _) in BashRuntime.RecordLines(item)) lines.Add(text);
        return lines;
    }

    private static List<string>? ReadListFile(PSCmdlet cmdlet, string commandName, FileSystemHelpers.OperandPath op)
    {
        if (Directory.Exists(op.Path))
        {
            FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: Is a directory");
            return null;
        }
        if (!File.Exists(op.Path))
        {
            FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: No such file or directory");
            return null;
        }
        try { return new List<string>(BashFileSystem.ReadLines(op.Path)); }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {op.Display}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Verify one checksum list. Returns true when it passed (no failures, nothing unreadable, and —
    /// under <c>--strict</c> — nothing improperly formatted).</summary>
    private static bool CheckOneList(
        PSCmdlet cmdlet, HashAlgorithmName algorithmName, string algorithmLabel, string commandName,
        ChecksumArgs plan, string listDisplay, List<string> lines, int hexLength)
    {
        int properLines = 0, improper = 0, mismatched = 0, unreadable = 0, verified = 0;
        int lineNo = 0;

        foreach (var line in lines)
        {
            lineNo++;
            if (line.Length == 0 || line[0] == '#') continue;

            if (!TryParseCheckLine(line, algorithmLabel, hexLength, out var parsed))
            {
                improper++;
                if (plan.Warn && !plan.Status)
                    FileSystemHelpers.WriteBashError(cmdlet,
                        $"{commandName}: {listDisplay}: {lineNo}: improperly formatted {algorithmLabel} checksum line");
                continue;
            }
            properLines++;

            string shown = parsed.Name.Contains('\\') || parsed.Name.Contains('\n')
                ? "\\" + parsed.Name.Replace("\\", "\\\\").Replace("\n", "\\n")
                : parsed.Name;
            string fpath;
            try { fpath = cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(parsed.Name); }
            catch { fpath = parsed.Name; }

            bool isDir = Directory.Exists(fpath);
            if (isDir || !File.Exists(fpath))
            {
                if (plan.IgnoreMissing && !isDir) continue;
                unreadable++;
                if (!plan.Status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{shown}: FAILED open or read"));
                FileSystemHelpers.WriteBashError(cmdlet,
                    $"{commandName}: {parsed.Name}: {(isDir ? "Is a directory" : "No such file or directory")}");
                continue;
            }

            string actual;
            try
            {
                using var s = BashFileSystem.OpenRead(fpath);
                actual = ComputeHexFromStream(algorithmName, s);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                unreadable++;
                if (!plan.Status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{shown}: FAILED open or read"));
                FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {parsed.Name}: {ex.Message}");
                continue;
            }

            verified++;
            if (string.Equals(actual, parsed.Hash, StringComparison.OrdinalIgnoreCase))
            {
                if (!plan.Status && !plan.Quiet) cmdlet.WriteObject(BashRuntime.NewBashObject($"{shown}: OK"));
            }
            else
            {
                mismatched++;
                if (!plan.Status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{shown}: FAILED"));
            }
        }

        bool ok = true;
        if (properLines == 0)
        {
            FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {listDisplay}: no properly formatted checksum lines found");
            return false;
        }
        if (!plan.Status)
        {
            if (improper > 0)
                FileSystemHelpers.WriteBashError(cmdlet,
                    $"{commandName}: WARNING: {improper} {(improper == 1 ? "line is" : "lines are")} improperly formatted");
            if (unreadable > 0)
                FileSystemHelpers.WriteBashError(cmdlet,
                    $"{commandName}: WARNING: {unreadable} listed {(unreadable == 1 ? "file" : "files")} could not be read");
            if (mismatched > 0)
                FileSystemHelpers.WriteBashError(cmdlet,
                    $"{commandName}: WARNING: {mismatched} computed {(mismatched == 1 ? "checksum" : "checksums")} did NOT match");
        }
        if (plan.IgnoreMissing && verified == 0 && unreadable == 0)
        {
            FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {listDisplay}: no file was verified");
            ok = false;
        }
        if (mismatched > 0 || unreadable > 0 || (plan.Strict && improper > 0)) ok = false;
        return ok;
    }

    private static string ComputeHex(HashAlgorithmName name, byte[] bytes)
    {
        using var hasher = IncrementalHash.CreateHash(name);
        hasher.AppendData(bytes);
        var hashBytes = hasher.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Hash a stream in 80 KB chunks — the streaming counterpart of
    /// <see cref="ComputeHex"/> for file input, so a multi-gigabyte file is never
    /// loaded into memory.
    /// </summary>
    private static string ComputeHexFromStream(HashAlgorithmName name, Stream stream)
    {
        using var hasher = IncrementalHash.CreateHash(name);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>
/// What the md5sum / sha1sum / sha256sum cmdlets do with their pipeline, record by record, so stdin
/// is never held. Hash mode (no operand, or a <c>-</c> operand) feeds each record's exact bytes
/// (<see cref="RecordByteEncoder"/>: text + boundary newline unless unterminated, the same stream
/// <c>RecordStreamText</c> joined) straight into an <see cref="IncrementalHash"/>; check mode keeps the
/// records (a checksum LIST is read as lines, and is small); anything else (a file operand, a usage
/// error, --help) ignores the pipeline exactly as before.
/// </summary>
internal sealed class ChecksumStdin
{
    private enum Mode { Undecided, Ignore, Hash, Buffer }

    private readonly HashAlgorithmName _algorithm;
    private readonly string _command;
    private Mode _mode;
    private IncrementalHash? _hasher;
    private RecordByteEncoder? _bytes;
    private List<PSObject>? _buffered;
    private string? _hex;

    public ChecksumStdin(HashAlgorithmName algorithm, string command)
    {
        _algorithm = algorithm;
        _command = command;
    }

    /// <summary>True once any record arrived in hash mode.</summary>
    public bool HasInput { get; private set; }

    /// <summary>Records fed to the hasher (test seam: none of them are retained).</summary>
    public int Streamed { get; private set; }

    /// <summary>Records retained (check mode only; test seam, 0 in hash mode).</summary>
    public int Retained => _buffered?.Count ?? 0;

    /// <summary>The checksum list for check mode (null when none was piped).</summary>
    public IList<PSObject>? Buffered => _buffered;

    public void Add(PSObject item, string[] args)
    {
        if (_mode == Mode.Undecided) _mode = Decide(args);
        switch (_mode)
        {
            case Mode.Hash:
                _hasher ??= IncrementalHash.CreateHash(_algorithm);
                _bytes ??= new RecordByteEncoder();
                HasInput = true;
                Streamed++;
                _bytes.Encode(item, Append);
                break;
            case Mode.Buffer:
                (_buffered ??= new List<PSObject>()).Add(item);
                break;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes) => _hasher!.AppendData(bytes);

    private Mode Decide(string[] args)
    {
        var plan = ChecksumEngine.Plan(_command, args);
        if (plan.Parsed.HasError || plan.Error is not null
            || plan.Parsed.Has(OptSpecSet.HelpId) || plan.Parsed.Has(OptSpecSet.VersionId))
            return Mode.Ignore;
        bool readsStdin = plan.Operands.Count == 0 || plan.Operands.Contains("-");
        if (!readsStdin) return Mode.Ignore;
        return plan.Check ? Mode.Buffer : Mode.Hash;
    }

    /// <summary>The hex digest of everything streamed (of the empty input when nothing was).</summary>
    public string Hex()
    {
        if (_hex is not null) return _hex;
        _hasher ??= IncrementalHash.CreateHash(_algorithm);
        _bytes?.Finish(Append);
        _hex = Convert.ToHexString(_hasher.GetHashAndReset()).ToLowerInvariant();
        _hasher.Dispose();
        return _hex;
    }
}
/// <summary>
/// Binary cmdlet for <c>md5sum</c>. Delegates to <see cref="ChecksumEngine.Run"/>. Direct PowerShell calls:
/// <c>-c</c> (Confirm) and <c>-w</c> (WarningAction/-WarningVariable/-WhatIf) collide with common parameters
/// and are declared decoy switches, re-injected before the scan.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMd5sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashMd5sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-w</c> (warn). Decoy — ambiguous between <c>-WarningAction</c>/<c>-WarningVariable</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    internal ChecksumStdin Stdin { get; } = new(HashAlgorithmName.MD5, "md5sum");

    private string[] EffectiveArgs() => BashRuntime.PrependDecoys(Arguments, (C.IsPresent, "-c"), (W.IsPresent, "-w"));

    protected override void ProcessRecord()
    {
        if (InputObject != null) Stdin.Add(InputObject, EffectiveArgs());
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.MD5, "MD5", "md5sum",
            EffectiveArgs(), Stdin);
    }
}

/// <summary>Binary cmdlet for <c>sha1sum</c>. See <see cref="InvokeBashMd5sumCommand"/>.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSha1sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashSha1sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-w</c> (warn). Decoy — ambiguous between <c>-WarningAction</c>/<c>-WarningVariable</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    internal ChecksumStdin Stdin { get; } = new(HashAlgorithmName.SHA1, "sha1sum");

    private string[] EffectiveArgs() => BashRuntime.PrependDecoys(Arguments, (C.IsPresent, "-c"), (W.IsPresent, "-w"));

    protected override void ProcessRecord()
    {
        if (InputObject != null) Stdin.Add(InputObject, EffectiveArgs());
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.SHA1, "SHA1", "sha1sum",
            EffectiveArgs(), Stdin);
    }
}

/// <summary>Binary cmdlet for <c>sha256sum</c>. See <see cref="InvokeBashMd5sumCommand"/>.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSha256sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashSha256sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-w</c> (warn). Decoy — ambiguous between <c>-WarningAction</c>/<c>-WarningVariable</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    internal ChecksumStdin Stdin { get; } = new(HashAlgorithmName.SHA256, "sha256sum");

    private string[] EffectiveArgs() => BashRuntime.PrependDecoys(Arguments, (C.IsPresent, "-c"), (W.IsPresent, "-w"));

    protected override void ProcessRecord()
    {
        if (InputObject != null) Stdin.Add(InputObject, EffectiveArgs());
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.SHA256, "SHA256", "sha256sum",
            EffectiveArgs(), Stdin);
    }
}
