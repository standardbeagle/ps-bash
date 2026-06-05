using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashExpand</c> function
/// (REFACTOR-2 follow-on). Converts tabs to spaces, with tab stops every
/// <c>-t N</c> columns (default 8).
///
/// Behavioral parity oracle: the original psm1 function. Supported flags
/// match the oracle exactly:
/// <list type="bullet">
/// <item><c>-t N</c>  — set uniform tab width to N (default 8).</item>
/// <item><c>-tN</c>   — joined form of <c>-t N</c>.</item>
/// <item><c>--tabs=N</c> — long form.</item>
/// </list>
/// The psm1 oracle does not implement multi-stop lists (<c>-t 4,8,12</c>);
/// passing one is preserved as a parity error (<c>[int]</c> cast throws on a
/// comma string — same in C# via <c>int.Parse</c>).
///
/// Two-path structure, matching the oracle:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — no operands + pipeline input present: each
/// pipeline item's <c>BashText</c> is split on <c>\n</c> after a trailing
/// newline trim; each resulting sub-line is fed through the tab-expansion
/// loop.</item>
/// <item><b>File mode</b> — operands are file paths (glob-expanded via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>). Each file is read
/// with CRLF normalization and split into lines.</item>
/// </list>
///
/// Tab-expansion math (byte-for-byte parity with oracle): track current column
/// per line; on <c>\t</c>, emit <c>(tabWidth - col % tabWidth)</c> spaces and
/// advance the column by that amount; on any other char, append it and
/// advance the column by one.
///
/// Output: each expanded line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// Flag-binding hazard: <c>-t</c> is a value flag whose name does NOT
/// prefix-collide with any PowerShell common parameter (no
/// <c>-Tab*</c> common parameters exist). It stays in the catch-all
/// <see cref="Arguments"/> array and is parsed by the manual scan in
/// <see cref="EndProcessing"/>. No <c>SwitchParameter</c> declarations are
/// needed.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink and sets <c>$global:LASTEXITCODE = 1</c>,
/// matching the oracle's behavior (the oracle relied on
/// <c>Read-BashFileLines</c> to do the same thing).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExpand")]
[OutputType(typeof(string))]
public sealed class InvokeBashExpandCommand : PSCmdlet
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
                         "param($n) Show-BashHelp $n", "expand"))
            {
                WriteObject(line);
            }
            return;
        }

        int tabWidth = 8;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            // -tN (joined, digits only)
            if (a.Length > 2 && a[0] == '-' && a[1] == 't' && IsAllDigits(a, 2))
            {
                tabWidth = int.Parse(a.Substring(2));
                i++;
                continue;
            }
            // -t N (separate)
            if (a == "-t" && (i + 1) < args.Length)
            {
                tabWidth = int.Parse(args[i + 1]);
                i += 2;
                continue;
            }
            // --tabs=N
            if (a.StartsWith("--tabs=", StringComparison.Ordinal))
            {
                tabWidth = int.Parse(a.Substring("--tabs=".Length));
                i++;
                continue;
            }
            operands.Add(a);
            i++;
        }

        var lines = new List<string>();

        // Pipeline mode: no operands, take from $input.
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
            bool hadError = false;
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
                    // StreamReader.ReadLine() semantics: split on \n; a
                    // trailing newline does not produce a spurious empty
                    // final line.
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
                        lines.Add(string.Empty);
                        continue;
                    }
                    foreach (var l in body.Split('\n'))
                    {
                        lines.Add(l);
                    }
                }
            }
            if (hadError)
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
            }
        }

        // Tab-expansion pass — byte-for-byte parity with oracle.
        foreach (var line in lines)
        {
            WriteObject(BashRuntime.NewBashObject(ExpandTabs(line, tabWidth)));
        }
    }

    private static string ExpandTabs(string line, int tabWidth)
    {
        var sb = new StringBuilder(line.Length);
        int col = 0;
        foreach (var ch in line)
        {
            if (ch == '\t')
            {
                int spaces = tabWidth - (col % tabWidth);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                sb.Append(ch);
                col++;
            }
        }
        return sb.ToString();
    }

    private static bool IsAllDigits(string s, int startIndex)
    {
        for (int k = startIndex; k < s.Length; k++)
        {
            if (s[k] < '0' || s[k] > '9') return false;
        }
        return true;
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
            FileSystemHelpers.WriteBashError(this, $"expand: {normalized}: {msg}");
            return null;
        }
    }
}
