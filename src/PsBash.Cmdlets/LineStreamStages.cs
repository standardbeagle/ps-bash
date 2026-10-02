using System.Text;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// A single fused-pipeline stage as a lazy line→line transform (PERF task
/// 01KXQ0KMG5C26BWXNVPZXBVA6H, phase 2b). Where phase-2a ran the inner PowerShell
/// pipeline (one <c>PSCustomObject</c> per line + per-stage pipeline dispatch),
/// a streaming stage consumes an <see cref="IEnumerable{String}"/> of line texts
/// and yields line texts directly — no per-line object allocation, no pipeline
/// engine. The executor renders each yielded string as <c>line + Environment.NewLine</c>,
/// which is byte-identical to the unfused path's serialization (a
/// <c>PsBash.TextOutput</c> object / bare string renders exactly that way — see
/// <c>InvokeBashFusedPipelineCommand.RenderItem</c>).
///
/// <para>Laziness gives head-early-exit for free: when a downstream stage stops
/// pulling (e.g. <c>head</c> after N lines), the upstream generators are abandoned
/// mid-iteration, so the producer stops producing — exactly like a real pipe's
/// SIGPIPE.</para>
///
/// <para><b>Per-argv opt-in:</b> a stage is created by
/// <see cref="LineStreamRegistry.TryCreate"/> only when the command's argv falls
/// inside the CERTIFIED subset for that command (byte-parity proven against the
/// real cmdlet). Any argv outside the subset returns <c>false</c> — the whole fused
/// chain then declines and runs phase-2a's delegate+batch fallback. Correctness
/// always wins; the streaming lane is a pure speedup for the cases it covers.</para>
/// </summary>
public interface ILineStreamStage
{
    /// <summary>Transform the input line stream lazily. The producer stage ignores
    /// <paramref name="input"/> (a fused pipeline receives no external stdin).</summary>
    IEnumerable<string> Run(IEnumerable<string> input);

    /// <summary>The stage's bash exit code, valid AFTER <see cref="Run"/> has been
    /// fully enumerated (grep sets 1 on no-match). The executor propagates only the
    /// LAST stage's code to <c>$global:LASTEXITCODE</c>, matching an unfused pipe.</summary>
    int ExitCode { get; }
}

/// <summary>
/// Maps a fused stage's command name + argv to a streaming <see cref="ILineStreamStage"/>,
/// or declines (returns false) so the fused executor falls back to phase-2a. Each
/// command's core is EXTRACTED from (or reuses helpers of) its real cmdlet so the
/// streamed output is identical to the unfused path by construction; the
/// fused-vs-unfused parity tests are the guard.
/// </summary>
public static class LineStreamRegistry
{
    /// <summary>Commands with a streaming core in this wave. A fused pipeline streams
    /// only when EVERY stage is here AND accepts its argv.</summary>
    /// <param name="resolvePath">
    /// How a stage turns a relative FILE operand into a full path. The fused cmdlet passes
    /// PowerShell's own resolver (<c>SessionState.Path</c>), which is what the real
    /// <c>Invoke-Bash*</c> cmdlets use — so a stage and the cmdlet it stands in for can
    /// never disagree about which file a relative operand names. Omitted (unit tests,
    /// direct callers) → <see cref="Path.GetFullPath(string)"/> against the process cwd.
    /// See <see cref="PsBash.Cmdlets.LineStream.CatFileStage"/> for the three silent
    /// wrong-file bugs this parameter exists to end.
    /// </param>
    public static bool TryCreate(string name, string[] argv, out ILineStreamStage stage, Func<string, string>? resolvePath = null)
    {
        stage = null!;
        ILineStreamStage? s = name switch
        {
            "seq" => SeqStage.TryCreate(argv),
            "cat" => CatStage.TryCreate(argv, resolvePath),
            "rev" => RevStage.TryCreate(argv),
            "head" => HeadStage.TryCreate(argv),
            "wc" => WcStage.TryCreate(argv),
            "grep" => GrepStage.TryCreate(argv),
            "sed" => SedStage.TryCreate(argv),
            // S2: sort + uniq (LineStream/SortStage.cs, LineStream/UniqStage.cs).
            // sort is the lane's first BLOCKING core — see its class remarks for what
            // that costs (no downstream early-exit past it).
            "sort" => SortStage.TryCreate(argv),
            "uniq" => UniqStage.TryCreate(argv),
            // S3: the remaining five names of PsEmitter.FusePipelineAllowlist, so no chain
            // declines on an ARBITRARY stage any more. Two carry warnings worth reading at
            // the call site: `tr` transforms each record WHOLE (the newline is an ordinary
            // translatable character to tr, not a record boundary — see TrStage), and `tac`
            // is BLOCKING like sort (reversal needs the last line first).
            "tr" => TrStage.TryCreate(argv),
            "cut" => CutStage.TryCreate(argv),
            "tail" => TailStage.TryCreate(argv),
            "tac" => TacStage.TryCreate(argv),
            "nl" => NlStage.TryCreate(argv),
            _ => null,
        };
        if (s is null) return false;
        stage = s;
        return true;
    }
}

