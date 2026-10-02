using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary><c>ls -I</c> / <c>--hide</c> / <c>-B</c>: the fnmatch(FNM_PERIOD) matcher and the cmdlet filter (GNU 9.4 expectations).</summary>
public class LsIgnoreTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public LsIgnoreTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("*.c", "alpha.c", true)]
    [InlineData("*.c", "alpha.h", false)]
    [InlineData("*", ".hid", false)]            // FNM_PERIOD: a leading period needs a literal period
    [InlineData(".*", ".hid", true)]
    [InlineData(".*", ".", true)]
    [InlineData("?hid", ".hid", false)]
    [InlineData("[a-c]*", "beta.h", true)]
    [InlineData("[a-c]*", "gamma", false)]
    [InlineData("[!a-c]*", "gamma", true)]
    [InlineData("[^a-c]*", "alpha", false)]
    [InlineData("a b", "a b", true)]
    [InlineData("a\\*", "a*", true)]
    [InlineData("a\\*", "ab", false)]
    [InlineData("*~", "bk~", true)]
    [InlineData("*~", ".old~", false)]
    [InlineData(".*~", ".old~", true)]
    [InlineData("[[:digit:]]*", "9lives", true)]
    [InlineData("*.C", "x.c", false)]           // case-sensitive
    public void Glob_MatchesLikeFnmatchPeriod(string pattern, string name, bool expected)
        => Assert.Equal(expected, LsGlob.Match(pattern, name));

    private string[] Names(string dir, string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Invoke-BashLs {args} '{dir}'").Invoke();
        pwsh.Commands.Clear();
        return res.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private static string MakeDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "psb-lsi-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        foreach (var n in new[] { "alpha.c", "beta.h", "gamma", ".hid", "bk~", ".old~" }) File.WriteAllText(Path.Combine(d, n), "");
        return d;
    }

    [Fact]
    public void Ignore_Hide_And_Backups_FilterTheListing()
    {
        var d = MakeDir();
        try
        {
            Assert.Equal(new[] { "beta.h", "bk~", "gamma" }, Names(d, "'-I' '*.c'"));
            Assert.Equal(new[] { "gamma" }, Names(d, "'-I' '*.c' '--ignore=*.h' '-B'"));
            Assert.Equal(new[] { "alpha.c", "beta.h", "gamma" }, Names(d, "'-B'"));
            Assert.Equal(new[] { ".hid", "beta.h", "gamma" }, Names(d, "'-aB' '-I' '*.c' '-I' '.'  '-I' '..'"));
            Assert.Equal(new[] { "alpha.c", "bk~", "gamma" }, Names(d, "'--hide=*.h'"));
            // -a / -A disable --hide but never -I
            Assert.Equal(new[] { ".hid", ".old~", "alpha.c", "beta.h", "bk~", "gamma" }, Names(d, "'-A' '--hide=*'"));
            Assert.DoesNotContain("alpha.c", Names(d, "'-A' '-I' '*.c' '--hide=zzz'"));
        }
        finally { Directory.Delete(d, true); }
    }
}
