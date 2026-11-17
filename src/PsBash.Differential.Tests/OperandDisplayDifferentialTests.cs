using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for how a diagnostic names an operand: as typed. Readers resolve the operand to a full path
/// before opening it, and OperandDisplay maps that back. An operand under the working directory that the reader never
/// recorded fell to the cwd-stripping guess, so <c>cat "$PWD/x"</c> and <c>cat ./x</c> both printed <c>cat: x: …</c>.
/// </summary>
public class OperandDisplayDifferentialTests
{
    [SkippableFact]
    public Task MissingOperand_InEveryTypedForm_IsNamedAsTyped()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; mkdir sub; "
            + "for op in \"$d/nope\" ./nope nope sub/../nope \"$d/sub/nope\"; do "
            + "cat \"$op\"; head -n1 \"$op\"; wc -l \"$op\"; grep x \"$op\"; sort \"$op\"; tail -n1 \"$op\"; "
            + "done 2>&1 | sed \"s|$d|D|g\"; cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(30));
}
