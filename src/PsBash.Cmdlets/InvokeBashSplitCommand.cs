using System.Management.Automation;

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
    /// <summary>The bash <c>-d</c> (numeric suffixes) switch.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>The bash <c>-a N</c> (suffix length) value flag.</summary>
    [Parameter]
    public int? A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        int? lineCount = null;
        bool numericSuffix = D.IsPresent;
        int suffixLength = A ?? 2;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-l" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    lineCount = parsed;
                }
                i++;
                continue;
            }
            if (arg.StartsWith("--lines=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--lines=".Length), out var parsed))
                {
                    lineCount = parsed;
                }
                continue;
            }
            if (arg == "-d" || arg == "--numeric-suffixes")
            {
                numericSuffix = true;
                continue;
            }
            if (arg == "-a" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    suffixLength = parsed;
                }
                i++;
                continue;
            }
            if (arg.StartsWith("--suffix-length=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--suffix-length=".Length), out var parsed))
                {
                    suffixLength = parsed;
                }
                continue;
            }
            operands.Add(arg);
        }

        if (lineCount is null || lineCount <= 0)
        {
            lineCount = 1000;
        }
        if (suffixLength < 1)
        {
            suffixLength = 2;
        }

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

        WritePieces(lines, cwd, prefix, lineCount.Value, suffixLength, numericSuffix, fileReadPath);
    }

    private void WritePieces(
        IEnumerable<string> lines,
        string cwd,
        string prefix,
        int lineCount,
        int suffixLength,
        bool numericSuffix,
        string? fileReadPath)
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
                    if (!WriteChunk(chunk, cwd, prefix, chunkIndex, suffixLength, numericSuffix))
                    {
                        return;
                    }
                    chunkIndex++;
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                WriteChunk(chunk, cwd, prefix, chunkIndex, suffixLength, numericSuffix);
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
        bool numericSuffix)
    {
        string suffix = numericSuffix
            ? chunkIndex.ToString().PadLeft(suffixLength, '0')
            : BuildAlphaSuffix(chunkIndex, suffixLength);

        string outName = prefix + suffix;
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
