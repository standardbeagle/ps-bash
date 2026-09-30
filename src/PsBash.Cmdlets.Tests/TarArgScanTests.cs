using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for tar (shared ordered parser), checked against GNU tar 1.35
/// (`wsl bash`): option PARSE errors exit 64 (EX_USAGE), semantic errors (no mode, two modes, empty
/// archive, bad --strip-components) exit 2; abbreviations list candidates in argp table order;
/// the first argument may be an old-style option word without a dash (`tar czf a.tgz d`), whose
/// value options (f, C, ...) consume the following arguments in order; -C is positional on create
/// (`tar cf a.tar -C d f` archives d/f as `f`). ps-bash refuses the rest of tar (other modes, the
/// bzip2/xz/lzma/zstd filters, incremental/multi-volume/xattr/ownership/... families) with exit 2.
/// </summary>
public class TarArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashTarCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("tar");
        if (p.Error is { } m) return "ERR " + m;
        string src = string.Join(",", p.Sources.Select(s => (s.Dir is null ? "" : "(" + s.Dir.Replace('\\', '/') + ")") + s.Path));
        return $"mode={p.Mode} z={p.Gzip} v={p.Verbose} k={p.Keep} a={p.AutoCompress} O={p.ToStdout} f={p.ArchiveFile ?? "-"} C={(p.ChangeDir ?? "-").Replace('\\', '/')} strip={p.StripComponents} ex=[{string.Join(",", p.Excludes)}] src=[{src}]";
    }

    [Theory]
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[d]", "-c", "-f", "a.tar", "d")]
    [InlineData("mode=create z=True v=False k=False a=False O=False f=a.tgz C=- strip=0 ex=[] src=[d]", "-czf", "a.tgz", "d")]
    [InlineData("mode=create z=True v=True k=False a=False O=False f=a.tgz C=- strip=0 ex=[] src=[d]", "-cvzfa.tgz", "d")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "-xf", "a.tar")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "--extract", "--file=a.tar")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "--get", "--file", "a.tar")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "--ext", "--file=a.tar")]  // FIX (abbreviation; --fil is ambiguous: file, files-from)
    [InlineData("mode=list z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "-tf", "a.tar")]
    [InlineData("mode=list z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "--list", "-f", "a.tar")]
    [InlineData("mode=extract z=False v=False k=True a=False O=True f=a.tar C=- strip=0 ex=[] src=[]", "-xkOf", "a.tar")]
    [InlineData("mode=create z=False v=False k=False a=True O=False f=a.tar.gz C=- strip=0 ex=[] src=[d]", "-caf", "a.tar.gz", "d")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=out strip=0 ex=[] src=[]", "-xf", "a.tar", "-C", "out")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=out strip=0 ex=[] src=[]", "-xf", "a.tar", "--directory=out")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=out strip=0 ex=[] src=[]", "-xf", "a.tar", "--dir", "out")]  // FIX
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=out strip=0 ex=[] src=[]", "-xf", "a.tar", "-Cout")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=a/b strip=0 ex=[] src=[]", "-xf", "a.tar", "-C", "a", "-C", "b")]  // -C chains
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=/abs strip=0 ex=[] src=[]", "-xf", "a.tar", "-C", "a", "-C", "/abs")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=2 ex=[] src=[]", "-xf", "a.tar", "--strip-components=2")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=2 ex=[] src=[]", "-xf", "a.tar", "--strip-components", "2")]
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=1 ex=[] src=[]", "-xf", "a.tar", "--strip=1")]  // FIX (abbreviation)
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[*.o,x] src=[d]", "-cf", "a.tar", "--exclude=*.o", "--exclude", "x", "d")]
    // -C is positional on create: GNU tar archives d/f as `f`
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=d strip=0 ex=[] src=[(d)f]", "-cf", "a.tar", "-C", "d", "f")]  // FIX (was: -C ignored on create)
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=d strip=0 ex=[] src=[x,(d)f]", "-cf", "a.tar", "x", "-C", "d", "f")]
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=d/e strip=0 ex=[] src=[(d)f,(d/e)g]", "-cf", "a.tar", "-C", "d", "f", "-C", "e", "g")]
    // old-style first word
    [InlineData("mode=create z=True v=False k=False a=False O=False f=b.tgz C=- strip=0 ex=[] src=[d]", "czf", "b.tgz", "d")]
    [InlineData("mode=extract z=False v=True k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "xvf", "a.tar")]
    [InlineData("mode=list z=False v=False k=False a=False O=False f=b.tar C=- strip=0 ex=[] src=[]", "tf", "b.tar")]
    [InlineData("mode=create z=False v=False k=False a=False O=False f=b3.tar C=d strip=0 ex=[] src=[(d)f]", "cf", "b3.tar", "-C", "d", "f")]
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=d strip=0 ex=[] src=[(d)f]", "cfC", "a.tar", "d", "f")]  // f and C consume the next two words in order
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "xf", "a.tar")]
    [InlineData("mode=create z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[-x]", "cf", "a.tar", "--", "-x")]
    // accepted no-ops
    [InlineData("mode=extract z=False v=False k=False a=False O=False f=a.tar C=- strip=0 ex=[] src=[]", "-xmpf", "a.tar", "--overwrite", "--wildcards", "--no-same-owner", "--touch")]
    // GNU semantic errors (exit 2)
    [InlineData("ERR tar: You must specify one of the '-Acdtrux', '--delete' or '--test-label' options")]
    [InlineData("ERR tar: You must specify one of the '-Acdtrux', '--delete' or '--test-label' options", "-z")]
    [InlineData("ERR tar: You must specify one of the '-Acdtrux', '--delete' or '--test-label' options", "-f", "a.tar")]
    [InlineData("ERR tar: You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option", "-cx", "-f", "a.tar", "d")]
    [InlineData("ERR tar: You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option", "--create", "--extract", "-f", "a.tar")]
    [InlineData("ERR tar: x: Invalid number of elements", "--strip-components=x", "-xf", "b.tar")]
    [InlineData("ERR tar: -1: Invalid number of elements", "--strip-components=-1", "-xf", "b.tar")]
    [InlineData("ERR tar: -xf: Invalid number of elements", "--strip-components", "-xf", "b.tar")]
    // parse errors (exit 64)
    [InlineData("ERR tar: unrecognized option '--zzz'", "--zzz")]
    [InlineData("ERR tar: invalid option -- 'e'", "-e")]
    [InlineData("ERR tar: invalid option -- 'q'", "-q")]
    [InlineData("ERR tar: invalid option -- 'E'", "-cE")]
    [InlineData("ERR tar: option requires an argument -- 'f'", "-c", "-f")]  // FIX (was: "you must specify -f")
    [InlineData("ERR tar: option requires an argument -- 'C'", "-C")]
    [InlineData("ERR tar: option '--directory' requires an argument", "--dir")]
    [InlineData("ERR tar: option '--exclude' requires an argument", "--exclude")]
    [InlineData("ERR tar: option requires an argument -- 'f'", "cf")]  // old-style f with no word left
    [InlineData("ERR tar: option '--st' is ambiguous; possibilities: '--starting-file' '--strip-components'", "--st")]
    [InlineData("ERR tar: option '--s' is ambiguous; possibilities: '--sparse' '--sparse-version' '--seek' '--skip-old-files' '--same-owner' '--same-permissions' '--same-order' '--sort' '--selinux' '--starting-file' '--suffix' '--strip-components' '--show-defaults' '--show-snapshot-field-ranges' '--show-omitted-dirs' '--show-transformed-names' '--show-stored-names'", "--s")]
    [InlineData("ERR tar: option '--ex' is ambiguous; possibilities: '--extract' '--exclude' '--exclude-from' '--exclude-caches' '--exclude-caches-under' '--exclude-caches-all' '--exclude-tag' '--exclude-ignore' '--exclude-ignore-recursive' '--exclude-tag-under' '--exclude-tag-all' '--exclude-vcs' '--exclude-vcs-ignores' '--exclude-backups'", "--ex")]
    [InlineData("ERR tar: option '--create' doesn't allow an argument", "--create=1")]
    // valid GNU tar options ps-bash refuses (exit 2)
    [InlineData("ERR tar: option '-j' is recognized but not supported by ps-bash", "-cjf", "a.tbz", "d")]
    [InlineData("ERR tar: option '-J' is recognized but not supported by ps-bash", "-cJf", "a.txz", "d")]
    [InlineData("ERR tar: option '-Z' is recognized but not supported by ps-bash", "-cZf", "a.Z", "d")]
    [InlineData("ERR tar: option '--bzip2' is recognized but not supported by ps-bash", "-xf", "a.tar", "--bzip2")]
    [InlineData("ERR tar: option '--zstd' is recognized but not supported by ps-bash", "-xf", "a.tar", "--zstd")]
    [InlineData("ERR tar: option '--lzma' is recognized but not supported by ps-bash", "--lzma")]
    [InlineData("ERR tar: option '-r' is recognized but not supported by ps-bash", "-rf", "a.tar", "x")]
    [InlineData("ERR tar: option '-u' is recognized but not supported by ps-bash", "-uf", "a.tar", "x")]
    [InlineData("ERR tar: option '-A' is recognized but not supported by ps-bash", "-A")]
    [InlineData("ERR tar: option '-d' is recognized but not supported by ps-bash", "-d")]
    [InlineData("ERR tar: option '--delete' is recognized but not supported by ps-bash", "--delete")]
    [InlineData("ERR tar: option '-h' is recognized but not supported by ps-bash", "-chf", "a.tar", "d")]
    [InlineData("ERR tar: option '-T' is recognized but not supported by ps-bash", "-cf", "a.tar", "-T", "list")]
    [InlineData("ERR tar: option '-X' is recognized but not supported by ps-bash", "-cf", "a.tar", "-X", "list", "d")]
    [InlineData("ERR tar: option '-P' is recognized but not supported by ps-bash", "-cPf", "a.tar", "d")]
    [InlineData("ERR tar: option '-s' is recognized but not supported by ps-bash", "-s")]
    [InlineData("ERR tar: option '--transform' is recognized but not supported by ps-bash", "--transform=s/a/b/", "-cf", "a.tar", "d")]
    [InlineData("ERR tar: option '--one-file-system' is recognized but not supported by ps-bash", "--one-file-system")]
    [InlineData("ERR tar: option '--use-compress-program' is recognized but not supported by ps-bash", "--use-compress-program=gzip")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_ParseErrors64_Unsupported2()
    {
        Assert.Equal(64, InvokeBashTarCommand.ScanArgs(new[] { "--zzz" }).ErrorExitCode);
        Assert.Equal(64, InvokeBashTarCommand.ScanArgs(new[] { "-f" }).ErrorExitCode);
        Assert.Equal(64, InvokeBashTarCommand.ScanArgs(new[] { "--s" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashTarCommand.ScanArgs(new[] { "-j" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashTarCommand.ScanArgs(new[] { "--bzip2" }).ErrorExitCode);
        Assert.True(InvokeBashTarCommand.ScanArgs(new[] { "--version" }).Has("version"));
        Assert.True(InvokeBashTarCommand.ScanArgs(new[] { "--help" }).Has("help"));
    }

    [Fact]
    public void NormalizeOldStyle_LeavesDashedAndEmptyArgvAlone_AndIsIdempotent()
    {
        Assert.Empty(InvokeBashTarCommand.NormalizeOldStyle(Array.Empty<string>()));
        Assert.Equal(new[] { "-cf", "a" }, InvokeBashTarCommand.NormalizeOldStyle(new[] { "-cf", "a" }));
        var once = InvokeBashTarCommand.NormalizeOldStyle(new[] { "czf", "a.tgz", "d" });
        Assert.Equal(new[] { "-c", "-z", "-f", "a.tgz", "d" }, once);
        Assert.Equal(once, InvokeBashTarCommand.NormalizeOldStyle(once));
    }
}
