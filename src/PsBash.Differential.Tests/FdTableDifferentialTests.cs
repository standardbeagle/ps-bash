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
}
