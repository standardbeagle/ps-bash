using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The shell's descriptor table (docs/specs/shell-fds-and-bash-source.md, "The descriptor table"): one table for fds
/// 0..N, <c>n&gt;&amp;m</c> copies what m names at that point (left to right), an <c>exec</c> is permanent wherever it runs,
/// and a subshell or command substitution works on a copy. Each case is one (dup form, command kind, exec context)
/// combination; stderr is captured into a file and printed with bash's <c>script: line N:</c> prefix removed.
/// <para>Class: ps-bash had no table for fds 1 and 2 (an <c>exec &gt;f</c> became <c>{ rest; } &gt;f</c> for the rest of
/// the list it was in, so it ended with a function or a loop body), a copy of a numbered descriptor was a pipe into it
/// taken out of the redirect order (<c>2&gt;&amp;3</c>, <c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c> were comments), a capture was not
/// fd 1 to a copy made inside it, and a closed stdout discarded the output with status 0.</para>
/// </summary>
public class FdTableDifferentialTests
{
    private const string Prelude = "cd \"$(mktemp -d)\" || exit\nerr() { sed -E 's/^([^:]*: )?(line [0-9]+: )?//' \"$1\"; }\n";

    // R1: an exec of fd 1 / fd 2 stays in force after the construct it ran in (function, if, loop, group, source, eval).
    [SkippableTheory]
    [InlineData("exec 5>&1\nfx() { exec >log1; echo in-fn; }\nfx\necho after-fn\nexec >&5 5>&-\necho back\ncat log1")]
    [InlineData("exec 5>&1\nif true; then exec >log1; fi\necho after-if\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 5>&1\nfor i in 1; do exec >log1; done\necho after-loop\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 5>&1\nwhile :; do exec >log1; break; done\necho after-while\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 5>&1\n{ exec >log1; }\necho after-group\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 5>&1\necho 'exec >log1' > s.sh\n. ./s.sh\necho after-source\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 5>&1\neval 'exec >log1'\necho after-eval\nexec >&5 5>&-\ncat log1")]
    [InlineData("exec 6>&2\nfx() { exec 2>elog; }\nfx\necho to-err >&2\nexec 2>&6 6>&-\necho back\ncat elog")]
    [InlineData("exec 5>&1\nexec >logR\necho in-log\nexec >&5\necho restored\nexec 5>&-\ncat logR")]
    [InlineData("( exec >sub; echo in-sub )\necho after-sub\ncat sub")]
    [InlineData("x=$(exec >/dev/null; echo hidden)\necho \"x=[$x]\"")]
    [InlineData("( exec >/nonexist/f; echo x ) 2>e11\necho \"s=$?\"\nerr e11")]
    [InlineData("exec 5>&1\nfx() { exec >log; }\nfx\necho a\ncat log 2>e\ns=$?\nexec >&5 5>&-\necho \"s=$s\"\nerr e\ncat log")]
    public Task Exec_OfAStandardDescriptor_IsPermanentInItsShell(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // R2: a copy is of what the descriptor names at that point, applied left to right, on every command kind.
    [SkippableTheory]
    [InlineData("exec 3>&1\nx=$(echo to3 >&3; echo cap)\necho \"x=$x\"")]
    [InlineData("exec 3>&1\ne=$( { echo out; echo err >&2; } 2>&1 1>&3 )\necho \"e=$e\"")]
    [InlineData("exec 3>&1\ne=$(echo out 2>&1 1>&3)\necho \"e=[$e]\"")]
    [InlineData("exec 3>f3\n{ echo g; } >&3\nfor i in 1; do echo l; done >&3\nif true; then echo i; fi >&3\ncase a in a) echo c;; esac >&3\nexec 3>&-\ncat f3")]
    [InlineData("exec 3>e3\n{ echo err >&2; } 2>&3\necho x 2>&3 >&2\nexec 3>&-\ncat e3")]
    [InlineData("{ exec 3>&1; } >f\necho x >&3\nexec 3>&-\necho f:\ncat f")]
    [InlineData("( { echo o; echo e >&2; } 3>&1 1>&2 2>&3 | sed 's/^/P:/' ) 2>e35\necho e35:\ncat e35")]
    [InlineData("( echo o 3>&1 1>&2 2>&3 ) 2>e\necho \"e=$(cat e)\"")]
    [InlineData("exec 3>f\n{ echo o; echo e >&2; } 2>&1 >&3 | sed 's/^/P:/'\nexec 3>&-\necho f:\ncat f")]
    [InlineData("exec 3>f\n{ echo o; echo e >&2; } >&3 2>&1 | sed 's/^/P:/'\nexec 3>&-\necho f:\ncat f")]
    [InlineData("exec 3>f\n( exec 3>g; echo a >&3 )\necho b >&3\nexec 3>&-\ncat f\necho --\ncat g")]
    [InlineData("x=$(exec 5>f5; echo in)\necho \"x=$x\"\necho y 2>/dev/null >&5\necho \"s=$?\"")]
    [InlineData("fx() { echo d >&3; }\nexec 3>f\nfx\n( echo c >&3 )\nexec 3>&-\ncat f")]
    [InlineData("for i in 1 2 3; do echo $i >>f; done\ncat f")]
    public Task Dup_CopiesWhatTheDescriptorNamesNow(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // A background job works on a copy of the table taken at the `&`, and its redirections are its own.
    [SkippableTheory]
    [InlineData("exec 3>f\n{ echo j >&3; } & wait\nexec 3>&-\ncat f")]
    [InlineData("exec 3>f\n{ exec 3>&-; } & wait\necho p >&3\nexec 3>&-\ncat f")]
    [InlineData("exec 3>f\n( exec 3>g ) & wait\necho k >&3\nexec 3>&-\ncat f\necho --\ncat g")]
    [InlineData("{ for i in 1 2 3; do echo a$i; done > fa; } & for i in 1 2 3; do echo b$i; done > fb\nwait\ncat fa fb")]
    public Task Job_GetsACopyOfTheTable(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // `cmd 2>&1` copies fd 1 into fd 2 BEFORE cmd runs (dup2): an exec of fd 1 inside moves fd 1 only, and stderr still
    // goes where fd 1 was. The merge was a pipeline join, so stderr followed the exec into log.
    [SkippableTheory]
    [InlineData("exec 5>&1\n{ exec >log; echo out; echo err >&2; } 2>&1\nexec >&5 5>&-\necho log:\ncat log")]
    [InlineData("exec 5>&1\nfx() { exec >log; echo err >&2; }\nfx 2>&1\ns=$?\nexec >&5 5>&-\necho \"s=$s\"\necho log:\ncat log")]
    [InlineData("exec 5>&1\nfor i in 1; do exec >log; echo e$i >&2; done 2>&1\nexec >&5 5>&-\necho log:\ncat log")]
    [InlineData("( exec >log; echo err >&2 ) 2>&1\necho log:\ncat log")]
    [InlineData("{ exec >log; echo err >&2; } 2>&1 | sed 's/^/P:/'\necho log:\ncat log")]
    [InlineData("x=$({ exec >log; echo o; echo e >&2; } 2>&1)\necho \"x=$x\"\necho log:\ncat log")]
    [InlineData("{ { exec >log; echo o; echo e >&2; } 2>&1; } >f\necho f:\ncat f\necho log:\ncat log")]
    [InlineData("exec 5>&1\n{ { exec >log; echo e >&2; } 2>&1; echo e2 >&2; } 2>&1\nexec >&5 5>&-\necho log:\ncat log")]
    [InlineData("{ exec 3>&1; echo t >&3; } 2>&1 | sed 's/^/P:/'")]
    [InlineData("exec 5>&1\n{ exec 3>&1; exec >log; echo out; echo err >&2; echo t >&3; exec 3>&-; } 2>&1\nexec >&5 5>&-\necho log:\ncat log")]
    [InlineData("{ echo o; echo e >&2; } 2>&1 >f\necho f:\ncat f")]
    [InlineData("x=$({ echo o; echo e >&2; } 2>&1 >f)\necho \"x=$x\"\necho f:\ncat f")]
    [InlineData("exec 5>&1\nexec >log\n{ echo o; echo e >&2; } 2>&1 >f\nexec >&5 5>&-\necho log:\ncat log\necho f:\ncat f")]
    public Task Merge_CopiesStderrFromStdoutBeforeTheCommandRuns(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // A reader of the file the shell's fd 1 names (`exec >log` persists): GNU cat and grep refuse it (grep unless it
    // writes nothing per line or one match at most); every other reader reads what was there. Each line ps-bash wrote
    // as it was made landed after what the reader had left, so it read for ever.
    private const string Reader = "R() { exec 5>&1; fx() { exec >log; }; fx; echo a; eval \"$1\" 2>e; s=$?; exec >&5 5>&-; "
        + "echo \"s=$s\"; err e; echo log:; cat log; }\n";

    [SkippableTheory]
    [InlineData("R 'grep a log'")]
    [InlineData("R 'grep -n a log'")]
    [InlineData("R 'grep zzz log'")]
    [InlineData("R 'grep -s a log'")]
    [InlineData("R 'grep -c a log'")]
    [InlineData("R 'grep -q a log'")]
    [InlineData("R 'grep -l a log'")]
    [InlineData("R 'grep -m1 a log'")]
    [InlineData("R 'grep -m2 a log'")]
    [InlineData("R 'grep a < log'")]
    [InlineData("R 'cat < log'")]
    [InlineData("R 'cat - < log'")]
    [InlineData("R 'cat -n log'")]
    [InlineData("R 'cat log | cat'")]
    [InlineData("R 'sed -n p log'")]
    [InlineData("R 'sed -n p < log'")]
    [InlineData("R 'awk 1 log'")]
    [InlineData("R 'cut -c1 log'")]
    [InlineData("R 'head -n 5 log'")]
    [InlineData("R 'tail -n 5 log'")]
    [InlineData("R 'uniq log'")]
    [InlineData("R 'sort log'")]
    [InlineData("R 'wc -l log'")]
    public Task Reader_OfTheShellsOwnOutputFile_IsRefusedOrReadsWhatWasThere(string script)
        => AssertOracle.EqualAsync(Prelude + Reader + script);

    // R6: a closed descriptor fails the write.
    [SkippableTheory]
    [InlineData("( echo hi >&- ) 2>e12\necho \"s=$?\"\nerr e12")]
    [InlineData("( printf 'p\\n' 1>&- ) 2>e13\necho \"s=$?\"\nerr e13")]
    [InlineData("exec 3>&-\n( echo x >&3 ) 2>e14\necho \"s=$?\"\nerr e14")]
    [InlineData("echo q 2>&-\necho \"s=$?\"")]
    [InlineData("( exec 1>&-; echo x ) 2>e09\necho \"s=$?\"\nerr e09")]
    [InlineData("( exec >&-; printf 'p\\n'; echo \"s=$?\" >&2; printf ''; echo \"s=$?\" >&2 ) 2>e09p\nerr e09p")]
    public Task Closed_DescriptorFailsTheWrite(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // A redirect of a descriptor that is not open (`>&5`, `2>&5`, `<&5`, `3>&5`; 5 never opened) fails the command with
    // "5: Bad file descriptor", status 1, and the command never runs. bash applies the list left to right, so the report
    // goes where fd 2 points at that moment: `2>/dev/null >&5` is silent, `>&5 2>/dev/null` shows it, `2>e >&5` writes it
    // into e, `2>&1 >&5` down the pipe, `2>&3 >&5` into descriptor 3, and a redirect after the failing one is never
    // made (`>&5 2>e` does not create e). It was reported on stderr whatever came before (a route-stage check), or the
    // command ran (`cat <&5` with an empty stdin, `3>&5` with status 0; a compound's `<&5` became PowerShell's
    // background `&`).
    [SkippableTheory]
    [InlineData("{ echo y 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y >&5 2>/dev/null; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("echo y 2>e >&5\necho \"s=$?\"\nerr e")]
    [InlineData("{ { echo y; } 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("f() { echo y; }\n{ f 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ ls / 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y 2>&- >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("exec 3>t\n{ echo y 2>/dev/null 2>&3 >&5; echo \"s=$?\"; } 2>E\nexec 3>&-\necho t:\nerr t\nerr E")]
    [InlineData("{ cat 2>/dev/null <&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ cat <&5 2>/dev/null; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ for i in 1; do echo y; done 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ ( echo y ) 2>/dev/null >&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y 2>/dev/null 1>&5 | cat; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ x=$(echo y 2>/dev/null >&5); echo \"s=$? x=$x\"; } 2>E\nerr E")]
    [InlineData("{ echo y 2>/dev/null >&5 2>e; echo \"s=$?\"; } 2>E\nerr E\n[ -e e ] && echo e-made || echo no-e")]
    [InlineData("{ echo y >&5 2>e; echo \"s=$?\"; } 2>E\nerr E\n[ -e e ] && echo e-made || echo no-e")]
    [InlineData("echo y 2>e 3>&5\necho \"s=$?\"\nerr e")]
    [InlineData("{ { echo y; } 2>/dev/null 3>&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y 3>&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ cat >&6 <&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ cat >o <&5; echo \"s=$?\"; } 2>E\nerr E\n[ -e o ] && echo o-made")]
    [InlineData("exec 3>t\necho y 2>&3 >&5\necho \"s=$?\"\nexec 3>&-\necho t:\nerr t")]
    [InlineData("f() { read -r l <&5; echo \"l=$l s=$?\"; }\n{ f 2>/dev/null; } 2>E\nerr E")]
    [InlineData("{ while read l; do echo $l; done 2>/dev/null <&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y 3>&5 2>e; echo \"s=$?\"; } 2>E\nerr E\n[ -e e ] && echo e-made || echo no-e")]
    [InlineData("{ true 3>&5 || echo caught; } 2>E\nerr E")]
    [InlineData("{ ( set -e; echo y 2>/dev/null >&5; echo after ); echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ ( set -e; echo y 2>/dev/null 3>&5; echo after ); echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ ( set -e; cat 2>/dev/null <&5; echo after ); echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("exec 3>&1\n{ echo y 2>/dev/null 3>&5 >&3; echo \"s=$?\"; } 2>E\nexec 3>&-\nerr E")]
    [InlineData("exec 4>t\necho y 2>&4 3>&5\necho \"s=$?\"\nexec 4>&-\necho t:\nerr t")]
    [InlineData("{ echo y 2>&1 >&5 | sed -E 's/^([^:]*: )?(line [0-9]+: )?/P:/'; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ read l 2>/dev/null <&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ x=$(cat 2>&1 <&5); echo \"s=$?\"; echo \"$x\" >X; } 2>E\nerr X\nerr E")]
    [InlineData("exec 3>&1\n{ echo y 2>/dev/null 3>&- >&3; echo \"s=$?\"; } 2>E\nexec 3>&-\nerr E")]
    // A command that writes nothing makes its redirections too (`true >&7` was status 0).
    [InlineData("if { true >&7; } 2>/dev/null; then echo open; else echo closed; fi\n{ : >&7; echo \"s=$?\"; : 2>/dev/null >&7; echo \"s=$?\"; : <&7; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ : >&7 >f; echo \"s=$?\"; } 2>E\nerr E\n[ -e f ] && echo f-made || echo no-f")]
    public Task RedirectError_BadDescriptor_GoesWhereFd2PointsAtThatPoint(string script)
        => AssertOracle.EqualAsync(Prelude + script);

    // The same for an open that fails or a target that is not one word, on a numbered descriptor (`3>/nonexist/f`,
    // `3>$v` with v='a b'): status 1, reported where fd 2 points at that point, the command not run. The command ran
    // (status 0) and `3>$v` opened a file named `a b`.
    [SkippableTheory]
    [InlineData("{ echo y 2>/dev/null 3>/nonexist/f; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("{ echo y 3>/nonexist/f 2>/dev/null; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("echo y 2>e 3>/nonexist/f\necho \"s=$?\"\nerr e")]
    [InlineData("{ echo y >/nonexist/f 3>&5; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("exec 3>t\necho y 2>&3 >/nonexist/f\necho \"s=$?\"\nexec 3>&-\necho t:\nerr t")]
    [InlineData("{ echo y 2>/dev/null 3>&5 4>/nonexist/f; echo \"s=$?\"; } 2>E\nerr E")]
    [InlineData("v=\"a b\"\n{ echo y 2>/dev/null 3>$v; echo \"s=$?\"; } 2>E\nerr E\nls")]
    [InlineData("v=\"a b\"\n{ echo y 3>$v; echo \"s=$?\"; } 2>E\nerr E\nls")]
    public Task RedirectError_OpenOrAmbiguous_GoesWhereFd2PointsAtThatPoint(string script)
        => AssertOracle.EqualAsync(Prelude + script);
}
