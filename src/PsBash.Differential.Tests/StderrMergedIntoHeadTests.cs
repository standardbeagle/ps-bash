using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>cmd 2&gt;&amp;1 | head -1</c> where the first line is the command's stderr: head stops the pipeline while
/// the producer is inside its error write. The stop was swallowed there and the rest of the script never ran.
/// </summary>
public class StderrMergedIntoHeadTests
{
    [SkippableTheory]
    [InlineData("ls /nonexist_zz 2>&1 | head -1; echo after")]
    [InlineData("ls /nonexist_zz 2>&1 | head -2; echo after")]
    public Task StderrFirstLine_HeadStops_ScriptContinues(string script)
        => AssertOracle.EqualAsync(script);
}
