using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashNl</c> function
/// (REFACTOR-2 follow-on). Numbers input lines, GNU coreutils <c>nl</c>-style:
/// each numbered line is rendered as <c>"{N,6}\t{LINE}"</c> (6-column
/// right-aligned line number, tab, then the line). By default empty lines
/// are emitted unnumbered (a bare empty line); <c>-ba</c> (also accepted as
/// <c>-b a</c>) numbers every line including empty lines.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashNl</c> function.
/// The cmdlet reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no file operands and pipeline
/// input is present, each pipeline item's <c>BashText</c> is split on <c>\n</c>
/// after trailing-newline trim; each resulting sub-line is collected into the
/// line buffer.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is read with CRLF normalization and split into lines using
/// <c>StreamReader.ReadLine()</c> semantics (no spurious trailing empty
/// line).</item>
/// </list>
///
/// Output: each line is emitted via <see cref="BashRuntime.NewBashObject(string)"/>
/// — the default <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// Flag binding: <c>-ba</c> is a compound short flag (no PowerShell common-
/// parameter prefix collision — case-insensitive <c>-ba</c> does not abbreviate
/// any of <c>-Verbose</c> / <c>-Debug</c> / <c>-Confirm</c> / <c>-WhatIf</c> /
/// <c>-Error*</c> / <c>-Warning*</c> / <c>-Information*</c> / <c>-Out*</c> /
/// <c>-Progress*</c> / <c>-PipelineVariable</c>). The bare token therefore
/// stays in <see cref="Arguments"/> and is parsed by the manual scan, matching
/// the psm1 oracle byte-for-byte. The oracle also accepts the split form
/// <c>-b a</c> (two consecutive tokens) — preserved here.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink (parameter-bound <c>InvokeScript</c> —
/// no <c>ScriptBlock</c> construction, AOT-safe) and continues with the next
/// operand. The psm1 oracle did not set <c>$LASTEXITCODE = 1</c> for nl (the
/// internal <c>Read-BashFileLines</c> returns <c>$null</c> silently on miss);
/// parity is preserved.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNl")]
[OutputType(typeof(string))]
public sealed class InvokeBashNlCommand : PSCmdlet
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
                         "param($n) Show-BashHelp $n", "nl"))
            {
                WriteObject(line);
            }
            return;
        }

        // Parse flags manually (mirrors the psm1 oracle's while loop).
        bool numberAll = false;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];

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

            // Case-sensitive match per the psm1 oracle's `-ceq`.
            if (string.Equals(arg, "-ba", StringComparison.Ordinal))
            {
                numberAll = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-b", StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && string.Equals(args[i], "a", StringComparison.Ordinal))
                {
                    numberAll = true;
                }
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // Collect lines.
        var lines = new List<string>();

        if (operands.Count == 0 && _pipeline.Count > 0)
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
        else
        {
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    var fileLines = ReadFileLines(filePath);
                    if (fileLines == null) continue;
                    lines.AddRange(fileLines);
                }
            }
        }

        // Number and emit.
        int lineNum = 0;
        foreach (var line in lines)
        {
            if (!numberAll && line.Length == 0)
            {
                WriteObject(BashRuntime.NewBashObject(string.Empty));
            }
            else
            {
                lineNum++;
                // psm1 oracle format: '{0,6}\t{1}' -f $lineNum, $line
                string bashText = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0,6}\t{1}", lineNum, line);
                WriteObject(BashRuntime.NewBashObject(bashText));
            }
        }
    }

    private List<string>? ReadFileLines(string path)
    {
        try
        {
            string content = BashFileSystem.ReadAllText(path);
            var result = new List<string>();
            // StreamReader.ReadLine() semantics: split on \n, no spurious
            // trailing empty line if content ends with \n.
            bool trailingNl = content.EndsWith("\n");
            if (trailingNl)
            {
                content = content.Substring(0, content.Length - 1);
            }
            if (content.Length == 0 && !trailingNl)
            {
                return result;
            }
            if (content.Length == 0 && trailingNl)
            {
                result.Add(string.Empty);
                return result;
            }
            foreach (var line in content.Split('\n'))
            {
                result.Add(line);
            }
            return result;
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"nl: {normalized}: {msg}");
            return null;
        }
    }
}