/// <summary>Producer: <c>seq</c>. Reuses <see cref="SeqCore"/> (the same value
/// generator the cmdlet uses). Ignores pipeline input.</summary>
internal sealed class SeqStage : ILineStreamStage
{
    private readonly IEnumerable<string> _values; // lazy: nothing is formatted until pulled
    private readonly string? _separator;
    private SeqStage(IEnumerable<string> values, string? separator) { _values = values; _separator = separator; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // --help / --version are cmdlet-owned output paths — decline.
        foreach (var a in argv)
            if (a == "--help" || a == "--version") return null;
        try
        {
            var status = SeqCore.Generate(argv, false, out var values, out var sep, out _, out _);
            if (status != SeqCore.Status.Ok) return null; // zero increment → fallback emits error
            return new SeqStage(values, sep);
        }
        catch
        {
            // Non-numeric operand etc. — let the real cmdlet reproduce the error.
            return null;
        }
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        if (_separator != null)
        {
            yield return string.Join(_separator, _values);
            yield break;
        }
        foreach (var v in _values) yield return v;
    }
}

/// <summary>Passthrough: bare <c>cat</c> (no flags, no file operands). A FILE-operand
/// invocation is handed to <see cref="CatFileStage"/> (the producer form that unblocks
/// <c>cat f | grep x | sort</c>); flags still need the cmdlet's numbering/glob paths and
/// decline there.</summary>
internal sealed class CatStage : ILineStreamStage
{
    private CatStage() { }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv, Func<string, string>? resolvePath = null)
    {
        // A bare `cat` (or `cat -`, the explicit stdin marker) is a pure line
        // passthrough. `cat FILE…` is a PRODUCER — a different stage entirely.
        bool allStdin = true;
        foreach (var a in argv)
            if (a != "-") { allStdin = false; break; }
        if (allStdin) return new CatStage();
        return CatFileStage.TryCreate(argv, resolvePath);
    }

    public IEnumerable<string> Run(IEnumerable<string> input) => input;
}

/// <summary><c>rev</c>: reverse each line. Only pipeline mode (no operands).</summary>
internal sealed class RevStage : ILineStreamStage
{
    private RevStage() { }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
        => argv.Length == 0 ? new RevStage() : null; // any arg = file/help/version mode

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        foreach (var line in input) yield return Reverse(line);
    }

    private static string Reverse(string s)
    {
        if (s.Length <= 1) return s;
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}

/// <summary><c>head</c>: first N lines (default 10). Certified subset: <c>-n N</c> /
/// <c>-nN</c> / <c>-N</c> / bare positional number, non-negative, no file operands,
/// no <c>-c</c> byte mode. Lazy — stops pulling after N lines (upstream early-exit).</summary>
internal sealed class HeadStage : ILineStreamStage
{
    private readonly int _count;
    private HeadStage(int count) { _count = count; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // The cmdlet's own resolver (shared ordered parser + NUM rules) decides first, so this
        // core can NEVER accept an argv the cmdlet would reject or interpret differently.
        var plan = InvokeBashHeadCommand.Plan(argv);
        if (plan.Declined || plan.BytesMode || plan.Operands.Count > 0 || plan.Count < 0) return null;

        // Certified subset within that: only -n N / -nN / -N (no '+' count, no -q, no --lines
        // spelling variants beyond the ids below, no `--`, no bare positional number).
        foreach (var tok in plan.Parsed.Tokens)
        {
            if (tok.Kind == ArgTokKind.DoubleDash || tok.Kind == ArgTokKind.Operand) return null;
            if (tok.OptId != "lines" && tok.OptId != "num") return null;
            if (tok.Value!.StartsWith('+')) return null;
        }
        return new HeadStage(plan.Count);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        if (_count <= 0) yield break;
        int emitted = 0;
        foreach (var line in input)
        {
            yield return line;
            if (++emitted >= _count) yield break; // stop pulling upstream
        }
    }

}

