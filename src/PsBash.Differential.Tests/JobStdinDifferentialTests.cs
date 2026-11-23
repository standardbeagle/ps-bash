using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash-vs-ps-bash for ONE stdin read by the shell, its programs and its background jobs (docs/specs/stdio-messaging.md
/// §4.7): every reader reads one position, as bash's share one open file description. The programs are named by their
/// Unix path, so these run where bash's coreutils are.
/// </summary>
public class JobStdinDifferentialTests
{
    // REGRESSION: the foreground program joined the pipe a background program held, and when it exited the pipe stayed up
    // with the rest of the input in it: `read` found the stdin empty and done (`a end`, 9 runs in 10). A background
    // program that reads nothing starved the shell's reads the same way (`a b`; `a=`).
    [SkippableFact] public Task ReadLoop_BesideABackgroundProgram_ReadsEveryLine()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            printf 'a\nb\nc\n' > in.txt
            while read x; do /usr/bin/true & /usr/bin/echo $x; done < in.txt; wait; echo end-file
            seq 3 | while read x; do /usr/bin/true & /usr/bin/echo $x; done; wait; echo end-pipe
            printf 'a\nb\nc\n' | while read x; do /usr/bin/sleep 0.3 & echo "$x"; done; wait; echo end-sleep
            seq 5 | { /usr/bin/sleep 1 & sleep 0.5; read a; echo "a=$a"; wait; read b; echo "b=$b"; /usr/bin/cat; }
            printf 'hi\n' | { /usr/bin/sleep 1 & sleep 0.5; /usr/bin/cat; wait; }
            echo hi2 | { /usr/bin/cat & wait; }
            """ + "\n");
    }

    // REGRESSION: a program that started while the last one's pipe was being put back opened a second pipe and took the
    // records behind it first (`a=2 b=1`, 8 runs in 15).
    [SkippableFact] public Task Records_AfterProgramsComeAndGo_KeepTheirOrder()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            seq 50000 | { /usr/bin/true & /usr/bin/true; /usr/bin/true; wait; read a; read b; echo "a=$a b=$b"; /usr/bin/cat >/dev/null; }
            seq 50000 | { for i in 1 2 3 4 5 6 7 8 9 10; do /usr/bin/true & /usr/bin/true & wait; done; /usr/bin/cat; } | /usr/bin/md5sum
            seq 20000 | { for i in 1 2 3 4 5; do /usr/bin/true & /usr/bin/true; done; wait; read a; echo "a=$a"; /usr/bin/wc -l; }
            """ + "\n");
    }

    // REGRESSION: a program that `env`, `command` or `time` runs read the process stdin, not the compound's (nothing); a
    // `command prog &` job went to a job host, which reads none of it.
    [SkippableFact] public Task ProgramsRunByACommand_ReadTheCompoundsStdin()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            echo in1 | { env X=1 /usr/bin/cat; }
            echo in2 | { env X=1 /usr/bin/cat & wait; }
            echo in3 | { command /usr/bin/cat & wait; }
            echo in4 | { time /usr/bin/cat & wait; } 2>/dev/null
            printf 'in5\nrest\n' | { env X=1 /usr/bin/head -n1; read r; echo "r=$r"; }
            echo in6 | { command cat & wait; }
            echo in7 | { command -p command /usr/bin/cat & wait; }
            """ + "\n");
    }

    // REGRESSION (gap 1): a job that runs in a host process of its own (it changes the cwd, defines or calls a function,
    // runs eval) read none of the compound's stdin: `b=1` where bash says `b=2`, `x=` where bash says `x=1`.
    [SkippableFact] public Task AJobInAHostOfItsOwn_ReadsTheCompoundsStdin()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            seq 3 | { read a & wait; read b; echo "a=$a b=$b"; }
            seq 3 | { cd / && read a & wait; read b; echo "b=$b"; }
            seq 3 | { f() { read x; echo "x=$x"; }; f & wait; read b; echo "b=$b"; }
            seq 3 | { { cd /; read x; echo "x=$x"; } & wait; /usr/bin/cat; }
            echo hi | { eval 'read q; echo "q=$q"' & wait; }
            seq 3 | { cd / && /usr/bin/sleep 0.2 & read b; wait; echo "b=$b"; }
            seq 3 | { { cd /; /usr/bin/head -n1; } & wait; echo end; }
            """ + "\n");
    }

    // REGRESSION (gap 2): `nohup prog &` read /dev/null, not the compound's stdin. GNU nohup leaves a stdin that is not a
    // terminal alone. The background child is awaited by its output file: ps-bash's `wait` does not wait for a nohup
    // child (it is no job of the shell's; a separate gap).
    [SkippableFact] public Task Nohup_ReadsTheCompoundsStdin()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            echo in | { nohup /usr/bin/cat > o.txt & for i in $(seq 100); do [ -s o.txt ] && break; sleep 0.1; done; }; /usr/bin/cat o.txt
            echo in2 | nohup /usr/bin/cat > o2.txt; /usr/bin/cat o2.txt
            echo in3 | { nohup /usr/bin/cat > o3.txt; read r; echo "r=$r"; }; /usr/bin/cat o3.txt
            """ + "\n");
    }
}
