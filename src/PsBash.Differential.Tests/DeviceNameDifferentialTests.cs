using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The device × consumer matrix (docs/specs/paths.md "Device names"): every site that takes a script-supplied path
/// treats a <c>/dev/*</c> name as the device, never as a file. REGRESSION: <c>echo {v}&gt;/dev/stdout</c> (the fd table)
/// opened <c>C:\dev\stdout</c> - "No such file or directory" wherever no <c>C:\dev</c> exists, a junk file where one
/// does. Each consumer had its own list of names, or none.
/// The scripts keep bash's stderr text off the compared output (it names the script), send stderr through a pipe when
/// its content matters, and never leave <c>/dev/stdout</c> writing to the test's own stdout: the WSL oracle's stdout
/// is a Windows pipe that <c>/dev/stdout</c> cannot reopen, so each such command runs into <c>| cat</c>.
/// </summary>
public class DeviceNameDifferentialTests
{
    // The fd table: `{var}>dev`, `exec N>dev`. A stream name is a dup of that descriptor.
    [SkippableTheory]
    [InlineData("{ echo x {v}>/dev/stdout; echo \"v=$((v>=10))\"; } 2>&1 | cat")]
    [InlineData("{ exec {w}>/dev/stdout; echo viaw >&$w; exec {w}>&-; } 2>&1 | cat")]
    [InlineData("{ exec 3>/dev/stdout; echo x3 >&3; exec 3>&-; } 2>&1 | sed 's/^/P:/'")]
    [InlineData("{ exec 3>>/dev/stdout; echo a3 >&3; exec 3>&-; } 2>&1 | cat")]
    [InlineData("{ exec 3>/dev/fd/1; echo f1 >&3; exec 3>&-; } 2>&1 | cat")]
    [InlineData("exec 3>/dev/null; echo gone >&3; echo \"s=$?\"; exec 3>&-")]
    [InlineData("exec 4</dev/null; read -u 4 line; echo \"s=$? [$line]\"; exec 4<&-")]
    // The oils case that failed on every machine with no C:\dev (spec/oils/redirect.spec.sh).
    [InlineData("{\necho {myvar}>/dev/stdout\nstarting_fd=$myvar\necho x= {myvar}>/dev/stdout\necho $((myvar-starting_fd))\necho {myvar}>/dev/stdout\necho $((myvar-starting_fd))\n} 2>&1 | cat")]
    public Task FdTable_DeviceTarget_MatchesBash(string script) => AssertOracle.EqualAsync(script);