/// <summary><c>wc</c> terminal aggregator. Certified subset: any argv the cmdlet accepts with NO
/// file operands (column selectors in any order/bundling/long form — the cmdlet's own
/// <see cref="InvokeBashWcCommand.Plan"/> resolves them, so this core can never accept an argv the
/// cmdlet would reject). Reuses <see cref="InvokeBashWcCommand.FormatWcText"/> + counting helpers
/// so the output line is identical to the cmdlet.</summary>
internal sealed class WcStage : ILineStreamStage
{
    private readonly bool _l, _w, _c, _m, _L;
    // _c = bytes selector (-c), _m = chars selector (-m), _L = max-line-length.
    private WcStage(bool l, bool w, bool c, bool m, bool bigL) { _l = l; _w = w; _c = c; _m = m; _L = bigL; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        var plan = InvokeBashWcCommand.Plan(argv);
        if (plan.Declined || plan.Operands.Count > 0) return null; // file operands, errors, help
        return new WcStage(plan.Lines, plan.Words, plan.Bytes, plan.Chars, plan.MaxLine);
    }
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        int lines = 0, words = 0, bytes = 0, chars = 0, maxLine = 0;
        foreach (var line in input)
        {
            lines++;
            words += InvokeBashWcCommand.CountWordsInLine(line);
            bytes += PsBash.Core.RawBytes.GetByteCount(line) + 1;
            int cp = InvokeBashWcCommand.CountCodePointsInLine(line);
            chars += cp + 1;
            if (cp > maxLine) maxLine = cp;
        }
        // Empty input still prints the zero counts (GNU: `seq 1 0 | wc` = 0 0 0), as the cmdlet does.
        // FormatWcText's parameter order is (lines, words, CHARS, BYTES, maxLine): -m before -c.
        // (This call used to pass _c/_m swapped, so a fused `wc -c` printed the CHAR count — equal
        // to the byte count for ASCII, which is why the ASCII parity corpus never noticed.)
        yield return InvokeBashWcCommand.FormatWcText(
            _l, _w, _m, _c, _L, lines, words, bytes, chars, maxLine, string.Empty);
    }
}

/// <summary><c>grep</c> pipeline mode. The cmdlet's <c>Plan</c> decides first; certified subset
/// within it: exactly ONE pattern (the first non-flag operand or a single <c>-e</c>) with the
/// boolean flags <c>-i -v -n -c -w -F -E -G</c> (bundles included). Declines multiple patterns,
/// <c>-o/-A/-B/-C/-m/-q/-r/-l/-L/-x/-s/-H/-h/-f/-P</c>, file operands, other long forms,
/// and <c>--</c> — all handled by the cmdlet on fallback. Regex assembly + matching
/// are the cmdlet's own shared helpers
/// (<see cref="InvokeBashGrepCommand.TryBuildRegexes"/> /
/// <see cref="InvokeBashGrepCommand.MatchLine"/>), so no ladder is duplicated here —
/// the two paths cannot drift.</summary>
internal sealed class GrepStage : ILineStreamStage, ILineStreamDiagnostics
{
    private readonly List<Regex> _regexes;
    private readonly bool _invert, _lineNumbers, _countOnly;
    private int _exit = 1; // grep: 1 = no match (set 0 on first match)
    public int ExitCode => _exit;

    private GrepStage(List<Regex> regexes, bool invert, bool lineNumbers, bool countOnly)
    {
        _regexes = regexes; _invert = invert; _lineNumbers = lineNumbers; _countOnly = countOnly;
    }

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // The cmdlet's own resolver (shared ordered parser, GNU option table) decides first, so this
        // core can NEVER accept an argv the cmdlet would reject or read differently
        // (LineStreamArgAgreementTests).
        var plan = InvokeBashGrepCommand.Plan(argv);
        if (plan.Declined) return null;

