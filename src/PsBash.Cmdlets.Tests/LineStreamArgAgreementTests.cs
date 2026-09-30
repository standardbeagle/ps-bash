using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The fused lane's compiled cores do their OWN per-argv certification. Once a cmdlet parses its
/// argv with the shared ordered parser, a core must never accept an argv the cmdlet would reject
/// (bad flag, bad NUM, unsupported flag, --help/--version) or read differently. Each migrated
/// command's core now asks the cmdlet's own resolver first; this table is the guard: over a broad
/// argv corpus, "core certifies" implies "cmdlet accepts", and the certified subset stays
/// certified (so the fused lane does not silently regress to the batch fallback).
/// </summary>
public class LineStreamArgAgreementTests
{
    private static readonly string[][] Corpus =
    {
        Array.Empty<string>(),
        new[] { "-n", "5" }, new[] { "-n5" }, new[] { "-5" }, new[] { "-n", "0" }, new[] { "-n", "+2" },
        new[] { "-n", "-2" }, new[] { "-n", "abc" }, new[] { "-nabc" }, new[] { "-n", "1K" }, new[] { "-n" },
        new[] { "--lines=3" }, new[] { "--lines", "3" }, new[] { "--li=3" },
        new[] { "-c", "3" }, new[] { "-c3" }, new[] { "--bytes=3" },
        new[] { "-q" }, new[] { "-qn2" }, new[] { "--quiet" }, new[] { "--silent" },
        new[] { "-v" }, new[] { "-z" }, new[] { "--verbose" }, new[] { "--zero-terminated" },
        new[] { "--help" }, new[] { "--version" }, new[] { "--vers" }, new[] { "--he" },
        new[] { "--bogus" }, new[] { "-x" }, new[] { "--" }, new[] { "-" }, new[] { "f" }, new[] { "--", "-n" },
        new[] { "-n1", "-5" }, new[] { "-1", "-2" }, new[] { "5" }, new[] { "5", "f" },
        new[] { "-n", "3", "-n", "2" }, new[] { "-n", "3", "-c", "2" },
        new[] { "-f" }, new[] { "-F" }, new[] { "--follow" }, new[] { "--fo" }, new[] { "-s", "1" }, new[] { "+2" }, new[] { "-n", "+2" },
        new[] { "--retry" }, new[] { "--pid=1" }, new[] { "-nx" }, new[] { "-c", "+2" },
        new[] { "-l" }, new[] { "-lw" }, new[] { "-lwcmL" }, new[] { "--bytes" }, new[] { "--li" }, new[] { "--total=always" },
        new[] { "--files0-from=f" }, new[] { "-l", "f" }, new[] { "-lx" },
        new[] { "-", "-" }, new[] { "-n", "-" }, new[] { "-A" }, new[] { "--number" }, new[] { "-u" }, new[] { "-", "f" },
        new[] { "-s", "x" }, new[] { "-sx" }, new[] { "--separator=x" }, new[] { "-b" }, new[] { "-r" }, new[] { "--sep", "x" },
        new[] { "-ba" }, new[] { "-b", "a" }, new[] { "-bn" }, new[] { "-bx" }, new[] { "-bpfoo" }, new[] { "-nrz" }, new[] { "-n", "xx" },
        new[] { "-w", "3" }, new[] { "-w3" }, new[] { "-w0" }, new[] { "-v", "5" }, new[] { "-i", "2" }, new[] { "-h", "a" }, new[] { "-p" },
        new[] { "-s:" }, new[] { "--number-format=rz" },
        new[] { "-c" }, new[] { "-d" }, new[] { "-D" }, new[] { "-cD" }, new[] { "--count" }, new[] { "--all-repeated=x" }, new[] { "-z" }, new[] { "--group" },
        new[] { "-f", "1" }, new[] { "-f", "x" }, new[] { "-f1" }, new[] { "-5" }, new[] { "-w", "0" }, new[] { "-ic" }, new[] { "-cdui" },
        new[] { "-d:", "-f1" }, new[] { "-d", ":", "-f2-" }, new[] { "-c1-3" }, new[] { "-b", "1" }, new[] { "-f1", "-c1" }, new[] { "-s", "-f1" }, new[] { "-f0" }, new[] { "-f3-1" }, new[] { "--complement", "-f1" }, new[] { "--output-delimiter=x", "-f1" }, new[] { "-f1", "file" },
        new[] { "--skip-fields=1" }, new[] { "--check-chars=2" }, new[] { "--ignore-case" },
        // grep / sed (GNU getopt: patterns, scripts, bundles)
        new[] { "a" }, new[] { "-i", "a" }, new[] { "-e", "a" }, new[] { "-ea" }, new[] { "-ie", "a" }, new[] { "-e", "a", "-e", "b" },
        new[] { "-E", "a|b" }, new[] { "-E", "-F", "a" }, new[] { "-F", "-F", "a" }, new[] { "-G", "-E", "a" }, new[] { "-P", "a" },
        new[] { "-c", "a" }, new[] { "-vc", "a" }, new[] { "-cn", "a" }, new[] { "-1", "a" }, new[] { "-A1", "a" }, new[] { "-A", "x", "a" },
        new[] { "a", "f" }, new[] { "-m1", "a" }, new[] { "-m", "x", "a" }, new[] { "--color", "a" }, new[] { "--color=bogus", "a" },
        new[] { "--colo=always", "a" }, new[] { "-o", "a" }, new[] { "-x", "a" }, new[] { "-f", "p" }, new[] { "-y", "a" }, new[] { "--", "-a" },
        new[] { "-V" }, new[] { "--line-buffered", "a" }, new[] { "-s", "a" }, new[] { "-n", "-e", "a" }, new[] { "-w", "-F", "-e", "a" },
        new[] { "s/a/b/" }, new[] { "-n", "p" }, new[] { "-ne", "p" }, new[] { "-n", "-e", "p", "-e", "p" }, new[] { "-E", "s/(a)/\\1/" },
        new[] { "-r", "s/a/b/" }, new[] { "-i", "s/a/b/", "f" }, new[] { "-i.bak", "s/a/b/", "f" }, new[] { "-s", "p" }, new[] { "-z", "p" },
        new[] { "-u", "p" }, new[] { "--posix", "p" }, new[] { "--debug", "p" }, new[] { "--expression=p" }, new[] { "--regexp-extended", "p" },
        new[] { "-l", "5", "p" }, new[] { "-l" , "x", "p" }, new[] { "-nE", "p" }, new[] { "-nr", "p" }, new[] { "-nf", "s" },
    };

