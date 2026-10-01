using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Streaming <c>tr</c> with newline-touching tables, and command-substitution capture of exact-bytes
/// records (<c>ConvertTo-BashCapture</c>): the bytes must equal bash's however the records are cut.
/// Output is bracketed so a missing / extra trailing newline is visible.
/// </summary>
public class TrNewlineCaptureDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData("seq 1 3 | tr -d '\\n'; echo '|'")]
    [InlineData("seq 1 3 | tr '\\n' ,; echo '|'")]
    [InlineData("printf 'a\\nb\\n' | tr '\\n' ' '; echo '|'")]
    [InlineData("printf 'a\\n\\n\\nb\\n' | tr -s '\\n'; echo '|'")]
    [InlineData("printf 'a\\n\\n\\nb\\n' | tr -s '\\n' ' '; echo '|'")]
    [InlineData("printf 'a\\nb' | tr -d '\\n'; echo '|'")]
    [InlineData("printf 'a\\nb\\n' | tr '\\n' ' ' | wc -c")]
    [InlineData("printf 'one two\\nthree\\n' | tr -cs 'a-z' '\\n'; echo '|'")]
    [InlineData("printf 'a b\\n' | tr ' ' '\\n'; echo '|'")]
    [InlineData("printf 'a\\nb\\n' | tr '\\n' x | tr x '\\n'; echo '|'")]
    [InlineData("yes | tr '\\n' x | head -c 5; echo '|'")]
    [InlineData("yes abc | tr -d '\\n' | head -c 7; echo '|'")]
    public Task NewlineTouchingTables_StreamWithExactBytes(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("x=$(printf 'a\\nb\\n' | tr '\\n' ,); echo \"[$x]\"")]
    [InlineData("x=$(seq 1 3 | tr -d '\\n'); echo \"[$x]\"")]
    [InlineData("x=$(seq 1 3 | tr '\\n' ' '); echo \"[$x]\"")]
    [InlineData("echo $(printf 'a\\nb\\n' | tr '\\n' ,)")]
    [InlineData("echo \"$(printf 'a\\n\\n\\nb\\n' | tr -s '\\n' ' ')|\"")]
    [InlineData("x=$(printf a; printf b); echo \"[$x]\"")]
    [InlineData("x=$(printf 'x\\n'; echo y); echo \"[$x]\"")]
    [InlineData("x=$(printf 'a\\nb\\n'; echo c); echo \"[$x]\"")]
    [InlineData("x=$(echo -n a; echo b); echo \"[$x]\"")]
    [InlineData("x=$(echo -n a; echo -n b); echo \"[$x]\"")]
    [InlineData("x=$(printf 'k=v\\n' | tr '=' '\\n'); echo \"[$x]\"")]
    public Task CommandSubstitution_GluesExactRecords(string script) => Eq(script);
}
