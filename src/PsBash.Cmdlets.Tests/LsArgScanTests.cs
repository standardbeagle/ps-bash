using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// ls on the shared ordered parser: option table and errors vs GNU coreutils 9.4
/// (oracle: <c>wsl ls</c>), plus end-to-end behaviour of the last-wins conflicts.
/// </summary>
public class LsArgScanTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public LsArgScanTests(SharedPwshFixture fixture) => _fixture = fixture;

    private static string Ids(params string[] argv)
    {
        var p = InvokeBashLsCommand.ScanArgs(argv);
        Assert.Null(p.Error);
        return string.Join(",", p.Tokens.Select(t => t.Kind == ArgTokKind.Option ? t.OptId + (t.Value is null ? "" : "=" + t.Value)
            : t.Kind == ArgTokKind.DoubleDash ? "--" : "'" + t.Raw));
    }

    [Theory]
    [InlineData("-la", "long,all")]
    [InlineData("-lhR", "long,human,recursive")]
    [InlineData("-1l", "one,long")]
    [InlineData("--all", "all")]
    [InlineData("--alm", "almost-all")]             // unique prefix
    [InlineData("--rec", "recursive")]
    [InlineData("--rev", "reverse")]
    [InlineData("--human-readable", "human")]
    [InlineData("--hu", "human")]
    [InlineData("--col", "color")]
    [InlineData("--color=never", "color=never")]
    [InlineData("--classify", "classify")]
    [InlineData("--classify=always", "classify=always")]
    [InlineData("-F", "classify-short")]
    [InlineData("--sort=size", "sort=size")]
    [InlineData("--size", "blocks")]
    [InlineData("--group", "group-dirs-first")]
    [InlineData("a|-l|b", "'a,long,'b")]             // options after operands
    [InlineData("-l|--|-x", "long,--,'-x")]           // nothing after -- is an option
    [InlineData("-", "'-")]
    [InlineData("-Cx", "vertical,across")]
    [InlineData("-m", "commas")]
    [InlineData("-I|*.c", "ignore=*.c")]
    [InlineData("-I*.c", "ignore=*.c")]
    [InlineData("--ignore=*.c", "ignore=*.c")]
    [InlineData("--ignore|*.c", "ignore=*.c")]
    [InlineData("--hide=*.c", "hide=*.c")]
    [InlineData("--hide|x", "hide=x")]
    [InlineData("-B", "ignore-backups")]
    [InlineData("--ignore-backups", "ignore-backups")]
    [InlineData("--ignore-b", "ignore-backups")]
    [InlineData("-w|40", "width=40")]
    [InlineData("-w40", "width=40")]
    [InlineData("--width=40", "width=40")]
    [InlineData("--wid=3", "width=3")]              // unique prefix of --width
    [InlineData("-T|4", "tabsize=4")]
    [InlineData("--format=across", "format=across")]
    public void Scan_AcceptsImplementedOptions(string joined, string expected)
        => Assert.Equal(expected, Ids(joined.Split('|')));

    [Theory]
    [InlineData("-Y", ArgErrorKind.Unrecognized, 2)]          // ls: invalid option -- 'Y'
    [InlineData("--nope", ArgErrorKind.Unrecognized, 2)]
    [InlineData("--c", ArgErrorKind.Ambiguous, 2)]            // '--classify' '--color' '--context'
    [InlineData("--a", ArgErrorKind.Ambiguous, 2)]
    [InlineData("--dir", ArgErrorKind.Ambiguous, 2)]          // '--directory' '--dired'
    [InlineData("--sort", ArgErrorKind.MissingValue, 2)]
    [InlineData("--all=x", ArgErrorKind.UnexpectedValue, 2)]
    [InlineData("--ign=x", ArgErrorKind.Ambiguous, 2)]        // '--ignore-backups' '--ignore'
    [InlineData("--hi=x", ArgErrorKind.Ambiguous, 2)]         // '--hide-control-chars' '--hide' '--hyperlink'
    [InlineData("-I", ArgErrorKind.MissingValue, 2)]          // ls: option requires an argument -- 'I'
    [InlineData("-w", ArgErrorKind.MissingValue, 2)]          // ls: option requires an argument -- 'w'
    [InlineData("-T", ArgErrorKind.MissingValue, 2)]
    [InlineData("--format", ArgErrorKind.MissingValue, 2)]
    [InlineData("-Q", ArgErrorKind.ValidButUnsupported, 2)]   // quoting
    [InlineData("--zero", ArgErrorKind.ValidButUnsupported, 2)]
    [InlineData("-lQ", ArgErrorKind.ValidButUnsupported, 2)]  // inside a bundle
    public void Scan_Errors(string argv, ArgErrorKind kind, int exit)
    {
        var p = InvokeBashLsCommand.ScanArgs(argv.Split('|'));
        Assert.Equal(kind, p.Error!.Value.Kind);
        Assert.Equal(exit, p.ErrorExitCode);
    }

    [Fact]
    public void Scan_AmbiguityListsCandidatesInGnuTableOrder()
    {
        // oracle: ls: option '--a' is ambiguous; possibilities: '--all' '--almost-all' '--author'
        var e = InvokeBashLsCommand.ScanArgs(new[] { "--a" }).Error!.Value.Message("ls");
        Assert.Contains("'--all'", e);
        Assert.True(e.IndexOf("'--all'") < e.IndexOf("'--almost-all'") && e.IndexOf("'--almost-all'") < e.IndexOf("'--author'"), e);
        var d = InvokeBashLsCommand.ScanArgs(new[] { "--d" }).Error!.Value.Message("ls");
        Assert.True(d.IndexOf("'--directory'") < d.IndexOf("'--dired'") && d.IndexOf("'--dired'") < d.IndexOf("'--dereference'"), d);
    }

    // ---- end to end: what actually prints ----

    private string[] Names(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private string MakeDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "psb-ls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "small"), "x");                 // 1 byte, oldest
        File.WriteAllText(Path.Combine(d, "big"), new string('y', 500));   // biggest, middle
        File.WriteAllText(Path.Combine(d, "mid"), new string('z', 50));    // newest
        File.SetLastWriteTime(Path.Combine(d, "small"), DateTime.Now.AddHours(-3));
        File.SetLastWriteTime(Path.Combine(d, "big"), DateTime.Now.AddHours(-2));
        File.SetLastWriteTime(Path.Combine(d, "mid"), DateTime.Now.AddHours(-1));
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        return d;
    }

    [Fact]
    public void LastSortOptionWins()
    {
        var d = MakeDir();
        try
        {
            // oracle (ls -tS / ls -St): the later of -t / -S decides.
            Assert.Equal(new[] { "sub", "big", "mid", "small" }, Names($"Invoke-BashLs '-t' '-S' '{d}'").Where(n => n != "sub").Prepend("sub").ToArray());
            Assert.Equal("big", Names($"Invoke-BashLs '-t' '-S' '{d}'").First(n => n != "sub"));
            Assert.Equal("mid", Names($"Invoke-BashLs '-S' '-t' '{d}'").First(n => n != "sub"));
            Assert.Equal("big", Names($"Invoke-BashLs '-tS' '{d}'").First(n => n != "sub"));
            Assert.Equal("mid", Names($"Invoke-BashLs '-St' '{d}'").First(n => n != "sub"));
            Assert.Equal("big", Names($"Invoke-BashLs '--sort=size' '{d}'").First(n => n != "sub"));
            Assert.Equal("mid", Names($"Invoke-BashLs '--sort=time' '{d}'").First(n => n != "sub"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void LastIndicatorWins_AndClassifyWhen()
    {
        var d = MakeDir();
        try
        {
            Assert.Contains("sub/", Names($"Invoke-BashLs '-pF' '{d}'"));
            Assert.Contains("sub/", Names($"Invoke-BashLs '--classify' '{d}'"));
            Assert.Contains("sub", Names($"Invoke-BashLs '--classify=never' '{d}'"));
            Assert.DoesNotContain("sub/", Names($"Invoke-BashLs '-F' '--classify=never' '{d}'"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void DashOneNeverCancelsLong()
    {
        var d = MakeDir();
        try
        {
            // oracle: ls -l1 and ls -1l both print the long format.
            Assert.All(Names($"Invoke-BashLs '-l1' '{d}'").Where(l => !l.StartsWith("total ")), l => Assert.Matches(@"^[-d][rwx-]{9} ", l));
            Assert.All(Names($"Invoke-BashLs '-1l' '{d}'").Where(l => !l.StartsWith("total ")), l => Assert.Matches(@"^[-d][rwx-]{9} ", l));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void LongOptionSpellings_ActLikeShort()
    {
        var d = MakeDir();
        try
        {
            File.WriteAllText(Path.Combine(d, ".hidden"), "");
            Assert.Contains(".hidden", Names($"Invoke-BashLs '--all' '{d}'"));
            Assert.DoesNotContain(".hidden", Names($"Invoke-BashLs '{d}'"));
            Assert.Equal(Names($"Invoke-BashLs '-r' '{d}'"), Names($"Invoke-BashLs '--reverse' '{d}'"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Theory]
    [InlineData("'-Y'", 2, "invalid option -- 'Y'")]
    [InlineData("'--nope'", 2, "unrecognized option '--nope'")]
    [InlineData("'-Q'", 2, "not supported by ps-bash")]
    [InlineData("'--format=bogus'", 1, "invalid argument 'bogus' for '--format'")]
    [InlineData("'-w' 'x'", 2, "invalid line width: 'x'")]
    [InlineData("'-w' '-3'", 2, "invalid line width: '-3'")]
    [InlineData("'-T' 'x'", 2, "invalid tab size: 'x'")]
    [InlineData("'--color=bogus'", 1, "invalid argument 'bogus' for '--color'")]
    [InlineData("'--classify=bogus'", 1, "invalid argument 'bogus' for '--classify'")]
    [InlineData("'--sort=bogus'", 1, "invalid argument 'bogus' for '--sort'")]
    [InlineData("'--sort=none'", 2, "not supported by ps-bash")]
    public void UsageErrors_ExitLikeGnu(string args, int exit, string stderr)
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashLs " + args).AssertFailed(exit, stderr);

    [Fact]
    public void DashDash_EndsOptions_OperandIsAFileName()
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashLs '--' '-x-no-such'").AssertFailed(2, "cannot access");

    [Fact]
    public void DirectCall_BareDecoys_StillBind()
    {
        // `-a` / `-d` / `-p` / `-i` prefix-collide with parameters on a direct PowerShell call.
        var d = MakeDir();
        try
        {
            File.WriteAllText(Path.Combine(d, ".hidden"), "");
            Assert.Contains(".hidden", Names($"Invoke-BashLs -a '{d}'"));
            Assert.Contains("sub/", Names($"Invoke-BashLs -p '{d}'"));
            Assert.Single(Names($"Invoke-BashLs -d '{d}'"));
            Assert.DoesNotContain(".hidden", Names($"Invoke-BashLs -i '{d}'"));
        }
        finally { Directory.Delete(d, true); }
    }
}
