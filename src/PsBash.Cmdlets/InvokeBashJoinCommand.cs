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
/// <item>Build a lookup from file 2 keyed by the join field. The lookup is
/// a <c>Dictionary&lt;string, List&lt;string[]&gt;&gt;</c> so that duplicate
/// keys preserve insertion order and emit one output row per file-2 match.</item>
/// <item>Stream file 1 lines in order. For each, split on the delimiter,
/// take the key field (skipping rows whose split has fewer fields than the
/// key column), and for each matching file-2 row emit
/// <c>key + delim + file1-rest + delim + file2-rest</c>.</item>
/// </list>
///
/// Both files stream with CRLF normalization and StreamReader.ReadLine
/// semantics — a trailing newline does not produce a spurious empty final
/// line. Paths resolve via
/// <c>SessionState.Path.GetUnresolvedProviderPathFromPSPath</c> (no glob
/// expansion — matching the oracle exactly). Missing files emit a bash-style
/// <c>join: PATH: No such file or directory</c> error via the psm1
/// <c>Write-BashError</c> shim and return with no further output. Missing
/// operand (&lt; 2 file operands) emits <c>join: missing operand</c> and
/// returns.
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

    /// <summary>-i (case-insensitive) decoy: bare -i is ambiguous with -Information*.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>-a FILENUM decoy: bare -a abbreviates the cmdlet's own -Arguments.</summary>
    [Parameter] public string? A { get; set; }

    /// <summary>-v FILENUM decoy: bare -v abbreviates -Verbose.</summary>
    [Parameter] public string? V { get; set; }

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
        bool ignoreCase = I.IsPresent;     // bare -i arrives via the decoy
        var aFiles = new HashSet<int>();   // -a FILENUM: also print that file's unpaired lines
        var vFiles = new HashSet<int>();   // -v FILENUM: print ONLY that file's unpaired lines
        // Bare -a/-v arrive via the decoy parameters (they abbreviate -Arguments/-Verbose).
        if (A != null && int.TryParse(A, out var aDecoy)) aFiles.Add(aDecoy);
        if (V != null && int.TryParse(V, out var vDecoy)) vFiles.Add(vDecoy);
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

            // -j N: join on the same field in both files.
            if (arg == "-j" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var jf)) { field1 = jf; field2 = jf; }
                i++;
                continue;
            }

            // -a FILENUM / -v FILENUM (separated or joined like -a1).
            if ((arg == "-a" || arg == "-v") && (i + 1) < args.Length && int.TryParse(args[i + 1], out var fnSep))
            {
                (arg == "-a" ? aFiles : vFiles).Add(fnSep);
                i++;
                continue;
            }
            if (arg.Length == 3 && arg[0] == '-' && (arg[1] == 'a' || arg[1] == 'v') && (arg[2] == '1' || arg[2] == '2'))
            {
                (arg[1] == 'a' ? aFiles : vFiles).Add(arg[2] - '0');
                continue;
            }

            // -i / --ignore-case: case-insensitive key comparison.
            if (arg == "-i" || arg == "--ignore-case") { ignoreCase = true; continue; }

            operands.Add(arg);
        }

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "join: missing operand");
            return;
        }

        string path1 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[0]);
        string path2 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[1]);

        IEnumerator<string>? file1 = null;
        bool hasFile1Line;
        try
        {
            file1 = BashFileSystem.ReadLines(path1).GetEnumerator();
            hasFile1Line = file1.MoveNext();
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            file1?.Dispose();
            WriteReadError(path1, ex);
            return;
        }

        // String.Split takes a char[]; we use a single-char or multi-char
        // delimiter consistently via Split(string[], StringSplitOptions).
        var delimAsArray = new[] { delimiter };

        // Output controls for -a / -v:
        //   emitPaired   — print matched rows (suppressed when -v is given alone)
        //   emitUnpaired1 — also print file-1 lines with no match (-a1 / -v1)
        //   emitUnpaired2 — also print file-2 lines with no match (-a2 / -v2)
        bool emitPaired = !(vFiles.Count > 0 && aFiles.Count == 0);
        bool emitUnpaired1 = aFiles.Contains(1) || vFiles.Contains(1);
        bool emitUnpaired2 = aFiles.Contains(2) || vFiles.Contains(2);

        // Build lookup from file2 keyed by join field. Comparer honors -i.
        var cmp = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var file2Map = new Dictionary<string, List<string[]>>(cmp);
        var matchedKeys2 = new HashSet<string>(cmp);
        var file2Order = new List<string>(); // preserve key first-seen order for -a2/-v2 output
        int keyIdx2 = field2 - 1;
        try
        {
            foreach (var line in BashFileSystem.ReadLines(path2))
            {
                var fields = line.Split(delimAsArray, StringSplitOptions.None);
                if (keyIdx2 >= fields.Length) { continue; }
                var key = fields[keyIdx2];
                if (!file2Map.TryGetValue(key, out var bucket))
                {
                    bucket = new List<string[]>();
                    file2Map[key] = bucket;
                    file2Order.Add(key);
                }
                bucket.Add(fields);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            file1?.Dispose();
            WriteReadError(path2, ex);
            return;
        }

        int keyIdx1 = field1 - 1;
        try
        {
            while (hasFile1Line)
            {
                EmitJoinedRows(file1.Current, delimAsArray, keyIdx1, keyIdx2, file2Map,
                    delimiter, emitPaired, emitUnpaired1, matchedKeys2);
                hasFile1Line = file1.MoveNext();
            }

            // -a2 / -v2: emit file-2 lines whose key never matched file 1.
            if (emitUnpaired2)
            {
                foreach (var key in file2Order)
                {
                    if (matchedKeys2.Contains(key)) continue;
                    foreach (var fields2 in file2Map[key])
                    {
                        WriteObject(BashRuntime.NewBashObject(ReorderKeyFirst(fields2, keyIdx2, delimiter)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteReadError(path1, ex);
        }
        finally
        {
            file1?.Dispose();
        }
    }

    private void EmitJoinedRows(
        string line,
        string[] delimAsArray,
        int keyIdx1,
        int keyIdx2,
        Dictionary<string, List<string[]>> file2Map,
        string delimiter,
        bool emitPaired,
        bool emitUnpaired1,
        HashSet<string> matchedKeys2)
    {
        var fields1 = line.Split(delimAsArray, StringSplitOptions.None);
        if (keyIdx1 >= fields1.Length) { return; }
        var key = fields1[keyIdx1];

        if (!file2Map.TryGetValue(key, out var matches))
        {
            // Unpaired file-1 line: print it (key first) under -a1 / -v1.
            if (emitUnpaired1)
            {
                WriteObject(BashRuntime.NewBashObject(ReorderKeyFirst(fields1, keyIdx1, delimiter)));
            }
            return;
        }

        matchedKeys2.Add(key);
        if (!emitPaired) return;

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

    /// <summary>Reorder a line's fields with the join key first (GNU's unpaired-line shape).</summary>
    private static string ReorderKeyFirst(string[] fields, int keyIdx, string delimiter)
    {
        if (keyIdx >= fields.Length) return string.Join(delimiter, fields);
        var parts = new List<string> { fields[keyIdx] };
        for (int c = 0; c < fields.Length; c++)
        {
            if (c != keyIdx) parts.Add(fields[c]);
        }
        return string.Join(delimiter, parts);
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"join: {normalized}: {msg}");
    }
}
