using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// bash refuses a script whose first line holds a NUL ("cannot execute binary file", 126) and runs one with a NUL
/// on a later line (oracle: `printf 'echo one \0 echo two\n' > f; bash f` -> 126; `printf 'echo one\necho \0two\n'`
/// -> runs). End to end: spec/oils/bugs.spec.sh "file with NUL byte".
/// </summary>
public sealed class ScriptFileTests
{
    [Theory]
    [InlineData("echo one \0 echo two\n", true)]
    [InlineData("\0", true)]
    [InlineData("echo one\necho \0two\n", false)]   // NUL after the first newline: bash runs it
    [InlineData("echo hi\n", false)]
    [InlineData("", false)]
    public void LooksBinary_NulBeforeTheFirstNewline(string text, bool binary)
        => Assert.Equal(binary, ScriptFile.LooksBinary(text));

    [Fact]
    public void LooksBinary_OnlyTheFirst80CharsAreSampled()
    {
        Assert.True(ScriptFile.LooksBinary(new string('a', 79) + "\0"));
        Assert.False(ScriptFile.LooksBinary(new string('a', 80) + "\0"));
    }
}
