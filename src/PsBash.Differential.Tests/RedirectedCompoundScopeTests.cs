using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A redirected compound (<c>{ …; } 2&gt;/dev/null</c>, <c>if …; fi &gt;f</c>, <c>while …; done &lt;&lt;&lt; x</c>)
/// runs in the current shell: an array element it assigns survives. It ran in a child PowerShell scope, which kept
/// scalars (in <c>$env:</c>) but dropped arrays.</summary>
public class RedirectedCompoundScopeTests
{
    [Fact]
    public Task RedirectedCompound_KeepsArrayAssignments() => AssertOracle.EqualAsync(
        "if true; then a[1]=x; fi 2>/dev/null; declare -p a\n{ b[1]=y; b+=(z); } >/dev/null; declare -p b\n"
        + "for i in 1 2; do c[i]=$i; done 2>&1; declare -p c\n"
        + "f() { { q[2]=w; } 2>/dev/null; declare -p q; }; f\n"
        + "(d[1]=sub) 2>/dev/null; declare -p d 2>/dev/null || echo d-unset\n"
        + "case k in k) e=(1 2) ;; esac 2>/dev/null; declare -p e");
}
