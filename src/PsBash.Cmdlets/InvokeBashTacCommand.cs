using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTac</c> function
/// (REFACTOR-2 follow-on). Reverses the list of input lines, matching the
/// GNU coreutils <c>tac</c> command (reverse cat).
///
/// Behavioral parity oracle: the original psm1 function. <c>tac</c>'s flag
/// surface is <c>-s SEP</c> / <c>--separator=SEP</c> (custom record separator)
/// plus <c>--help</c>. The cmdlet reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no operands and pipeline input
/// is present, each pipeline item's <c>BashText</c> is trimmed of trailing
/// newlines and split on <c>\n</c>; the resulting line list is accumulated.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is read with CRLF normalization and split into lines (no trailing
/// newline carried per line — matching <c>StreamReader.ReadLine()</c>).</item>
/// </list>
/// After collection: when <c>-s SEP</c> is set, the lines are joined with
/// <c>\n</c>, split on <c>SEP</c>, the chunks reversed, and each emitted;
/// otherwise the lines themselves are reversed and emitted.
///
/// Output: each chunk/line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// No PowerShell common-parameter prefix collision: <c>-s</c> has no
/// colliding common parameter (no <c>-S*</c> common params), so it stays in
/// <see cref="Arguments"/> and is parsed by the manual value-flag scan.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink (parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>,
/// no <see cref="ScriptBlock"/> construction — AOT-safe) and sets
/// <c>$global:LASTEXITCODE = 1</c>, matching the oracle's behavior for missing
/// targets. The <c>Read-BashFileLines</c> helper the psm1 oracle called is
/// small enough to inline as a single <see cref="File.ReadAllText(string)"/>
/// with CRLF normalization and <c>\n</c> split.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTac")]
[OutputType(typeof(string))]
public sealed class InvokeBashTacCommand : PSCmdlet
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
                         "param($n) Show-BashHelp $n", "tac"))
            {
                WriteObject(line);
            }
            return;
        }

        string? separator = null;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-s" && (i + 1) < args.Length)
            {
                separator = args[i + 1];
                i++;
                continue;
            }
            if (arg.StartsWith("--separator=", StringComparison.Ordinal))
            {
                separator = arg.Substring("--separator=".Length);
                continue;
            }
            operands.Add(arg);
        }

        var lines = new List<string>();
        bool hadError = false;

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            // Pipeline mode — trim trailing newlines, split on \n, accumulate.
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
            // File mode — read each operand (glob-expanded), split on \n.
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    string? content = ReadFileText(filePath);
                    if (content == null)
                    {
                        hadError = true;
                        continue;
                    }
                    // StreamReader.ReadLine() semantics: split on \n; a trailing
                    // newline does not produce a spurious empty final line.
                    string body = content;
                    if (body.EndsWith("\n"))
                    {
                        body = body.Substring(0, body.Length - 1);
                    }
                    if (body.Length == 0)
                    {
                        continue;
                    }
                    foreach (var l in body.Split('\n'))
                    {
                        lines.Add(l);
                    }
                }
            }
        }

        if (separator != null)
        {
            // Join everything with \n, split on the user-supplied separator,
            // reverse the chunks, emit each. Matches the oracle's
            // ($lines -join "`n").Split($separator) + [Array]::Reverse path.
            string all = string.Join("\n", lines);
            // String.Split(string) requires a non-empty separator on .NET; an
            // empty -s SEP value is undefined in GNU tac (in practice it errors
            // out). Mirror the oracle which falls through to the no-separator
            // branch on an empty string (PowerShell `if ($separator)` is false
            // for empty string).
            if (separator.Length == 0)
            {
                lines.Reverse();
                foreach (var line in lines)
                {
                    WriteObject(BashRuntime.NewBashObject(line));
                }
            }
            else
            {
                var chunks = all.Split(new[] { separator }, StringSplitOptions.None);
                Array.Reverse(chunks);
                foreach (var chunk in chunks)
                {
                    WriteObject(BashRuntime.NewBashObject(chunk));
                }
            }
        }
        else
        {
            lines.Reverse();
            foreach (var line in lines)
            {
                WriteObject(BashRuntime.NewBashObject(line));
            }
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private string? ReadFileText(string path)
    {
        try
        {
            return BashFileSystem.ReadAllText(path);
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"tac: {normalized}: {msg}");
            return null;
        }
    }
}
