using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The streaming push-engine (<see cref="SedEngine"/>) against the verbatim whole-input engine it replaced
/// (<see cref="LegacySedEngine"/>): identical output records for random scripts over random inputs, covering every
/// address form (<c>$</c>, line, range, regex range, N,/re/, 0,/re/, step, negation) and every command, including
/// the engine's quirks (N not advancing the line number, D restart discarding queued output, ranged c).
/// </summary>
public class SedEngineEquivalenceTests
{
    private static readonly string[] Addresses =
    {
        "", "", "", "2", "$", "1", "3", "2,4", "1,$", "2,$", "/a/", "/b/", "/^$/", "/a/,/b/", "/b/,/a/", "/x/,/ab/",
        "2,/b/", "0,/b/", "1,/a/", "1~2", "2~3", "0~2", "/a/,3", "3,2",
    };

    private static readonly string[] Commands =
    {
        "s/a/X/", "s/a/X/g", "s/b/Y/2", "s/a/Z/p", "d", "D", "p", "P", "N", "N", "=", "q", "Q",
        "a ADD", "i INS", "c CHG", "y/ab/xy/", "s/^/>/", "s/$/</",
    };

    private static readonly string[] Alphabet = { "a", "b", "ab", "", "x", "aab", "ba", "xx" };

    [Fact]
    public void RandomScripts_MatchTheLegacyEngine()
    {
        var rng = new Random(20261001);
        int compared = 0;
        for (int iter = 0; iter < 6000; iter++)
        {
            int n = rng.Next(1, 5);
            var exprs = new List<string>();
            for (int k = 0; k < n; k++)
            {
                string addr = Addresses[rng.Next(Addresses.Length)];
                string neg = addr.Length > 0 && rng.Next(6) == 0 ? "!" : "";
                exprs.Add(addr + neg + Commands[rng.Next(Commands.Length)]);
            }
            if (!InvokeBashSedCommand.TryBuildCommands(exprs, false, out var cmds)) continue;

            int len = rng.Next(0, 9);
            var input = new string[len];
            for (int i = 0; i < len; i++) input[i] = Alphabet[rng.Next(Alphabet.Length)];
            bool quiet = rng.Next(4) == 0;

            LegacySedEngine.SuppressDefault = quiet;
            var expected = LegacySedEngine.ProcessLines(input, cmds);
            var actual = SedEngine.ProcessLines(input, cmds, quiet);

            Assert.True(expected.SequenceEqual(actual),
                $"script [{string.Join(" ; ", exprs)}] quiet={quiet} input=[{string.Join("|", input)}]\n" +
                $"expected: [{string.Join("|", expected)}]\nactual:   [{string.Join("|", actual)}]");
            compared++;
        }
        Assert.True(compared > 3000, $"only {compared} scripts parsed");
    }

    [Fact]
    public void Run_IsLazy_OutputAppearsBeforeTheInputEnds()
    {
        // 1:1 filter: record k is out once record k+1 (or the end) is fed — never all-at-end.
        InvokeBashSedCommand.TryBuildCommands(new[] { "s/a/X/" }, false, out var cmds);
        var log = new List<string>();
        IEnumerable<string> Source()
        {
            for (int i = 1; i <= 4; i++) { log.Add("P" + i); yield return "a" + i; }
        }
        foreach (var line in SedEngine.Run(Source(), cmds, false)) log.Add("T" + line);
        Assert.Equal(new[] { "P1", "TX1", "P2", "TX2", "P3", "TX3", "P4", "TX4" }, log);
    }

    [Fact]
    public void Run_Quit_StopsReadingTheInput()
    {
        InvokeBashSedCommand.TryBuildCommands(new[] { "2q" }, false, out var cmds);
        int pulled = 0;
        IEnumerable<string> Source()
        {
            while (true) { pulled++; yield return "l" + pulled; }
        }
        var output = SedEngine.Run(Source(), cmds, false).ToList();
        Assert.Equal(new[] { "l1", "l2" }, output);
        Assert.True(pulled <= 3, $"pulled {pulled}");
    }
}
