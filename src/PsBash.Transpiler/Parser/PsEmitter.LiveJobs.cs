using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// The background jobs (<c>cmd &amp;</c>) of the unit being emitted whose stdout and stderr are the SHELL's own:
    /// their output may go straight to the session's output path while they run (<c>Invoke-BashBackground -Live</c>,
    /// docs/specs/background-jobs.md section 12). Null for eval / source / trap text, which runs inside a caller whose
    /// output may be redirected or captured. By reference: two identical <c>sleep 1 &amp;</c> may sit in different contexts.
    /// </summary>
    [ThreadStatic]
    private static HashSet<Command>? _liveJobs;

    /// <summary>
    /// Every <see cref="Command.Background"/> reached from <paramref name="root"/> through statements whose output is
    /// not redirected, piped, or captured. A job inside a function body, a multi-stage pipeline, a command or process
    /// substitution, a compound command with an output redirect, or after a command-less <c>exec &gt;file</c> is left
    /// out: its output belongs to that redirect, which a live write to the shell's stdout would bypass, so it keeps the
    /// collected-at-<c>wait</c> behaviour.
    /// </summary>
    internal static HashSet<Command> CollectLiveJobs(Command root)
    {
        var live = new HashSet<Command>(ReferenceEqualityComparer.Instance);
        Walk(root);
        return live;

        void Walk(Command? c)
        {
            if (c is null) return;
            using var depth = NestingGuard.Enter();
            switch (c)
            {
                case Command.Background bg:
                    live.Add(bg);
                    break;
                case Command.CommandList l:
                    foreach (var s in l.Commands)
                    {
                        // `exec >log; rest`: the rest runs under the shell-wide redirection.
                        if (StdExecRedirects(s) is { } execRedirects && HasOutputRedirect(execRedirects)) return;
                        Walk(s);
                    }
                    break;
                case Command.AndOrList a:
                    foreach (var s in a.Commands) Walk(s);
                    break;
                case Command.Pipeline { Commands.Length: 1 } p:
                    Walk(p.Commands[0]);
                    break;
                case Command.If i when !HasOutputRedirect(i.Redirects):
                    foreach (var arm in i.Arms) { Walk(arm.Cond); Walk(arm.Body); }
                    Walk(i.ElseBody);
                    break;
                case Command.While w when !HasOutputRedirect(w.Redirects):
                    Walk(w.Cond);
                    Walk(w.Body);
                    break;
                case Command.ForIn f when !HasOutputRedirect(f.Redirects):
                    Walk(f.Body);
                    break;
                case Command.ForArith fa when !HasOutputRedirect(fa.Redirects):
                    Walk(fa.Body);
                    break;
                case Command.Case cs when !HasOutputRedirect(cs.Redirects):
                    foreach (var arm in cs.Arms) Walk(arm.Body);
                    break;
                case Command.BraceGroup bg when !HasOutputRedirect(bg.Redirects):
                    Walk(bg.Body);
                    break;
                case Command.Subshell sub when !HasOutputRedirect(sub.Redirects):
                    Walk(sub.Body);
                    break;
                // Functions (called from anywhere, under any redirect), multi-stage pipelines, select, simple commands: not entered.
            }
        }
    }

    /// <summary>True when one of <paramref name="redirects"/> sends stdout or stderr somewhere (anything but an input redirect).</summary>
    private static bool HasOutputRedirect(System.Collections.Immutable.ImmutableArray<Redirect> redirects)
    {
        if (redirects.IsDefaultOrEmpty) return false;
        foreach (var r in redirects)
        {
            if (r.Op is "<" or "<<" or "<<-" or "<<<" or "<&") continue;
            return true;
        }
        return false;
    }
}
