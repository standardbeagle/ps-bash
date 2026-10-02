using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>cp option resolution (command-line order, GNU coreutils 9.4 rules). Pure: no filesystem.</summary>
public class CpPlanTests
{
    private static CpPlan Plan(params string[] argv)
    {
        var parsed = InvokeBashCpCommand.ScanArgs(argv.Concat(new[] { "a", "b" }).ToArray());
        Assert.Null(parsed.Error);
        Assert.True(CpPlan.TryBuild(parsed, _ => null, out var plan, out var error), error);
        return plan;
    }

    private static string? Error(Func<string, string?>? env, params string[] argv)
    {
        var parsed = InvokeBashCpCommand.ScanArgs(argv.Concat(new[] { "a", "b" }).ToArray());
        Assert.Null(parsed.Error);
        Assert.False(CpPlan.TryBuild(parsed, env ?? (_ => null), out _, out var error));
        return error;
    }

    [Theory]
    [InlineData(CpExisting.Unspecified, "-f")]
    [InlineData(CpExisting.Ask, "-i")]
    [InlineData(CpExisting.Skip, "-n")]
    [InlineData(CpExisting.Skip, "-i", "-n")]                 // the last of -i/-n wins ...
    [InlineData(CpExisting.Ask, "-n", "-i")]
    [InlineData(CpExisting.Ask, "-f", "-i")]                  // ... and -f never cancels -i
    [InlineData(CpExisting.Skip, "--update=none")]
    [InlineData(CpExisting.Unspecified, "-n", "--update=all")]
    [InlineData(CpExisting.Skip, "--update=all", "--update=none")]
    [InlineData(CpExisting.Skip, "-n", "-u")]                  // -u only adds "newer"; it does not lift -n
    public void Existing_LastDecides(CpExisting expected, params string[] argv)
    {
        Assert.Equal(expected, Plan(argv).Existing);
    }

    [Theory]
    [InlineData(true, "-u")]
    [InlineData(true, "--update")]
    [InlineData(true, "--update=older")]
    [InlineData(true, "--update=ol")]
    [InlineData(false, "--update=all")]
    [InlineData(false, "-u", "--update=all")]
    [InlineData(false, "-u", "--update=none")]
    [InlineData(true, "--update=all", "-u")]
    public void UpdateOlder_FollowsTheUpdateWords(bool expected, params string[] argv)
    {
        Assert.Equal(expected, Plan(argv).UpdateOlder);
    }

    [Fact]
    public void NoClobberWarning_OnlyForTheShortAndLongNoClobber()
    {
        Assert.True(Plan("-n").NoClobberWarning);
        Assert.True(Plan("--no-clobber").NoClobberWarning);
        Assert.False(Plan("--update=none").NoClobberWarning);
        Assert.False(Plan("-i").NoClobberWarning);
    }

    [Theory]
    [InlineData("update", "bogus", "invalid argument 'bogus' for '--update'")]
    [InlineData("update", "", "ambiguous argument '' for '--update'")]
    [InlineData("update", "none-fail", "invalid argument 'none-fail' for '--update'")]   // not a word in 9.4
    [InlineData("reflink", "bogus", "invalid argument 'bogus' for '--reflink'")]
    [InlineData("sparse", "bogus", "invalid argument 'bogus' for '--sparse'")]
    public void BadWords_AreUsageErrorsListingTheValidOnes(string option, string word, string fragment)
    {
        var error = Error(null, $"--{option}={word}");
        Assert.Contains(fragment, error);
        Assert.Contains("Valid arguments are:", error);
        Assert.EndsWith("Try 'cp --help' for more information.", error);
    }

    [Theory]
    [InlineData(CpReflink.Auto)]
    [InlineData(CpReflink.Always, "--reflink")]
    [InlineData(CpReflink.Always, "--reflink=always")]
    [InlineData(CpReflink.Always, "--reflink=al")]
    [InlineData(CpReflink.Auto, "--reflink=auto")]
    [InlineData(CpReflink.Never, "--reflink=never")]
    [InlineData(CpReflink.Never, "--reflink=always", "--reflink=never")]
    public void Reflink_BareMeansAlways(CpReflink expected, params string[] argv)
    {
        Assert.Equal(expected, Plan(argv).Reflink);
    }

