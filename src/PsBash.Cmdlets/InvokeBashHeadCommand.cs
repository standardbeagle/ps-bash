using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashHead</c> function
/// (REFACTOR-2 Phase 1c). Emits the leading lines (or bytes) of pipeline or
/// file input, matching the bash <c>head</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact value-flag parsing and dual-mode behavior:
/// <list type="bullet">
/// <item>Flags: <c>-n N</c> / <c>-nN</c> (line count, default 10), <c>-c N</c>
/// / <c>-cN</c> (byte count), the legacy <c>-N</c> shorthand, a bare leading
/// positional number, and <c>--</c> to end flag scanning — all parsed in the
/// same order as the psm1 oracle's manual <c>while</c> loop.</item>
/// <item>Pipeline mode (no operands, pipeline non-empty): byte mode joins all
/// input with <c>\n</c> and emits the first N UTF-8 bytes as a string; line
/// mode emits the first N lines, splitting multi-line items and passing
/// single-line items through as their original typed object.</item>
/// <item>File mode: operands resolved via the psm1 <c>Resolve-BashGlob</c>
/// (reimplemented in C# — a <see cref="PSCmdlet"/> reaches
/// <see cref="PSCmdlet.SessionState"/>); byte mode emits the first N bytes of
/// each file; line mode streams the first N lines, each as a typed
/// <c>PsBash.CatLine</c> PSObject whose <c>BashText</c> is the raw line with no
/// trailing newline — exactly as the psm1 oracle emitted.</item>
/// </list>
///
/// Common-parameter audit: <c>-n</c> and <c>-c</c> do not prefix-collide with
/// any PowerShell common parameter, so they are safely scanned out of
/// <see cref="Arguments"/> rather than declared as parameters (matching the
/// psm1 oracle's <c>$args</c> scan). The <c>--help</c> path delegates to the
/// psm1 <c>Show-BashHelp</c>; a file-read error delegates to the psm1
/// <c>Write-BashError</c> — both via string-bodied
/// <c>InvokeCommand.InvokeScript</c>, no ScriptBlock construction (AOT-safe).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHead")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashHeadCommand : PSCmdlet
{
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

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "head"))
            {
                WriteObject(line);
            }
            return;
        }

        int count = 10;
        int? byteCount = null;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-n", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(2)))
            {
                count = int.Parse(arg.Substring(2));
                i++;
                continue;
            }

            if (arg == "-n")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int n))
                {
                    count = n;
                }
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-c", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(2)))
            {
                byteCount = int.Parse(arg.Substring(2));
                i++;
                continue;
            }

            if (arg == "-c")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int c))
                {
                    byteCount = c;
                }
                i++;
                continue;
            }

            // Legacy -N shorthand (e.g. head -5).
            if (arg.Length > 1 && arg[0] == '-' && IsAllDigits(arg.Substring(1)))
            {
                count = int.Parse(arg.Substring(1));
                i++;
                continue;
            }

            // Bare leading positional number (e.g. head 5).
            if (operands.Count == 0 && arg.Length > 0 && IsAllDigits(arg))
            {
                count = int.Parse(arg);
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // Pipeline mode
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            if (byteCount != null)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < _pipeline.Count; k++)
                {
                    if (k > 0) sb.Append('\n');
                    sb.Append(BashRuntime.GetBashText(_pipeline[k]));
                }
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                int take = Math.Min(byteCount.Value, bytes.Length);
                WriteObject(Encoding.UTF8.GetString(bytes, 0, take));
                return;
            }

            int emitted = 0;
            foreach (var item in _pipeline)
            {
                if (emitted >= count) break;
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        if (emitted >= count) break;
                        WriteObject(subLine);
                        emitted++;
                    }
                }
                else
                {
                    WriteObject(item);
                    emitted++;
                }
            }
            return;
        }

        // File mode
        foreach (var filePath in ResolveGlob(operands))
        {
            if (byteCount != null)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(filePath);
                    int take = Math.Min(byteCount.Value, bytes.Length);
                    WriteObject(Encoding.UTF8.GetString(bytes, 0, take));
                }
                catch (Exception ex)
                {
                    FileSystemHelpers.WriteBashError(this, $"head: cannot read '{filePath}': {ex.Message}");
                }
                continue;
            }

            StreamReader? reader = OpenReader(filePath, "head");
            if (reader == null) continue;

            try
            {
                int li = 0;
                string? line;
                while (li < count && (line = reader.ReadLine()) != null)
                {
                    li++;
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.CatLine");
                    obj.Properties.Add(new PSNoteProperty("LineNumber", li));
                    obj.Properties.Add(new PSNoteProperty("Content", line));
                    obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                    obj.Properties.Add(new PSNoteProperty(
                        "BashText", BashRuntime.NormalizeBashText(line)));
                    WriteObject(obj);
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// psm1 oracle: <c>Open-BashFileReader</c> — opens a sequential-scan
    /// <see cref="FileStream"/>, skips a UTF-8 BOM if present, and wraps it in a
    /// BOM-less UTF-8 <see cref="StreamReader"/>. On failure emits a bash-style
    /// error via the psm1 <c>Write-BashError</c> sink and returns <c>null</c>.
    /// </summary>
    private StreamReader? OpenReader(string path, string command)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            string normalized = path.Replace('\\', '/');
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            FileSystemHelpers.WriteBashError(this, $"{command}: {normalized}: {msg}");
            return null;
        }

        var bom = new byte[3];
        int read = fs.Read(bom, 0, 3);
        bool hasBom = read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
        if (!hasBom && read > 0)
        {
            fs.Seek(0, SeekOrigin.Begin);
        }

        return new StreamReader(fs, new UTF8Encoding(false));
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashWcCommand"/> for the rationale).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path.GetResolvedProviderPathFromPSPath(
                                 p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
                }

                if (matched.Count == 0)
                {
                    yield return p;
                }
                else
                {
                    foreach (var m in matched)
                    {
                        yield return m;
                    }
                }
            }
            else
            {
                yield return SessionState.Path.GetUnresolvedProviderPathFromPSPath(p);
            }
        }
    }
}
