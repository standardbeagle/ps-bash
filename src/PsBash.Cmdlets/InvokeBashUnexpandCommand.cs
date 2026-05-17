using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUnexpand</c> function
/// (REFACTOR-2 follow-on). Converts runs of spaces back to tabs, matching the
/// GNU coreutils <c>unexpand</c> command — the inverse of <c>expand</c>.
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet reproduces
/// its two-mode structure byte-for-byte:
/// <list type="bullet">
/// <item><b>Default (leading-only) mode</b> — count L leading spaces, emit
/// <c>floor(L/tabWidth)</c> tabs followed by <c>L%tabWidth</c> spaces, then the
/// remainder of the line unchanged. Partial runs that don't reach a tabstop stay
/// as spaces.</item>
/// <item><b><c>-a</c> (all) mode</b> — walk every character. On each space,
/// bump a column counter and a space-run counter; when the column reaches a
/// tab-stop boundary (<c>col % tabWidth == 0</c>) AND at least two spaces have
/// accumulated, emit one tab and reset the run. On any non-space, flush any
/// pending spaces as literals and append the character. A partial run at end of
/// line stays as literal spaces.</item>
/// </list>
///
/// Flag surface: <c>-t N</c> / <c>-tN</c> / <c>--tabs=N</c> (tab width, default
/// 8), <c>-a</c> / <c>--all</c> (all-mode), <c>--first-only</c> (default mode —
/// preserved for arg-compat). <c>-a</c> is declared as an explicit
/// <see cref="SwitchParameter"/> because the bare token <c>-a</c> would
/// otherwise prefix-match the cmdlet's own <c>-Arguments</c> parameter — the
/// same hazard the <c>uname</c> migration handled. <c>-t</c> has no PowerShell
/// common-parameter prefix collision and is scanned out of
/// <see cref="Arguments"/> by the manual value-flag loop.
///
/// Pipeline + file dual mode follows the rev/strings pattern: pipeline mode
/// when there are no operands and pipeline input is present; file mode
/// otherwise, with glob expansion via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>. Files are read with
/// <see cref="File.ReadAllText(string)"/> and CRLF-normalized to LF.
///
/// Output uses <see cref="BashRuntime.NewBashObject(string)"/> — the same
/// default <c>PsBash.TextOutput</c> shape the psm1 oracle produced.
///
/// <c>--help</c> delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe: fixed script body, user-controlled tokens never concatenated
/// into the body).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUnexpand")]
[OutputType(typeof(string))]
public sealed class InvokeBashUnexpandCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// <c>-a</c> declared as an explicit <see cref="SwitchParameter"/> because
    /// the bare token <c>-a</c> would otherwise prefix-match the cmdlet's own
    /// <c>-Arguments</c> parameter under PowerShell parameter binding (it is
    /// the only declared parameter starting with 'a'), causing a "Missing an
    /// argument for parameter 'Arguments'" error. Same hazard <c>uname</c>
    /// handled.
    /// </summary>
    [Parameter]
    public SwitchParameter a { get; set; }

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
                         "param($n) Show-BashHelp $n", "unexpand"))
            {
                WriteObject(line);
            }
            return;
        }

        int tabWidth = 8;
        bool allSpaces = a.IsPresent;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // -tN (joined form): "-t" followed by one or more digits.
            if (arg.Length > 2 && arg[0] == '-' && arg[1] == 't' && AllDigits(arg, 2))
            {
                tabWidth = int.Parse(arg.AsSpan(2));
                continue;
            }
            // -t N (separated form): consume next arg.
            if (arg == "-t" && i + 1 < args.Length)
            {
                tabWidth = int.Parse(args[i + 1]);
                i++;
                continue;
            }
            // --tabs=N
            if (arg.StartsWith("--tabs=", StringComparison.Ordinal))
            {
                tabWidth = int.Parse(arg.AsSpan("--tabs=".Length));
                continue;
            }
            // -a (case-sensitive per oracle's -ceq) or --all.
            if (arg == "-a" || arg == "--all")
            {
                allSpaces = true;
                continue;
            }
            if (arg == "--first-only")
            {
                allSpaces = false;
                continue;
            }
            operands.Add(arg);
        }

        var lines = new List<string>();

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var sub in trimmed.Split('\n'))
                    {
                        lines.Add(sub);
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
                    string? content = ReadFileText(filePath);
                    if (content == null) continue;
                    string body = content;
                    if (body.EndsWith("\n"))
                    {
                        body = body.Substring(0, body.Length - 1);
                    }
                    if (body.Length == 0) continue;
                    foreach (var l in body.Split('\n'))
                    {
                        lines.Add(l);
                    }
                }
            }
        }

        foreach (var line in lines)
        {
            string transformed = allSpaces
                ? UnexpandAll(line, tabWidth)
                : UnexpandLeading(line, tabWidth);
            WriteObject(BashRuntime.NewBashObject(transformed));
        }
    }

    /// <summary>
    /// Default mode: convert only the leading run of spaces to tabs.
    /// Mirrors the psm1 oracle's leading-only branch.
    /// </summary>
    private static string UnexpandLeading(string line, int tabWidth)
    {
        int leading = 0;
        while (leading < line.Length && line[leading] == ' ')
        {
            leading++;
        }
        if (leading == 0) return line;
        int tabs = leading / tabWidth;
        int remain = leading % tabWidth;
        var sb = new StringBuilder(tabs + remain + (line.Length - leading));
        sb.Append('\t', tabs);
        sb.Append(' ', remain);
        sb.Append(line, leading, line.Length - leading);
        return sb.ToString();
    }

    /// <summary>
    /// -a mode: convert every run of spaces (at any column) that crosses a
    /// tabstop boundary with at least two spaces in the run. Mirrors the psm1
    /// oracle's all-spaces branch byte-for-byte.
    /// </summary>
    private static string UnexpandAll(string line, int tabWidth)
    {
        var sb = new StringBuilder(line.Length);
        int col = 0;
        int spaceRun = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                spaceRun++;
                col++;
                if ((col % tabWidth) == 0 && spaceRun >= 2)
                {
                    sb.Append('\t');
                    spaceRun = 0;
                }
            }
            else
            {
                if (spaceRun > 0)
                {
                    sb.Append(' ', spaceRun);
                    spaceRun = 0;
                }
                sb.Append(ch);
                col++;
            }
        }
        if (spaceRun > 0)
        {
            sb.Append(' ', spaceRun);
        }
        return sb.ToString();
    }

    private static bool AllDigits(string s, int start)
    {
        if (start >= s.Length) return false;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private string? ReadFileText(string path)
    {
        try
        {
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"unexpand: {normalized}: {msg}");
            return null;
        }
    }
}