    [Theory]
    [InlineData(CpDeref.Default)]
    [InlineData(CpDeref.Always, "-L")]
    [InlineData(CpDeref.Always, "--dereference")]
    [InlineData(CpDeref.Never, "-P")]
    [InlineData(CpDeref.Never, "--no-dereference")]
    [InlineData(CpDeref.Never, "-d")]
    [InlineData(CpDeref.CommandLine, "-H")]
    [InlineData(CpDeref.Never, "-L", "-P")]
    [InlineData(CpDeref.Always, "-P", "-L")]
    [InlineData(CpDeref.Never, "-a")]
    [InlineData(CpDeref.Always, "-a", "-L")]
    public void Deref_LastWins(CpDeref expected, params string[] argv)
    {
        Assert.Equal(expected, Plan(argv).Deref);
    }

    [Fact]
    public void HardAndSymbolic_AreExclusive_ButEitherAlone_IsFine()
    {
        Assert.Equal(CpLink.Hard, Plan("-l").Link);
        Assert.Equal(CpLink.Symbolic, Plan("-s").Link);
        Assert.Equal(CpLink.Symbolic, Plan("--symbolic-link").Link);
        Assert.Contains("cannot make both hard and symbolic links", Error(null, "-l", "-s"));
    }

    [Fact]
    public void TargetAndNoTargetDirectory_Conflict()
    {
        Assert.Equal("cp: cannot combine --target-directory (-t) and --no-target-directory (-T)", Error(null, "-t", "d", "-T"));
    }

    [Fact]
    public void Backup_ShortSuffixAndLongForms()
    {
        Assert.False(Plan("-v").BackupEnabled);

        var b = Plan("-b");
        Assert.True(b.BackupEnabled);
        Assert.Equal(BackupKind.Existing, b.Backup);
        Assert.Equal("~", b.BackupSuffix);

        Assert.Equal(BackupKind.Numbered, Plan("--backup=numbered").Backup);
        Assert.Equal(BackupKind.None, Plan("-b", "--backup=off").Backup);

        var s = Plan("-S", ".bak");                          // -S alone turns backups on
        Assert.True(s.BackupEnabled);
        Assert.Equal(".bak", s.BackupSuffix);
        Assert.Equal(".bak", Plan("--suffix=.bak").BackupSuffix);
    }

    [Fact]
    public void Backup_ReadsTheEnvironment()
    {
        string? Env(string n) => n switch { "VERSION_CONTROL" => "numbered", "SIMPLE_BACKUP_SUFFIX" => ".env", _ => null };
        var parsed = InvokeBashCpCommand.ScanArgs(new[] { "-b", "a", "b" });
        Assert.True(CpPlan.TryBuild(parsed, Env, out var plan, out _));
        Assert.Equal(BackupKind.Numbered, plan.Backup);
        Assert.Equal(".env", plan.BackupSuffix);

        var bad = Error(n => n == "VERSION_CONTROL" ? "bogus" : null, "-b");
        Assert.Contains("for '$VERSION_CONTROL'", bad);
    }

    [Fact]
    public void Archive_IsRecursiveAndNeverFollowsLinks()
    {
        var p = Plan("-a");
        Assert.True(p.Recursive);
        Assert.Equal(CpDeref.Never, p.Deref);
    }

    [Fact]
    public void Flags_AreRecorded()
    {
        var p = Plan("-rfv", "--debug", "-x", "--parents", "--attributes-only", "--remove-destination", "--strip-trailing-slashes");
        Assert.True(p.Recursive && p.Force && p.Verbose && p.Debug && p.OneFileSystem && p.Parents
                    && p.AttributesOnly && p.RemoveDestination && p.StripSlashes);
        Assert.Equal("d", Plan("-t", "d").TargetDirectory);
        Assert.Equal("d", Plan("--target-directory=d").TargetDirectory);
        Assert.True(Plan("-T").NoTargetDirectory);
    }
}
