using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// chmod-style mode strings (<c>mkdir -m</c>): octal and symbolic, compiled against mkdir's base
/// mode 0777 and the process umask. Every expected value was produced by real GNU coreutils 9.4
/// (<c>umask 022; mkdir -m SPEC d; stat -c %a d</c> under <c>wsl bash</c>). The mode a directory
/// ENDS UP with (<see cref="FileModeSpec.ResultingDirMode"/>) follows mkdir.c + gnulib dirchownmod: a sticky-only
/// change (<c>+t</c>) gives 1755 and <c>g-s</c> 755 because the directory is created without group/other write and
/// the chmod is skipped when no MENTIONED bit differs (see <c>FinalMode</c> table).
/// </summary>
public class FileModeSpecTests
{
    private const int Umask022 = 0x12; // 0022

    private static int Mode(string spec, int umask = Umask022)
    {
        Assert.True(FileModeSpec.TryParse(spec, isDirectory: true, umask, baseMode: 0x1FF, out var mode), spec);
        return mode;
    }

    [Theory]
    [InlineData("700", "700")]
    [InlineData("0755", "755")]
    [InlineData("755", "755")]
    [InlineData("7777", "7777")]
    [InlineData("1777", "1777")]
    [InlineData("02755", "2755")]
    [InlineData("4755", "4755")]
    [InlineData("2777", "2777")]
    [InlineData("0", "0")]
    [InlineData("000", "0")]
    [InlineData("00", "0")]
    [InlineData("0777", "777")]
    // symbolic, umask 022, base 0777
    [InlineData("u=rwx,go=rx", "755")]
    [InlineData("a=rx", "555")]
    [InlineData("u+w,g-r", "737")]
    [InlineData("+x", "777")]
    [InlineData("-x", "666")]
    [InlineData("+w", "777")]
    [InlineData("a-w", "555")]
    [InlineData("=rx", "555")]
    [InlineData("=x", "111")]
    [InlineData("=r,u+w", "644")]
    [InlineData("a=r,u+w", "644")]
    [InlineData("go-rwx", "700")]
    [InlineData("go+w", "777")]
    [InlineData("u=rwx,g=u,o=g", "777")]
    [InlineData("u=r,g=u", "447")]
    [InlineData("g=u", "777")]
    [InlineData("u=", "77")]
    [InlineData("ug=rw", "667")]
    [InlineData("o=rwx", "777")]
    [InlineData("ugo=x", "111")]
    [InlineData("a=rwx,g-w", "757")]
    [InlineData("u+w,u-w", "577")]
    [InlineData("u=rX", "577")]
    [InlineData("a=rX", "555")]
    [InlineData("u+X", "777")]
    [InlineData("g+s", "2777")]
    [InlineData("u+s", "4777")]
    [InlineData("u=rwxs,g=rx,o=", "4750")]
    public void TryParse_MatchesGnuMkdirUnderUmask022(string spec, string expectedOctal)
    {
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Mode(spec));
    }

    // The umask only shapes clauses with NO who (gnulib mode_adjust: value &= who ? affected : ~umask).
    [Theory]
    [InlineData("+x", 0x3F /*077*/, "777")]
    [InlineData("-x", 0x3F, "677")]
    [InlineData("-w", 0x3F, "577")]
    [InlineData("-w", 0x12, "577")]   // 0222 & ~022 = 0200: only the owner's bit is removed
    [InlineData("-w", 0x02, "557")]   // umask 002
    [InlineData("-w", 0x00, "555")]
    [InlineData("=rx", 0x3F, "500")]
    [InlineData("=rwx", 0x3F, "700")]
    [InlineData("=rwx", 0x02, "775")]
    [InlineData("=rwx", 0x00, "777")]
    [InlineData("a=rx", 0x3F, "555")]   // an explicit who ignores the umask
    [InlineData("o-r", 0x3F, "773")]
    public void TryParse_UmaskAppliesOnlyToWholessClauses(string spec, int umask, string expectedOctal)
    {
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Mode(spec, umask));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("rwx")]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("08")]
    [InlineData("7778")]
    [InlineData("10000")]
    [InlineData("00800")]
    [InlineData("12x")]
    [InlineData("u=q")]
    [InlineData("a=rwxq")]
    [InlineData("x+u")]
    [InlineData("+,")]
    [InlineData("u+w,")]
    [InlineData(",u+w")]
    [InlineData("u=ug")]
    [InlineData("u=rw,")]
    public void TryParse_RejectsWhatGnuCallsAnInvalidMode(string spec)
    {
        Assert.False(FileModeSpec.TryParse(spec, isDirectory: true, Umask022, baseMode: 0x1FF, out _));
    }

    [Fact]
    public void TryParse_AnEmptyPermissionListIsAccepted_AsInGnu()
    {
        // `mkdir -m ug+ d` exits 0 in GNU: an empty permission list changes nothing.
        Assert.True(FileModeSpec.TryParse("ug+", isDirectory: true, Umask022, baseMode: 0x1FF, out _));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("555", false)]
    [InlineData("400", false)]
    [InlineData("200", true)]
    [InlineData("755", true)]
    [InlineData("7777", true)]
    public void OwnerCanWrite_IsTheOwnerWriteBitOnly(string octal, bool expected)
    {
        Assert.Equal(expected, FileModeSpec.OwnerCanWrite(Convert.ToInt32(octal, 8)));
    }

    private static int Final(string spec, int umask = Umask022)
    {
        Assert.True(FileModeSpec.TryParse(spec, isDirectory: true, umask, baseMode: 0x1FF, out var mode, out var mentioned), spec);
        return FileModeSpec.ResultingDirMode(mode, mentioned, umask);
    }

    // Oracle: `mkdir -m SPEC q; stat -c %a q` (coreutils 9.4). Identical under umask 0, 022 and 077 for every row
    // here except the explicit umask rows below.
    [Theory]
    [InlineData("+t", "1755")]
    [InlineData("a+t", "1755")]
    [InlineData("o+t", "1755")]      // 'o' covers the sticky bit (gnulib: o -> S_ISVTX | S_IRWXO)
    [InlineData("g-s", "755")]
    [InlineData("u-s", "755")]
    [InlineData("a-s", "755")]
    [InlineData("-s", "755")]
    [InlineData("-st", "755")]
    [InlineData("u-s,o-t", "755")]
    [InlineData("g-s,o+t", "1755")]
    [InlineData("u=rwx,+t", "1755")]
    [InlineData("+t,u-w", "1555")]
    [InlineData("a-w,+t", "1555")]
    [InlineData("+s", "6777")]
    [InlineData("u+s", "4777")]
    [InlineData("g+s", "2777")]
    [InlineData("ug+s", "6777")]
    [InlineData("u=rwx,g+s", "2777")]
    [InlineData("g+s,u-w", "2577")]
    [InlineData("+s,g-w", "6757")]
    [InlineData("u+s,-t", "4777")]
    [InlineData("-t", "777")]        // no special bit is 'mentioned' -> plain chmod to 777
    [InlineData("o-t", "777")]
    [InlineData("g-t", "777")]
    [InlineData("u+t", "777")]
    [InlineData("o+s", "777")]
    [InlineData("u+x", "777")]
    [InlineData("+w", "777")]
    [InlineData("1777", "1777")]
    [InlineData("777", "777")]
    [InlineData("755", "755")]
    [InlineData("a=s", "6000")]
    [InlineData("=t", "1000")]
    [InlineData("a=t,u+rwx", "1700")]
    public void FinalMode_MatchesGnuMkdir(string spec, string expectedOctal)
    {
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Final(spec));
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Final(spec, 0));
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Final(spec, 0x3F));
    }

    [Theory]
    [InlineData("=t,+x", 0x12, "1111")]
    [InlineData("=t,+x", 0x3F, "1100")]
    public void FinalMode_UmaskStillShapesWholessClauses(string spec, int umask, string expectedOctal)
    {
        Assert.Equal(Convert.ToInt32(expectedOctal, 8), Final(spec, umask));
    }}
