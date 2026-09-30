using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSplit</c> function
/// (REFACTOR-2 follow-on). Partitions a file (or pipeline content) into pieces
/// of <c>-l N</c> lines each, naming them <c>{PREFIX}{suffix}</c> where the
/// suffix is alphabetic (<c>aa</c>, <c>ab</c>, …, <c>zz</c>, <c>aaa</c>, …) by
/// default, or zero-padded numeric with <c>-d</c>. PREFIX defaults to
/// <c>x</c>; suffix length defaults to <c>2</c>.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact behavior:
/// <list type="bullet">
/// <item><c>-l N</c> / <c>--lines=N</c> — lines per piece, default 1000.</item>
/// <item><c>-d</c> / <c>--numeric-suffixes</c> — numeric suffix instead of
/// alphabetic.</item>
/// <item><c>-a N</c> / <c>--suffix-length=N</c> — suffix length, default 2.</item>
/// <item>Positional operands: <c>FILE [PREFIX]</c>; <c>-</c> reads from
/// pipeline. With no operands and pipeline input, falls back to stdin mode
/// with the default <c>x</c> prefix. With no operands and no pipeline,
/// emits a bash-style "missing operand" error.</item>
/// <item>Output files written via <see cref="File.WriteAllText(string,string)"/>
/// to the current working directory (resolved against <c>$PWD</c>) — exact
/// parity with the oracle's <c>Join-Path $PWD $outName</c> behavior.</item>
/// </list>
///
/// Common-parameter collisions:
/// <list type="bullet">
/// <item><c>-d</c> prefix-collides with <c>-Debug</c>; declared as the explicit
/// <see cref="D"/> <see cref="SwitchParameter"/>.</item>
/// <item><c>-a N</c>: the bare token <c>-a</c> prefix-matches the cmdlet's own
/// <see cref="Arguments"/> parameter; declared as the explicit
/// <see cref="A"/> int parameter so <c>-a 3</c> binds cleanly.</item>
/// <item><c>-l</c> has no PowerShell common-parameter prefix collision, so it
/// stays in <see cref="Arguments"/> and is parsed by the manual value-flag
/// scan below.</item>
/// </list>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSplit")]
[OutputType(typeof(string))]
public sealed class InvokeBashSplitCommand : PSCmdlet
{
    /// <summary>
    /// Valid GNU <c>split</c> options ps-bash does not implement, refused loudly (exit 2) instead of
    /// the misleading "No such file or directory". (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] SplitValidButUnsupported =
    {
        "-n", "--number",
        "-C", "--line-bytes",
        "-t", "--separator",
        "--filter",
        "--verbose",
        "-e", "--elide-empty-files",
        "-x", "--hex-suffixes",
        "-u", "--unbuffered",
    };

    private const string OptLines = "lines", OptBytes = "bytes", OptSuffixLen = "suffixlen",
        OptNumeric = "numeric", OptAddSuffix = "addsuffix";

    /// <summary>
    /// split's option surface (GNU coreutils 9.4). Implemented: -l/--lines, -b/--bytes (GNU SIZE
    /// suffixes), -a/--suffix-length, -d and --numeric-suffixes[=FROM], --additional-suffix, and the
    /// obsolete <c>-NUM</c> (= <c>-l NUM</c>).
    /// </summary>
    private static readonly OptSpecSet SplitSpec = new(
        new[]
        {
            new OptSpec(OptLines, 'l', "lines", OptKind.Value),
            new OptSpec(OptBytes, 'b', "bytes", OptKind.Value),
            new OptSpec(OptSuffixLen, 'a', "suffix-length", OptKind.Value),
            new OptSpec(OptNumeric, 'd', null),
            new OptSpec(OptNumeric, '\0', "numeric-suffixes", OptKind.OptionalValue),
            new OptSpec(OptAddSuffix, '\0', "additional-suffix", OptKind.Value),
        },
        validButUnsupported: SplitValidButUnsupported,
        allowAbbrev: true,
        numericShorthandId: OptLines,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, SplitSpec);