    private static bool CmdletAccepts(string name, string[] argv) => name switch
    {
        "head" => !InvokeBashHeadCommand.Plan(argv).Declined,
        "tail" => !InvokeBashTailCommand.Plan(argv).Declined,
        "wc" => !InvokeBashWcCommand.Plan(argv).Declined,
        "cat" => !InvokeBashCatCommand.Plan(argv).Declined,
        "tac" => !InvokeBashTacCommand.Plan(argv).Declined,
        "nl" => !InvokeBashNlCommand.Plan(argv).Declined,
        "uniq" => !InvokeBashUniqCommand.Plan(argv).Declined,
        "cut" => !InvokeBashCutCommand.Plan(argv).Declined,
        "grep" => !InvokeBashGrepCommand.Plan(argv).Declined,
        "sed" => !InvokeBashSedCommand.Plan(argv).Declined,
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Commands => new[] { new object[] { "head" }, new object[] { "tail" }, new object[] { "wc" }, new object[] { "cat" }, new object[] { "tac" }, new object[] { "nl" }, new object[] { "uniq" }, new object[] { "cut" }, new object[] { "grep" }, new object[] { "sed" } };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Core_NeverCertifiesAnArgvTheCmdletRejects(string name)
    {
        foreach (var argv in Corpus)
        {
            if (LineStreamRegistry.TryCreate(name, argv, out _))
            {
                Assert.True(CmdletAccepts(name, argv),
                    $"{name} core certified [{string.Join(" ", argv)}] but the cmdlet rejects it");
            }
        }
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    public void Core_StillCertifiesTheCommonSubset(string name)
    {
        Assert.True(LineStreamRegistry.TryCreate(name, Array.Empty<string>(), out _));
        Assert.True(LineStreamRegistry.TryCreate(name, new[] { "-n", "5" }, out _));
        Assert.True(LineStreamRegistry.TryCreate(name, new[] { "-n5" }, out _));
        Assert.True(LineStreamRegistry.TryCreate(name, new[] { "-5" }, out _));
    }
}
