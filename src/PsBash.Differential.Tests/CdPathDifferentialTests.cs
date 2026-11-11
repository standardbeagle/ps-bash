using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>cd</c> searches CDPATH for a relative target that does not start with <c>.</c>, and echoes the directory when a
/// non-empty entry (even <c>.</c>) found it. ps-bash ignored CDPATH: <c>CDPATH=/srv; cd app</c> failed or landed in
/// ./app. A marker file in each candidate shows where cd landed without printing a path (Windows paths differ).
/// </summary>
public class CdPathDifferentialTests
{
    private const string Setup =
        "b=\"$(mktemp -d)\"; mkdir -p \"$b/a/foo\" \"$b/b/foo\" \"$b/foo\"; touch \"$b/a/foo/A\" \"$b/b/foo/B\" \"$b/foo/C\"\n";

    [Theory]
    // `.` entry first: ./foo wins, and is echoed.
    [InlineData("cd \"$b\"; CDPATH=\".:$b/a\"; [ -n \"$(cd foo)\" ] && echo printed; cd \"$b\"; cd foo >/dev/null; echo \"st=$? $(ls)\"")]
    // Empty entry first: ./foo wins, nothing echoed.
    [InlineData("cd \"$b\"; CDPATH=\":$b/a\"; [ -n \"$(cd foo)\" ] && echo printed; cd \"$b\"; cd foo; echo \"st=$? $(ls)\"")]
    // CDPATH beats the current directory.
    [InlineData("cd \"$b\"; CDPATH=\"$b/a\"; [ -n \"$(cd foo)\" ] && echo printed; cd \"$b\"; cd foo >/dev/null; echo \"st=$? $(ls)\"")]
    // A missing entry is skipped; a trailing slash is fine; an expanded target uses CDPATH too.
    [InlineData("cd \"$b\"; CDPATH=\"$b/nope:$b/b/\"; d=foo; cd $d >/dev/null; echo \"st=$? $(ls)\"")]
    // `./x`, an unknown name, and a relative entry that does not hold the target fall back to the current directory.
    [InlineData("CDPATH=\"$b/a\"; cd \"$b\"; cd ./foo; echo \"st=$? $(ls)\"; cd \"$b\"; cd zz 2>/dev/null; echo \"st=$?\"; cd \"$b/a\"; CDPATH=a; cd foo; echo \"st=$? $(ls)\"")]
    // No CDPATH: plain relative cd; a target with shell syntax in it is just a name.
    [InlineData("unset CDPATH; cd \"$b\"; cd foo; echo \"st=$? $(ls)\"; CDPATH=\"$b/a\"; cd \"$b\"; d='foo;echo pwned'; cd \"$d\" 2>/dev/null; echo \"st=$?\"")]
    public Task Cd_SearchesCdPath(string script) => AssertOracle.EqualAsync(Setup + script);
}
