using System.Collections.Immutable;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Guard for the ONE redirection operator table (<see cref="RedirectOperators"/>, docs/specs/parser-grammar.md
/// section Redirections): every operator, with no prefix, an <c>N</c> prefix and a <c>{name}</c> prefix, after a simple
/// command, a brace group, a loop and <c>exec</c>, must parse as a redirection (its descriptor or name on the redirect,
/// never left as a word) and transpile to PowerShell that PowerShell's parser accepts (ParseabilityContract branch A).
/// The lexer and the parser each kept their own list and disagreed: <c>read -u 3 b 3&lt;&lt;&lt; x</c> passed <c>3</c> as a
/// name, <c>{ ...; } 3&lt;&lt;&lt; x</c> and <c>done 3&lt;&lt;&lt; x</c> did not parse, <c>exec {v}&lt;&lt;&lt; x</c> ran <c>{v}</c>, and
/// <c>exec {hd}&lt;&lt;EOF</c> emitted PowerShell that did not parse. A new operator, prefix or command kind is covered
/// here by construction: the cases are generated from the table.
/// </summary>
public class RedirectOperatorTableGuardTests
{
    private static readonly string[] Prefixes = ["", "3", "{fd}"];
    private static readonly string[] Kinds = ["simple", "group", "loop", "exec"];

    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (kind, text) in RedirectOperators.All)
            foreach (var prefix in Prefixes)
            {
                // `&>` / `&>>` name stdout and stderr both: bash reads `3&>f` as the word 3, then `&>f`.
                if (prefix.Length > 0 && !RedirectOperators.TakesFdPrefix(kind)) continue;
                foreach (var shape in Kinds)
                    data.Add(text, prefix, shape);
            }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryOperator_WithEveryPrefix_OnEveryCommandKind_IsARedirectAndValidPowerShell(string op, string prefix, string shape)
    {
        string bash = Script(op, prefix, shape);

        var ast = BashParser.Parse(bash);
        var (words, redirects) = Redirections(Unwrap(ast!));
        Assert.True(redirects.Count > 0, $"no redirection parsed\n  bash: {bash}");
        if (prefix == "3")
            Assert.Contains(redirects, r => r.Fd == 3 && r.FdVar is null);
        else if (prefix == "{fd}")
            Assert.Contains(redirects, r => r.FdVar == "fd");
        Assert.DoesNotContain(words, w => w is "3" or "{fd}" or "EOF" or "w" or "f.txt");

        Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.AssertNoCrash(bash, $"{shape} {prefix}{op}"));
    }

    /// <summary>
    /// A <c>{name}</c> word NOT glued to the operator is a word, never the descriptor variable (bash: <c>echo {fd} &gt;f</c>
    /// writes <c>{fd}</c>), and <c>&amp;&gt;</c> / <c>&amp;&gt;&gt;</c> never take the prefix even glued (<c>{fd}&amp;&gt;f</c>).
    /// </summary>
    public static TheoryData<string, string> SpacedNameCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (kind, text) in RedirectOperators.All)
        {
            foreach (var shape in new[] { "simple", "exec" })
                data.Add(text, shape + " spaced");
            if (!RedirectOperators.TakesFdPrefix(kind))
                data.Add(text, "simple glued");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SpacedNameCases))]
    public void NameWordNotGluedToAPrefixTakingOperator_IsAWord_AndValidPowerShell(string op, string form)
    {
        string head = form.StartsWith("exec", StringComparison.Ordinal) ? "exec echo" : "echo";
        string redirect = (form.EndsWith("glued", StringComparison.Ordinal) ? "{fd}" : "{fd} ") + op + " " + Target(op);
        string bash = head + " " + redirect + Body(op);

        var (words, redirects) = Redirections(Unwrap(BashParser.Parse(bash)!));
        Assert.Contains("{fd}", words);
        Assert.DoesNotContain(redirects, r => r.FdVar is not null);
        Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.AssertNoCrash(bash, $"{form} {op}"));
    }

    /// <summary>An assignment-only command with any redirection, prefixed or not, is still the shell's assignment
    /// (bash: <c>x=1 3&lt;&lt;&lt;y; echo "[$x]"</c> prints <c>[1]</c>) and transpiles to valid PowerShell.</summary>
    public static TheoryData<string, string> AssignmentCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (kind, text) in RedirectOperators.All)
            foreach (var prefix in Prefixes)
                if (prefix.Length == 0 || RedirectOperators.TakesFdPrefix(kind))
                    data.Add(text, prefix);
        return data;
    }

    [Theory]
    [MemberData(nameof(AssignmentCases))]
    public void AssignmentOnly_WithEveryOperator_KeepsTheAssignment_AndValidPowerShell(string op, string prefix)
    {
        string bash = "x=1 " + prefix + op + " " + Target(op) + Body(op);

        var assignment = Assert.IsType<Command.ShAssignment>(Unwrap(BashParser.Parse(bash)!));
        Assert.Equal("x", Assert.Single(assignment.Pairs).Name);
        Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.AssertNoCrash(bash, $"x=1 {prefix}{op}"));
    }

    private static string Target(string op) =>
        op is "<<" or "<<-" ? "EOF" : op == "<<<" ? "w" : op is "<&" or ">&" ? "-" : "f.txt";

    private static string Body(string op) => op switch { "<<" => "\nbody\nEOF\n", "<<-" => "\nbody\n\tEOF\n", _ => "" };

    /// <summary>A here-text on a descriptor other than stdin opens that descriptor; so does exec's stdin here-text,
    /// which is the SHELL's stdin (<c>exec &lt;&lt;&lt; w; read l</c>), not input piped into an exec that runs nothing.</summary>
    [Theory]
    [InlineData("read -u 3 b 3<<< x", 3, 4)]
    [InlineData("read -u 3 b 3<<EOF\nx\nEOF\n", 3, 4)]
    [InlineData("exec 4<<< four", 4, 1)]
    [InlineData("exec <<< w", 0, 1)]
    [InlineData("exec <<EOF\nx\nEOF\n", 0, 1)]
    public void HereText_OnADescriptorOrExecsStdin_IsARedirect_NotTheCommandsInput(string bash, int fd, int words)
    {
        var simple = Assert.IsType<Command.Simple>(Unwrap(BashParser.Parse(bash)!));
        Assert.True(simple.HereDocs.IsDefaultOrEmpty, "the here-text is not the command's input");
        Assert.Contains(simple.Redirects, r => r.Fd == fd && r.Here is not null);
        Assert.Equal(words, simple.Words.Length);
    }

    [Fact]
    public void Transpile_ExecStdinHereText_TheRestReadsIt()
    {
        var ps = PsBash.Core.Transpiler.BashTranspiler.Transpile("exec <<< w; read l; echo \"$l\"")!;

        Assert.DoesNotContain("| $global:LASTEXITCODE = 0", ps);
        Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.AssertNoCrash("exec <<< w; read l; echo \"$l\""));
    }

    private static string Script(string op, string prefix, string shape)
    {
        bool hereDoc = op is "<<" or "<<-";
        string target = hereDoc ? "EOF" : op == "<<<" ? "w" : op is "<&" or ">&" ? "-" : "f.txt";
        string redirect = prefix + op + " " + target;
        string line = shape switch
        {
            "simple" => "cat " + redirect,
            "group" => "{ cat; } " + redirect,
            "loop" => "while read -r l; do echo \"$l\"; done " + redirect,
            _ => "exec " + redirect,
        };
        return hereDoc ? line + "\nbody\n" + (op == "<<-" ? "\t" : "") + "EOF\n" : line;
    }

    private static Command Unwrap(Command c) => c switch
    {
        Command.CommandList { Commands.Length: 1 } l => Unwrap(l.Commands[0]),
        Command.Pipeline { Commands.Length: 1 } p => Unwrap(p.Commands[0]),
        _ => c,
    };

    /// <summary>The literal words of a simple command (none for a compound one) and every redirection, stdin's
    /// here-documents included.</summary>
    private static (List<string?> Words, List<Redirect> Redirects) Redirections(Command c)
    {
        switch (c)
        {
            case Command.Simple s:
            {
                var redirects = s.Redirects.IsDefaultOrEmpty ? new List<Redirect>() : [.. s.Redirects];
                if (!s.HereDocs.IsDefaultOrEmpty)
                    redirects.AddRange(s.HereDocs.Select(h => new Redirect("<<", h.Fd, new CompoundWord(ImmutableArray<WordPart>.Empty), h.FdVar, Here: h)));
                return (s.Words.Select(w => w.Parts is [WordPart.Literal { Value: var v }] ? v : null).ToList(), redirects);
            }
            case Command.BraceGroup g: return ([], g.Redirects.IsDefaultOrEmpty ? [] : [.. g.Redirects]);
            case Command.While w: return ([], w.Redirects.IsDefaultOrEmpty ? [] : [.. w.Redirects]);
            default: throw new Xunit.Sdk.XunitException($"unexpected command {c.GetType().Name}");
        }
    }
}
