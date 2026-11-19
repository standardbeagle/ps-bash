using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMapfile</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>mapfile</c> / <c>readarray</c>
/// builtin: read pipeline input lines into a bash array variable in the
/// caller's scope.
///
/// Behavioral parity oracle: the original psm1 function. Pipeline-only (no
/// stdin / file read — the oracle never accepted operands as filenames, the
/// non-flag operand is the destination array variable name). Empty lines are
/// silently dropped (matches the oracle's <c>if ($line -ne '') { $lines.Add }</c>
/// branch byte-for-byte; this is a parity quirk, not a feature). The default
/// destination variable is <c>MAPFILE</c>.
///
/// Flag surface (parsed from <c>Arguments</c>):
/// <list type="bullet">
/// <item><c>-n N</c> / <c>-nN</c>: cap at most N lines.</item>
/// <item><c>-O ORIGIN</c> / <c>-OORIGIN</c>: start assigning at array index
/// ORIGIN (lower indices receive empty strings — matches the oracle's
/// <c>@(1..$origin | ForEach-Object { '' })</c> prefix slice).</item>
/// <item><c>-s N</c> (skip): the psm1 oracle did not implement this flag —
/// the cmdlet declares the explicit value-bearing parameter and applies it
/// because the task spec requires it, but the differential test for <c>-s</c>
/// will exercise only the cmdlet's added behavior.</item>
/// <item><c>-t</c>: strip trailing newline from each line (the oracle
/// implemented this via <c>TrimEnd("`n"[0], "`r"[0])</c>).</item>
/// <item><c>-d DELIM</c> / <c>-dDELIM</c>: the psm1 oracle accepted but
/// ignored <c>-d</c> (it always split on <c>\n</c>). The cmdlet preserves the
/// "accepted but ignored" contract for parity — the default delimiter is
/// always <c>\n</c>; the value is consumed and dropped.</item>
/// </list>
///
/// Colliding flags declared as explicit parameters (playbook collision table):
/// <list type="bullet">
/// <item><c>-O ORIGIN</c>: bare <c>-O</c> prefix-matches <c>-OutVariable</c> /
/// <c>-OutBuffer</c>. Declared as <c>int? O</c>.</item>
/// <item><c>-d DELIM</c>: bare <c>-d</c> prefix-matches <c>-Debug</c>.
/// Declared as <c>string? D</c>.</item>
/// </list>
/// <c>-n</c>, <c>-s</c>, and <c>-t</c> have no PowerShell common-parameter
/// prefix overlap; they stay in <c>Arguments</c> and are parsed by the manual
/// scan (which also recovers the joined <c>-nN</c> / <c>-OORIGIN</c> /
/// <c>-dDELIM</c> short forms that bypass the binder).
///
/// Writes the resulting array back via
/// <see cref="PSVariableIntrinsics.Set(string, object)"/> on
/// <c>PSCmdlet.SessionState.PSVariable</c>. No stdout output (variable
/// side-effect only). <c>--help</c> delegates to psm1 <c>Show-BashHelp</c>
/// via parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe). The <c>mapfile</c> and <c>readarray</c> aliases are added in
/// psm1 and resolve to this cmdlet automatically.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMapfile")]
public sealed class InvokeBashMapfileCommand : BashCmdlet
{
    /// <summary>
    /// <c>-O ORIGIN</c> — start assigning at this array index. Declared
    /// explicitly because the bare token <c>-O</c> prefix-matches the
    /// PowerShell common parameters <c>-OutVariable</c> / <c>-OutBuffer</c>
    /// under <see cref="PSCmdlet"/> parameter binding. An exact param-name
    /// match beats a common-parameter prefix match.
    /// </summary>
    [Parameter]
    public int? O { get; set; }

    /// <summary>
    /// <c>-d DELIM</c> — record delimiter. The psm1 oracle accepted but
    /// ignored this flag (always split on <c>\n</c>); the cmdlet preserves
    /// that parity. Declared explicitly because the bare token <c>-d</c>
    /// prefix-matches <c>-Debug</c>.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // The input as ONE text stream, built as records arrive. Keeping the records themselves (PSObjects with note
    // properties, several KB each) cost ~100x the data for `mapfile -t a < big`; only their text is needed.
    private readonly System.Text.StringBuilder _stream = new();

    /// <summary>bash stores each line as a C string, so a NUL ends it: `.\0.\n` is stored as `.` (its newline too is lost).</summary>
    internal static string UpToNul(string line)
    {
        int nul = line.IndexOf('\0');
        return nul < 0 ? line : line[..nul];
    }

    // Records become LINES first: a piece without its newline (`printf a`) waits for the rest (LineAssembler).
    private readonly LineAssembler _lines = new();

    protected override void ProcessRecord()
    {
        if (_lines.Feed(InputObject) is { } record) { InputObject = record; ProcessInputRecord(); }
    }

