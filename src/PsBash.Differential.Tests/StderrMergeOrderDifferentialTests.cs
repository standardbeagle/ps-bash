using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A child process whose stderr the script merges into its stdout keeps the order it wrote the two in: bash hands it
/// ONE open file for fd 1 and fd 2, so there is only one sequence of writes. ps-bash read the child's stdout and
/// stderr on two pipes and merged them afterwards, so lines written back to back came out in whatever order its two
/// readers ran (<c>printf ... | sh 2&gt;&amp;1</c> printed <c>two</c> before <c>one</c>). The rule and the one primitive
/// that implements it: <c>docs/specs/stdio-messaging.md</c> §4.
/// <para>
/// Matrix: every way ps-bash starts a child (a nested shell with a <c>-c</c> program or a program on stdin, a program
/// run directly, and a program run by <c>env</c>, <c>command</c>, <c>xargs</c>, <c>find -exec</c>, <c>timeout</c>,
/// awk's <c>system()</c>) × every bash form that merges (<c>2&gt;&amp;1</c>, <c>|&amp;</c>, <c>&amp;&gt;f</c>,
/// <c>&gt;f 2&gt;&amp;1</c>, a merging group or subshell, a command substitution) and the two forms that look like
/// a merge in the emitted PowerShell but are not (<c>2&gt;f</c>, <c>2&gt;&amp;1 &gt;/dev/null</c>). Each child writes
/// 20 alternating stdout/stderr lines with no pauses: two pipes almost never keep that order, one always does.
/// </para>
/// <para>
/// The program is one the host has: <c>perl</c> (every Linux/macOS test host) and <c>pwsh</c> (Windows). Both sets of
/// cases exist on every platform; a case whose program is not on PATH is skipped, so the matrix always runs with at
/// least one real program. Each line is written with an explicit LF and unbuffered.
/// </para>
/// </summary>
public class StderrMergeOrderDifferentialTests
{
    private const string Lines = "20";

    /// <summary>The programs: name on PATH, and the simple command that writes o1 e1 o2 e2 ... o20 e20.</summary>
    private static readonly (string Name, string Command)[] Programs =
    [
        ("perl", "perl -e \"$pl\""),
        ("pwsh", "pwsh -NoProfile -NonInteractive -Command \"$ps1\""),
    ];

    /// <summary>The children that are ps-bash's own nested shell or awk (no program needed).</summary>
    private static readonly string[] ShellChildren =
    [
        "bash -c 'for i in $(seq " + Lines + "); do echo o$i; echo e$i >&2; done'",
        "printf 'for i in $(seq " + Lines + "); do echo o$i; echo e$i >&2; done\\n' | sh",
        "awk 'BEGIN { system(\"for i in $(seq " + Lines + "); do echo o$i; echo e$i >&2; done\") }'",
    ];

    /// <summary>The ways a program is run. <c>PROG</c> is replaced by the program's command.</summary>
    private static readonly string[] ProgramChildren =
    [
        "PROG",                                    // directly (Invoke-BashNative)
        "env PSB_X=1 PROG",
        "command PROG",
        "printf '' | xargs PROG",
        "find \"$d\" -maxdepth 0 -exec PROG \\;",
        "timeout 120 PROG",
    ];

    /// <summary>The merge forms. <c>CHILD</c> is replaced by the child command.</summary>
    public static readonly string[] Forms =
    [
        "CHILD 2>&1",
        "CHILD |& cat",
        "CHILD &>\"$d/f\"; cat \"$d/f\"",
        "CHILD >\"$d/f\" 2>&1; cat \"$d/f\"",
        "{ CHILD; echo end; } 2>&1",
        "( CHILD ) 2>&1 | cat",
        "x=$(CHILD 2>&1); echo \"$x\"",
        // not merges: stderr goes elsewhere, stdout alone comes out (then the file)
        "CHILD 2>\"$d/f\"; echo ---; cat \"$d/f\"",
        "CHILD 2>&1 >/dev/null",
    ];

