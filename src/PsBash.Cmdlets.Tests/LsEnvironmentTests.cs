using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>ls</c> reads <c>COLUMNS</c>, <c>TABSIZE</c> and <c>TIME_STYLE</c> from the PROCESS environment, so these tests change
/// it and must not overlap any other ls test: the collection runs alone (<see cref="ProcessWorkingDirectoryCollection"/>).
/// </summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public class LsEnvironmentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public LsEnvironmentTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string[] Ls(string dir, string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Invoke-BashLs {args} '{dir}'").Invoke();
        pwsh.Commands.Clear();
        return res.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private static string MakeDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "psb-lse-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        foreach (var n in new[] { "aaaa", "bbbb", "cccc", "dddd", "eeee", "ffff" })
        {
            var p = Path.Combine(d, n);
            File.WriteAllText(p, "");
            File.SetLastWriteTime(p, new DateTime(2024, 1, 3, 10, 0, 0));
        }
        return d;
    }

    private static void With(string name, string? value, Action body)
    {
        var old = Environment.GetEnvironmentVariable(name);
        try { Environment.SetEnvironmentVariable(name, value); body(); }
        finally { Environment.SetEnvironmentVariable(name, old); }
    }

    [Fact]
    public void Columns_IsTheDefaultWidth_AndWBeatsIt_AndZeroIsUnlimited()
    {
        var d = MakeDir();
        try
        {
            With("COLUMNS", "20", () =>
            {
                Assert.Equal(new[] { "aaaa  cccc  eeee", "bbbb  dddd  ffff" }, Ls(d, "'-C'"));
                Assert.Single(Ls(d, "'-C' '-w' '100'"));
            });
            With("COLUMNS", "0", () => Assert.Single(Ls(d, "'-C'")));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Columns_InvalidValue_IsWarnedAboutAndIgnored_OnlyForColumnLayouts()
    {
        var d = MakeDir();
        try
        {
            With("COLUMNS", "abc", () =>
            {
                var pwsh = _fixture.AcquireFresh();
                var res = pwsh.AddScript($"Invoke-BashLs '-C' '{d}'").Invoke();
                pwsh.Commands.Clear();
                var err = Assert.Single(pwsh.Streams.Error);
                Assert.Contains("ignoring invalid width in environment variable COLUMNS: 'abc'", err.ToString());
                Assert.NotEmpty(res);                                   // still listed, at the default 80 columns
                pwsh.Streams.ClearStreams();
                Assert.Equal(6, Ls(d, "'-1'").Length);
            });
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Tabsize_Environment_IsTheDefaultTabSize()
    {
        var d = MakeDir();
        try
        {
            With("TABSIZE", "0", () => Assert.DoesNotContain('\t', Ls(d, "'-x' '-w' '1000'")[0]));
            With("TABSIZE", "3", () => Assert.Contains('\t', Ls(d, "'-x' '-w' '1000'")[0]));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void TimeStyle_Environment_IsTheDefault_OptionBeatsIt()
    {
        var d = MakeDir();
        try
        {
            With("TIME_STYLE", "long-iso", () =>
            {
                Assert.Contains("2024-01-03 10:00 cccc", Ls(d, "'-l'").First(l => l.EndsWith("cccc")));
                Assert.Contains("Jan  3  2024", Ls(d, "'-l' '--time-style=locale'").First(l => l.EndsWith("cccc")));
            });
        }
        finally { Directory.Delete(d, true); }
    }
}
