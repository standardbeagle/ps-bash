using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>exec &gt;&amp;2</c>, <c>exec &gt;file</c>, <c>exec 2&gt;&amp;1</c>, <c>exec &lt;file</c>: a command-less <c>exec</c> on the shell's
/// own stdin/stdout/stderr applies to every later command of the list. It printed a stray "0" (<c>exec &gt;&amp;2</c>) or
/// "redirecting the shell's own stdin/stdout/stderr with exec is not supported", and the later commands kept the old
/// descriptors. A binding before it (<c>FOO=bar exec &gt;&amp;2</c>) does not stay.
/// </summary>
public class ExecStandardFdTests
{
    [Fact]
    public Task ExecOfAStandardDescriptor_AppliesToTheRestOfTheList() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"\n"
        + "( exec >o.txt; echo to-file; printf 'pf\\n' ); echo \"file: $(tr '\\n' ' ' < o.txt)\"\n"
        + "( exec 1>/dev/null; echo hidden ); echo visible\n"
        + "( exec >&2; echo to-err ) 2>/dev/null; echo after-err\n"
        + "( exec 1>&2; echo on-stderr )\n"
        + "( exec 2>&1; echo e1 >&2 ) 2>/dev/null\n"
        + "printf 'l1\\nl2\\n' > in.txt; ( exec <in.txt; read a; read b; echo \"read: $a $b\" )\n"
        + "( FOO=bar exec >&2; echo \"FOO=[$FOO]\" ) 2>&1\n"
        + "echo \"end s=$?\"");
}
