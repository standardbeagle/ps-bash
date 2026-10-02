using PsBash.Core;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTail</c> function
/// (REFACTOR-2 Phase 1c). Emits the trailing lines (or bytes) of pipeline or
/// file input, matching the bash <c>tail</c> command.
///
/// Behavioral parity oracle: GNU coreutils 9.4 <c>tail</c>:
/// <list type="bullet">
/// <item>Flags: <c>-n N</c> / <c>-nN</c> / <c>-n +N</c> (line count, default
/// 10; the <c>+N</c> form switches to from-line mode), <c>-c N</c> / <c>-cN</c>
/// / <c>-c +N</c> (byte count / from-byte), the legacy <c>-N</c> shorthand, a
/// bare leading positional number, <c>-q</c> / <c>-v</c> (the LAST wins) and the "==> name &lt;=="
/// headers, <c>-z</c> (NUL-terminated records), <c>-f</c> / <c>--follow[=name|descriptor]</c> /
/// <c>-F</c> / <c>--retry</c> / <c>--pid=PID</c> / <c>--max-unchanged-stats=N</c> / <c>-s SECS</c>,
/// and <c>--</c>.</item>
/// <item>Pipeline mode: from-line mode skips the first N-1 items and emits the
/// rest; otherwise a circular buffer keeps only the last N items in memory.
/// Multi-line items are split; single-line items pass through as their
/// original typed object.</item>
/// <item>File mode: every operand in turn (a header before each when several, or with -v);
/// byte mode emits the last N bytes (or from byte N for <c>+N</c>); from-line mode streams and
/// emits from line N; otherwise a circular buffer emits the last N lines. Each file line is a typed
/// <c>PsBash.CatLine</c> PSObject with <c>BashText</c> equal to the raw line; a missing final newline
/// of the last line is copied through. The <c>-</c> operand is the pipeline.</item>
/// <item>Follow mode emits the initial tail of every file, then polls ALL of them (<see cref="TailFollower"/>),
/// printing a header whenever the output switches to another file.</item>
/// </list>
///
/// Common-parameter audit: <c>-n</c>, <c>-c</c>, <c>-f</c>, <c>-s</c> do not
/// prefix-collide with any PowerShell common parameter, so they are scanned out
/// of <see cref="Arguments"/> (matching the psm1 oracle's <c>$args</c> scan).
/// Operands are resolved via the psm1 <c>Resolve-BashGlob</c> slice
/// reimplemented in C# (a <see cref="PSCmdlet"/> reaches
/// <see cref="PSCmdlet.SessionState"/>). The <c>--help</c> path delegates to
/// the psm1 <c>Show-BashHelp</c>; a follow-mode error goes through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTail")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashTailCommand : PSCmdlet
{
    // -c VALUE prefix-collides with -Confirm under PowerShell's
    // case-insensitive binder; without an explicit declaration the bare
    // -c gets eaten by -Confirm and the value lands as a positional
    // (treated as a file operand). Declared as a value-bearing string
    // parameter so 'tail -c 30 file' binds correctly.
    [Parameter] public string? C { get; set; }

    /// <summary>Decoy for <c>-v</c> (--verbose, force headers).
    /// Bare <c>-v</c> silently bound <c>-Verbose</c>; re-injected below.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU <c>tail</c> options ps-bash does not implement (none left: -v, -z, -F, --retry,
    /// --pid, --max-unchanged-stats all work). (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] TailValidButUnsupported = Array.Empty<string>();

    private const string OptLines = "lines", OptBytes = "bytes", OptQuiet = "quiet", OptNum = "num",
        OptFollow = "follow", OptSleep = "sleep", OptVerbose = "verbose", OptZero = "zero",
        OptFollowName = "followname", OptRetry = "retry", OptPid = "pid", OptMaxUnchanged = "maxunchanged";

    /// <summary>GNU tail long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] TailLongOptionOrder = { "silent", "sleep-interval", "verbose", "version" };

    /// <summary>
    /// tail's option surface (GNU coreutils 9.4: -c -f -F -n -q -s -v -z + long forms; -NUM
    /// obsolete shorthand). <c>--follow</c> takes an OPTIONAL attached value (<c>name</c> /
    /// <c>descriptor</c>).
    /// </summary>
    private static readonly OptSpecSet TailSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'c', "bytes", OptKind.Value),
            new OptSpec(OptLines, 'n', "lines", OptKind.Value),
            new OptSpec(OptFollow, 'f', null),                            // -f takes no value in a bundle
            new OptSpec(OptFollow, '\0', "follow", OptKind.OptionalValue), // --follow[=name|descriptor]
            new OptSpec(OptFollowName, 'F', null),                        // -F = --follow=name --retry
            new OptSpec(OptQuiet, 'q', "quiet"),
            new OptSpec(OptQuiet, '\0', "silent"),
            new OptSpec(OptSleep, 's', "sleep-interval", OptKind.Value),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptZero, 'z', "zero-terminated"),
            new OptSpec(OptRetry, '\0', "retry"),
            new OptSpec(OptPid, '\0', "pid", OptKind.Value),
            new OptSpec(OptMaxUnchanged, '\0', "max-unchanged-stats", OptKind.Value),
        },
        validButUnsupported: TailValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        digitOptionWording: "option used in invalid context",
        longOptionOrder: TailLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TailSpec);

    /// <summary>The resolved meaning of a tail argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class TailArgs
    {
        public ParsedArgs Parsed = null!;
        /// <summary>Lines to print (magnitude; a leading <c>-</c> means the same as none).</summary>
        public int Count = 10;
        /// <summary>GNU <c>-n +N</c>: print from line N onward.</summary>
        public bool FromLine;
        public int ByteCount;
        /// <summary>GNU <c>-c +N</c>: print from byte N onward.</summary>
        public bool BytesFromStart;
        /// <summary>True when the LAST of -c / -n on the line was -c (GNU: last one wins).</summary>
        public bool BytesMode;
        public bool Follow;
        /// <summary>-F or --follow=name: follow the NAME (it can be replaced), not the open file.</summary>
        public bool FollowName;
        /// <summary>--retry or -F: keep trying files that are or become inaccessible.</summary>
        public bool Retry;
        /// <summary>--pid=PID (0 = none): stop following once that process has died.</summary>
        public long Pid;
        public double SleepInterval = 1.0;
        /// <summary>The LAST of -q/-v: when "==> name &lt;==" headers print.</summary>
        public FileHeaders.Mode Headers = FileHeaders.Mode.Default;
        /// <summary>-z: records end at NUL instead of newline (line mode only).</summary>
        public bool Zero;
        /// <summary>Warnings GNU prints before doing anything (--retry / --pid without -f).</summary>
        public List<string> Warnings = new();
        public List<string> Operands = new();
        /// <summary>A usage error the scan itself cannot see (bad NUM, bad -s, bad --follow value); exit 1.</summary>
        public string? Error;

        /// <summary>True when nothing further should execute: scan error, value error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve. GNU rules kept: last -c/-n wins; NUM takes a sign (<c>+</c> = from the
    /// start) and a multiplier suffix (<see cref="GnuNumber"/>), an invalid one is an error; the
    /// obsolete <c>-NUM</c> is only valid as the FIRST argument; the obsolete <c>+NUM</c> first
    /// argument (<c>tail +2</c>) means <c>-n +2</c>. The legacy ps-bash extension — a bare leading
    /// positional number is the line count (<c>tail 5</c>, Pester-pinned) — is preserved.
    /// </summary>
    internal static TailArgs Plan(string[] args)
    {
        // A valid obsolete first argument (-NUM[bcl][f] / +NUM[bcl][f]) is rewritten to the options
        // it stands for; anything else digit-shaped is an "invalid context" error from the scan.
        if (TryExpandObsolete(args, out var expanded)) args = expanded;

        var t = new TailArgs { Parsed = ScanArgs(args) };
        t.Operands = t.Parsed.Operands();
        if (t.Parsed.HasError) return t;

        bool retryFlag = false, pidGiven = false;
        foreach (var tok in t.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptLines:
                    if (!GnuNumber.TryParse(tok.Value!, out int lines, out char lsign))
                    {
                        t.Error = $"tail: invalid number of lines: '{tok.Value}'";
                        return t;
                    }
                    t.Count = lines;
                    t.FromLine = lsign == '+';
                    t.BytesMode = false;
                    break;
                case OptBytes:
                    if (!GnuNumber.TryParse(tok.Value!, out int bytes, out char bsign))
                    {
                        t.Error = $"tail: invalid number of bytes: '{tok.Value}'";
                        return t;
                    }
                    t.ByteCount = bytes;
                    t.BytesFromStart = bsign == '+';
                    t.BytesMode = true;
                    break;
                case OptFollow:
                    if (tok.Value is { } how)
                    {
                        if (!IsFollowHow(how))
                        {
                            t.Error = $"tail: invalid argument '{how}' for '--follow'\n"
                                      + "Valid arguments are:\n  - 'descriptor'\n  - 'name'";
                            return t;
                        }
                        t.FollowName = "name".StartsWith(how, StringComparison.Ordinal);
                    }
                    else
                    {
                        t.FollowName = false;
                    }
                    t.Follow = true;
                    break;
                case OptFollowName:
                    t.Follow = true;
                    t.FollowName = true;
                    retryFlag = true;
                    break;
                case OptRetry:
                    retryFlag = true;
                    break;
                case OptPid:
                    if (!ulong.TryParse(tok.Value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out ulong pid) || pid > int.MaxValue)
                    {
                        t.Error = $"tail: invalid PID: '{tok.Value}'";
                        return t;
                    }
                    t.Pid = (long)pid;
                    pidGiven = true;
                    break;
                case OptMaxUnchanged:
                    if (!ulong.TryParse(tok.Value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        t.Error = $"tail: invalid maximum number of unchanged stats between opens: '{tok.Value}'";
                        return t;
                    }
                    break;
                case OptSleep:
                    if (!double.TryParse(tok.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double secs) || secs < 0)
                    {
                        t.Error = $"tail: invalid number of seconds: '{tok.Value}'";
                        return t;
                    }
                    t.SleepInterval = secs;
                    break;
                case OptQuiet: t.Headers = FileHeaders.Mode.Never; break;
                case OptVerbose: t.Headers = FileHeaders.Mode.Always; break;
                case OptZero: t.Zero = true; break;
            }
        }

        t.Retry = retryFlag;
        // GNU's start-up warnings (stderr, no effect on the status).
        if (retryFlag && !t.Follow)
            t.Warnings.Add("tail: warning: --retry ignored; --retry is useful only when following");
        else if (retryFlag && !t.FollowName)
            t.Warnings.Add("tail: warning: --retry only effective for the initial open");
        if (pidGiven && !t.Follow)
            t.Warnings.Add("tail: warning: PID ignored; --pid=PID is useful only when following");

        // Legacy ps-bash extension, on the FIRST operand only and never after `--`:
        //   tail 5   -> line count 5 (Pester-pinned)
        foreach (var tok in t.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Operand) continue;
            if (!tok.AfterDoubleDash && IsAllDigits(tok.Raw))
            {
                t.Count = BashRuntime.ParseCountClamped(tok.Raw.AsSpan());
                t.Operands.RemoveAt(0);
            }
            break; // only the first operand qualifies
        }
        return t;
    }

    /// <summary>
    /// GNU tail's obsolete first argument <c>-NUM[bcl][f]</c> / <c>+NUM[bcl][f]</c> (oracle-checked,
    /// coreutils 9.4): <c>c</c> = bytes, <c>l</c> = lines, <c>b</c> = 512-byte blocks (bytes), a trailing
    /// <c>f</c> follows. It is honoured only when nothing else could be an option: at most one further
    /// argument (a file, <c>-</c>, or <c>--</c>) or exactly <c>-- FILE</c>; otherwise <c>-2 -q</c>,
    /// <c>-2 a b</c>, <c>-2 a --</c> are "option used in invalid context" (a <c>+NUM</c> then stays an
    /// ordinary file operand). Rewrites to the equivalent <c>-n</c>/<c>-c</c> [+ <c>-f</c>] options.
    /// </summary>
    internal static bool TryExpandObsolete(string[] args, out string[] expanded)
    {
        expanded = args;
        if (args.Length == 0) return false;

        string a = args[0];
        if (a.Length < 2 || (a[0] != '-' && a[0] != '+') || !char.IsAsciiDigit(a[1])) return false;

        int i = 1;
        while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
        string digits = a.Substring(1, i - 1);
        bool bytes = false;
        string suffix = "";
        if (i < a.Length && a[i] is 'b' or 'c' or 'l')
        {
            bytes = a[i] != 'l';
            if (a[i] == 'b') suffix = "b";
            i++;
        }
        bool follow = false;
        if (i < a.Length && a[i] == 'f') { follow = true; i++; }
        if (i != a.Length) return false; // not the obsolete shape (`-2x`: a digit-in-bundle error)

        // Arity rule: the rest is nothing, one non-option word, `-`, `--`, or `-- WORD`.
        var rest = args.AsSpan(1);
        if (rest.Length > 0 && rest[0] == "--") rest = rest.Slice(1);
        else if (rest.Length == 1 && rest[0].Length > 1 && rest[0][0] == '-') return false;
        if (rest.Length > 1) return false;

        var list = new List<string>
        {
            bytes ? "-c" : "-n",
            (a[0] == '+' ? "+" : "") + digits + suffix,
        };
        if (follow) list.Add("-f");
        list.AddRange(args.Skip(1));
        expanded = list.ToArray();
        return true;
    }

    /// <summary>GNU argmatch: any non-empty prefix of <c>name</c> or <c>descriptor</c>.</summary>
    private static bool IsFollowHow(string v) =>
        v.Length > 0 && ("name".StartsWith(v, StringComparison.Ordinal)
                         || "descriptor".StartsWith(v, StringComparison.Ordinal));

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (bare -v binds -Verbose; -c binds
    /// the value decoy C). A value decoy becomes the two elements <c>-c VALUE</c>.</summary>
    private string[] ArgsWithDecoys()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (V.IsPresent, "-v"));
        if (C is not null) args = new[] { "-c", C }.Concat(args).ToArray();
        return args;
    }

    // ---- Streaming pipeline mode (memory bound) -------------------------------------------------
    // Arguments are resolved in BeginProcessing (before the first record), so pipeline input is
    // consumed as it arrives instead of being collected whole:
    //   -n +N / -c +N  pure streaming (skip, then pass through);
    //   -n N           ring of the last N records (O(N));
    //   -c N           ring of the last >= N bytes of record text (O(N + one record)).
    // -z (NUL records) and a "-" operand buffer the pipeline instead (EndProcessing).
    // File mode (operands present) runs in EndProcessing.
    private TailArgs? _plan;
    private bool _halt;
    private bool _pipeMode;
    private bool _bufferPipe;              // -z, or a "-" operand: keep every record for EndProcessing
    private readonly List<PSObject> _buffered = new();
    private long _lineIdx;                 // -n +N: records/lines seen so far
    private long _bytesToSkip;             // -c +N: bytes still to skip
    private Queue<object>? _lineRing;      // -n N
    private Queue<byte[]>? _byteRing;      // -c N
    private long _byteRingTotal;
    private bool _anyHeader;               // a header was written (the next one gets the blank separator)
    private string? _lastHeaderName;       // follow: the file whose header is on screen
    private bool _stdinHeaderDone;

    protected override void BeginProcessing()
    {
        // Re-inject the decoy-bound flags (-v, -c VALUE) so the shared parser sees them.
        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tail", args)) { _halt = true; return; }
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tail"))
            {
                WriteObject(line);
            }
            _halt = true;
            return;
        }

        // Shared ordered parser: bundles (-qn2), attached values (-n5, --lines=5, --bytes=5),
        // abbreviations (--li=1), `--`, and the unsupported/unknown classifier in ONE scan.
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tail", plan.Parsed)) { _halt = true; return; }
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tail", plan.Parsed)) { _halt = true; return; }
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError); // exit 1, GNU's usage status
            _halt = true;
            return;
        }
        foreach (var w in plan.Warnings) FileSystemHelpers.WriteStderr(this, w);

        _plan = plan;
        _pipeMode = plan.Operands.Count == 0;
        bool dashOperand = plan.Operands.Contains("-");
        _bufferPipe = (plan.Zero && !plan.BytesMode) || dashOperand;
        if (!_pipeMode)
        {
            return;
        }
        if (_bufferPipe) return;

        if (plan.BytesMode)
        {
            if (plan.BytesFromStart) _bytesToSkip = Math.Max((long)plan.ByteCount - 1, 0);
            else if (plan.ByteCount > 0) _byteRing = new Queue<byte[]>();
        }
        else if (plan.FromLine)
        {
            _bytesToSkip = 0;
        }
        else if (plan.Count > 0)
        {
            _lineRing = new Queue<object>(); // grows lazily: `tail -n 2000000000` must not preallocate
        }
    }

    private void WriteHeader(string name)
    {
        WriteObject(FileHeaders.Record(name, first: !_anyHeader));
        _anyHeader = true;
        _lastHeaderName = name;
    }

    protected override void ProcessRecord()
    {
        if (_halt || _plan is null || InputObject is null) return;
        var item = InputObject;

        if (_bufferPipe)
        {
            _buffered.Add(item);
            return;
        }
        if (!_pipeMode) return;

        // `tail -v` on stdin: the header precedes the first output (which only appears at the end for
        // the ring modes, but the header position is the same).
        if (!_stdinHeaderDone)
        {
            _stdinHeaderDone = true;
            if (_plan.Headers == FileHeaders.Mode.Always && (_plan.FromLine || (_plan.BytesMode && _plan.BytesFromStart)))
                WriteHeader(FileHeaders.StandardInput);
        }

        if (_plan.BytesMode)
        {
            // Byte stream text of this record: BashText + boundary \n unless unterminated.
            string text = BashRuntime.GetBashText(item);
            if (!text.EndsWith('\n') && !BashRuntime.IsUnterminated(item)) text += "\n";
            byte[] bytes = RawBytes.GetBytes(text);

            if (_plan.BytesFromStart)
            {
                int skip = (int)Math.Min(_bytesToSkip, bytes.Length);
                _bytesToSkip -= skip;
                if (skip == bytes.Length) return;
                // Byte slice: a TRANSFORMER — fresh text records carrying exactly the slice's bytes.
                foreach (var rec in BashRuntime.ByteSliceRecords(
                             RawBytes.GetString(bytes, skip, bytes.Length - skip)))
                    WriteObject(rec);
            }
            else if (_byteRing is not null)
            {
                long keep = _plan.ByteCount;
                _byteRing.Enqueue(bytes);
                _byteRingTotal += bytes.Length;
                // Drop whole leading chunks that can no longer contribute to the last N bytes.
                while (_byteRingTotal - _byteRing.Peek().Length >= keep)
                    _byteRingTotal -= _byteRing.Dequeue().Length;
            }
            return;
        }

        if (_plan.FromLine)
        {
            long skip = (long)_plan.Count - 1;
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                bool unterminated = BashRuntime.IsUnterminated(item);
                var pieces = trimmed.Split('\n');
                for (int p = 0; p < pieces.Length; p++)
                {
                    if (_lineIdx >= skip)
                        WriteObject(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
                    _lineIdx++;
                }
            }
            else
            {
                if (_lineIdx >= skip) WriteObject(item);
                _lineIdx++;
            }
            return;
        }

        if (_lineRing is null) return; // -n 0: GNU prints nothing
        {
            int cap = _plan.Count;
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                bool unterminated = BashRuntime.IsUnterminated(item);
                var pieces = trimmed.Split('\n');
                for (int p = 0; p < pieces.Length; p++)
                {
                    _lineRing.Enqueue(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
                    if (_lineRing.Count > cap) _lineRing.Dequeue();
                }
            }
            else
            {
                _lineRing.Enqueue(item);
                if (_lineRing.Count > cap) _lineRing.Dequeue();
            }
        }
    }

    protected override void EndProcessing()
    {
        if (_halt || _plan is null) return;
        var plan = _plan;
        var operands = plan.Operands;

        // Pipeline mode: the streaming parts already ran in ProcessRecord; flush the rings.
        if (_pipeMode)
        {
            // -v: the header goes before everything (once, even for empty input).
            if (plan.Headers == FileHeaders.Mode.Always && !(_stdinHeaderDone && (plan.FromLine || (plan.BytesMode && plan.BytesFromStart))))
                WriteHeader(FileHeaders.StandardInput);

            if (_bufferPipe)
            {
                EmitFromRecords(_buffered, plan);
                return;
            }
            if (_byteRing is not null)
            {
                long keep = plan.ByteCount;
                var all = new byte[_byteRingTotal];
                int off = 0;
                foreach (var chunk in _byteRing)
                {
                    Buffer.BlockCopy(chunk, 0, all, off, chunk.Length);
                    off += chunk.Length;
                }
                long start = Math.Max(0, all.Length - keep);
                string text = RawBytes.GetString(all, (int)start, (int)(all.Length - start));
                foreach (var rec in BashRuntime.ByteSliceRecords(text)) WriteObject(rec);
                _byteRing = null;
            }
            else if (_lineRing is not null)
            {
                while (_lineRing.Count > 0) WriteObject(_lineRing.Dequeue());
            }
            return;
        }

        // File mode
        var resolvedFiles = ResolveGlob(operands).ToList();
        if (resolvedFiles.Count == 0)
        {
            return;
        }

        bool headers = FileHeaders.Wanted(plan.Headers, operands.Count);
        var targets = new List<TailFollower.Target>();
        foreach (var filePath in resolvedFiles)
        {
            if (filePath == "-")
            {
                if (headers) WriteHeader(FileHeaders.StandardInput);
                EmitFromRecords(_buffered, plan);
                continue;
            }

            var target = new TailFollower.Target { Path = filePath, Display = FileHeaders.Display(this, filePath) };
            bool exists = File.Exists(filePath);
            long? openedAt = null;
            if (exists)
            {
                if (headers) WriteHeader(target.Display);
                try { openedAt = new FileInfo(filePath).Length; } catch { /* reported by the reader below */ }
                if (!EmitFile(filePath, plan)) openedAt = null;
            }
            else
            {
                // GNU prints the diagnostic at the failed open, in operand order.
                WriteFileReadError(filePath, "tail", new FileNotFoundException());
            }
            targets.Add(target);
            if (plan.Follow) target.Pos = openedAt ?? 0;
            if (plan.Follow) _initialOpen[target] = openedAt;
        }

        if (!plan.Follow) return;
        FollowLoop(plan, targets, headers);
    }

    private readonly Dictionary<TailFollower.Target, long?> _initialOpen = new();

    /// <summary>
    /// Follow every target until the pipeline stops, <c>--pid</c> dies, or no target is left. The initial
    /// output has been written; this prints only what is appended, with a header whenever the output
    /// switches to another file (more than one file, or -v).
    /// </summary>
    private void FollowLoop(TailArgs plan, List<TailFollower.Target> targets, bool headers)
    {
        var follower = new TailFollower(targets, plan.FollowName, plan.Retry,
            msg => FileSystemHelpers.WriteStderr(this, msg));
        foreach (var t in targets) follower.Start(t, _initialOpen[t]);

        if (!follower.AnyLeft)
        {
            FileSystemHelpers.WriteBashError(this, "tail: no files remaining");
            return;
        }

        // -v (or several files) puts a header before the output of whichever file speaks next.
        _lastHeaderName = headers && targets.Count > 0 ? targets.LastOrDefault(x => _initialOpen[x] is not null)?.Display : null;

        try
        {
            while (!Stopping)
            {
                bool pidDead = plan.Pid > 0 && !TailFollower.IsAlive(plan.Pid);
                follower.Poll((t, lines) =>
                {
                    if (headers && _lastHeaderName != t.Display) WriteHeader(t.Display);
                    foreach (var l in lines)
                        foreach (var obj in BashRuntime.EmitBashLines(l))
                            WriteObject(obj);
                });
                if (pidDead) return;
                if (!follower.AnyLeft)
                {
                    FileSystemHelpers.WriteBashError(this, "tail: no files remaining");
                    return;
                }
                Thread.Sleep((int)(plan.SleepInterval * 1000));
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tail: cannot follow file: {ex.Message}");
        }
    }

    /// <summary>
    /// GNU <c>tail -z</c> over a whole byte stream: the last N NUL-terminated pieces (<c>-n +N</c>: from
    /// piece N). The final piece may be unterminated and is then copied as it is.
    /// </summary>
    internal static string ZeroTail(string text, int count, bool fromStart)
    {
        if (text.Length == 0) return text;
        var pieces = new List<string>(text.Split('\0'));
        bool endsWithNul = text.EndsWith('\0');
        if (endsWithNul) pieces.RemoveAt(pieces.Count - 1);
        int total = pieces.Count;
        int first = fromStart ? Math.Max(count - 1, 0) : Math.Max(total - count, 0);
        if (!fromStart && count == 0) first = total;
        var sb = new StringBuilder();
        for (int i = first; i < total; i++)
        {
            sb.Append(pieces[i]);
            if (i < total - 1 || endsWithNul) sb.Append('\0');
        }
        return sb.ToString();
    }

    /// <summary>The buffered-pipeline body (-z, or the "-" operand): tail of whole records.</summary>
    private void EmitFromRecords(List<PSObject> records, TailArgs plan)
    {
        if (plan.BytesMode)
        {
            byte[] bytes = RawBytes.GetBytes(BashRuntime.RecordStreamText(records));
            long start = plan.BytesFromStart
                ? Math.Min(Math.Max((long)plan.ByteCount - 1, 0), bytes.Length)
                : Math.Max(0, bytes.Length - (long)Math.Max(plan.ByteCount, 0));
            foreach (var rec in BashRuntime.ByteSliceRecords(
                         RawBytes.GetString(bytes, (int)start, (int)(bytes.Length - start))))
                WriteObject(rec);
            return;
        }
        if (plan.Zero)
        {
            foreach (var rec in BashRuntime.ByteSliceRecords(
                         ZeroTail(BashRuntime.RecordStreamText(records), plan.Count, plan.FromLine)))
                WriteObject(rec);
            return;
        }

        // Newline records: split every item into lines (keeping typed single-line objects), then select.
        var all = new List<object>();
        foreach (var item in records)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                bool unterminated = BashRuntime.IsUnterminated(item);
                var pieces = trimmed.Split('\n');
                for (int p = 0; p < pieces.Length; p++)
                    all.Add(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
            }
            else
            {
                all.Add(item);
            }
        }
        int begin = plan.FromLine ? Math.Max(plan.Count - 1, 0) : Math.Max(all.Count - plan.Count, 0);
        if (!plan.FromLine && plan.Count == 0) begin = all.Count;
        for (int i = begin; i < all.Count; i++) WriteObject(all[i]);
    }

    /// <summary>
    /// The initial (non-follow) output of one file: the last N lines / bytes, or from line / byte N.
    /// Returns false when the file could not be read (the diagnostic has been written).
    /// </summary>
    private bool EmitFile(string filePath, TailArgs plan)
    {
        if (plan.BytesMode)
            return EmitFileBytes(filePath, plan.ByteCount, plan.BytesFromStart, "tail");

        if (plan.Zero)
        {
            try
            {
                string text = RawBytes.GetString(BashFileSystem.ReadAllBytes(filePath));
                foreach (var rec in BashRuntime.ByteSliceRecords(ZeroTail(text, plan.Count, plan.FromLine)))
                    WriteObject(rec);
                return true;
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                WriteFileReadError(filePath, "tail", ex);
                return false;
            }
        }

        // Text lines that remember whether the LAST one had its newline: GNU copies a missing final
        // newline through (a following header's separator then ends the line).
        try
        {
            int count = plan.Count;
            if (plan.FromLine)
            {
                int li = 0;
                foreach (var line in BashFileSystem.ReadTextLines(filePath))
                {
                    li++;
                    if (li >= count)
                        WriteObject(MakeCatLine(li, line.Text, filePath, !line.HasTrailingNewline));
                }
                return true;
            }

            if (count == 0)
            {
                foreach (var _ in BashFileSystem.ReadTextLines(filePath)) { break; } // still opens/validates the file
                return true;
            }
            var ring = new Queue<(string Text, bool Unterminated)>();
            int total = 0;
            foreach (var line in BashFileSystem.ReadTextLines(filePath))
            {
                ring.Enqueue((line.Text, !line.HasTrailingNewline));
                if (ring.Count > count) ring.Dequeue();
                total++;
            }
            int number = total - ring.Count;
            foreach (var (text, unterminated) in ring)
                WriteObject(MakeCatLine(++number, text, filePath, unterminated));
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteFileReadError(filePath, "tail", ex);
            return false;
        }
    }

    // Per-poll read cap for tail -f: bounds the byte[] a huge burst append allocates.
    private const long FollowReadCap = 64L << 20;

    /// <summary>
    /// Pure core of the <c>tail -f</c> poll: given a chunk of <paramref name="read"/>
    /// bytes read from the follow position (with <paramref name="avail"/> total bytes
    /// pending, capped by <paramref name="capBytes"/>), return the COMPLETE lines to emit
    /// and the number of bytes to advance the follow position past.
    /// <para>GNU <c>tail -f</c> emits only complete lines: a trailing newline-less
    /// fragment is WITHHELD (advance 0) until its newline arrives — EXCEPT when the
    /// pending data exceeds the cap (a pathological unterminated &gt;64MB run), which is
    /// force-emitted so following cannot stall. <c>'\n'</c> (0x0A) never occurs
    /// mid-UTF-8-sequence, so the byte-level newline scan is safe; a CRLF's <c>\r</c> is
    /// stripped to match the old <c>StreamReader.ReadLine</c> behaviour. Extracted from
    /// the I/O loop so the withhold/advance logic is unit-testable.</para>
    /// </summary>
    internal static (int Advance, List<string> Lines) SplitFollowChunk(
        byte[] buffer, int read, long avail, long capBytes)
    {
        var lines = new List<string>();
        if (read <= 0) return (0, lines);

        int lastNl = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNl < 0)
        {
            if (avail <= capBytes) return (0, lines); // withhold the whole fragment
            lastNl = read - 1;                          // cap exceeded — force-emit
        }

        var text = RawBytes.GetString(buffer, 0, lastNl + 1);
        var parts = text.Split('\n');
        int emitCount = text.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (int k = 0; k < emitCount; k++)
        {
            var l = parts[k];
            if (l.EndsWith('\r')) l = l[..^1];
            lines.Add(l);
        }
        return (lastNl + 1, lines);
    }

    private bool EmitFileBytes(string path, int byteCount, bool fromByte, string command)
    {
        try
        {
            using var fs = BashFileSystem.OpenRead(path);
            long safeCount = Math.Max(byteCount, 0);
            // `-c +N` starts AT byte N (1-based): skip N-1. (The old code skipped N.)
            long start = fromByte
                ? Math.Min(Math.Max(safeCount - 1, 0), fs.Length)
                : Math.Max(0, fs.Length - safeCount);
            fs.Seek(start, SeekOrigin.Begin);

            // Byte slice: a TRANSFORMER — fresh text records carrying exactly the slice's
            // bytes (GNU `tail -c 1` of `a\nc` is `c`, not `c\n`; the slice's own newlines
            // are kept), the same as the pipeline path and `head -c FILE`.
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            foreach (var rec in BashRuntime.ByteSliceRecords(
                         RawBytes.GetString(ms.GetBuffer(), 0, (int)ms.Length)))
                WriteObject(rec);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteFileReadError(path, command, ex);
            return false;
        }
    }

    private void WriteFileReadError(string path, string command, Exception ex)
    {
        // GNU: tail: cannot open 'x' for reading: No such file or directory
        FileSystemHelpers.WriteBashError(this,
            $"{command}: cannot open '{path.Replace('\\', '/')}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
    }

    private static PSObject MakeCatLine(int lineNumber, string content, string fileName, bool unterminated = false)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.CatLine");
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Content", content));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty(
            "BashText", BashRuntime.NormalizeBashText(content)));
        if (unterminated) obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        return obj;
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashWcCommand"/> for the rationale).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var rawP in paths)
        {
            if (rawP == "-") { yield return "-"; continue; }
            var p = FileSystemHelpers.NormalizeOperandPath(rawP);
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path.GetResolvedProviderPathFromPSPath(
                                 p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
                }

                if (matched.Count == 0)
                {
                    yield return p;
                }
                else
                {
                    foreach (var m in matched)
                    {
                        yield return m;
                    }
                }
            }
            else
            {
                var resolvedLiteral = SessionState.Path.GetUnresolvedProviderPathFromPSPath(p);
                OperandDisplay.Remember(this, resolvedLiteral, rawP);
                yield return resolvedLiteral;
            }
        }
    }
}
