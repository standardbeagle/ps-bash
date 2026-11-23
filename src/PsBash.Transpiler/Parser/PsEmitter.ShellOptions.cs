using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    [ThreadStatic]
    private static bool _inRunTimeExecBranch;

    /// <summary>The unit names <c>break</c>/<c>continue</c> as data (<see cref="NamesLoopControl"/>).</summary>
    [ThreadStatic]
    private static bool _namesLoopControl;

    /// <summary>
    /// <c>c=exec; $c 3&gt;f</c>: a command named at run time with only redirections becomes <c>exec</c>'s permanent redirect
    /// when the name is <c>exec</c> (bash), else it runs as itself with the redirections for that command. The name is
    /// known only at run time, so both forms are emitted behind <see cref="PsBuild.RunTimeNameBranch"/>.
    /// </summary>
    private static bool TryEmitRunTimeExecRedirect(Command.Simple cmd, out string result)
    {
        result = "";
        // A pipe-target stage is a subshell (an exec redirect there ends with it) and its input reaches only the stage's
        // own block: inside the branch block the command lost it (`printf x | $c >/dev/null` gave `grep -c .` nothing).
        if (_inRunTimeExecBranch || _pipeStageFeedsInput || cmd.Words.Length != 1 || cmd.Redirects.IsDefaultOrEmpty || !cmd.HereDocs.IsDefaultOrEmpty
            || !IsBareVariableCommandWord(cmd.Words[0]) || _definedFunctions?.Contains("exec") == true)
            return false;
        var asExec = cmd with
        {
            EnvPairs = [],
            Words = [new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("exec")))],
        };
        if (!TryEmitExec(asExec, out var execText)) return false;
        string otherwise;
        _inRunTimeExecBranch = true;
        try { otherwise = EmitSimple(cmd); }
        finally { _inRunTimeExecBranch = false; }
        result = PsBuild.RunTimeNameBranch(EmitAssignmentValue(cmd.Words[0]), "exec", execText, otherwise);
        return true;
    }
    /// <summary>
    /// True when running <paramref name="root"/> can turn <c>pipefail</c> on, so every pipeline in it reports each stage's
    /// status (<see cref="PsBuild.PipefailPipeline"/>). It is: <c>set</c> / <c>shopt</c> with a <c>pipefail</c> word or a
    /// word known only at run time (<c>set -o "$opt"</c>), a call of a function this unit defines with a <c>pipefail</c>
    /// word (<c>run set -o pipefail</c>), a command named at run time that may be <c>set</c>, eval text and a trap action
    /// (static text is parsed and judged the same way; text known only at run time counts), and an alias. A sourced file
    /// is not seen (it runs under the caller's mode, <see cref="InheritedPipefail"/>).
    /// <para>
    /// <b>Invariant</b>: decided on the parsed commands, never on the source text (docs/specs/emitter-strategy.md). A text
    /// scan for <c>pipefail</c> turned the mode on for <c>grep -c pipefail f</c>; the per-stage capture it emits was then
    /// invalid PowerShell in some positions, so a quoted word broke the parse of the whole line. Guard:
    /// <c>Core.Tests/PipefailModeEmitterTests</c>.
    /// </para>
    /// </summary>
    internal static bool CanSetPipefail(Command? root) => CanSetShellOption(root, PipefailWord, opaqueText: true);

    /// <summary><c>set -x</c> / <c>set -o xtrace</c> (or a word known only at run time): every command is traced.</summary>
    internal static bool CanSetXtrace(Command? root) => CanSetShellOption(root, XtraceWord, opaqueText: false);

    /// <summary><c>set -u</c> / <c>set -o nounset</c> (or a word known only at run time): every expansion is checked.</summary>
    internal static bool CanSetNounset(Command? root) => CanSetShellOption(root, NounsetWord, opaqueText: false);

    /// <summary>
    /// <c>set -e</c> / <c>set -o errexit</c>, or a word known only at run time (<c>set -$o</c>, <c>set -o $nm</c>,
    /// <c>c=set; $c -e</c>, <c>eval "$cmd"</c>): the unit gets the errexit statement checks (<see cref="EmitStatement"/>).
    /// <b>Invariant</b>: errexit is decided once, at run time, by the one check <see cref="PsBuild.ErrexitCheck"/> (the
    /// flag, the exempt-context counter, the subshell's scoped return); this only decides where that check is emitted,
    /// so it must say yes wherever the flag could be on. Text known only at run time counts (the checks are inert while
    /// the flag is off). REGRESSION: a regex over the source text missed <c>set -$o</c>, and the unit's bare
    /// <c>false</c> then ran its own <c>exit</c>, which knew neither the <c>||</c> that exempted it nor the subshell
    /// it was in (<c>( set -$o; f || echo caught )</c> ended the whole script).
    /// </summary>
    internal static bool CanSetErrexit(Command? root) => CanSetShellOption(root, ErrexitWord, opaqueText: true);

    /// <summary><c>set -E</c> / <c>set -o errtrace</c> (or a word known only at run time).</summary>
    internal static bool CanSetErrtrace(Command? root) => CanSetShellOption(root, ErrtraceWord, opaqueText: true);

    /// <summary><c>shopt -s inherit_errexit</c> (or a word known only at run time).</summary>
    internal static bool CanSetInheritErrexit(Command? root) => CanSetShellOption(root, InheritErrexitWord, opaqueText: true);

    private static readonly Func<string, bool> ErrexitWord = v => v == "errexit" || IsShortOptionCluster(v, 'e');
    private static readonly Func<string, bool> ErrtraceWord = v => v == "errtrace" || IsShortOptionCluster(v, 'E');
    private static readonly Func<string, bool> InheritErrexitWord = v => v == "inherit_errexit";

    /// <summary>
    /// True when running <paramref name="root"/> can set an ERR trap: a <c>trap</c> with an <c>ERR</c> operand or an
    /// operand known only at run time, in the unit, in static eval text or in a trap action, and text known only at
    /// run time (<c>eval "$cmd"</c>). Like <see cref="CanSetErrexit"/>, it decides only where the checks are emitted.
    /// </summary>
    internal static bool CanSetErrTrap(Command? root)
    {
        bool can = false;
        VisitAllCommands(root, c =>
        {
            if (can || c is not Command.Simple s || s.Words.IsDefaultOrEmpty) return;
            string? name = StaticCommandName(s, out int at);
            if (at >= s.Words.Length) return;
            switch (name)
            {
                case null:
                    // `$t 'echo E' ERR`: a command named at run time may be trap.
                    for (int i = at + 1; i < s.Words.Length && !can; i++)
                        can = TryGetStaticArgValue(s.Words[i]) is { } v && IsErrSignalWord(v);
                    break;
                case "trap":
                    // The action is the first operand; every later one is a signal (or one known only at run time).
                    bool action = false;
                    for (int i = at + 1; i < s.Words.Length && !can; i++)
                    {
                        string? v = TryGetStaticArgValue(s.Words[i]);
                        if (v is "--" && !action) continue;
                        if (v is "-p" or "-l" && !action) return;
                        if (!action) { action = true; if (v is not null && IsErrSignalWord(v) && s.Words.Length == i + 1) return; continue; }
                        can = v is null || IsErrSignalWord(v);
                    }
                    if (!can && StaticTrapAction(s, at) is { } text) can = ParsedTextMatches(text, CanSetErrTrap);
                    break;
                case "eval":
                    can = StaticEvalText(s, at) is not { } evalText || ParsedTextMatches(evalText, CanSetErrTrap);
                    break;
                case "fc":
                    can = FcRunsText(s.Words.AsSpan().Slice(at + 1));
                    break;
                case "source":
                case ".":
                    can = true;
                    break;
            }
        });
        return can;
    }

    private static bool IsErrSignalWord(string v) => v.Equals("ERR", StringComparison.OrdinalIgnoreCase) || v.Equals("SIGERR", StringComparison.OrdinalIgnoreCase);

    private static readonly Func<string, bool> PipefailWord = v => v.Contains("pipefail", StringComparison.Ordinal);
    private static readonly Func<string, bool> XtraceWord = v => v.Contains("xtrace", StringComparison.Ordinal) || IsShortOptionCluster(v, 'x');
    private static readonly Func<string, bool> NounsetWord = v => v.Contains("nounset", StringComparison.Ordinal) || IsShortOptionCluster(v, 'u');

    /// <summary><c>-eux</c>: a cluster of one-letter options turning <paramref name="letter"/> on (<c>+x</c> turns it off).</summary>
    private static bool IsShortOptionCluster(string v, char letter)
    {
        if (v.Length < 2 || v[0] != '-' || v[1] == '-') return false;
        for (int i = 1; i < v.Length; i++)
            if (!char.IsAsciiLetter(v[i])) return false;
        return v.IndexOf(letter, 1) > 0;
    }

    /// <summary>
    /// True when running <paramref name="root"/> can turn on the shell option an operand of <c>set</c> / <c>shopt</c>
    /// names when <paramref name="optionWord"/> accepts it: see <see cref="CanSetPipefail"/> for every way it can.
    /// <paramref name="opaqueText"/>: shell text known only at run time (<c>eval "$cmd"</c>, a run-time trap action or
    /// alias value) counts as able to (pipefail, whose capture is cheap); xtrace and nounset, which touch every command
    /// and read, count only words they can see.
    /// </summary>
    internal static bool CanSetShellOption(Command? root, Func<string, bool> optionWord, bool opaqueText)
    {
        var functions = new HashSet<string>(StringComparer.Ordinal);
        VisitAllCommands(root, c => { if (c is Command.ShFunction f) functions.Add(f.Name); });
        bool can = false;
        VisitAllCommands(root, c =>
        {
            if (!can && c is Command.Simple s && SimpleCanSetOption(s, functions, optionWord, opaqueText)) can = true;
        });
        return can;
    }

    /// <summary>
    /// True when <paramref name="root"/> reads <c>PIPESTATUS</c> in an expansion anywhere (a word, a command substitution,
    /// arithmetic, an expanding here-document, static eval text or trap action), so each command records its status there. A quoted
    /// <c>grep PIPESTATUS f</c> reads nothing.
    /// </summary>
    internal static bool ReadsPipestatus(Command? root)
    {
        bool reads = false;
        VisitAllCommands(root, c =>
        {
            if (reads) return;
            if (c is not Command.Simple s) return;
            string? name = StaticCommandName(s, out int at);
            // Shell text run later in this shell: eval text, a trap action (`trap 'echo ${PIPESTATUS[@]}' ERR`).
            string? text = name == "eval" ? StaticEvalText(s, at) : name == "trap" ? StaticTrapAction(s, at) : null;
            if (text is not null) reads = ParsedTextMatches(text, ReadsPipestatus);
        }, part =>
        {
            if (reads) return;
            reads = part switch
            {
                WordPart.SimpleVarSub v => v.Name == "PIPESTATUS",
                WordPart.BracedVarSub b => b.Name.Contains("PIPESTATUS", StringComparison.Ordinal),
                WordPart.ArithSub a => a.Expr.Source.Contains("PIPESTATUS", StringComparison.Ordinal),
                _ => false,
            };
        }, text => { if (!reads && text.Contains("PIPESTATUS", StringComparison.Ordinal)) reads = true; });
        return reads;
    }

    private static bool SimpleCanSetOption(Command.Simple s, HashSet<string> functions, Func<string, bool> optionWord, bool opaqueText)
    {
        if (s.Words.IsDefaultOrEmpty) return false;
        string? name = StaticCommandName(s, out int at);
        if (at >= s.Words.Length) return false;
        var args = s.Words.AsSpan().Slice(at + 1);
        Func<Command?, bool> judge = r => CanSetShellOption(r, optionWord, opaqueText);
        if (name is null)
            return !NamesAPath(s.Words[at]) && OperandsMayNameOption(args, optionWord);
        switch (name)
        {
            case "set":
            case "shopt":
                // `set -o "$opt"`: an operand known only at run time may name it. `set +o xtrace` turns it off.
                string? prev = null;
                foreach (var w in args)
                {
                    // `set -- "$@"`, `set -- -e`, `set a "$b"`: the words from `--` / `-` or the first operand that is no
                    // option (and no `-o` name) on are positional parameters, which turn nothing on.
                    if (name == "set" && TryGetStaticArgValue(w) is { } p
                        && (p is "--" or "-" || ((p.Length == 0 || p[0] is not ('-' or '+')) && !TakesOptionName(prev))))
                        return false;
                    if (TryGetStaticArgValue(w) is not { } v) { if (prev != "+o") return true; }
                    else if (prev != "+o" && optionWord(v)) return true;
                    prev = TryGetStaticArgValue(w);
                }
                return false;
            case "eval":
                return StaticEvalText(s, at) is { } text ? ParsedTextMatches(text, judge) : opaqueText;
            case "trap":
                // `trap 'set -u' EXIT`: the action is shell text run later in this shell. `trap - DEBUG` / `trap -p`
                // (every word known now, no action) runs nothing.
                if (StaticTrapAction(s, at) is { } action) return ParsedTextMatches(action, judge);
                foreach (var w in args)
                    if (TryGetStaticArgValue(w) is null) return opaqueText;
                return false;
            case "fc":
                // `fc -s`: the command it re-runs is known only at run time (a listing runs nothing).
                return opaqueText && FcRunsText(args);
            case "source":
            case ".":
                // The file's text is known only at run time; its `set -e` stays on in this shell.
                return opaqueText;
            case "alias":
                foreach (var w in args)
                {
                    if (TryGetStaticArgValue(w) is not { } v) return opaqueText;
                    int eq = v.IndexOf('=');
                    if (eq > 0 && ParsedTextMatches(v[(eq + 1)..], judge)) return true;
                }
                return false;
            default:
                return functions.Contains(name) && OperandsMayNameOption(args, optionWord);
        }
    }

    /// <summary><c>fc</c> re-runs (or edits and runs) a command unless an option word lists (<c>-l</c>, <c>-ln</c>).</summary>
    private static bool FcRunsText(ReadOnlySpan<CompoundWord> args)
    {
        foreach (var w in args)
            if (TryGetStaticArgValue(w) is { Length: > 1 } v && v[0] == '-' && v[1] != '-' && v.Contains('l')) return false;
        return true;
    }

    /// <summary><c>-o</c> / <c>+o</c> / <c>-eo</c>: the next operand of <c>set</c> is an option name.</summary>
    private static bool TakesOptionName(string? prev) =>
        prev is { Length: > 1 } p && p[0] is '-' or '+' && p[1] != '-' && p[^1] == 'o';

    /// <summary>
    /// The operands of a call that may run <c>set</c> (<c>run set -o pipefail</c>, <c>c=set; $c -x</c>): one names the
    /// option, or one is known only at run time beside a <c>set</c> / <c>shopt</c> / <c>-o</c> word. <c>$SUDO rm "$f"</c>
    /// names none.
    /// </summary>
    private static bool OperandsMayNameOption(ReadOnlySpan<CompoundWord> args, Func<string, bool> optionWord)
    {
        bool dynamic = false, setWord = false;
        string? prev = null;
        foreach (var w in args)
        {
            string? v = TryGetStaticArgValue(w);
            if (v is null) dynamic = true;
            else if (prev != "+o" && optionWord(v)) return true;
            else if (v is "set" or "shopt" or "-o") setWord = true;
            prev = v;
        }
        return dynamic && setWord;
    }

    /// <summary>The action text of <c>trap ACTION SIGNAL…</c> when it is known now; null otherwise.</summary>
    private static string? StaticTrapAction(Command.Simple s, int at)
    {
        for (int i = at + 1; i < s.Words.Length; i++)
        {
            if (TryGetStaticArgValue(s.Words[i]) is not { } v) return null;
            if (v == "--") continue;
            if (v.StartsWith('-')) return null;   // -p / -l list, nothing runs
            return v;
        }
        return null;
    }
    /// <summary>The command name after any <c>command</c> / <c>builtin</c> wrappers (index in <paramref name="at"/>); null when named at run time.</summary>
    private static string? StaticCommandName(Command.Simple s, out int at)
    {
        at = 0;
        while (at < s.Words.Length && TryGetStaticArgValue(s.Words[at]) is "command" or "builtin") at++;
        return at < s.Words.Length ? TryGetStaticArgValue(s.Words[at]) : null;
    }

    /// <summary>A word that is all one quoted part (<c>"$@"</c>, <c>"$cmd"</c>): it never splits into several words.</summary>
    private static bool IsQuotedWholeWord(CompoundWord w) =>
        w.Parts.Length == 1 && w.Parts[0] is WordPart.DoubleQuoted or WordPart.SingleQuoted;

    /// <summary>The text of an <c>eval</c> whose every word is known now (<see cref="EmitEval"/> inlines it); null otherwise.</summary>
    private static string? StaticEvalText(Command.Simple s, int at)
    {
        var parts = new List<string>(s.Words.Length);
        for (int i = at + 1; i < s.Words.Length; i++)
        {
            if (TryReconstructBashSource(s.Words[i]) is not { } text) return null;
            parts.Add(text);
        }
        if (parts.Count > 0 && parts[0] == "--") parts.RemoveAt(0);
        return string.Join(' ', parts);
    }

    /// <summary>Parse <paramref name="text"/> and judge it with <paramref name="judge"/>; text that does not parse counts as a match.</summary>
    private static bool ParsedTextMatches(string text, Func<Command?, bool> judge)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool sawBad = BashParser.SawBadSubstitution;
        try
        {
            using var depth = NestingGuard.Enter();
            return judge(BashParser.Parse(text));
        }
        catch (ParseException)
        {
            return true;
        }
        finally
        {
            BashParser.SawBadSubstitution = sawBad;   // this probe parse is not the unit's
        }
    }

    /// <summary>
    /// True when a word of <paramref name="root"/> other than a command name is the text <c>break</c> or <c>continue</c>
    /// (<c>c=break</c>, <c>run continue</c>): a command named at run time may then be loop control, so its lookup is told
    /// what a <c>break</c> does at that call site (Resolve-BashCommand -LoopMode). A bare PowerShell <c>break</c> there
    /// left whatever loop was on the call stack, or ended the whole script when there was none.
    /// </summary>
    internal static bool NamesLoopControl(Command? root)
    {
        bool names = false;
        static bool IsLoopWord(CompoundWord? w) => w is not null && TryGetStaticArgValue(w) is "break" or "continue";
        VisitAllCommands(root, c =>
        {
            if (names) return;
            switch (c)
            {
                case Command.Simple s:
                    for (int i = 1; i < s.Words.Length && !names; i++) names = IsLoopWord(s.Words[i]);
                    if (!s.EnvPairs.IsDefaultOrEmpty)
                        foreach (var e in s.EnvPairs) names |= IsLoopWord(e.Value);
                    break;
                case Command.ShAssignment a:
                    foreach (var p in a.Pairs) names |= IsLoopWord(p.Value);
                    break;
            }
        });
        return names;
    }

    /// <summary>The <c>-LoopMode</c> argument of a run-time name lookup here (<see cref="NamesLoopControl"/>); empty when the unit names no loop control or the mode is the caller's.</summary>
    private static string RunTimeNameLoopModeArg() =>
        _namesLoopControl && LoopModeAtCallSite() is var m and >= 0 ? " -LoopMode " + m : "";
    /// <summary>
    /// Visit every command in <paramref name="root"/>, also those inside command and process substitutions in its words
    /// (which the emitter emits in the same pass, under the same modes). <paramref name="onPart"/> sees every word part,
    /// <paramref name="onExpandedText"/> the raw text bash expands later (an expanding here-document body).
    /// </summary>
    private static void VisitAllCommands(Command? root, Action<Command> onCommand, Action<WordPart>? onPart = null, Action<string>? onExpandedText = null)
    {
        void Word(CompoundWord? w)
        {
            if (w is null) return;
            foreach (var p in w.Parts) Part(p);
        }
        void Words(ImmutableArray<CompoundWord> ws)
        {
            if (ws.IsDefaultOrEmpty) return;
            foreach (var w in ws) Word(w);
        }
        void Redirects(ImmutableArray<Redirect> rs)
        {
            if (rs.IsDefaultOrEmpty) return;
            foreach (var r in rs)
            {
                Word(r.Target);
                if (r.Here is { Expand: true } h) onExpandedText?.Invoke(h.Body);
            }
        }
        void Assignments(ImmutableArray<Assignment> pairs)
        {
            if (pairs.IsDefaultOrEmpty) return;
            foreach (var a in pairs)
            {
                Word(a.Value);
                if (a.ArrayValue is { } arr) Words(arr.Elements);
            }
        }
        void Part(WordPart p)
        {
            onPart?.Invoke(p);
            switch (p)
            {
                case WordPart.DoubleQuoted dq: foreach (var inner in dq.Parts) Part(inner); break;
                case WordPart.BracedVarSub { ArgWord: { } arg }: foreach (var inner in arg) Part(inner); break;
                case WordPart.CommandSub { Body: Command body }: Visit(body); break;
                case WordPart.ProcessSub { Body: Command body }: Visit(body); break;
            }
        }
        void Visit(Command? node)
        {
            using var depth = NestingGuard.Enter();
            VisitCommands(node, c =>
            {
                onCommand(c);
                switch (c)
                {
                    case Command.Simple s:
                        Words(s.Words);
                        if (!s.EnvPairs.IsDefaultOrEmpty) foreach (var e in s.EnvPairs) Word(e.Value);
                        Redirects(s.Redirects);
                        if (!s.HereDocs.IsDefaultOrEmpty)
                            foreach (var h in s.HereDocs) if (h.Expand) onExpandedText?.Invoke(h.Body);
                        break;
                    case Command.ShAssignment a: Assignments(a.Pairs); Assignments(a.TempPrefix); Redirects(a.Redirects); break;
                    case Command.BoolExpr b: Words(b.Inner); break;
                    case Command.ForIn f: Words(f.List); Redirects(f.Redirects); break;
                    case Command.Select sel: Words(sel.List); break;
                    case Command.Case cs: Word(cs.Expr); Redirects(cs.Redirects); break;
                    case Command.If i: Redirects(i.Redirects); break;
                    case Command.While w: Redirects(w.Redirects); break;
                    case Command.Subshell sub: Redirects(sub.Redirects); break;
                    case Command.BraceGroup bg: Redirects(bg.Redirects); break;
                    case Command.ArithCommand ac: onPart?.Invoke(new WordPart.ArithSub(ac.Expr)); break;
                    case Command.ForArith fa:
                        foreach (var e in new[] { fa.Init, fa.Cond, fa.Step })
                            if (e is not null) onPart?.Invoke(new WordPart.ArithSub(e));
                        Redirects(fa.Redirects);
                        break;
                }
            });
        }
        Visit(root);
    }
}
