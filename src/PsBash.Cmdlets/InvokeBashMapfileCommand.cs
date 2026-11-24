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
/// <item><c>-u FD</c>: read from descriptor FD (<see cref="BashFdTable"/>) instead of the pipeline; a closed one is
/// bash's "FD: invalid file descriptor: Bad file descriptor", status 1.</item>
/// </list>
/// Every value is checked as bash checks it (status 1, bash's message), and the array is the first operand.
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
    /// match beats a common-parameter prefix match. A string, checked as bash checks it: as an int, <c>-O x</c>
    /// was a parameter-binding error instead of bash's "x: invalid array origin".
    /// </summary>
    [Parameter]
    public string? O { get; set; }

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

    /// <summary>
    /// The name the script used (<c>readarray</c>; <c>mapfile</c> when absent): the two are one builtin, but bash's
    /// diagnostics name the one that was typed. Set by the emitter, which maps both names to this cmdlet.
    /// </summary>
    [Parameter]
    public string? BashName { get; set; }

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

        // bash's mapfile options (d:n:O:s:tu:C:c:); any other is its usage error, status 2, named as invoked
        // (REGRESSION: `mapfile -Q` / `readarray --bogus` were ignored, status 0).
        if (FileSystemHelpers.TryRejectBuiltinOptions(this, BashName == "readarray" ? "readarray" : "mapfile", args,
                "dnOstuCc", "dnOsuCc")) return;

        string name = BashName == "readarray" ? "readarray" : "mapfile";
        long? count = null;
        long origin = 0, skip = 0;
        bool originGiven = false, stripTrailing = false;
        char delim = '\n';
        int? fromFd = null;

        // Each option value is checked as bash checks it, in order, and a bad one is bash's message, status 1.
        // REGRESSION: `mapfile -u 9` (a closed fd), `-u x`, `-n x`, `-O -1`, `-c 0` and a bad array name were ignored
        // (status 0); -u was never read, so its value became the array name.
        bool Fail(string message)
        {
            FileSystemHelpers.WriteBuiltinError(this, $"{name}: {message}");
            return false;
        }
        bool Apply(char letter, string value)
        {
            long n;
            switch (letter)
            {
                case 'd':
                    delim = value.Length == 0 ? '\0' : value[0];
                    return true;
                case 'n':
                    if (!BashNumber(value, 0, uint.MaxValue, out n)) return Fail($"{value}: invalid line count");
                    count = n;
                    return true;
                case 's':
                    if (!BashNumber(value, 0, uint.MaxValue, out n)) return Fail($"{value}: invalid line count");
                    skip = n;
                    return true;
                case 'O':
                    if (!BashNumber(value, 0, uint.MaxValue, out n)) return Fail($"{value}: invalid array origin");
                    origin = n;
                    originGiven = true;
                    return true;
                case 'c':
                    return BashNumber(value, 1, uint.MaxValue, out _) || Fail($"{value}: invalid callback quantum");
                case 'u':
                    if (!BashNumber(value, 0, int.MaxValue, out n)) return Fail($"{value}: invalid file descriptor specification");
                    int fd = (int)n;
                    // A name for the shell's stdin (`exec 5<&0; mapfile -u 5`) reads stdin, as `-u 0` does (read -u alike).
                    if (fd > 2 && BashFdTable.ReadsShellStdin(fd)) fd = 0;
                    else if (fd > 2 && !BashFdTable.IsOpen(fd)) return Fail($"{fd}: invalid file descriptor: Bad file descriptor");
                    fromFd = fd;
                    return true;
                default:
                    // -C CALLBACK: taken, not run (a ps-bash gap).
                    return true;
            }
        }

        // The binder takes -O and -d when they are words of their own (they collide with -OutVariable / -Debug); the rest
        // is bash's internal_getopt: option words up to `--` or the first operand, a value letter taking the rest of its
        // word or the next word (TryRejectBuiltinOptions refused every other letter).
        if (D is not null) delim = D.Length == 0 ? '\0' : D[0];
        if (O is not null && !Apply('O', O)) return;
        int i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            for (int k = 1; k < a.Length; k++)
            {
                char c = a[k];
                if (c == 't') { stripTrailing = true; continue; }
                string? value = k + 1 < a.Length ? a[(k + 1)..] : i + 1 < args.Length ? args[++i] : null;
                if (value is null)
                {
                    FileSystemHelpers.WriteBuiltinUsageError(this, name, $"{name}: -{c}: option requires an argument");
                    return;
                }
                if (!Apply(c, value)) return;
                break;
            }
        }

        // The array is the first operand (bash ignores any after it), MAPFILE without one; a name that is not an identifier
        // (`a[1]`, `a b`, `$(…)`) is refused before anything is read. The check also keeps PowerShell metacharacters out
        // of the variable name (Directive 12).
        string varName = i < args.Length ? args[i] : "MAPFILE";
        if (!InvokeBashReadCommand.IsValidIdentifier(varName))
        {
            Fail($"`{varName}': not a valid identifier");
            return;
        }

        // -n 0 is every line.
        long? limit = count is > 0 ? count : null;
        List<string> lines;
        if (fromFd is int ufd && ufd > 2)
        {
            // -u N: the records come from that descriptor, and only those taken are consumed (`readarray -u 3 -n 1 a;
            // read -u 3 b` gives b the second line). The pipeline (the shell's stdin) is not read.
            _stream.Clear();
            try { lines = ReadDescriptor(ufd, delim, stripTrailing, skip, limit); }
            catch (InvalidOperationException ex)
            {
                Fail(ex.Message);
                return;
            }
        }
        else
        {
            // Cut the rebuilt byte stream at the delimiter like bash: empty records are kept, an unterminated tail is the
            // last element, and without -t every element keeps its delimiter.
            lines = new List<string>();
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

            // -s N: skip the first N lines; -n N: then at most N.
            if (skip >= lines.Count) lines.Clear();
            else if (skip > 0) lines.RemoveRange(0, (int)skip);
            if (limit is long max && lines.Count > max) lines = lines.GetRange(0, (int)max);
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

    /// <summary>bash's <c>legal_number</c> (blanks around it, a sign) within <paramref name="min"/>..<paramref name="max"/>.</summary>
    private static bool BashNumber(string text, long min, long max, out long n)
        => long.TryParse(text,
               System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite
               | System.Globalization.NumberStyles.AllowTrailingWhite,
               System.Globalization.CultureInfo.InvariantCulture, out n)
           && n >= min && n <= max;

    /// <summary>
    /// <c>mapfile -u FD</c>: records read one at a time from the descriptor (<see cref="BashFdTable.ReadRecord"/>), so a
    /// <c>-n</c> limit leaves the rest there; the first <paramref name="skip"/> are dropped. As on the pipeline, a CRLF
    /// line is a line. Throws <see cref="InvalidOperationException"/> when the descriptor is not open for reading.
    /// </summary>
    private static List<string> ReadDescriptor(int fd, char delim, bool strip, long skip, long? limit)
    {
        var lines = new List<string>();
        for (long seen = 0; limit is not long max || lines.Count < max; seen++)
        {
            string? record = BashFdTable.ReadRecord(fd, delim, -1, out bool terminated);
            if (record is null) break;
            if (seen < skip) continue;
            if (delim == '\n' && terminated && record.EndsWith('\r')) record = record[..^1];
            lines.Add(UpToNul(strip || delim == '\0' || !terminated ? record : record + delim));
        }
        return lines;
    }
}
