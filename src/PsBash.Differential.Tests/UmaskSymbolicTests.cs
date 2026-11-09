using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>umask symbolic modes (u=rwx,g+r,a=): one operator per clause, no permission copy.</summary>
public class UmaskSymbolicTests
{
    [Fact] public Task Remove_UserWrite() => AssertOracle.EqualAsync("umask 0000; umask u-rw; echo s=$?; umask");
    [Fact] public Task Set_ThenClear_Duplicates() => AssertOracle.EqualAsync("umask 0111; umask u=rwx,u=rw,u=r,u=,g=rwx; umask");
    [Fact] public Task DefaultWho_IsAll() => AssertOracle.EqualAsync("umask 0124; umask =rx; echo s=$?; umask");
    [Fact] public Task Copy_IsRejected() => AssertOracle.EqualAsync("umask 0124; umask a=u 2>/dev/null; echo s=$?; umask");
    [Fact] public Task TwoOperators_AreRejected() => AssertOracle.EqualAsync("umask 0124; umask u+r+w 2>/dev/null; echo s=$?; umask");
    [Fact] public Task EmptyClause_IsRejected() => AssertOracle.EqualAsync("umask 0124; umask 'u+r,,u-r' 2>/dev/null; echo s=$?; umask");
}
