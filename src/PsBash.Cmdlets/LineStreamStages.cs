using System.Text;
using System.Text.RegularExpressions;

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
    public static bool TryCreate(string name, string[] argv, out ILineStreamStage stage)
    {
        stage = null!;
        ILineStreamStage? s = name switch
        {
            "seq" => SeqStage.TryCreate(argv),
            "cat" => CatStage.TryCreate(argv),
            "rev" => RevStage.TryCreate(argv),
            "head" => HeadStage.TryCreate(argv),
            "wc" => WcStage.TryCreate(argv),
            "grep" => GrepStage.TryCreate(argv),
            "sed" => SedStage.TryCreate(argv),
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
    private readonly List<string> _values;
    private readonly string? _separator;
    private SeqStage(List<string> values, string? separator) { _values = values; _separator = separator; }
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

/// <summary>Passthrough: bare <c>cat</c> (no flags, no file operands). Any flag or
/// file operand needs the cmdlet's file/glob/numbering paths — decline.</summary>
internal sealed class CatStage : ILineStreamStage
{
    private CatStage() { }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // Only a bare `cat` (or `cat -`, the explicit stdin marker) is a pure
        // line passthrough. Anything else (flags, files) → decline.
        foreach (var a in argv)
            if (a != "-") return null;
        return new CatStage();
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
        int count = 10;
        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];
            if (a == "-n")
            {
                i++;
                // Decline a '+'-prefixed count (rare, and int.TryParse("+5")==5 is an
                // ambiguity we won't certify) — the cmdlet handles it on fallback.
                if (i >= argv.Length || argv[i].StartsWith('+')
                    || !int.TryParse(argv[i], out count) || count < 0) return null;
                i++;
                continue;
            }
            if (a.Length > 2 && a.StartsWith("-n", StringComparison.Ordinal) && IsAllDigits(a, 2))
            {
                count = ParseClamp(a, 2); i++; continue;
            }
            // Legacy -N shorthand (head -5).
            if (a.Length > 1 && a[0] == '-' && IsAllDigits(a, 1))
            {
                count = ParseClamp(a, 1); i++; continue;
            }
            // Anything else (-c byte mode, --lines, -q, files, --, negative, bare
            // positional number, unknown flag) → decline to the cmdlet.
            return null;
        }
        return new HeadStage(count);
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

    private static bool IsAllDigits(string s, int start)
    {
        if (start >= s.Length) return false;
        for (int i = start; i < s.Length; i++)
            if (!char.IsDigit(s[i])) return false;
        return true;
    }

    private static int ParseClamp(string s, int start)
        => BashRuntime.ParseCountClamped(s.AsSpan(start));
}

/// <summary><c>wc</c> terminal aggregator. Certified subset: bare, or a single
/// <c>-l</c>/<c>-w</c>/<c>-c</c>/<c>-m</c>/<c>-L</c> (no bundles, no long forms, no
/// file operands). Reuses <see cref="InvokeBashWcCommand.FormatWcText"/> +
/// counting helpers so the output line is identical to the cmdlet.</summary>
internal sealed class WcStage : ILineStreamStage
{
    private readonly bool _l, _w, _c, _m, _L;
    private WcStage(bool l, bool w, bool c, bool m, bool bigL) { _l = l; _w = w; _c = c; _m = m; _L = bigL; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        bool l = false, w = false, c = false, m = false, bigL = false;
        foreach (var a in argv)
        {
            switch (a)
            {
                case "-l": l = true; break;
                case "-w": w = true; break;
                case "-c": c = true; break;
                case "-m": m = true; break;
                case "-L": bigL = true; break;
                default: return null; // bundles, long forms, files, unknown → decline
            }
        }
        return new WcStage(l, w, c, m, bigL);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        int lines = 0, words = 0, bytes = 0, chars = 0, maxLine = 0;
        foreach (var line in input)
        {
            lines++;
            words += InvokeBashWcCommand.CountWordsInLine(line);
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            int cp = InvokeBashWcCommand.CountCodePointsInLine(line);
            chars += cp + 1;
            if (cp > maxLine) maxLine = cp;
        }
        // The cmdlet emits nothing when no record arrived in pipeline mode.
        if (lines == 0 && words == 0 && bytes == 0) yield break;
        yield return InvokeBashWcCommand.FormatWcText(
            _l, _w, _c, _m, _L, lines, words, bytes, chars, maxLine, string.Empty);
    }
}

