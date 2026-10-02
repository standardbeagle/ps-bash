using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Backup type / suffix / numbering shared by cp, mv and ln. Every row was read from GNU coreutils 9.4.</summary>
public class BackupControlTests
{
    private static string? Env(string? versionControl = null, string? suffix = null, string name = "") =>
        name switch { "VERSION_CONTROL" => versionControl, "SIMPLE_BACKUP_SUFFIX" => suffix, _ => null };

    private static (bool Ok, BackupKind Kind, string? Error) Resolve(string? control, string? versionControl = null)
    {
        var ok = BackupControl.TryResolve("cp", control, n => Env(versionControl, null, n), out var kind, out var error);
        return (ok, kind, error);
    }

    [Theory]
    [InlineData("none", BackupKind.None)]
    [InlineData("off", BackupKind.None)]
    [InlineData("simple", BackupKind.Simple)]
    [InlineData("never", BackupKind.Simple)]
    [InlineData("existing", BackupKind.Existing)]
    [InlineData("nil", BackupKind.Existing)]
    [InlineData("numbered", BackupKind.Numbered)]
    [InlineData("t", BackupKind.Numbered)]
    [InlineData("no", BackupKind.None)]       // unique prefix of "none"
    [InlineData("nu", BackupKind.Numbered)]
    [InlineData("sim", BackupKind.Simple)]
    [InlineData("ex", BackupKind.Existing)]
    public void ControlWord_MatchesLikeXargmatch(string word, BackupKind expected)
    {
        var r = Resolve(word);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expected, r.Kind);
    }

    [Theory]
    [InlineData("bogus", "invalid argument 'bogus' for 'backup type'")]
    [InlineData("n", "ambiguous argument 'n' for 'backup type'")]   // none / nil / numbered
    public void BadControlWord_IsAUsageErrorListingTheValidWords(string word, string fragment)
    {
        var r = Resolve(word);
        Assert.False(r.Ok);
        Assert.Contains(fragment, r.Error);
        Assert.Contains("'none', 'off'", r.Error);
        Assert.Contains("'numbered', 't'", r.Error);
        Assert.EndsWith("Try 'cp --help' for more information.", r.Error);
    }

    [Fact]
    public void NoWord_UsesTheEnvironment_ThenExisting()
    {
        Assert.Equal(BackupKind.Existing, Resolve(null).Kind);
        Assert.Equal(BackupKind.Existing, Resolve("").Kind);   // `--backup=` means "no word"
        Assert.Equal(BackupKind.Numbered, Resolve(null, "numbered").Kind);
        Assert.Equal(BackupKind.None, Resolve(null, "none").Kind);
        Assert.Equal(BackupKind.Simple, Resolve("simple", "numbered").Kind);   // an explicit word wins
    }

    [Fact]
    public void BadEnvironment_IsNamedAsSuch()
    {
        var r = Resolve(null, "bogus");
        Assert.False(r.Ok);
        Assert.Contains("invalid argument 'bogus' for '$VERSION_CONTROL'", r.Error);
    }

    [Fact]
    public void Suffix_OptionBeatsEnvironmentBeatsTilde()
    {
        Assert.Equal(".bak", BackupControl.Suffix(".bak", n => n == "SIMPLE_BACKUP_SUFFIX" ? ".s" : null));
        Assert.Equal(".s", BackupControl.Suffix(null, n => n == "SIMPLE_BACKUP_SUFFIX" ? ".s" : null));
        Assert.Equal("~", BackupControl.Suffix(null, _ => null));
        Assert.Equal("~", BackupControl.Suffix("", _ => null));
    }

    private static IEnumerable<string> Dir(params string[] names) => names;

    [Theory]
    [InlineData(BackupKind.None, null)]
    [InlineData(BackupKind.Simple, "~")]
    [InlineData(BackupKind.Numbered, ".~1~")]
    [InlineData(BackupKind.Existing, "~")]            // no numbered backup yet: simple
    public void SuffixFor_WithNoPriorBackups(BackupKind kind, string? expected)
    {
        Assert.Equal(expected, BackupControl.SuffixFor("d/b", kind, "~", _ => Dir("b", "other")));
    }

    [Theory]
    [InlineData(BackupKind.Numbered, ".~4~")]
    [InlineData(BackupKind.Existing, ".~4~")]         // a numbered backup exists: keep numbering
    [InlineData(BackupKind.Simple, "~")]
    public void SuffixFor_NumberingFollowsTheHighestExisting(BackupKind kind, string expected)
    {
        Assert.Equal(expected, BackupControl.SuffixFor("d/b", kind, "~", _ => Dir("b", "b.~1~", "b.~3~", "b~")));
    }

    [Fact]
    public void HighestNumber_IgnoresLookalikes()
    {
        var names = Dir("b.~9~x", "b.~~", "b.~a~", "bb.~7~", "b.~12~", "b.~2~", "B.~99~");
        Assert.Equal(12, BackupControl.HighestNumber("d/b", _ => names));
    }

    [Fact]
    public void MakeBackup_RenamesToTheChosenName()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"psb-bk-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "b");
            File.WriteAllText(f, "one");
            Assert.Equal("~", BackupControl.MakeBackup(f, BackupKind.Simple, "~"));
            Assert.False(File.Exists(f));
            Assert.Equal("one", File.ReadAllText(f + "~"));

            File.WriteAllText(f, "two");
            Assert.Equal(".~1~", BackupControl.MakeBackup(f, BackupKind.Numbered, "~"));
            File.WriteAllText(f, "three");
            Assert.Equal(".~2~", BackupControl.MakeBackup(f, BackupKind.Existing, "~"));
            Assert.Equal("three", File.ReadAllText(f + ".~2~"));

            File.WriteAllText(f, "four");
            Assert.Null(BackupControl.MakeBackup(f, BackupKind.None, "~"));
            Assert.True(File.Exists(f));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