        // Certified subset within that: -i -v -n -c -w -F -E -G (bundles fine: one token per letter)
        // with exactly ONE pattern (first operand or a single -e) and no file operand. Everything else
        // (-o/-A/-B/-C/-m/-q/-r/-l/-L/-x/-s/-H/-h/-f/-P, --include/..., `--`, --color) runs the cmdlet.
        foreach (var tok in plan.Parsed.Tokens)
        {
            if (tok.Kind == ArgTokKind.DoubleDash) return null;
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case "ignore-case": case "invert": case "line-number": case "count": case "word":
                case "F": case "E": case "G": case "regexp":
                    break;
                default:
                    return null;
            }
        }

        var patterns = new List<string>();
        foreach (var (isFile, value) in plan.PatternSources)
        {
            if (isFile) return null;
            patterns.Add(value);
        }
        var operands = plan.Operands;
        if (plan.PatternSources.Count == 0)
        {
            if (operands.Count == 0) return null; // no pattern → usage error path
            patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }
        if (operands.Count > 0) return null; // file operand(s) → file mode, decline
        if (patterns.Count != 1) return null; // multiple patterns not certified — decline

        // Shared ladder with the cmdlet (lineRegexp=false — -x is declined above).
        if (!InvokeBashGrepCommand.TryBuildRegexes(
                patterns, plan.Fixed, plan.Extended, plan.Word, lineRegexp: false, plan.IgnoreCase,
                out var regexes, out _))
            return null; // invalid regex → decline; the cmdlet emits the error

        return new GrepStage(regexes, plan.Invert, plan.LineNumbers, plan.Count);
    }

    private readonly List<string> _diagnostics = new();

    /// <summary>stderr lines the stage produced (GNU's <c>binary file matches</c> notice for a NUL in the input).</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// The cmdlet's own engine (<see cref="GrepScanner"/>), so the binary-input rule (a NUL makes the stream
    /// binary: a selected line prints nothing and the notice goes to stderr) and the output decorations are
    /// identical to the unfused lane.
    /// </summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        var pending = new List<string>();
        var opts = new GrepOptions { Invert = _invert, LineNumbers = _lineNumbers, Count = _countOnly };
        var scanner = new GrepScanner(opts, _regexes,
            (text, _, _, _, _, _) => pending.Add(text), msg => _diagnostics.Add(msg));
        scanner.Begin("(standard input)", showName: false, startBinary: false, sizeHint: -1);
        foreach (var line in input)
        {
            bool more = scanner.Feed(line, 1, null);
            foreach (var p in pending) yield return p;
            pending.Clear();
            if (!more) break;
        }
        scanner.End();
        foreach (var p in pending) yield return p;
        _exit = scanner.AnyMatch ? 0 : 1;
    }
}

/// <summary>A fused stage that can report stderr diagnostics after it has been enumerated.</summary>
internal interface ILineStreamDiagnostics
{
    IReadOnlyList<string> Diagnostics { get; }
}

/// <summary><c>sed</c> pipeline mode. Certified subset: <c>-n</c>, <c>-E</c>/<c>-r</c>,
/// <c>-e EXPR</c> (repeatable) or a first-operand expression, and bundles of
/// <c>n/E/r</c>. Declines <c>-i</c> (in-place), <c>-f</c> (script file), and file
/// operands. Reuses the cmdlet's <see cref="InvokeBashSedCommand.TryBuildCommands"/>
/// + <see cref="SedEngine"/> so the transform is identical.</summary>
internal sealed class SedStage : ILineStreamStage
{
    private readonly List<InvokeBashSedCommand.SedCommand> _commands;
    private readonly bool _suppress;
    private SedStage(List<InvokeBashSedCommand.SedCommand> commands, bool suppress)
    { _commands = commands; _suppress = suppress; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // The cmdlet's own resolver (shared ordered parser, GNU option table) decides first, so this
        // core can NEVER accept an argv the cmdlet would reject or read differently
        // (LineStreamArgAgreementTests).
        var plan = InvokeBashSedCommand.Plan(argv);
        if (plan.Declined) return null;

        // Certified subset within that: -n, -E/-r, -e EXPR (repeatable, bundles fine) or one script
        // operand. Declines -i -f -s -z and the accepted no-ops (they are the cmdlet's), `--`, file
        // operands, and a script starting with `#n` (the cmdlet owns that magic comment).
        foreach (var tok in plan.Parsed.Tokens)
        {
            if (tok.Kind == ArgTokKind.DoubleDash) return null;
            if (tok.Kind != ArgTokKind.Option) continue;
            if (tok.OptId != InvokeBashSedCommand.OptQuiet && tok.OptId != InvokeBashSedCommand.OptExpr
                && tok.OptId != InvokeBashSedCommand.OptExtended)
                return null;
        }

        var expressions = new List<string>();
        foreach (var (isFile, value) in plan.Sources)
        {
            if (isFile) return null;
            expressions.Add(value);
        }
        var operands = plan.Operands;
        if (plan.Sources.Count == 0)
        {
            if (operands.Count == 0) return null;
            expressions.Add(operands[0]);
            operands.RemoveAt(0);
        }
        if (operands.Count > 0) return null; // file operand(s) → file mode, decline
        if (expressions[0].StartsWith("#n", StringComparison.Ordinal)) return null;
        // `-e 'a\' -e text`: the cmdlet joins such chunks into one command, so leave them to it.
        foreach (var expr in expressions)
            if (expr.EndsWith('\\')) return null;

        if (!InvokeBashSedCommand.TryBuildCommands(expressions, plan.Extended, out var commands))
            return null; // parse error → cmdlet reports it

        return new SedStage(commands, plan.Quiet);
    }

    // The engine is a push state machine, so the stage streams: records flow through with a one-record
    // lookahead and a downstream early exit (head) stops reading the upstream.
    public IEnumerable<string> Run(IEnumerable<string> input) => SedEngine.Run(input, _commands, _suppress);
}
