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

    // bash open_shell_script: ENOENT -> 127, a directory -> 126 "Is a directory" (oracle: `bash /nonexist.sh` -> 127,
    // `bash "$(mktemp -d)"` -> 126). The launcher once answered 2 on the run path and 127 on the `-n` path.
    [Fact]
    public void CheckOpenable_MissingFile_Is127()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ps-bash-missing-{Guid.NewGuid():N}.sh");
        var failure = ScriptFile.CheckOpenable(path);
        Assert.NotNull(failure);
        Assert.Equal(127, failure.Value.Status);
        Assert.EndsWith(": No such file or directory", failure.Value.Message);
    }

    [Fact]
    public void CheckOpenable_Directory_Is126()
    {
        var dir = Directory.CreateTempSubdirectory("ps-bash-scriptdir-").FullName;
        try
        {
            var failure = ScriptFile.CheckOpenable(dir);
            Assert.NotNull(failure);
            Assert.Equal(126, failure.Value.Status);
            Assert.Equal($"{dir}: {dir}: Is a directory", failure.Value.Message);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void CheckOpenable_ExistingFile_IsNull()
    {
        var path = Path.GetTempFileName();
        try { Assert.Null(ScriptFile.CheckOpenable(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadFailure_OnlyAMissingFileIs127()
    {
        Assert.Equal(127, ScriptFile.ReadFailure("s", new FileNotFoundException()).Status);
        Assert.Equal(127, ScriptFile.ReadFailure("s", new DirectoryNotFoundException()).Status);
        Assert.Equal(126, ScriptFile.ReadFailure("s", new UnauthorizedAccessException()).Status);
        Assert.Equal(126, ScriptFile.ReadFailure("s", new IOException("sharing violation")).Status);
    }

    // Guard: every `ps-bash FILE` path checks FILE through ScriptFile, so the run and `-n` paths cannot drift apart again.
    [Fact]
    public void Launcher_ChecksTheScriptFileOnlyThroughScriptFile()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ScriptFileTests).Assembly.Location)!);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        var programPath = Path.Combine(dir!.FullName, "PsBash.Shell", "Program.cs");
        Assert.True(File.Exists(programPath), $"Could not find {programPath}");

        var src = File.ReadAllText(programPath);
        Assert.DoesNotContain("File.Exists(shellArgs.ScriptPath)", src);
        Assert.DoesNotContain("Directory.Exists(shellArgs.ScriptPath)", src);
        Assert.DoesNotContain("File.ReadAllTextAsync(shellArgs.ScriptPath);\n        }\n        catch (IOException", src);
    }
}
