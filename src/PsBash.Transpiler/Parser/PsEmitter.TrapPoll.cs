using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// True when running <paramref name="root"/> can give a real signal a trap, so each of its top-level statements ends
    /// with the signal poll (<see cref="PsBuild.SignalTrapPoll"/>, docs/specs/background-jobs.md section 13): it runs
    /// <c>trap</c>, or code whose commands the emitter cannot see.
    /// <para>
    /// <b>Invariant</b>: decided on the parsed commands, never on the source text. A text scan for <c>trap</c> missed
    /// <c>t=tr; eval "${t}ap 'echo T' USR1"</c> (the trap never ran) and polled for <c>echo trap</c>. Code the emitter
    /// cannot see is: run-time <c>eval</c> text, a history entry <c>fc</c> re-runs, a sourced file, a command named at run
    /// time (<c>$cmd</c>), and an alias (its text is read at run time). A static <c>eval</c>'s text is parsed and judged the same way. Guard:
    /// <c>Core.Tests/SignalTrapPollEmitterTests</c>.
    /// </para>
    /// </summary>
    internal static bool CanSetTrap(Command? root)
    {
        bool can = false;
        VisitCommands(root, c =>
        {
            if (!can && c is Command.Simple s && SimpleCanSetTrap(s)) can = true;
        }, c => !can && c is not Command.Subshell);   // a subshell's trap dies with it (bash forks it)
        return can;
    }

    private static bool SimpleCanSetTrap(Command.Simple s)
    {
        int i = 0;
        // `command trap ...` / `builtin eval ...` run the builtin they name.
        while (i < s.Words.Length && TryGetStaticArgValue(s.Words[i]) is "command" or "builtin") i++;
        if (i >= s.Words.Length) return false;
        if (TryGetStaticArgValue(s.Words[i]) is not { } name)
            return !NamesAPath(s.Words[i]);   // named at run time: may be trap or eval, unless it is a path
        switch (name)
        {
            case "trap":
            case "source":
            case ".":
            case "alias":
            // `fc -s` / `fc -e -` re-run a history entry: text known only at run time, as eval's.
            case "fc":
                return true;
            case "eval":
                return EvalTextCanSetTrap(s.Words.AsSpan().Slice(i + 1));
            default:
                return false;
        }
    }

    /// <summary>A command word with a literal <c>/</c> or a tilde (<c>$dir/go</c>, <c>~/bin/x</c>) names a file, never a builtin.</summary>
    private static bool NamesAPath(CompoundWord word)
    {
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.TildeSub:
                    return true;
                case WordPart.Literal { Value: var v } when v.Contains('/'):
                case WordPart.EscapedLiteral { Value: var e } when e.Contains('/'):
                case WordPart.SingleQuoted { Value: var q } when q.Contains('/'):
                    return true;
                case WordPart.DoubleQuoted dq:
                    foreach (var inner in dq.Parts)
                        if (inner is WordPart.Literal { Value: var d } && d.Contains('/')) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>Static eval text (every word known now, as <see cref="EmitEval"/> inlines it) is parsed and judged; text
    /// known only at run time, or that does not parse, can set a trap.</summary>
    private static bool EvalTextCanSetTrap(ReadOnlySpan<CompoundWord> args)
    {
        var parts = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (TryReconstructBashSource(arg) is not { } text) return true;
            parts.Add(text);
        }
        if (parts.Count > 0 && parts[0] == "--") parts.RemoveAt(0);
        if (parts.Count == 0) return false;
        // `eval eval eval ... trap`: one level per eval, bounded like every recursive descent (host-lifecycle-contract.md).
        bool sawBad = BashParser.SawBadSubstitution;
        try
        {
            using var depth = NestingGuard.Enter();
            return CanSetTrap(BashParser.Parse(string.Join(' ', parts)));
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
}
