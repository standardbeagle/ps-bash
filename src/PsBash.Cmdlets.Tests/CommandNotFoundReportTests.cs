using Xunit;
using PsBash.Cmdlets;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The report for a command word that resolved to nothing. bash 5.2 is the oracle (checked in WSL):
/// <c>./$(printf %0300d 0)</c> is "File name too long" 126, <c>./x/LONG/y</c> with no x is "No such file or
/// directory" 127, a directory is "Is a directory" 126, a bare name "command not found" 127. Every slash word was
/// "command not found" 127 (oils command_ "Name too long" expected 126).
/// </summary>
public class CommandNotFoundReportTests
{
    private static readonly string Long = new('0', 300);
    private static bool OnlyDot(string p) => p is "." or "..";

    [Theory]
    [InlineData("nosuch", "bash: nosuch: command not found", 127)]
    [InlineData("./nosuch", "bash: ./nosuch: No such file or directory", 127)]
    [InlineData("/nosuch/x", "bash: /nosuch/x: No such file or directory", 127)]
    [InlineData("a\\b", "bash: a\\b: No such file or directory", 127)]
    public void Describe_MissingFile_ReportsLikeBash(string name, string message, int status)
        => Assert.Equal((message, status), CommandNotFoundReport.Describe(name, OnlyDot));

    [Fact]
    public void Describe_OverlongComponentInAnExistingDirectory_IsFileNameTooLong()
        => Assert.Equal(($"bash: ./{Long}: File name too long", 126), CommandNotFoundReport.Describe("./" + Long, OnlyDot));

    [Fact]
    public void Describe_OverlongComponentPastAMissingDirectory_IsNoSuchFile()
        => Assert.Equal(($"bash: ./x/{Long}/y: No such file or directory", 127),
            CommandNotFoundReport.Describe("./x/" + Long + "/y", OnlyDot));

    [Fact]
    public void Describe_ComponentOfExactlyNameMaxBytes_IsNotTooLong()
        => Assert.Equal(127, CommandNotFoundReport.Describe("./" + new string('a', 255), OnlyDot).Status);

    [Fact]
    public void Describe_NameMaxCountsUtf8Bytes_NotChars()
        // 128 two-byte characters are 256 bytes: over NAME_MAX although only 128 chars.
        => Assert.Equal(126, CommandNotFoundReport.Describe("./" + new string('é', 128), OnlyDot).Status);

    [Fact]
    public void Describe_WholeWordAtPathMax_IsFileNameTooLong()
    {
        string name = "./" + string.Join("/", Enumerable.Repeat(new string('d', 200), 21));
        Assert.Equal(126, CommandNotFoundReport.Describe(name, _ => true).Status);
    }

    [Fact]
    public void Describe_Directory_IsADirectory()
        => Assert.Equal(("bash: ./d: Is a directory", 126), CommandNotFoundReport.Describe("./d", p => p is "." or "./d"));

    [Fact]
    public void Describe_ExistsProbeThatThrows_IsTreatedAsMissing()
        => Assert.Equal(127, CommandNotFoundReport.Describe("./x/y", p => p == "." ? true : throw new IOException("bad")).Status);

    [Fact]
    public void DescribeIn_RealDirectory_IsADirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "psb-cnf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            var r = CommandNotFoundReport.DescribeIn("./sub", dir);
            Assert.Equal(new object[] { "bash: ./sub: Is a directory", 126 }, r);
            Assert.Equal(127, CommandNotFoundReport.DescribeIn("./missing", dir)[1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
