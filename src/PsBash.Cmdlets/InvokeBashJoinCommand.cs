using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashJoin</c> function
/// (REFACTOR-2 follow-on). Relational join of two files on a common key
/// column, matching GNU coreutils <c>join</c>.
///
/// Behavioral parity oracle: the original psm1 function. Flag surface:
/// <c>-t SEP</c> (delimiter, defaults to a single space), joined form
/// <c>-tC</c> (single-char delimiter); <c>-1 N</c> (key column for file 1,
/// 1-based, default 1); <c>-2 N</c> (key column for file 2, default 1);
/// <c>--</c> end-of-flags; <c>--help</c>.
///
/// Algorithm (byte-for-byte parity with the psm1 oracle):
/// <list type="number">
/// <item>Read both files into line arrays.</item>
/// <item>Build a lookup from file 2 keyed by the join field. The lookup is
/// a <c>Dictionary&lt;string, List&lt;string[]&gt;&gt;</c> so that duplicate
/// keys preserve insertion order and emit one output row per file-2 match.</item>
/// <item>Iterate file 1 lines in order. For each, split on the delimiter,
/// take the key field (skipping rows whose split has fewer fields than the
/// key column), and for each matching file-2 row emit
/// <c>key + delim + file1-rest + delim + file2-rest</c>.</item>
/// </list>
///
/// Both files: read via <see cref="System.IO.File.ReadAllText(string)"/>
/// with CRLF normalization and <c>\n</c> split, mirroring the oracle's
/// <c>Read-BashFileLines</c> slice (StreamReader.ReadLine semantics — a
/// trailing newline does not produce a spurious empty final line). Paths
/// resolve via <c>SessionState.Path.GetUnresolvedProviderPathFromPSPath</c>
/// (no glob expansion — matching the oracle exactly). Missing files emit a
/// bash-style <c>join: PATH: No such file or directory</c> error via the
/// psm1 <c>Write-BashError</c> shim and return with no further output.
/// Missing operand (&lt; 2 file operands) emits <c>join: missing operand</c>
/// and returns.
///
/// No PowerShell common-parameter prefix collision: <c>-t</c>, <c>-1</c>,
/// and <c>-2</c> have no overlap with any common parameter, so all three
/// stay in <see cref="Arguments"/> and are parsed by a manual value-flag
/// scan.
///
/// Output: one bare <c>PsBash.TextOutput</c> string per joined row via
/// <see cref="BashRuntime.NewBashObject(string)"/>.
///
/// AOT-safe: no <see cref="ScriptBlock"/> construction; <c>--help</c> and
/// error emission route through parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashJoin")]
[OutputType(typeof(string))]
public sealed class InvokeBashJoinCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "join", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "join"))
            {
                WriteObject(line);
            }
            return;
        }

        string delimiter = " ";
        int field1 = 1;
        int field2 = 1;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                continue;
            }

            if (arg == "-t" && (i + 1) < args.Length)
            {
                delimiter = args[i + 1];
                i++;
                continue;
            }

            // Joined form: -tC (exactly one char after -t).
            if (arg.Length == 3 && arg[0] == '-' && arg[1] == 't')
            {
                delimiter = arg.Substring(2, 1);
                continue;
            }

            if (arg == "-1" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed1))
                {
                    field1 = parsed1;
                }
                i++;
                continue;
            }

            if (arg == "-2" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed2))
                {
                    field2 = parsed2;
                }
                i++;
                continue;
            }

            operands.Add(arg);
        }

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "join: missing operand");
            return;
        }

        string path1 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[0]);
        string path2 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[1]);

        string[]? lines1 = ReadFileLines(path1);
        if (lines1 == null) { return; }
        string[]? lines2 = ReadFileLines(path2);
        if (lines2 == null) { return; }

        // String.Split takes a char[]; we use a single-char or multi-char
        // delimiter consistently via Split(string[], StringSplitOptions).
        var delimAsArray = new[] { delimiter };

        // Build lookup from file2 keyed by join field. Use Ordinal comparer
        // (matches the oracle's [System.StringComparer]::Ordinal).
        var file2Map = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        int keyIdx2 = field2 - 1;
        foreach (var line in lines2)
        {
            var fields = line.Split(delimAsArray, StringSplitOptions.None);
            if (keyIdx2 >= fields.Length) { continue; }
            var key = fields[keyIdx2];
            if (!file2Map.TryGetValue(key, out var bucket))
            {
                bucket = new List<string[]>();
                file2Map[key] = bucket;
            }
            bucket.Add(fields);
        }

        int keyIdx1 = field1 - 1;
        foreach (var line in lines1)
        {
            var fields1 = line.Split(delimAsArray, StringSplitOptions.None);
            if (keyIdx1 >= fields1.Length) { continue; }
            var key = fields1[keyIdx1];

            if (!file2Map.TryGetValue(key, out var matches)) { continue; }

            foreach (var fields2 in matches)
            {
                var parts = new List<string>();
                parts.Add(key);
                for (int c = 0; c < fields1.Length; c++)
                {
                    if (c != keyIdx1) { parts.Add(fields1[c]); }
                }
                for (int c = 0; c < fields2.Length; c++)
                {
                    if (c != keyIdx2) { parts.Add(fields2[c]); }
                }
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, parts)));
            }
        }
    }

    /// <summary>
    /// Read a file into an array of lines (no trailing newline carried per
    /// line). On failure, emit a bash-style error via the psm1
    /// <c>Write-BashError</c> shim and return <c>null</c>.
    /// </summary>
    private string[]? ReadFileLines(string path)
    {
        string content;
        try
        {
            content = BashFileSystem.ReadAllTextRaw(path);
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"join: {normalized}: {msg}");
            return null;
        }

        // CRLF normalization + StreamReader.ReadLine() semantics: a trailing
        // newline does not produce a spurious empty final line.
        string body = content.Replace("\r\n", "\n");
        if (body.EndsWith("\n"))
        {
            body = body.Substring(0, body.Length - 1);
        }
        if (body.Length == 0)
        {
            return Array.Empty<string>();
        }
        return body.Split('\n');
    }
}