    internal sealed class SplitArgs
    {
        public ParsedArgs Parsed = null!;
        public int Lines = 1000;
        public long? Bytes;
        public int SuffixLength = 2;
        public bool Numeric;
        public int NumericStart;
        public string AdditionalSuffix = string.Empty;
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate like GNU (exit 1): counts are positive (<c>-l 0</c>, <c>-l x</c>, <c>-b 1x</c>
    /// are errors — the old scan silently used 1000 lines), <c>-b</c> takes GNU SIZE suffixes
    /// (K M G ... KB MB ...; the old scan knew only K/M/G and read <c>1KB</c> as 1),
    /// <c>-l</c> together with <c>-b</c> is "cannot split in more than one way", and a third operand
    /// is "extra operand".
    /// </summary>
    internal static SplitArgs Plan(string[] args)
    {
        var s = new SplitArgs { Parsed = ScanArgs(args) };
        s.Operands = s.Parsed.Operands();
        if (s.Parsed.HasError) return s;

        foreach (var tok in s.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string v = tok.Value ?? string.Empty;
            switch (tok.OptId)
            {
                case OptLines:
                    if (!TryPositive(v, out int lines)) { s.Error = $"split: invalid number of lines: '{v}'"; return s; }
                    s.Lines = lines;
                    break;
                case OptBytes:
                    if (!GnuNumber.TryParse(v, out int bytes, out char sign) || sign != '\0' || bytes < 1)
                    { s.Error = $"split: invalid number of bytes: '{v}'"; return s; }
                    s.Bytes = bytes;
                    break;
                case OptSuffixLen:
                    if (!TryDigits(v, out int len)) { s.Error = $"split: invalid suffix length: '{v}'"; return s; }
                    s.SuffixLength = len < 1 ? 2 : len;
                    break;
                case OptNumeric:
                    s.Numeric = true;
                    if (tok.Value is { Length: > 0 } from)
                    {
                        if (!TryDigits(from, out int start)) { s.Error = $"split: invalid suffix start: '{from}'"; return s; }
                        s.NumericStart = start;
                    }
                    break;
                case OptAddSuffix:
                    s.AdditionalSuffix = v;
                    break;
            }
        }

        if (s.Parsed.Has(OptLines) && s.Parsed.Has(OptBytes))
        {
            s.Error = "split: cannot split in more than one way";
            return s;
        }
        if (s.Operands.Count > 2)
        {
            s.Error = $"split: extra operand '{s.Operands[2]}'";
            return s;
        }
        return s;
    }

    private static bool TryDigits(string s, out int n)
    {
        n = 0;
        if (s.Length == 0) return false;
        long v = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            v = Math.Min(v * 10 + (c - '0'), int.MaxValue);
        }
        n = (int)v;
        return true;
    }

    private static bool TryPositive(string s, out int n) => TryDigits(s, out n) && n >= 1;
    /// <summary>The bash <c>-d</c> (numeric suffixes) switch.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>The bash <c>-a N</c> (suffix length) value flag.</summary>
    [Parameter]
    public int? A { get; set; }

    /// <summary>Decoy for the unsupported <c>-e</c> (--elide-empty-files). Bare
    /// <c>-e</c> prefix-collides with <c>-ErrorAction</c>/<c>-ErrorVariable</c> and
    /// crashed the binder; re-injected below so the classifier fires exit 2.</summary>
    [Parameter] public SwitchParameter E { get; set; }

    /// <summary>Decoy for the unsupported <c>-C N</c> (--line-bytes). Bare <c>-C</c>
    /// silently bound <c>-Confirm</c>; re-injected below so the classifier fires.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();
    private int _numericStart;

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        // Re-inject the decoy-bound flags (bare -d/-a/-e/-C never reach Arguments: the binder
        // eats or crashes them) so the shared parser sees the whole argv.
        var raw = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (D.IsPresent) pre.Add("-d");
        if (A.HasValue) { pre.Add("-a"); pre.Add(A.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (E.IsPresent) pre.Add("-e");
        if (C.IsPresent) pre.Add("-C");
        var args = pre.Count == 0 ? raw : pre.Concat(raw).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "split", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "split"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "split", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "split", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            return;
        }

        int? lineCount = plan.Lines;
        long? byteSize = plan.Bytes;
        string additionalSuffix = plan.AdditionalSuffix;
        bool numericSuffix = plan.Numeric;
        _numericStart = plan.NumericStart;
        int suffixLength = plan.SuffixLength;
        var operands = plan.Operands;
        IEnumerable<string> lines;
        string? fileReadPath = null;
        string prefix = "x";

        if (operands.Count >= 1)
        {
            string filePath = operands[0];
            if (filePath != "-")
            {
                filePath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(filePath);
            }
            if (filePath == "-")
            {
                var pipelineLines = new List<string>();
                CollectPipelineLines(pipelineLines);
                lines = pipelineLines;
            }
            else
            {
                lines = BashFileSystem.ReadLines(filePath);
                fileReadPath = filePath;
            }
            if (operands.Count >= 2)
            {
                prefix = operands[1];
            }
        }
        else if (_pipeline.Count > 0)
        {
            var pipelineLines = new List<string>();
            CollectPipelineLines(pipelineLines);
            lines = pipelineLines;
        }
        else
        {
            FileSystemHelpers.WriteBashError(this, "split: missing operand");
            return;
        }

        // Resolve working directory exactly as the oracle did: Join-Path $PWD ...
        string cwd = SessionState.Path.CurrentLocation.Path;

        // -b: byte-size mode. Reconstruct the (CRLF-normalized) content bytes and
        // chunk them by size, rather than by line count.
        if (byteSize is > 0)
        {
            WriteByteePieces(lines, cwd, prefix, byteSize.Value, suffixLength, numericSuffix, additionalSuffix);
            return;
        }

        WritePieces(lines, cwd, prefix, lineCount.Value, suffixLength, numericSuffix, fileReadPath, additionalSuffix);
    }

