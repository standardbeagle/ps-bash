using PsBash.Core.Parser;
using PsBash.Differential.Tests.Oracle;
using PsBash.Testing;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An option a command does not have is a usage error: a diagnostic on stderr and a failing status, and the command
/// does nothing else. REGRESSION: <c>basename/dirname/pwd/whoami --bogus</c> exited 0, <c>seq --bogus</c> crashed with
/// a .NET message and status 0, <c>yes --bogus</c> printed "--bogus" forever, <c>awk --bogus</c> ran "--bogus" as its
/// program, <c>times/unset/mapfile/compgen/jobs/wait/read/readonly/enable -Q</c> ran (status 0), <c>export/local -Q</c>
/// were "command not found" (127), <c>set --bogus</c> refused the whole script, and <c>nohup/xargs/which/tput/yq --bogus</c>
/// ran or looked up "--bogus". The primitives: the shared argv parser (<c>ArgParser</c> + <c>TryWriteParseError</c>,
/// <c>OptSpecSet.RequireOrder</c> for the commands that run a command) and, for builtins, one scan
/// (<c>BuiltinOptions.FindInvalid</c>) whose report is <c>NAME: OPT: invalid option</c> + the usage line, status 2
/// (<c>FileSystemHelpers.WriteBuiltinInvalidOption</c>, emitted <c>PsBuild.BuiltinInvalidOption</c>).
/// </summary>
public class UnknownOptionGuardTests
{
    /// <summary>
    /// The names that answer <c>--bogus-xyz</c> with no error IN BASH TOO, or that cannot run unattended, each with why.
    /// Everything else ps-bash maps or implements must fail with a message.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["echo"] = "prints its operands; bash's echo has no long options (status 0, no stderr)",
        ["true"] = "ignores its operands (bash: status 0)",
        [":"] = "ignores its operands (bash: status 0)",
        ["false"] = "ignores its operands (bash: status 1, no stderr)",
        ["test"] = "a one-operand test is true when the operand is non-empty (bash: status 0)",
        ["let"] = "evaluates `--bogus-xyz` as arithmetic (bash: status 0)",
        ["expr"] = "a lone operand is printed (GNU expr: status 0)",
        ["less"] = "interactive pager", ["more"] = "interactive pager",
        ["avtui"] = "ps-bash's own interactive tool", ["browse"] = "ps-bash's own interactive tool",
        ["gtui"] = "ps-bash's own interactive tool", ["psgit"] = "ps-bash's own interactive tool",
        ["psav"] = "ps-bash's own media tool (not a bash command)", ["psffmpeg"] = "ps-bash's own media tool (not a bash command)",
        ["balias"] = "ps-bash's own tool (not a bash command)", ["xan"] = "ps-bash's own tool (not a bash command)",
        ["break"] = "loop control (its own tests: DynamicBuiltinDifferentialTests)", ["continue"] = "loop control",
        ["return"] = "function control", ["exit"] = "ends the shell (its numeric-argument error has its own tests)",
        ["exec"] = "replaces the shell", ["logout"] = "ends a login shell", ["suspend"] = "stops the shell",
    };

    private static IEnumerable<string> Names()
        => ModuleAliases.Names.Concat(BashBuiltins.Names).Distinct().Where(n => !Exempt.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// GUARD: every command ps-bash maps or implements, given <c>--bogus-xyz</c>, fails with a status and a stderr line.
    /// A command added without option parsing fails here. Each runs in a subshell with no stdin, so a command that
    /// takes the word as an operand and waits for input (or loops, like the old <c>yes</c>) ends instead of hanging.
    /// </summary>
    [SkippableFact]
    public async Task EveryCommand_RejectsAnUnknownOption_WithAStatusAndAMessage()
    {
        var runner = PsBashRunner.Create().WithTimeout(240).WithMode(PsBashMode.M3_FileArg);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        var names = Names().ToList();
        var sb = new System.Text.StringBuilder("cd \"$(mktemp -d)\" || exit\n");
        foreach (var n in names)
        {
            var word = n == "[" ? "'['" : n;
            sb.Append("( ").Append(word).Append(" --bogus-xyz ) </dev/null >/dev/null 2>err; echo \"").Append(n)
              .Append(" $? $(wc -c <err)\"\n");
        }
        var r = await runner.RunScriptAsync(sb.ToString());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var line in r.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f.Length != 3) continue;
            seen.Add(f[0]);
            if (f[1] == "0" || f[2].Trim() == "0") failures.Add($"{f[0]}: status {f[1]}, {f[2].Trim()} bytes of stderr");
        }
        foreach (var n in names)
            if (!seen.Contains(n)) failures.Add($"{n}: no result line (the script stopped before it)");
        Assert.True(failures.Count == 0,
            "These accept an option they do not have (want a nonzero status and a diagnostic):\n" + string.Join("\n", failures));
    }

    /// <summary>The builtins' report, against bash: <c>NAME: OPT: invalid option</c>, the usage line, status 2.</summary>
    [SkippableFact]
    public Task Builtins_InvalidOption_MatchBash() => AssertOracle.EqualAsync(
        "for o in -Q --bogus; do\n"
        + "pwd $o; echo \"pwd $?\"; times $o; echo \"times $?\"; unset $o; echo \"unset $?\"\n"
        + "mapfile $o; echo \"mapfile $?\"; readarray $o; echo \"readarray $?\"; compgen $o; echo \"compgen $?\"\n"
        + "alias $o; echo \"alias $?\"; unalias $o; echo \"unalias $?\"; getopts $o; echo \"getopts $?\"\n"
        + "type $o; echo \"type $?\"; ulimit $o; echo \"ulimit $?\"; trap $o; echo \"trap $?\"\n"
        + "jobs $o; echo \"jobs $?\"; wait $o; echo \"wait $?\"; disown $o; echo \"disown $?\"\n"
        + "compopt $o; echo \"compopt $?\"; cd $o; echo \"cd $?\"; set $o; echo \"set $?\"; bind $o; echo \"bind $?\"\n"
        + "done 2>&1\n"
        + "{ read -Q; echo \"read $?\"; readonly -Q; echo \"readonly $?\"; export --bogus; echo \"export $?\"\n"
        + "enable -Q; echo \"enable $?\"; caller -Q; echo \"caller $?\"; builtin -Q; echo \"builtin $?\"\n"
        + "set -eQ; echo \"set-e $?\"; set +Q; echo \"set+ $?\"; local -Q; echo \"local-top $?\"\n"
        + "pwd -Q 2>/dev/null; echo \"quiet $?\"; read -Q 2>/dev/null; echo \"quiet $?\"; } 2>&1\n");

    /// <summary>The tools' reports, against GNU/Debian/procps/gawk: wording, hint or usage text, and status.</summary>
    [SkippableFact]
    public Task Tools_UnknownOption_MatchTheirGnuReports() => AssertOracle.EqualAsync(
        "{ for c in basename dirname whoami seq yes nohup xargs; do $c --bogus-xyz </dev/null; echo \"$c $?\"; $c -Q </dev/null; echo \"$c $?\"; done\n"
        + "seq 2 -w 4; echo \"seq-op $?\"; seq -s, 3; echo \"seq-s $?\"; seq -w -2 1; echo \"seq-neg $?\"\n"
        + "yes a -Q | head -1; echo \"yes-late ${PIPESTATUS[0]}\"\n"
        + "awk --bogus-xyz | head -2; echo \"awk ${PIPESTATUS[0]}\"; awk -Q 2>&1 | wc -l; echo \"awk-n ${PIPESTATUS[0]}\"\n"
        + "hostname --bogus-xyz | head -1; echo \"hostname ${PIPESTATUS[0]}\"\n"
        + "ps --bogus-xyz | head -1; echo \"ps ${PIPESTATUS[0]}\"\n"
        + "tput -Q | head -1; echo \"tput ${PIPESTATUS[0]}\"; tput --bogus | head -1; echo \"tput ${PIPESTATUS[0]}\"\n"
        + "which -Q >/dev/null; echo \"which $?\"; which --x >/dev/null; echo \"which $?\"\n"
        + "basename -z a/b | od -c | head -1; basename -a x/y z/w; basename -s .c a.c; dirname -z a/b | od -c | head -1\n"
        + "} 2>&1\n");
}