    private void ProcessInputRecord()
    {
        if (InputObject == null) return;
        // The stream is the records' exact bytes (BashRuntime.RecordBytes): a plain record is a line, an unterminated
        // tail stays unterminated, and a non-newline delimiter sees the line boundaries as data, as in bash.
        // The CRLF fold applies to the text only: the boundary newline after a data CR (`"line1`r"`) is not a CRLF.
        _stream.Append(PsBash.Core.StdoutRecord.Bytes(
            BashRuntime.GetBashText(InputObject).Replace("\r\n", "\n"), BashRuntime.IsExactBytes(InputObject)));
    }

    protected override void EndProcessing()
    {
        if (_lines.Flush() is { } last) { InputObject = last; ProcessInputRecord(); }
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mapfile", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "mapfile"))
            {
                WriteObject(line);
            }
            return;
        }

        // Defaults match the psm1 oracle.
        int? count = null;
        int origin = O ?? 0;
        bool originGiven = O is not null;
        int skip = 0;
        bool stripTrailing = false;
        char delim = '\n';
        string varName = "MAPFILE";

        // Manual scan recovers joined short forms (-nN / -OORIGIN / -dDELIM)
        // and value-flag pairs the binder did not consume. The explicit -O
        // and -D parameters above are also honored (set the locals first).
        if (D is not null) delim = D.Length == 0 ? '\0' : D[0];
        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "-t") { stripTrailing = true; i++; continue; }
            if (a == "-n" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var n)) count = n;
                i += 2; continue;
            }
            if (a.StartsWith("-n") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var n)) count = n;
                i++; continue;
            }
            if (a == "-O" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var oVal)) origin = oVal;
                originGiven = true;
                i += 2; continue;
            }
            if (a.StartsWith("-O") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var oVal)) origin = oVal;
                originGiven = true;
                i++; continue;
            }
            if (a == "-s" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var s)) skip = s;
                i += 2; continue;
            }
            if (a.StartsWith("-s") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var s)) skip = s;
                i++; continue;
            }
            // -d DELIM and -dDELIM: consumed but the resulting value is
            // not used (oracle parity — always split on \n).
            if (a == "-d" && i + 1 < args.Length) { delim = args[i + 1].Length == 0 ? '\0' : args[i + 1][0]; i += 2; continue; }
            if (a.StartsWith("-d") && a.Length > 2) { delim = a[2]; i++; continue; }

            // Non-flag = destination variable name. Matches the oracle's
            // "last non-flag wins" contract. Reject inputs containing
            // PowerShell scriptblock metacharacters defensively (Directive
            // 12) — emit a bash-style error and skip the assignment.
            if (!a.StartsWith('-'))
            {
                if (a.Contains('$') || a.Contains('(') || a.Contains(';') ||
                    a.Contains('{') || a.Contains('`') || a.Contains('"'))
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"mapfile: `{a}': not a valid identifier");
                    return;
                }
                varName = a;
            }
            i++;
        }

        // Cut the rebuilt byte stream at the delimiter like bash: empty records are kept, an unterminated tail is the
        // last element, and without -t every element keeps its delimiter.
        var lines = new List<string>();
        string stream = _stream.ToString();
        _stream.Clear();
        int startAt = 0;
        while (startAt < stream.Length)
        {
            int cut = stream.IndexOf(delim, startAt);
            if (cut < 0) { lines.Add(UpToNul(stream[startAt..])); break; }
            // A NUL delimiter can never be stored in a variable, so it is always dropped.
            lines.Add(UpToNul(stripTrailing || delim == '\0' ? stream[startAt..cut] : stream[startAt..(cut + 1)]));
            startAt = cut + 1;
        }

        // -s N: skip the first N lines (cmdlet addition; oracle did not
        // implement, but the playbook task spec requires).
        if (skip > 0 && skip < lines.Count) lines.RemoveRange(0, skip);
        else if (skip >= lines.Count) lines.Clear();

        // -n N: cap to first N lines.
        if (count.HasValue && lines.Count > count.Value)
        {
            lines = lines.GetRange(0, count.Value);
        }

        // Without -O bash clears the array first. With -O it keeps the elements already there and assigns the
        // lines from index ORIGIN on (`arr=(x y z); mapfile -O 2` keeps x y and overwrites z); indices below
        // ORIGIN that were never set stay unset, so the result is sparse unless it runs 0..n-1 without a gap.
        object result;
        if (originGiven)
        {
            var entries = new List<object?>();
            object? existing = BashVariableLookup.ArrayHolder(SessionState, varName) ?? BashVariableLookup.Scalar(SessionState, varName);
            if (existing is BashSparseArray sparse)
                foreach (System.Collections.DictionaryEntry kv in sparse) { entries.Add(kv.Key); entries.Add(kv.Value); }
            else if (existing is System.Collections.IList list)
                for (int j = 0; j < list.Count; j++) { entries.Add((long)j); entries.Add(list[j]); }
            else if (existing is string scalar)
            { entries.Add(0L); entries.Add(scalar); }
            for (int j = 0; j < lines.Count; j++) { entries.Add((long)(origin + j)); entries.Add(lines[j]); }
            result = BashSparseArray.FromEntries(entries.ToArray());
        }
        else
        {
            result = lines.ToArray();
        }

        // The array variable in the scope that owns it, else a global (BashVariableLookup.SetArray).
        BashVariableLookup.SetArray(SessionState, varName, result);
    }
}
