using System.Management.Automation;
using System.Text.RegularExpressions;

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
                         "param($n) Show-BashHelp $n", "strings"))
            {
                WriteObject(line);
            }
            return;
        }

        int minLength = 4;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-n" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    minLength = parsed;
                }
                i++;
                continue;
            }
            if (arg.StartsWith("--bytes=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--bytes=".Length), out var parsed))
                {
                    minLength = parsed;
                }
                continue;
            }
            operands.Add(arg);
        }

        if (minLength < 1)
        {
            // GNU strings rejects N < 1; psm1 oracle would build an invalid
            // regex {0,}. Guard so we always have a sane pattern.
            minLength = 1;
        }

        string content = string.Empty;

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            var parts = new List<string>(_pipeline.Count);
            foreach (var item in _pipeline)
            {
                parts.Add(BashRuntime.GetBashText(item));
            }
            content = string.Join("\n", parts);
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (var filePath in ResolveGlob(operands))
            {
                string? fileText = ReadFileText(filePath, "strings");
                if (fileText == null)
                {
                    continue;
                }
                sb.Append(fileText);
            }
            content = sb.ToString();
        }

        if (content.Length == 0)
        {
            return;
        }

        // psm1 oracle: [regex]::Matches($content, "[\x20-\x7E]{$minLength,}")
        var pattern = "[\\x20-\\x7E]{" + minLength + ",}";
        foreach (Match m in Regex.Matches(content, pattern))
        {
            WriteObject(BashRuntime.NewBashObject(m.Value));
        }
    }

    /// <summary>
    /// psm1 oracle: <c>Read-BashFileBytes</c> — reads a file as text with CRLF
    /// normalization; on failure emits a bash-style error and returns
    /// <c>null</c>.
    /// </summary>
    private string? ReadFileText(string path, string command)
    {
        try
        {
            return BashFileSystem.ReadAllText(path);
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
    }

    /// <summary>
    /// Same glob slice as <see cref="InvokeBashCatCommand"/>: <c>*</c>/<c>?</c>
    /// expands against the current location; literal paths fall through via
    /// the unresolved-PS-path provider so <see cref="ReadFileText"/> can emit a
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
