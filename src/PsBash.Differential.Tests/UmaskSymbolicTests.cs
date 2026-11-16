using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>umask symbolic modes (u=rwx,g+r,a=): one operator per clause, no permission copy.</summary>
public class UmaskSymbolicTests
{
    [SkippableFact] public Task Remove_UserWrite() => AssertOracle.EqualAsync("umask 0000; umask u-rw; echo s=$?; umask");
    [SkippableFact] public Task Set_ThenClear_Duplicates() => AssertOracle.EqualAsync("umask 0111; umask u=rwx,u=rw,u=r,u=,g=rwx; umask");
    [SkippableFact] public Task DefaultWho_IsAll() => AssertOracle.EqualAsync("umask 0124; umask =rx; echo s=$?; umask");
    [SkippableFact] public Task Copy_IsRejected() => AssertOracle.EqualAsync("umask 0124; umask a=u 2>/dev/null; echo s=$?; umask");
    [SkippableFact] public Task TwoOperators_AreRejected() => AssertOracle.EqualAsync("umask 0124; umask u+r+w 2>/dev/null; echo s=$?; umask");
    [SkippableFact] public Task EmptyClause_IsRejected() => AssertOracle.EqualAsync("umask 0124; umask 'u+r,,u-r' 2>/dev/null; echo s=$?; umask");
    // oils builtin-umask: an unknown option letter is not a symbolic mode (status 2, mask unchanged).
    [SkippableFact] public Task InvalidOption_LeavesMask() => AssertOracle.EqualAsync("umask 0124; umask -rwx 2>/dev/null; echo s=$?; umask");
    [SkippableFact] public Task PrintReusable() => AssertOracle.EqualAsync("umask 0027; umask -p; umask -p -S; umask -S 0077; umask -pS");
}
