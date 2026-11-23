using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// True when the background job <paramref name="cmd"/> needs a host process of its own (<c>Invoke-BashBackground
    /// -Isolated</c>, docs/specs/background-jobs.md section 12.6): it can change something an in-process job SHARES with
    /// the shell. On Windows that process costs a <c>ps-bash-host</c> start, seconds on a cold host, against milliseconds
    /// for a job runspace, so only these go there:
    /// <list type="bullet">
    /// <item>the working directory, which is the process's (<c>cd</c>, <c>pushd</c>, <c>popd</c>);</item>
    /// <item>the readonly, attribute and nameref tables, which are process-wide (<c>readonly</c>, <c>declare</c>,
    /// <c>typeset</c>, <c>local</c>);</item>
    /// <item>the program lookup, which reads the process <c>PATH</c> (an assignment to <c>PATH</c>);</item>
    /// <item>every other state builtin (<c>set</c>, <c>shopt</c>, <c>trap</c>, <c>exec</c>, <c>umask</c>, <c>read</c>, ...),
    /// <c>eval</c>/<c>source</c> text, a command named by an expansion, a function of the unit (the job runspace does not
    /// have the shell's functions), a function definition, <c>select</c>, a job in a job;</item>
    /// <item>any of these inside a command or process substitution of the job.</item>
    /// </list>
    /// What is NOT a reason: writing a scalar or an array. An in-process job's scalars are its own copy taken at the
    /// <c>&amp;</c> (<c>ScalarOverlay</c>, section 3) and its arrays are globals of its own runspace, so an assignment,
    /// <c>FOO=1 prog</c>, a loop variable, <c>${x=1}</c>, <c>$((n++))</c>, <c>(( ))</c> and <c>let</c> stay in-process. Loops
    /// and assigning expansions went to a host process before that copy existed: <c>for i in 1 2 3; do echo $i; done &amp;</c>
    /// took about 5 s to start on Windows against 10 ms for <c>echo a &amp;</c>.
    /// </summary>
    internal static bool JobNeedsOwnProcess(Command cmd) => cmd switch
    {
        Command.Simple s => SimpleNeedsOwnProcess(s),
        Command.ShAssignment a => a.IsDeclare || a.IsLocal || a.IsReadonly || a.IsNameref || a.IsAssoc || a.IsGlobal || a.Attrs is not null
            || a.Pairs.Any(AssignmentNeedsOwnProcess)
            || (!a.TempPrefix.IsDefaultOrEmpty && a.TempPrefix.Any(AssignmentNeedsOwnProcess))
            || RedirectsNeedOwnProcess(a.Redirects),
        Command.Pipeline p => p.Commands.Any(JobNeedsOwnProcess),
        Command.AndOrList l => l.Commands.Any(JobNeedsOwnProcess),
        Command.CommandList l => l.Commands.Any(JobNeedsOwnProcess),
        Command.If i => i.Arms.Any(arm => JobNeedsOwnProcess(arm.Cond) || JobNeedsOwnProcess(arm.Body))
            || (i.ElseBody is not null && JobNeedsOwnProcess(i.ElseBody)) || RedirectsNeedOwnProcess(i.Redirects),
        Command.While w => JobNeedsOwnProcess(w.Cond) || JobNeedsOwnProcess(w.Body) || RedirectsNeedOwnProcess(w.Redirects),
        Command.ForIn f => IsProcessPath(f.Var) || f.List.Any(WordNeedsOwnProcess) || JobNeedsOwnProcess(f.Body)
            || RedirectsNeedOwnProcess(f.Redirects),
        Command.ForArith f => ArithNeedsOwnProcess(f.Init) || ArithNeedsOwnProcess(f.Cond) || ArithNeedsOwnProcess(f.Step)
            || JobNeedsOwnProcess(f.Body) || RedirectsNeedOwnProcess(f.Redirects),
        Command.Case c => WordNeedsOwnProcess(c.Expr) || c.Arms.Any(arm => arm.Patterns.Any(HasSubstitutionText) || JobNeedsOwnProcess(arm.Body))
            || RedirectsNeedOwnProcess(c.Redirects),
        Command.ArithCommand a => ArithNeedsOwnProcess(a.Expr),
        Command.BoolExpr b => b.Inner.Any(WordNeedsOwnProcess),
        Command.Subshell sub => JobNeedsOwnProcess(sub.Body) || RedirectsNeedOwnProcess(sub.Redirects),
        Command.BraceGroup g => JobNeedsOwnProcess(g.Body) || RedirectsNeedOwnProcess(g.Redirects),
        // A function definition, select (reads the terminal), a job in a job, anything new: assume it does.
        _ => true,
    };

    private static bool SimpleNeedsOwnProcess(Command.Simple s)
    {
        if (s.EnvPairs.Any(p => IsProcessPath(p.Name) || (p.Value is not null && WordNeedsOwnProcess(p.Value)))) return true;
        if (RedirectsNeedOwnProcess(s.Redirects)) return true;
        if (s.Words.IsDefaultOrEmpty) return false; // assignments only
        if (GetLiteralValue(s.Words[0]) is not { } name) return true; // `$cmd args`: could be cd
        if (name != "let" && _stateBuiltins.Contains(name)) return true;
        if (_unitFunctions?.Contains(name) ?? false) return true;
        return s.Words.Any(WordNeedsOwnProcess);
    }

    private static bool AssignmentNeedsOwnProcess(Assignment a)
        => IsProcessPath(a.Name)
            || (a.Value is not null && WordNeedsOwnProcess(a.Value))
            || (a.ArrayValue is not null && a.ArrayValue.Elements.Any(WordNeedsOwnProcess));

    // In an in-process job a program name is looked up in the PROCESS PATH (background-jobs.md section 3, not covered).
    private static bool IsProcessPath(string storeName) => storeName == PsBash.Core.BashNameCase.ToStore("PATH");

    private static bool RedirectsNeedOwnProcess(ImmutableArray<Redirect> redirects)
        => !redirects.IsDefaultOrEmpty && redirects.Any(r => WordNeedsOwnProcess(r.Target));

    private static bool WordNeedsOwnProcess(CompoundWord word) => PartsNeedOwnProcess(word.Parts);

    // A command or process substitution runs in the job's process: its `cd` moves the process too.
    private static bool PartsNeedOwnProcess(ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case WordPart.CommandSub { Body: var body } when body is not Command c || JobNeedsOwnProcess(c):
                case WordPart.ProcessSub { Body: var pbody } when pbody is not Command pc || JobNeedsOwnProcess(pc):
                    return true;
                case WordPart.DoubleQuoted dq when PartsNeedOwnProcess(dq.Parts):
                    return true;
                case WordPart.BracedVarSub { ArgWord: { } arg } when PartsNeedOwnProcess(arg):
                    return true;
                case WordPart.BracedVarSub { Suffix: { } suffix } when HasSubstitutionText(suffix):
                    return true;
                case WordPart.ArithSub a when ArithNeedsOwnProcess(a.Expr):
                    return true;
            }
        }
        return false;
    }

    private static bool ArithNeedsOwnProcess(ArithmeticSyntax? expr) => expr is not null && HasSubstitutionText(expr.Source);

    // Unparsed text (an arithmetic source, a case pattern, an operator's word) that runs a command: `$(` that is not `$((`,
    // or a backquote. Judged by sight, so a quoted one counts too (the job then only starts slower).
    private static bool HasSubstitutionText(string text)
    {
        if (text.Contains('`')) return true;
        for (int i = text.IndexOf("$(", StringComparison.Ordinal); i >= 0; i = text.IndexOf("$(", i + 2, StringComparison.Ordinal))
            if (i + 2 >= text.Length || text[i + 2] != '(') return true;
        return false;
    }
}
