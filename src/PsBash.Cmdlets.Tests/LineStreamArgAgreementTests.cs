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
    };

    private static bool CmdletAccepts(string name, string[] argv) => name switch
    {
        "head" => !InvokeBashHeadCommand.Plan(argv).Declined,
        "tail" => !InvokeBashTailCommand.Plan(argv).Declined,
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Commands => new[] { new object[] { "head" }, new object[] { "tail" } };

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