    private const string Prologue =
        "ps1='foreach($i in 1.." + Lines + "){[Console]::Out.Write(\"o$i`n\");[Console]::Error.Write(\"e$i`n\")}'\n" +
        "pl='$|=1; for $i (1.." + Lines + "){print \"o$i\\n\"; print STDERR \"e$i\\n\"}'\n" +
        "d=$(mktemp -d)\n";

    public static TheoryData<string, string, string> Matrix()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var child in ShellChildren)
            foreach (var form in Forms)
                data.Add("", child, form);
        foreach (var (name, command) in Programs)
            foreach (var child in ProgramChildren)
                foreach (var form in Forms)
                    data.Add(name, child.Replace("PROG", command, StringComparison.Ordinal), form);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Matrix))]
    public Task Merged_ChildOutput_KeepsWriteOrder(string program, string child, string form)
    {
        SkipWithout(program);
        return AssertOracle.EqualAsync(
            Prologue + form.Replace("CHILD", child, StringComparison.Ordinal) + "\necho rc=$?\nrm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>The exit status, piped input and arguments of a program run with one pipe are its own.</summary>
    [SkippableTheory]
    [InlineData("perl", "perl -e 'print STDERR \"x\\n\"; exit 7' 2>&1; echo rc=$?")]
    [InlineData("perl", "printf 'a\\nb\\n' | perl -ne '$|=1; print \"in:$_\"; print STDERR \"err:$_\"' 2>&1; echo rc=$?")]
    [InlineData("perl", "perl -e 'print join(\"|\",@ARGV),\"\\n\"' -- -foo:bar --x=1 '' 'a b' 2>&1; echo rc=$?")]
    [InlineData("pwsh", "pwsh -NoProfile -NonInteractive -Command '[Console]::Error.Write(\"x`n\"); exit 7' 2>&1; echo rc=$?")]
    [InlineData("pwsh", "printf 'a\\nb\\n' | pwsh -NoProfile -NonInteractive -Command '$input | % { [Console]::Out.Write(\"in:$_`n\"); [Console]::Error.Write(\"err:$_`n\") }' 2>&1; echo rc=$?")]
    // -File, not -Command: -Command folds the trailing words into its command text, so pwsh never saw them as
    // arguments and the case froze a ParserError (whose ANSI-only last line pwsh writes or not by host).
    [InlineData("pwsh", "d=$(mktemp -d); printf '%s\\n' '[Console]::Out.Write(($args -join \"|\") + \"`n\")' > \"$d/a.ps1\"; pwsh -NoProfile -NonInteractive -File \"$d/a.ps1\" -foo:bar --x=1 '' 'a b' 2>&1; echo rc=$?; rm -rf \"$d\"")]
    // ps-bash's not-found message has no "line N:" prefix (a separate gap): compare the message's tail.
    [InlineData("", "x=$(no_such_cmd_zz 2>&1); echo \"rc=$? ${x##*: }\"")]
    public Task Merged_Program_KeepsStatusInputAndArguments(string program, string script)
    {
        SkipWithout(program);
        return AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Skip a case whose program this host does not have (pwsh on a minimal Linux, perl on Windows). Recording the
    /// oracle (<c>PSBASH_ORACLE_RECORD=1</c>, in WSL, which has both) never skips, so every cassette exists.
    /// </summary>
    private static void SkipWithout(string program)
    {
        if (program.Length == 0 || Environment.GetEnvironmentVariable("PSBASH_ORACLE_RECORD") == "1") return;
        Skip.IfNot(OnPath(program), $"{program} is not on PATH on this host; the matrix runs with the program it has");
    }

    private static bool OnPath(string program)
    {
        string[] names = OperatingSystem.IsWindows() ? [program + ".exe", program + ".cmd", program] : [program];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                try { if (File.Exists(Path.Combine(dir, name))) return true; }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        return false;
    }
}
