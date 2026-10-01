using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

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
/// <c>join: PATH: No such file or directory</c> error via
/// <see cref="FileSystemHelpers.WriteBashError"/> and return with no further output. Missing
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

    /// <summary>
    /// Valid GNU join options ps-bash does not implement, refused loudly (exit 2). <c>-o FORMAT</c> and
    /// <c>-e STRING</c> used to be silently swallowed as file operands / ignored, so `join -o 0,1.2 a b`
    /// printed the DEFAULT format. (A string[] on purpose: CommonParameterCollisionGuardTests
    /// enumerates static string sets.)
    /// </summary>
    private static readonly string[] JoinValidButUnsupported =
    {
        "-o", "-e",
        "--check-order", "--nocheck-order", "--header",
        "-z", "--zero-terminated",
    };

    private const string OptDelim = "t", OptField1 = "1", OptField2 = "2", OptJoinField = "j",
        OptA = "a", OptV = "v", OptIgnoreCase = "i";

    /// <summary>
    /// join's option surface (GNU coreutils 9.4). Implemented: -1 -2 -j -t -a -v -i/--ignore-case.
    /// </summary>
    private static readonly OptSpecSet JoinSpec = new(
        new[]
        {
            new OptSpec(OptField1, '1', null, OptKind.Value),
            new OptSpec(OptField2, '2', null, OptKind.Value),
            new OptSpec(OptJoinField, 'j', null, OptKind.Value),
            new OptSpec(OptDelim, 't', null, OptKind.Value),
            new OptSpec(OptA, 'a', null, OptKind.Value),
            new OptSpec(OptV, 'v', null, OptKind.Value),
            new OptSpec(OptIgnoreCase, 'i', "ignore-case"),
        },
        validButUnsupported: JoinValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, JoinSpec);

    internal sealed class JoinArgs
    {
        public ParsedArgs Parsed = null!;
        public string Delimiter = " ";
        /// <summary>
        /// True once <c>-t</c> was given: fields are then split on EXACTLY that character. Without it GNU
        /// separates fields by runs of blanks (space/tab) and ignores leading blanks.
        /// </summary>
        public bool DelimiterExplicit;
        public int Field1 = 1, Field2 = 1;
        public bool IgnoreCase;
        public HashSet<int> AFiles = new(), VFiles = new();
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate like GNU: a field number must be a positive integer, <c>-a</c>/<c>-v</c> take 1 or
    /// 2, <c>-t</c> a single character (<c>\0</c> = NUL). The old scan silently ignored a bad
    /// <c>-1 x</c> (kept field 1), took <c>-a3</c> as an operand and accepted a multi-char <c>-t</c>.
    /// Options are applied in argv order (last <c>-1</c>/<c>-2</c>/<c>-j</c> wins).
    /// </summary>
    internal static JoinArgs Plan(string[] args)
    {
        var j = new JoinArgs { Parsed = ScanArgs(args) };
        j.Operands = j.Parsed.Operands();
        if (j.Parsed.HasError) return j;

        foreach (var tok in j.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string v = tok.Value ?? string.Empty;
            switch (tok.OptId)
            {
                case OptIgnoreCase: j.IgnoreCase = true; break;
                case OptField1:
                case OptField2:
                case OptJoinField:
                    if (!TryField(v, out int n)) { j.Error = $"join: invalid field number: '{v}'"; return j; }
                    if (tok.OptId != OptField2) j.Field1 = n;
                    if (tok.OptId != OptField1) j.Field2 = n;
                    break;
                case OptA:
                case OptV:
                    if (v is not ("1" or "2")) { j.Error = $"join: invalid field number: '{v}'"; return j; }
                    (tok.OptId == OptA ? j.AFiles : j.VFiles).Add(v[0] - '0');
                    break;
                case OptDelim:
                    j.DelimiterExplicit = true;
                    if (v == "\\0") j.Delimiter = "\0";
                    else if (v.Length > 1) { j.Error = $"join: multi-character tab '{v}'"; return j; }
                    else j.Delimiter = v;
                    break;
            }
        }
        return j;
    }

    private static bool TryField(string s, out int n)
    {
        n = 0;
        if (s.Length == 0) return false;
        long v = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            v = Math.Min(v * 10 + (c - '0'), int.MaxValue);
        }
        n = (int)v;
        return n >= 1;
    }

    /// <summary>-i (case-insensitive) decoy: bare -i is ambiguous with -Information*.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>-a FILENUM decoy: bare -a abbreviates the cmdlet's own -Arguments.</summary>
    [Parameter] public string? A { get; set; }

    /// <summary>-v FILENUM decoy: bare -v abbreviates -Verbose.</summary>
    [Parameter] public string? V { get; set; }

    /// <summary>Arguments with the decoy-bound flags re-injected (<c>-i</c>, <c>-a N</c>, <c>-v N</c>).</summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (I.IsPresent) pre.Add("-i");
        if (A is not null) { pre.Add("-a"); pre.Add(A); }
        if (V is not null) { pre.Add("-v"); pre.Add(V); }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }

    protected override void EndProcessing()
    {
        var args = ArgsWithDecoys();

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

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "join", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "join", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            return;
        }

        string delimiter = plan.Delimiter;
        int field1 = plan.Field1;
        int field2 = plan.Field2;
        bool ignoreCase = plan.IgnoreCase;
        var aFiles = plan.AFiles;   // -a FILENUM: also print that file's unpaired lines
        var vFiles = plan.VFiles;   // -v FILENUM: print ONLY that file's unpaired lines
        var operands = plan.Operands;

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, operands.Count == 0
                ? "join: missing operand"
                : $"join: missing operand after '{operands[0]}'");
            return;
        }
        if (operands.Count > 2)
        {
            FileSystemHelpers.WriteBashError(this, $"join: extra operand '{operands[2]}'");
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
        _blankMode = !plan.DelimiterExplicit;

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
                var fields = SplitFields(line, delimAsArray);
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

    // Default (no -t): GNU splits a line on RUNS of blanks and ignores leading blanks, so "a  b" has two
    // fields; a trailing blank leaves one empty last field ("d 4 " is d, 4, ""). With -t the split is exact.
    private bool _blankMode;
    private static readonly System.Text.RegularExpressions.Regex s_blankRun = new("[ \t]+");

    private string[] SplitFields(string line, string[] delimAsArray) =>
        _blankMode
            ? s_blankRun.Split(line.TrimStart(' ', '\t'))
            : line.Split(delimAsArray, StringSplitOptions.None);

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
        var fields1 = SplitFields(line, delimAsArray);
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
