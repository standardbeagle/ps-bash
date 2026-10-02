using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU tail -v/-q/-z, "==> name <==" headers, --retry / --pid / --max-unchanged-stats / -F option handling
/// and the deterministic <see cref="TailFollower"/> state machine (message texts: coreutils 9.4 under wsl).
/// </summary>
public class TailHeadersFollowTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "psb-tailf-" + Guid.NewGuid().ToString("N")[..10]);
    public TailHeadersFollowTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Theory]
    [InlineData("a\0b\0c\0", 2, false, "b\0c\0")]
    [InlineData("a\0b\0c", 2, false, "b\0c")]
    [InlineData("a\0b\0c", 2, true, "b\0c")]
    [InlineData("a\0b\0c", 0, false, "")]
    [InlineData("", 3, false, "")]
    public void ZeroTail_Pure(string input, int n, bool from, string expected) =>
        Assert.Equal(expected, InvokeBashTailCommand.ZeroTail(input, n, from));

    [Fact]
    public void Plan_FollowNameRetryPid()
    {
        var p = InvokeBashTailCommand.Plan(new[] { "-F", "--pid=7", "--max-unchanged-stats=3", "f" });
        Assert.True(p.Follow); Assert.True(p.FollowName); Assert.True(p.Retry);
        Assert.Equal(7, p.Pid);
        Assert.Null(p.Error);
        Assert.True(InvokeBashTailCommand.Plan(new[] { "--follow=na", "f" }).FollowName);
        Assert.False(InvokeBashTailCommand.Plan(new[] { "-f", "f" }).FollowName);
    }

    [Fact]
    public void Plan_Warnings_AndErrors()
    {
        Assert.Contains("--retry ignored", InvokeBashTailCommand.Plan(new[] { "--retry", "f" }).Warnings[0]);
        Assert.Contains("--retry only effective for the initial open", InvokeBashTailCommand.Plan(new[] { "-f", "--retry", "f" }).Warnings[0]);
        Assert.Contains("PID ignored", InvokeBashTailCommand.Plan(new[] { "--pid=1", "f" }).Warnings[0]);
        Assert.Equal("tail: invalid PID: 'x'", InvokeBashTailCommand.Plan(new[] { "--pid=x", "f" }).Error);
        Assert.StartsWith("tail: invalid maximum number of unchanged stats", InvokeBashTailCommand.Plan(new[] { "--max-unchanged-stats=x", "f" }).Error);
    }

    [Fact]
    public void Plan_QuietVerbose_LastWins()
    {
        Assert.Equal(FileHeaders.Mode.Always, InvokeBashTailCommand.Plan(new[] { "-qv" }).Headers);
        Assert.Equal(FileHeaders.Mode.Never, InvokeBashTailCommand.Plan(new[] { "-vq" }).Headers);
        Assert.True(InvokeBashTailCommand.Plan(new[] { "-z" }).Zero);
    }

    [Fact]
    public void Follower_NameMode_ReportsDisappearReappearTruncate()
    {
        var path = Path.Combine(_tmp, "f");
        File.WriteAllText(path, "1\n2\n");
        var warns = new List<string>(); var got = new List<string>();
        var t = new TailFollower.Target { Path = path, Display = "f" };
        var fol = new TailFollower(new[] { t }, byName: true, retry: true, warns.Add);
        fol.Start(t, new FileInfo(path).Length);

        File.AppendAllText(path, "3\npartial");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Equal(new[] { "3" }, got);          // the unterminated fragment waits

        File.AppendAllText(path, "\n");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Equal(new[] { "3", "partial" }, got);

        File.Delete(path);
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Contains("tail: 'f' has become inaccessible: No such file or directory", warns);

        File.WriteAllText(path, "n\n");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Contains("tail: 'f' has appeared;  following new file", warns);
        Assert.Equal("n", got[^1]);

        File.WriteAllText(path, "");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Contains("tail: f: file truncated", warns);
        File.AppendAllText(path, "t\n");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Equal("t", got[^1]);
        Assert.True(fol.AnyLeft);
    }

    [Fact]
    public void Follower_DescriptorMode_WithoutRetry_DropsAMissingFile()
    {
        var t = new TailFollower.Target { Path = Path.Combine(_tmp, "nope"), Display = "nope" };
        var fol = new TailFollower(new[] { t }, byName: false, retry: false, _ => { });
        fol.Start(t, null);
        Assert.False(fol.AnyLeft);
    }

    [Fact]
    public void Follower_RetryOnMissing_PicksUpTheFileWhenItAppears()
    {
        var path = Path.Combine(_tmp, "late");
        var warns = new List<string>(); var got = new List<string>();
        var t = new TailFollower.Target { Path = path, Display = "late" };
        var fol = new TailFollower(new[] { t }, byName: false, retry: true, warns.Add);
        fol.Start(t, null);
        Assert.True(fol.AnyLeft);
        File.WriteAllText(path, "hi\n");
        fol.Poll((_, l) => got.AddRange(l));
        Assert.Contains("tail: 'late' has appeared;  following new file", warns);
        Assert.Equal(new[] { "hi" }, got);
    }

    [Fact]
    public void IsAlive_CurrentProcessYes_BogusPidNo()
    {
        Assert.True(TailFollower.IsAlive(Environment.ProcessId));
        Assert.False(TailFollower.IsAlive(2000000000));
    }
}
