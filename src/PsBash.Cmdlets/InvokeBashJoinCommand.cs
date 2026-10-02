using System.Linq;
using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for <c>join</c>: a port of GNU coreutils 9.4 <c>join.c</c> (a MERGE join over two files
/// sorted on their join fields, with run lookahead), oracle-checked in `wsl bash`.
///
/// <para>Implemented: <c>-1 -2 -j -t -a -v -i -e -o FORMAT|auto --check-order --nocheck-order --header
/// -z</c>. A line that lacks the join field has an EMPTY key; without <c>-t</c> fields are separated by
/// runs of blanks (leading blanks ignored; under <c>-z</c> a newline is a blank too), with <c>-t</c> the
/// split is exact. Output separator: the <c>-t</c> character, else one space.</para>
///
/// <para><b>Order check</b> (<c>check_order</c>): each line READ after a file's first is compared with the
/// previous line of that file on the join field. By default only once an unpairable line has been seen
/// (reads of lookahead lines before that are never checked); the first disorder per file prints
/// <c>join: FILE:LINE: is not sorted: TEXT</c> and the run continues, ending with
/// <c>join: input is not in sorted order</c> and exit 1. <c>--check-order</c> checks always and stops at
/// the first disorder (exit 1); <c>--nocheck-order</c> never checks. After the merge loop the rest of both
/// files is still read (and printed under -a/-v), so disorder there is found.</para>
///
/// <para><b>-o</b> specs (comma or blank separated, repeatable and accumulating): <c>0</c> (the join
/// field), <c>FILE.FIELD</c>; a missing/empty field prints the <c>-e</c> string (empty by default). With
/// no <c>-o</c>, <c>-o auto</c> builds <c>0, 1.k.., 2.k..</c> from the FIRST line of each file (join
/// fields excluded). <c>-e</c> also replaces empty fields in the default format.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashJoin")]
[OutputType(typeof(string))]
public sealed class InvokeBashJoinCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>Valid GNU join options ps-bash does not implement (none left).</summary>
    private static readonly string[] JoinValidButUnsupported = Array.Empty<string>();

    private const string OptDelim = "t", OptField1 = "1", OptField2 = "2", OptJoinField = "j",
        OptA = "a", OptV = "v", OptIgnoreCase = "i", OptO = "o", OptE = "e",
        OptCheck = "check", OptNoCheck = "nocheck", OptHeader = "header", OptZero = "zero";

    private static readonly string[] JoinLongOrder =
        { "ignore-case", "check-order", "nocheck-order", "header", "zero-terminated" };

    private static readonly OptSpecSet JoinSpec = new(
        new[]
        {
            new OptSpec(OptField1, '1', null, OptKind.Value),
            new OptSpec(OptField2, '2', null, OptKind.Value),
            new OptSpec(OptJoinField, 'j', null, OptKind.Value),
            new OptSpec(OptDelim, 't', null, OptKind.Value),
            new OptSpec(OptA, 'a', null, OptKind.Value),
            new OptSpec(OptV, 'v', null, OptKind.Value),
            new OptSpec(OptO, 'o', null, OptKind.Value),
            new OptSpec(OptE, 'e', null, OptKind.Value),
            new OptSpec(OptIgnoreCase, 'i', "ignore-case"),
            new OptSpec(OptCheck, '\0', "check-order"),
            new OptSpec(OptNoCheck, '\0', "nocheck-order"),
            new OptSpec(OptHeader, '\0', "header"),
            new OptSpec(OptZero, 'z', "zero-terminated"),
        },
        validButUnsupported: JoinValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: JoinLongOrder);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, JoinSpec);

    /// <summary>One <c>-o</c> output spec: <c>File</c> 0 = the join field, else 1|2 and a 1-based <c>Field</c>.</summary>
    internal readonly record struct OutSpec(int File, int Field);

    internal sealed class JoinArgs
    {
        public ParsedArgs Parsed = null!;
        public string Delimiter = " ";
        /// <summary>True once <c>-t</c> was given: fields split on EXACTLY that character.</summary>
        public bool DelimiterExplicit;
        public int Field1 = 1, Field2 = 1;
        public bool IgnoreCase, Zero, Header, Auto;
        public HashSet<int> AFiles = new(), VFiles = new();
        public List<string> Operands = new();
        public List<OutSpec> OutList = new();
        public string? Filler;
        public InvokeBashCommCommand.OrderCheck Check = InvokeBashCommCommand.OrderCheck.Default;
        public string? Error;
    }

    /// <summary>
    /// Scan + validate like GNU: a field number is a positive integer, <c>-a</c>/<c>-v</c> take 1 or 2,
    /// <c>-t</c> a single character (<c>\0</c> = NUL; two different ones = "incompatible tabs"), a second
    /// join field that differs from an earlier one is "incompatible join fields A, B" (0-based, as GNU
    /// prints them), and <c>-o</c> specs are validated with GNU's three messages.
    /// </summary>
    internal static JoinArgs Plan(string[] args)
    {
        var j = new JoinArgs { Parsed = ScanArgs(args) };
        j.Operands = j.Parsed.Operands();
        if (j.Parsed.HasError) return j;
        int f1 = -1, f2 = -1;   // 0-based, -1 = unset
        bool tabSet = false;

        foreach (var tok in j.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string v = tok.Value ?? string.Empty;
            switch (tok.OptId)
            {
                case OptIgnoreCase: j.IgnoreCase = true; break;
                case OptZero: j.Zero = true; break;
                case OptHeader: j.Header = true; break;
                case OptCheck: j.Check = InvokeBashCommCommand.OrderCheck.Enabled; break;
                case OptNoCheck: j.Check = InvokeBashCommCommand.OrderCheck.Disabled; break;
                case OptField1:
                case OptField2:
                case OptJoinField:
                    if (!TryField(v, out int n)) { j.Error = $"join: invalid field number: '{v}'"; return j; }
                    n--;
                    if (tok.OptId != OptField2)
                    {
                        if (f1 >= 0 && f1 != n) { j.Error = $"join: incompatible join fields {f1}, {n}"; return j; }
                        f1 = n;
                    }
                    if (tok.OptId != OptField1)
                    {
                        if (f2 >= 0 && f2 != n) { j.Error = $"join: incompatible join fields {f2}, {n}"; return j; }
                        f2 = n;
                    }
                    break;
                case OptA:
                case OptV:
                    if (v is not ("1" or "2")) { j.Error = $"join: invalid field number: '{v}'"; return j; }
                    (tok.OptId == OptA ? j.AFiles : j.VFiles).Add(v[0] - '0');
                    break;
                case OptDelim:
                    string d;
                    if (v == "\\0") d = "\0";
                    else if (v.Length > 1) { j.Error = $"join: multi-character tab '{v}'"; return j; }
                    else d = v;
                    if (tabSet && d != j.Delimiter) { j.Error = "join: incompatible tabs"; return j; }
                    tabSet = true;
                    j.DelimiterExplicit = true;
                    j.Delimiter = d;
                    break;
                case OptE: j.Filler = v; break;
                case OptO:
                    if (v == "auto") { j.Auto = true; break; }
                    foreach (var piece in v.Split(',', ' ', '\t'))
                    {
                        if (!TryOutSpec(piece, out var spec, out var err)) { j.Error = "join: " + err; return j; }
                        j.OutList.Add(spec);
                    }
                    break;
            }
        }
        j.Field1 = f1 < 0 ? 1 : f1 + 1;
        j.Field2 = f2 < 0 ? 1 : f2 + 1;
        return j;
    }

    private static bool TryOutSpec(string s, out OutSpec spec, out string error)
    {
        spec = default; error = "";
        if (s == "0") { spec = new OutSpec(0, 0); return true; }
        if (s.Length == 0 || s[0] is not ('1' or '2'))
        {
            error = s.Length > 0 && s[0] == '0' ? $"invalid field specifier: '{s}'" : $"invalid file number in field spec: '{s}'";
            return false;
        }
        if (s.Length < 2 || s[1] != '.') { error = $"invalid field specifier: '{s}'"; return false; }
        string num = s.Substring(2);
        if (!TryField(num, out int n)) { error = $"invalid field number: '{num}'"; return false; }
        spec = new OutSpec(s[0] - '0', n);
        return true;
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

    /// <summary>-e STRING decoy: bare -e is ambiguous with -ErrorAction/-ErrorVariable.</summary>
    [Parameter] public string? E { get; set; }

    /// <summary>-o FORMAT decoy: bare -o is ambiguous with -OutVariable/-OutBuffer.</summary>
    [Parameter] public string? O { get; set; }

    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (I.IsPresent) pre.Add("-i");
        if (A is not null) { pre.Add("-a"); pre.Add(A); }
        if (V is not null) { pre.Add("-v"); pre.Add(V); }
        if (E is not null) { pre.Add("-e"); pre.Add(E); }
        if (O is not null) { pre.Add("-o"); pre.Add(O); }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }

    private readonly List<object> _stdin = new();

    protected override void ProcessRecord()
    {
        if (InputObject is not null) _stdin.Add(InputObject);
    }

    /// <summary>One input line with its split fields and join key.</summary>
    private sealed class Row
    {
        public string Text = "";
        public string[] F = Array.Empty<string>();
        public string Key = "";
    }

    /// <summary>Raised to end the run after a --check-order disorder (the message is already written).</summary>
    private sealed class JoinAbort : Exception { }

    /// <summary>One input file: lazy lines, one-line lookahead, per-file order-check state.</summary>
    private sealed class Src
    {
        public string Name = "";
        public int Which;           // 1 or 2
        public IEnumerator<string> E = null!;
        public int LineNo;
        public Row? Pending;
        public Row? Prev;
        public Row? First;
        public bool Issued;
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
        if (operands[0] == "-" && operands[1] == "-")
        {
            FileSystemHelpers.WriteBashError(this, "join: both files cannot be standard input");
            return;
        }

        new Merge(this, plan, operands, _stdin).Run();
    }

    private sealed class Merge
    {
        private readonly InvokeBashJoinCommand _c;
        private readonly JoinArgs _p;
        private readonly Src[] _src = new Src[2];
        private readonly int[] _keyIdx;
        private readonly string _sep;
        private readonly bool _blank, _zero;
        private bool _seenUnpairable;
        private bool _anyIssued;
        private readonly bool _pairables, _unp1, _unp2;
        private List<OutSpec>? _format;
        private static readonly Regex s_blankRun = new("[ \t]+", RegexOptions.Compiled);
        private static readonly Regex s_blankRunNl = new("[ \t\n]+", RegexOptions.Compiled);

        public Merge(InvokeBashJoinCommand c, JoinArgs p, List<string> operands, List<object> stdin)
        {
            _c = c; _p = p;
            _keyIdx = new[] { p.Field1 - 1, p.Field2 - 1 };
            _sep = p.DelimiterExplicit ? p.Delimiter : " ";
            _blank = !p.DelimiterExplicit;
            _zero = p.Zero;
            _pairables = p.VFiles.Count == 0;
            _unp1 = p.AFiles.Contains(1) || p.VFiles.Contains(1);
            _unp2 = p.AFiles.Contains(2) || p.VFiles.Contains(2);
            for (int i = 0; i < 2; i++)
            {
                string op = operands[i];
                IEnumerable<string> lines;
                if (op == "-")
                    lines = _zero ? NulRecords.FromPipeline(stdin)
                                  : stdin.SelectMany(o => BashRuntime.RecordLines(o).Select(r => r.Text));
                else
                {
                    string path = c.SessionState.Path.GetUnresolvedProviderPathFromPSPath(op);
                    lines = _zero ? NulRecords.ReadFile(path) : BashFileSystem.ReadLines(path);
                }
                _src[i] = new Src { Name = op, Which = i + 1, E = lines.GetEnumerator() };
                _paths[i] = op == "-" ? "-" : c.SessionState.Path.GetUnresolvedProviderPathFromPSPath(op);
            }
        }

        private readonly string[] _paths = new string[2];

        private string[] Split(string line)
        {
            if (!_blank) return line.Split(_p.Delimiter, StringSplitOptions.None);
            var rx = _zero ? s_blankRunNl : s_blankRun;
            return rx.Split(line.TrimStart(' ', '\t', _zero ? '\n' : ' '));
        }

        private int Cmp(string a, string b)
            => _p.IgnoreCase
                ? Math.Sign(string.Compare(a, b, StringComparison.OrdinalIgnoreCase))
                : Math.Sign(string.CompareOrdinal(a, b));

        private Row MakeRow(string text, int which)
        {
            var f = Split(text);
            int k = _keyIdx[which - 1];
            return new Row { Text = text, F = f, Key = k < f.Length ? f[k] : "" };
        }

        /// <summary>Read the next line of a file (no lookahead), applying GNU's check_order.</summary>
        private Row? ReadRow(Src s, bool check = true)
        {
            if (!s.E.MoveNext()) return null;
            s.LineNo++;
            var row = MakeRow(s.E.Current, s.Which);
            s.First ??= row;
            if (check)
            {
                var mode = _p.Check;
                if (s.Prev is { } prev && mode != InvokeBashCommCommand.OrderCheck.Disabled
                    && (mode == InvokeBashCommCommand.OrderCheck.Enabled || _seenUnpairable)
                    && !s.Issued && Cmp(prev.Key, row.Key) > 0)
                {
                    _c.WriteObjectError($"join: {s.Name}:{s.LineNo}: is not sorted: {row.Text}");
                    s.Issued = true;
                    _anyIssued = true;
                    if (mode == InvokeBashCommCommand.OrderCheck.Enabled) throw new JoinAbort();
                }
                s.Prev = row;
            }
            return row;
        }

        private Row? Next(Src s)
        {
            if (s.Pending is { } p) { s.Pending = null; return p; }
            return ReadRow(s);
        }

        /// <summary>The rest of the run of lines whose key equals <paramref name="first"/> (GNU's equal-run read); the first different line becomes lookahead.</summary>
        private List<Row> ReadRun(Src s, Row first)
        {
            var run = new List<Row> { first };
            while (true)
            {
                var r = ReadRow(s);
                if (r is null) break;
                if (Cmp(first.Key, r.Key) == 0) run.Add(r);
                else { s.Pending = r; break; }
            }
            return run;
        }
        private string Fill(string v) => v.Length == 0 && _p.Filler is { } f ? f : v;

        private void Emit(IEnumerable<string> fields)
        {
            string text = string.Join(_sep, fields);
            _c.WriteObject(_zero ? NulRecords.Record(text) : BashRuntime.NewBashObject(text));
        }

        private List<OutSpec>? Format()
        {
            if (_format is not null) return _format.Count == 0 ? null : _format;
            _format = new List<OutSpec>(_p.OutList);
            if (_format.Count == 0 && _p.Auto)
            {
                _format.Add(new OutSpec(0, 0));
                for (int w = 1; w <= 2; w++)
                {
                    int n = _src[w - 1].First?.F.Length ?? 0;
                    for (int i = 1; i <= n; i++)
                        if (i - 1 != _keyIdx[w - 1]) _format.Add(new OutSpec(w, i));
                }
            }
            return _format.Count == 0 ? null : _format;
        }

        private string Col(Row? r, int which, int field)
            => r is not null && field - 1 < r.F.Length && field >= 1 ? Fill(r.F[field - 1]) : Fill("");

        private void PrintPair(Row r1, Row r2)
        {
            if (Format() is { } fmt)
            {
                Emit(fmt.Select(s => s.File == 0 ? Fill(r1.Key) : s.File == 1 ? Col(r1, 1, s.Field) : Col(r2, 2, s.Field)));
                return;
            }
            var parts = new List<string> { Fill(r1.Key) };
            for (int c = 0; c < r1.F.Length; c++) if (c != _keyIdx[0]) parts.Add(Fill(r1.F[c]));
            for (int c = 0; c < r2.F.Length; c++) if (c != _keyIdx[1]) parts.Add(Fill(r2.F[c]));
            Emit(parts);
        }

        private void PrintUnpaired(Row r, int which)
        {
            if (Format() is { } fmt)
            {
                Emit(fmt.Select(s => s.File == 0 ? Fill(r.Key) : s.File == which ? Col(r, which, s.Field) : Fill("")));
                return;
            }
            var parts = new List<string> { Fill(r.Key) };
            for (int c = 0; c < r.F.Length; c++) if (c != _keyIdx[which - 1]) parts.Add(Fill(r.F[c]));
            Emit(parts);
        }

        private Src _cur = null!;

        public void Run()
        {
            _cur = _src[0];
            try
            {
                if (_p.Header)
                {
                    _cur = _src[0]; var h1 = ReadRow(_src[0], check: false);
                    _cur = _src[1]; var h2 = ReadRow(_src[1], check: false);
                    if (h1 is not null && h2 is not null) PrintPair(h1, h2);
                    else if (h1 is not null) PrintUnpaired(h1, 1);
                    else if (h2 is not null) PrintUnpaired(h2, 2);
                }
                _cur = _src[0]; var l1 = Next(_src[0]);
                _cur = _src[1]; var l2 = Next(_src[1]);

                while (l1 is not null && l2 is not null)
                {
                    int d = Cmp(l1.Key, l2.Key);
                    if (d < 0)
                    {
                        if (_unp1) PrintUnpaired(l1, 1);
                        _cur = _src[0]; l1 = Next(_src[0]);
                        _seenUnpairable = true;   // set AFTER the advance: that read is not yet checked
                    }
                    else if (d > 0)
                    {
                        if (_unp2) PrintUnpaired(l2, 2);
                        _cur = _src[1]; l2 = Next(_src[1]);
                        _seenUnpairable = true;
                    }
                    else
                    {
                        _cur = _src[0]; var run1 = ReadRun(_src[0], l1);
                        _cur = _src[1]; var run2 = ReadRun(_src[1], l2);
                        if (_pairables)
                            foreach (var a in run1) foreach (var b in run2) PrintPair(a, b);
                        _cur = _src[0]; l1 = Next(_src[0]);
                        _cur = _src[1]; l2 = Next(_src[1]);
                    }
                }

                Drain(l1, 1, _unp1);
                Drain(l2, 2, _unp2);
                if (_anyIssued) _c.WriteBashErrorPublic("join: input is not in sorted order");
            }
            catch (JoinAbort)
            {
                FileSystemHelpers.SetLastExitCode(_c, 1);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                    || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
                string msg = notFound ? "No such file or directory" : ex.Message;
                FileSystemHelpers.WriteBashError(_c, $"join: {_cur.Name}: {msg}");
            }
            finally
            {
                _src[0].E.Dispose();
                _src[1].E.Dispose();
            }
        }

        private void Drain(Row? first, int which, bool print)
        {
            var s = _src[which - 1];
            _cur = s;
            if (first is null) return;
            if (print) PrintUnpaired(first, which);
            Row? row;
            while ((row = Next(s)) is not null)
                if (print) PrintUnpaired(row, which);
        }
    }
    internal void WriteObjectError(string message) => FileSystemHelpers.WriteStderr(this, message);
    internal void WriteBashErrorPublic(string message) => FileSystemHelpers.WriteBashError(this, message);
}
