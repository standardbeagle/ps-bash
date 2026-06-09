using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashStrings</c> function
/// (REFACTOR-2 follow-on). Scans each operand file (or pipeline input) for
/// runs of printable characters at least N characters long and emits each run
/// as a separate line, matching the GNU binutils <c>strings</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact behavior:
/// <list type="bullet">
/// <item>The <c>-n N</c> flag (or <c>--bytes=N</c>) sets the minimum
/// printable-run length; default is 4 (GNU strings default).</item>
/// <item>"Printable" is the ASCII printable range <c>\x20</c>–<c>\x7E</c>
/// (space through tilde) — matching the psm1 oracle's
/// <c>[\x20-\x7E]{N,}</c> regex literally. Tab, newline, and other control
/// bytes are NOT considered printable. Note: the file is read as text via
/// <see cref="File.ReadAllText(string)"/> (UTF-8 with BOM detection), so the
/// scan operates on .NET <see cref="char"/> code units, exactly as the psm1
/// oracle did — multi-byte UTF-8 sequences are decoded first, then matched
/// against the ASCII printable range; non-ASCII characters are treated as
/// non-printable and split runs.</item>
/// <item>File mode: operands are resolved via the same glob slice
/// <see cref="InvokeBashCatCommand"/> uses; CRLF is normalized to LF; missing
/// files emit a bash-style error and are skipped. File contents are
/// concatenated before the regex scan (matching the oracle's
/// <c>$content += $fileText</c>).</item>
/// <item>Pipeline mode (no operands): every upstream item's BashText is
/// joined with <c>\n</c> separators and scanned.</item>
/// <item>Output: each printable run is a bare string emitted via
/// <see cref="BashRuntime.NewBashObject"/> (the default
/// <c>PsBash.TextOutput</c> fast path).</item>
/// </list>
///
/// psm1-only dependencies: <c>Read-BashFileBytes</c> (text read with CRLF
/// normalization, error continuation) is reimplemented in C# here; the
/// <c>Resolve-BashGlob</c> glob slice is reimplemented via
/// <see cref="PSCmdlet.SessionState"/>'s path provider, matching
/// <see cref="InvokeBashCatCommand"/>. <c>--help</c> delegates to the psm1
/// <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string,object[])"/>
/// (string-bodied — AOT-safe).
///
/// Common-parameter collision: the bash flag <c>-n</c> has no prefix collision
/// with any PowerShell common parameter (<c>-Verbose -Debug -Confirm -WhatIf
/// -Error*  -Warning* -Information* -Out* -Progress* -PipelineVariable</c>),
/// so it stays in <see cref="Arguments"/> and is parsed by the manual
/// value-flag scan below.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashStrings")]
[OutputType(typeof(string))]
public sealed class InvokeBashStringsCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Parsed-once state.
    private bool _parsed;
    private int _minLength = 4;
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present, or a
    // --help / --version request.
    private bool _suppressStdin;
    // Scanner state — instance so a streamed stdin run carries across records
    // exactly as the buffered scan did (a synthetic '\n' separates items, so a
    // printable run never actually spans two pipeline records).
    private readonly StringBuilder _run = new();
    private bool _seenRecord;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-n" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    _minLength = parsed;
                }
                i++;
                continue;
            }
            if (arg.StartsWith("--bytes=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--bytes=".Length), out var parsed))
                {
                    _minLength = parsed;
                }
                continue;
            }
            _operands.Add(arg);
        }

        if (_minLength < 1)
        {
            // GNU strings rejects N < 1; psm1 oracle would build an invalid
            // regex {0,}. Guard so we always have a sane pattern.
            _minLength = 1;
        }

        _suppressStdin = _operands.Count > 0;
    }

    private void ScanChar(char ch)
    {
        if (ch is >= '\x20' and <= '\x7E')
        {
            _run.Append(ch);
            return;
        }

        FlushRun();
    }

    private void FlushRun()
    {
        if (_run.Length >= _minLength)
        {
            WriteObject(BashRuntime.NewBashObject(_run.ToString()));
        }
        _run.Clear();
    }

    private void ScanText(string text)
    {
        foreach (var ch in text)
        {
            ScanChar(ch);
        }
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Stream the stdin scan instead of buffering every record first. The
        // synthetic '\n' between records (a non-printable that flushes the run)
        // reproduces the buffered scan's inter-item separator.
        if (_seenRecord) ScanChar('\n');
        _seenRecord = true;
        ScanText(BashRuntime.GetBashText(InputObject));
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "strings", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "strings"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_operands.Count == 0)
        {
            // Pipeline mode: records already streamed; flush the trailing run.
            FlushRun();
            return;
        }

        foreach (var filePath in ResolveGlob(_operands))
        {
            try
            {
                using var fs = BashFileSystem.OpenRead(filePath);
                using var reader = new StreamReader(
                    fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[16384];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        ScanChar(buffer[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                WriteReadError(filePath, "strings", ex);
            }
        }

        FlushRun();
    }

    private void WriteReadError(string path, string command, Exception ex)
    {
        string normalized = path.Replace('\\', '/');
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        FileSystemHelpers.WriteBashError(this, $"{command}: {normalized}: {msg}");
    }

    /// <summary>
    /// Same glob slice as <see cref="InvokeBashCatCommand"/>: <c>*</c>/<c>?</c>
    /// expands against the current location; literal paths fall through via
    /// the unresolved-PS-path provider so the scanner can emit a
    /// bash-style "no such file" error.
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
                    foreach (var resolved in SessionState.Path
                                 .GetResolvedProviderPathFromPSPath(p, out _))
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
