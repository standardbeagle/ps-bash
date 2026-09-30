using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Expected-value table for bash's echo BUILTIN option scan (oracle: `wsl bash`, xpg_echo off).
/// A leading run of -[neE]+ words are options; anything else ends option parsing.
/// </summary>
public class EchoArgScanTests
{
    [Theory]
    [InlineData("-n|-e|a", true, true, 2)]
    [InlineData("-ne|a", true, true, 1)]
    [InlineData("-en|x", true, true, 1)]
    [InlineData("-nx|a", false, false, 0)]        // -nx is literal
    [InlineData("--|a", false, false, 0)]         // no -- handling
    [InlineData("-e|-n|x", true, true, 2)]
    [InlineData("-|a", false, false, 0)]          // lone dash literal
    [InlineData("-E|a", false, false, 1)]
    [InlineData("--help", false, false, 0)]
    [InlineData("-n-|a", false, false, 0)]
    [InlineData("-neE|x", true, false, 1)]        // E after e: last wins
    [InlineData("-eE|x", false, false, 1)]
    [InlineData("-Ee|x", false, true, 1)]
    [InlineData("-e|-E|x", false, false, 2)]
    [InlineData("-E|-e|x", false, true, 2)]
    [InlineData("-e|--|x", false, true, 1)]       // -- stops the run and is printed
    [InlineData("-x|-n|a", false, false, 0)]      // first non-option stops; later -n literal
    [InlineData("a|-n", false, false, 0)]
    [InlineData("-e-n|x", false, false, 0)]
    [InlineData("-n||-n", true, false, 1)]        // empty word is an operand, stops
    [InlineData("", false, false, 0)]
    public void Scan(string joined, bool n, bool e, int first)
    {
        var args = joined.Length == 0 ? Array.Empty<string>() : joined.Split('|');
        var r = EchoArgScan.Scan(args);
        Assert.Equal((n, e, first), (r.NoNewline, r.Escapes, r.FirstOperand));
    }
}
