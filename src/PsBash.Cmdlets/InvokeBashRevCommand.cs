using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRev</c> function
/// (REFACTOR-2 follow-on). Reverses each input line character-by-character,
/// matching the bash <c>rev</c> command.
///
/// Behavioral parity oracle: the original psm1 function. <c>rev</c> has only
/// a <c>--help</c> flag (the psm1 oracle parses no other flags). The cmdlet
/// reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no operands and pipeline input
/// is present, each pipeline item's <c>BashText</c> is split on <c>\n</c> after
/// trailing-newline trim; each resulting sub-line is reversed and emitted as a
/// <c>PsBash.TextOutput</c> object.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is read with CRLF normalization, split into lines (no trailing
/// newline carried per line — matching <c>StreamReader.ReadLine()</c>), and
/// each line is reversed and emitted.</item>
/// </list>
///
/// Output: each reversed line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// No PowerShell common-parameter prefix collision: <c>rev</c> has no short
/// flags. The <see cref="Arguments"/> catch-all suffices.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink (parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>,
/// no <c>ScriptBlock</c> construction — AOT-safe) and sets
/// <c>$global:LASTEXITCODE = 1</c>, matching the oracle's behavior for missing
/// targets.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRev")]
[OutputType(typeof(string))]
public sealed class InvokeBashRevCommand : PSCmdlet
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

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "rev", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rev"))
            {
                WriteObject(line);
            }
            return;
        }

        // Pipeline mode: no operands, take from $input.
        if (args.Length == 0 && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        WriteObject(BashRuntime.NewBashObject(ReverseString(subLine)));
                    }
                }
                else
                {
                    WriteObject(BashRuntime.NewBashObject(ReverseString(trimmed)));
                }
            }
            return;
        }

        // File mode: each operand is a (possibly globbed) file path.
        bool hadError = false;
        foreach (var raw in args)
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
                bool trailingNl = body.EndsWith("\n");
                if (trailingNl)
                {
                    body = body.Substring(0, body.Length - 1);
                }
                if (body.Length == 0 && !trailingNl)
                {
                    continue;
                }
                if (body.Length == 0 && trailingNl)
                {
                    WriteObject(BashRuntime.NewBashObject(string.Empty));
                    continue;
                }
                foreach (var line in body.Split('\n'))
                {
                    WriteObject(BashRuntime.NewBashObject(ReverseString(line)));
                }
            }
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private static string ReverseString(string s)
    {
        if (s.Length <= 1) return s;
        var chars = s.ToCharArray();
        System.Array.Reverse(chars);
        return new string(chars);
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
            FileSystemHelpers.WriteBashError(this, $"rev: {normalized}: {msg}");
            return null;
        }
    }
}