    // Redirect targets beyond DevStreamRedirectDifferentialTests: /dev/fd/N, a target from a variable, the null device.
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"; exec 3>out3; echo to3 > /dev/fd/3; exec 3>&-; cat out3; ls")]
    [InlineData("d=/dev/stdout; echo viavar > \"$d\" | cat")]
    [InlineData("d=/dev/stderr; { echo viavar > \"$d\"; } 2>&1 | sed 's/^/E:/'")]
    [InlineData(": > /dev/null; echo \"s=$?\"; echo x > /dev/null; echo \"s=$?\"")]
    [InlineData("{ echo fd2 > /dev/fd/2; } 2>&1 | sed 's/^/E:/'")]
    public Task Redirect_DeviceTarget_MatchesBash(string script) => AssertOracle.EqualAsync(script);

    // Commands that write a named output file.
    [SkippableTheory]
    [InlineData("{ echo x | tee /dev/stderr; } 2>&1 | sort | sed 's/^/T:/'")]
    [InlineData("echo x | tee /dev/stdout | cat")]
    [InlineData("echo x | tee /dev/null")]
    [InlineData("{ echo x | tee /dev/fd/2 >/dev/null; } 2>&1 | sed 's/^/E:/'")]
    [InlineData("{ awk 'BEGIN{print \"x\" > \"/dev/stderr\"}'; } 2>&1 | sed 's/^/E:/'")]
    [InlineData("awk 'BEGIN{print \"x\" > \"/dev/stdout\"}' | cat")]
    [InlineData("awk 'BEGIN{print \"x\" > \"/dev/null\"}'; echo \"s=$?\"")]
    [InlineData("echo x | sed -n 'w /dev/stdout' | cat")]
    [InlineData("{ echo x | sed -n 'w /dev/stderr'; } 2>&1 | sed 's/^/E:/'")]
    [InlineData("cd \"$(mktemp -d)\"; mkdir d; touch d/a; find d -name a -fprint /dev/stdout | cat; { find d -name a -fprint /dev/stderr; } 2>&1 | sed 's/^/E:/'; ls")]
    [InlineData("printf 'b\\na\\n' | sort -o /dev/stdout | cat")]
    [InlineData("printf 'b\\na\\n' | sort -o /dev/null; echo \"s=$?\"")]
    [InlineData("printf 'a\\na\\n' | uniq - /dev/stdout | cat")]
    public Task OutputFile_DeviceName_MatchesBash(string script) => AssertOracle.EqualAsync(script);

    // Commands that read a named input.
    [SkippableTheory]
    [InlineData("echo in | cat /dev/stdin")]
    [InlineData("echo in | cat /dev/fd/0")]
    [InlineData("echo in | cat < /dev/stdin")]
    [InlineData("echo abc | tr a-c A-C < /dev/stdin")]
    [InlineData("cat < /dev/null; echo \"s=$?\"; cat /dev/null; echo \"s=$?\"")]
    [InlineData("echo in | awk 'BEGIN{getline l < \"/dev/stdin\"; print \"got \" l}'")]
    [InlineData("head -c 4 /dev/zero | od -An -c")]
    [InlineData("head -c 8 /dev/urandom | wc -c; head -c 8 /dev/random | wc -c")]
    [InlineData("od -An -tx1 -N 4 /dev/zero")]
    [InlineData("wc -c /dev/null; grep x /dev/null; echo \"s=$?\"; md5sum /dev/null")]
    [InlineData("read x < /dev/null; echo \"s=$? [$x]\"; mapfile -t arr < /dev/null; echo \"n=${#arr[@]}\"")]
    [InlineData("diff /dev/null /dev/null; echo \"s=$?\"; ls /dev/null")]
    public Task InputFile_DeviceName_MatchesBash(string script) => AssertOracle.EqualAsync(script);

    // File tests answer from the device, not from a filesystem lookup. (-w /dev/stdout is run into a pipe: bash
    // answers for whatever its stdout is.)
    [SkippableTheory]
    [InlineData("for t in -e -c -f -d -s -r -w; do [ $t /dev/null ]; echo \"$t null $?\"; done")]
    [InlineData("[ -e /dev/zero ]; echo $?; [ -c /dev/zero ]; echo $?; [ -f /dev/zero ]; echo $?; [ -c /dev/full ]; echo $?; [[ -e /dev/urandom ]]; echo $?; [[ -c /dev/urandom ]]; echo $?; [ -d /dev/null ]; echo $?")]
    [InlineData("{ test -e /dev/stdout; echo $?; [[ -w /dev/stdout ]]; echo $?; [ -f /dev/stdout ]; echo $?; [ -e /dev/stderr ]; echo $?; [ -e /dev/fd/1 ]; echo $?; [ -w /dev/fd/2 ]; echo $?; if [ ! -e /dev/stdout ]; then echo no; else echo yes; fi; } 2>&1 | cat")]
    [InlineData("[ -e /dev/fd/9 ]; echo \"fd9 $?\"; exec 9</dev/null; [ -e /dev/fd/9 ]; echo \"fd9 $?\"; exec 9<&-; [ -e /dev/fd/9 ]; echo \"fd9 $?\"")]
    [InlineData("echo in | { [[ -r /dev/stdin ]]; echo \"-r $?\"; [ -e /dev/fd/0 ]; echo \"-e $?\"; }")]
    public Task FileTest_DeviceName_MatchesBash(string script) => AssertOracle.EqualAsync(script);
}
