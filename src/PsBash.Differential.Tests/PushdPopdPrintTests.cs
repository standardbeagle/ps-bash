using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>pushd and popd print the directory stack (one line) after a successful change; word counts keep the test path-agnostic.</summary>
public class PushdPopdPrintTests
{
    [Fact] public Task Pushd_PrintsTheStack() => AssertOracle.EqualAsync("d=\"$(mktemp -d)\"; cd /; pushd /tmp > \"$d/1\"; pushd /tmp > \"$d/2\"; popd > \"$d/3\"; popd > \"$d/4\"; for i in 1 2 3 4; do wc -w < \"$d/$i\"; done");
    [Fact] public Task PushdFailure_PrintsNothing() => AssertOracle.EqualAsync("d=\"$(mktemp -d)\"; cd /; pushd /nonexistent-dir-zz > \"$d/f\" 2>/dev/null; echo rc=$?; wc -c < \"$d/f\"");
    [Fact] public Task PushdToDevNull_IsSilent() => AssertOracle.EqualAsync("cd /; pushd /tmp >/dev/null; popd >/dev/null; echo done");
}