    private void WriteByteePieces(
        IEnumerable<string> lines, string cwd, string prefix, long byteSize,
        int suffixLength, bool numericSuffix, string additionalSuffix)
    {
        byte[] bytes;
        try
        {
            var content = string.Join("\n", lines) + "\n";
            bytes = System.Text.Encoding.UTF8.GetBytes(content);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"split: {ex.Message}");
            return;
        }

        int chunkIndex = 0;
        for (long offset = 0; offset < bytes.Length; offset += byteSize)
        {
            int len = (int)Math.Min(byteSize, bytes.Length - offset);
            string suffix = numericSuffix
                ? (chunkIndex + _numericStart).ToString().PadLeft(suffixLength, '0')
                : BuildAlphaSuffix(chunkIndex, suffixLength);
            string outName = prefix + suffix + additionalSuffix;
            string outPath = Path.IsPathRooted(outName) ? outName : Path.Combine(cwd, outName);
            try
            {
                using var fs = File.Create(outPath);
                fs.Write(bytes, (int)offset, len);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this, $"split: {outPath.Replace('\\', '/')}: {ex.Message}");
                return;
            }
            chunkIndex++;
        }
    }

    private void WritePieces(
        IEnumerable<string> lines,
        string cwd,
        string prefix,
        int lineCount,
        int suffixLength,
        bool numericSuffix,
        string? fileReadPath,
        string additionalSuffix)
    {
        int chunkIndex = 0;
        var chunk = new List<string>(Math.Min(lineCount, 4096));

        try
        {
            foreach (var line in lines)
            {
                chunk.Add(line);
                if (chunk.Count >= lineCount)
                {
                    if (!WriteChunk(chunk, cwd, prefix, chunkIndex, suffixLength, numericSuffix, additionalSuffix))
                    {
                        return;
                    }
                    chunkIndex++;
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                WriteChunk(chunk, cwd, prefix, chunkIndex, suffixLength, numericSuffix, additionalSuffix);
            }
        }
        catch (Exception ex) when (fileReadPath is not null)
        {
            string normalized = fileReadPath.Replace('\\', '/');
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            FileSystemHelpers.WriteBashError(this, $"split: {normalized}: {msg}");
        }
    }

    private bool WriteChunk(
        List<string> chunk,
        string cwd,
        string prefix,
        int chunkIndex,
        int suffixLength,
        bool numericSuffix,
        string additionalSuffix)
    {
        string suffix = numericSuffix
            ? (chunkIndex + _numericStart).ToString().PadLeft(suffixLength, '0')
            : BuildAlphaSuffix(chunkIndex, suffixLength);

        string outName = prefix + suffix + additionalSuffix;
        string outPath = Path.IsPathRooted(outName)
            ? outName
            : Path.Combine(cwd, outName);

        string content = string.Join("\n", chunk) + "\n";
        try
        {
            File.WriteAllText(outPath, content);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            string normalized = outPath.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(
                this, $"split: {normalized}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reproduces the psm1 oracle's alphabetic-suffix loop:
    /// <c>chunkIndex</c> is decomposed into base-26 digits using the alphabet
    /// <c>a..z</c>, lowest-order digit on the right, padded with <c>a</c>
    /// (<c>aa</c>, <c>ab</c>, …, <c>az</c>, <c>ba</c>, …, <c>zz</c>).
    /// The oracle silently rolls over past <c>zz</c> (truncates higher bits),
    /// preserved here.
    /// </summary>
    private static string BuildAlphaSuffix(int chunkIndex, int suffixLength)
    {
        var chars = new char[suffixLength];
        int idx = chunkIndex;
        for (int si = 0; si < suffixLength; si++)
        {
            int charCode = (int)'a' + (idx % 26);
            chars[suffixLength - 1 - si] = (char)charCode;
            idx /= 26;
        }
        return new string(chars);
    }

    private void CollectPipelineLines(List<string> lines)
    {
        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    lines.Add(subLine);
                }
            }
            else
            {
                lines.Add(trimmed);
            }
        }
    }

}