/// <summary><c>grep</c> pipeline mode. Certified subset: exactly ONE pattern (the
/// first non-flag operand or a single <c>-e</c>) with the SINGLE boolean flags
/// <c>-i -v -n -c -w -F -E</c>. Declines flag bundles (`-in`, `-vc`: a bundle can
/// prefix-collide with the cmdlet binder, e.g. `-InputObject`), multiple patterns,
/// <c>-o/-A/-B/-C/-m/-q/-r/-l/-L/-x/-s/-H/-h/-f/-P</c>, file operands, long forms,
/// and <c>--</c> — all handled by the cmdlet on fallback. Regex assembly + matching
/// are the cmdlet's own shared helpers
/// (<see cref="InvokeBashGrepCommand.TryBuildRegexes"/> /
/// <see cref="InvokeBashGrepCommand.MatchLine"/>), so no ladder is duplicated here —
/// the two paths cannot drift.</summary>
internal sealed class GrepStage : ILineStreamStage
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
        bool ignoreCase = false, invert = false, lineNumbers = false, countOnly = false;
        bool wholeWord = false, fixedString = false, extended = false;
        var patterns = new List<string>();
        var operands = new List<string>();

        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];
            if (a == "-e")
            {
                i++;
                if (i >= argv.Length) return null;
                patterns.Add(argv[i]);
                i++;
                continue;
            }
            // A SINGLE supported boolean flag only. Bundles (`-in`, `-vc`, …) are
            // declined: a bundle can prefix-collide with the cmdlet's binder (e.g.
            // `-in` prefix-matches `-InputObject`), so its unfused behavior isn't the
            // simple char-by-char union — decline and let the cmdlet's decoy handling
            // run on fallback, keeping the two paths byte-identical.
            if (a.Length == 2 && a[0] == '-')
            {
                switch (a[1])
                {
                    case 'i': ignoreCase = true; break;
                    case 'v': invert = true; break;
                    case 'n': lineNumbers = true; break;
                    case 'c': countOnly = true; break;
                    case 'w': wholeWord = true; break;
                    case 'F': fixedString = true; break;
                    case 'E': extended = true; break;
                    default: return null; // unsupported single flag → decline
                }
                i++;
                continue;
            }
            if (a.Length > 1 && a[0] == '-' && a[1] != '-') return null; // bundle → decline
            // Non-flag operand: the pattern (first) — a second operand is a file → decline.
            operands.Add(a);
            i++;
        }

        if (patterns.Count == 0)
        {
            if (operands.Count == 0) return null; // no pattern → usage error path
            patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }
        if (operands.Count > 0) return null; // file operand(s) → file mode, decline
        if (patterns.Count != 1) return null; // multiple patterns not certified — decline

        // Shared ladder with the cmdlet (lineRegexp=false — -x is declined above).
        if (!InvokeBashGrepCommand.TryBuildRegexes(
                patterns, fixedString, extended, wholeWord, lineRegexp: false, ignoreCase,
                out var regexes, out _))
            return null; // invalid regex → decline; the cmdlet emits the error

        return new GrepStage(regexes, invert, lineNumbers, countOnly);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        int matchCount = 0;
        int lineNum = 0;
        foreach (var line in input)
        {
            lineNum++;
            InvokeBashGrepCommand.MatchLine(_regexes, line, _invert, out bool isMatch);
            if (!isMatch) continue;
            matchCount++;
            _exit = 0;
            if (_countOnly) continue;
            yield return _lineNumbers ? (lineNum + ":" + line) : line;
        }
        if (_countOnly)
        {
            _exit = matchCount == 0 ? 1 : 0;
            yield return matchCount.ToString();
        }
        else
        {
            _exit = matchCount == 0 ? 1 : 0;
        }
    }
}

/// <summary><c>sed</c> pipeline mode. Certified subset: <c>-n</c>, <c>-E</c>/<c>-r</c>,
/// <c>-e EXPR</c> (repeatable) or a first-operand expression, and bundles of
/// <c>n/E/r</c>. Declines <c>-i</c> (in-place), <c>-f</c> (script file), and file
/// operands. Reuses the cmdlet's <see cref="InvokeBashSedCommand.TryBuildCommands"/>
/// + <see cref="InvokeBashSedCommand.ProcessLines"/> so the transform is identical.</summary>
internal sealed class SedStage : ILineStreamStage
{
    private readonly List<InvokeBashSedCommand.SedCommand> _commands;
    private readonly bool _suppress;
    private SedStage(List<InvokeBashSedCommand.SedCommand> commands, bool suppress)
    { _commands = commands; _suppress = suppress; }
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        bool suppress = false, extended = false;
        var expressions = new List<string>();
        var operands = new List<string>();

        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];
            if (a == "--help" || a == "--version") return null;
            if (a == "-e")
            {
                i++;
                if (i >= argv.Length) return null;
                expressions.Add(argv[i]);
                i++;
                continue;
            }
            // Script-file / end-of-options / any long flag → cmdlet paths, decline.
            if (a == "-f" || a == "--" || a.StartsWith("--", StringComparison.Ordinal))
                return null;
            // A SINGLE supported flag only. Bundles (`-nE`, `-i.bak`, …) are declined —
            // as in grep, a bundle's unfused behavior may not be the char-by-char union
            // under the cmdlet binder, so let the cmdlet handle it on fallback.
            if (a.Length == 2 && a[0] == '-')
            {
                switch (a[1])
                {
                    case 'n': suppress = true; break;
                    case 'E': case 'r': extended = true; break;
                    default: return null; // -i (in-place) / unknown single flag → decline
                }
                i++;
                continue;
            }
            if (a.Length > 1 && a[0] == '-' && a[1] != '-') return null; // bundle / -i.bak → decline
            operands.Add(a);
            i++;
        }

        if (expressions.Count == 0)
        {
            if (operands.Count == 0) return null;
            expressions.Add(operands[0]);
            operands.RemoveAt(0);
        }
        if (operands.Count > 0) return null; // file operand(s) → file mode, decline

        if (!InvokeBashSedCommand.TryBuildCommands(expressions, extended, out var commands))
            return null; // parse error → cmdlet reports it

        return new SedStage(commands, suppress);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        // ProcessLines is a whole-input transform (N / D / address ranges need all
        // lines), so buffer the stream — same as the cmdlet's pipeline path. Still
        // removes the per-line PSObject allocation that this phase targets.
        var lines = input as List<string> ?? new List<string>(input);
        // SuppressDefault is a [ThreadStatic] carrier the pure ProcessLines reads; set
        // it immediately before the call. No reset needed: every ProcessLines caller
        // (cmdlet + this stage) sets it first, and cmdlet invocations never overlap on
        // one runspace thread — the value is always freshly written before it is read.
        InvokeBashSedCommand.SuppressDefault = _suppress;
        var output = InvokeBashSedCommand.ProcessLines(lines.ToArray(), _commands);
        foreach (var line in output) yield return line;
    }
}
