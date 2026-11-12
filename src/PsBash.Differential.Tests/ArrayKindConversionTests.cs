using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>declare -A</c> on an indexed array and <c>declare -a</c> on an associative one are refused (status 1, the array
/// unchanged); ps-bash replaced the array. A scalar becomes element 0 of the new associative array.
/// The names differ by more than case: <c>a</c> and <c>A</c> are one variable in ps-bash (intentional-differences.md).
/// </summary>
public class ArrayKindConversionTests
{
    [Fact]
    public Task DeclareRefusesToChangeAnArraysKind() => AssertOracle.EqualAsync(
        "ix=(1 2 3); eval 'declare -A ix=([a]=x)' 2>/dev/null; echo st:$?; declare -p ix\n"
        + "declare -A as=([a]=x); eval 'declare -a as=(1 2)' 2>/dev/null; echo st:$?; declare -p as\n"
        + "q=(1); declare -A q 2>/dev/null; echo st:$?; declare -A r; declare -a r 2>/dev/null; echo st:$?\n"
        + "sp=([3]=x); declare -A sp 2>/dev/null; echo st:$?\n"
        + "x=1; declare -A x; echo st:$?; declare -p x\n"
        + "y=(1 2); declare -a y=(3); echo st:$?; declare -p y\n"
        + "declare -A z=([k]=v); declare -A z=([j]=w); echo st:$?; declare -p z\n"
        + "w=(1 2); declare -A w v=(1 2) 2>/dev/null; echo st:$?; declare -p v");
}
