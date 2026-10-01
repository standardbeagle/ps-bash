using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Compound-command stdin: a compound used as a pipe stage (or given <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>)
/// puts its input in one shared queue, and every stdin-reading command inside is fed from it.
/// </summary>
public class CompoundStdinEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("printf 'b\\na\\n' | { sort; }")]
    [InlineData("printf 'x\\n' | ( cat )")]
    [InlineData("echo a | if true; then sort; fi")]
    [InlineData("echo a | for i in 1; do cat; done")]
    [InlineData("echo a | case x in x) wc -l;; esac")]
    public void Transpile_CompoundPipeStage_OpensStdinScopeAndFeedsReader(string bash)
    {
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);                       // scope opened
        Assert.Contains("in $input", ps);                           // pipe drained into it
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);             // reader fed from it
        Assert.Contains("finally { $global:__BashStdIn = $__psbash_stdin_prev }", ps); // parent restored
    }

    [Theory]
    [InlineData("{ sort; } < f")]
    [InlineData("( cat ) < f")]
    [InlineData("{ cat; } <<< hi")]
    [InlineData("if true; then sort; fi < f")]
    [InlineData("for i in 1; do cat; done < f")]
    public void Transpile_CompoundWithInputRedirect_OpensStdinScopeAndFeedsReader(string bash)
    {
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);
    }

    [Theory]
    [InlineData("{ sort; } <<EOF\nb\na\nEOF\n")]
    [InlineData("( cat ) <<EOF\nhi\nEOF\n")]
    [InlineData("( cat ) <<< hi")]
    [InlineData("while true; do cat; break; done <<EOF\nhi\nEOF\n")]
    public void Transpile_HeredocOrHereStringIntoCompound_FeedsItsStdin(string bash)
    {
        // REGRESSION. `( cat ) <<< hi` and `{ sort; } <<EOF` were parse errors
        // ("Unexpected token '<<<'" / "'<<'"): only brace groups took a here-string and no
        // compound took a here-document.
        var ps = T(bash);

        Assert.Contains("Queue[object]", ps);
        Assert.Contains(PsBuild.StdinFeed + " | ", ps);
    }

    [Fact]
    public void Transpile_EchoInsideCompoundStage_IsNotFed()
    {
        // echo ignores stdin: feeding it would consume what a later reader needs.
        var ps = T("echo a | { echo one; cat; }");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(PsBuild.StdinFeed + " | ")));
        Assert.DoesNotContain(PsBuild.StdinFeed + " | Invoke-BashEcho", ps);
    }

    [Fact]
    public void Transpile_CommandWithFileOperandInsideCompound_IsNotFed()
    {
        var ps = T("echo a | { cat file.txt; }");

        Assert.DoesNotContain(PsBuild.StdinFeed, ps);
    }

    [Fact]
    public void Transpile_CompoundWithoutStdin_EmitsNoScopeAndNoFeed()
    {
        // No pipe and no redirect: the commands inside keep emitting exactly as before.
        var ps = T("{ sort; cat; }");

        Assert.DoesNotContain("__BashStdIn", ps);
    }

    [Fact]
    public void Transpile_PipeTargetsInsideCompound_ReadThePipeNotTheScope()
    {
        // `{ grep a | sort; }`: only the FIRST stage reads the compound's stdin.
        var ps = T("echo a | { grep a | sort; }");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(PsBuild.StdinFeed)));
        Assert.Contains(PsBuild.StdinFeed + " | Invoke-BashGrep", ps);
    }

    [Fact]
    public void Transpile_NestedCompoundStage_SavesAndRestoresParentQueue()
    {
        var ps = T("echo a | { cat | { sort; }; }");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, "Queue\\[object\\]").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, "finally \\{ \\$global:__BashStdIn = \\$__psbash_stdin_prev \\}").Count);
    }

    [Fact]
    public void Transpile_WhileReadInsideScope_ReadsTheSharedQueueNotInput()
    {
        var ps = T("{ while read l; do echo $l; done; } < f");

        Assert.Contains(PsBuild.StdinFeed + " | ForEach-Object", ps);
    }

    [Fact]
    public void Transpile_WhileReadAsPipeStage_StillStreamsInput()
    {
        var ps = T("printf 'a\\n' | while read l; do echo $l; done");

        Assert.Contains("$input | ForEach-Object", ps);
        Assert.DoesNotContain("__BashStdIn", ps);
    }

    [Fact]
    public void Transpile_NativeInsideCompound_IsFedOnlyWhenItResolvesToAnApplication()
    {
        var ps = T("echo a | { somenativetool -x; }");

        Assert.Contains("-CommandType Application", ps);
        Assert.Contains("somenativetool", ps);
    }

    [Theory]
    [InlineData("cat", new string[0], true)]
    [InlineData("cat", new[] { "-n" }, true)]
    [InlineData("cat", new[] { "f.txt" }, false)]
    [InlineData("cat", new[] { "-" }, true)]
    [InlineData("cat", new[] { "f.txt", "-" }, true)]
    [InlineData("head", new[] { "-n", "2" }, true)]
    [InlineData("head", new[] { "-n2" }, true)]
    [InlineData("head", new[] { "-n", "2", "f" }, false)]
    [InlineData("sort", new[] { "-k", "2", "-r" }, true)]
    [InlineData("sort", new[] { "-t", ",", "-k2" }, true)]
    [InlineData("sort", new[] { "-o", "out" }, true)]
    [InlineData("sort", new[] { "a", "b" }, false)]
    [InlineData("wc", new[] { "-l" }, true)]
    [InlineData("grep", new[] { "pat" }, true)]
    [InlineData("grep", new[] { "-v", "pat" }, true)]
    [InlineData("grep", new[] { "pat", "f" }, false)]
    [InlineData("grep", new[] { "-e", "a", "-e", "b" }, true)]
    [InlineData("grep", new[] { "-e", "a", "f" }, false)]
    [InlineData("sed", new[] { "-n", "1p" }, true)]
    [InlineData("sed", new[] { "s/a/b/", "f" }, false)]
    [InlineData("sed", new[] { "-e", "s/a/b/" }, true)]
    [InlineData("awk", new[] { "-F:", "{print $1}" }, true)]
    [InlineData("awk", new[] { "-F", ":", "{print $1}", "f" }, false)]
    [InlineData("cut", new[] { "-d:", "-f2" }, true)]
    [InlineData("cut", new[] { "-d", ",", "-f", "1" }, true)]
    [InlineData("tr", new[] { "a", "b" }, true)]
    [InlineData("tee", new[] { "out.txt" }, true)]
    [InlineData("xargs", new[] { "echo" }, true)]
    [InlineData("echo", new string[0], false)]
    [InlineData("ls", new string[0], false)]
    [InlineData("read", new[] { "x" }, false)]
    [InlineData("mytool", new[] { "--flag" }, true)]
    public void ShouldFeed_EstimatesWhetherTheCommandReadsStdin(string name, string[] args, bool expected)
    {
        Assert.Equal(expected, StdinReaders.ShouldFeed(name, args));
    }

    [Fact]
    public void ShouldFeed_NonLiteralOperandCountsAsAFile()
    {
        Assert.False(StdinReaders.ShouldFeed("cat", new string?[] { null }));
        Assert.True(StdinReaders.ShouldFeed("grep", new string?[] { null }));   // the pattern
    }
}
