using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class HeredocTransformTests
{
    private readonly HeredocTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void UnquotedDelimiter_UsesDoubleQuoteHereString()
    {
        var input = "cat <<EOF\nline 1\nline 2\nEOF";
        var expected = "@\"\nline 1\nline 2\n\"@ | cat";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void QuotedDelimiter_UsesSingleQuoteHereString()
    {
        var input = "cat <<'EOF'\nline 1\nline 2\nEOF";
        var expected = "@'\nline 1\nline 2\n'@ | cat";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void CommandWithArgs()
    {
        var input = "grep -i foo <<EOF\nhello foo\nbar\nEOF";
        var expected = "@\"\nhello foo\nbar\n\"@ | grep -i foo";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void SingleLineBody()
    {
        var input = "cat <<EOF\nonly one line\nEOF";
        var expected = "@\"\nonly one line\n\"@ | cat";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void EmptyBody()
    {
        var input = "cat <<EOF\n\nEOF";
        var expected = "@\"\n\n\"@ | cat";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void CustomDelimiter()
    {
        var input = "cat <<MARKER\nsome text\nMARKER";
        var expected = "@\"\nsome text\n\"@ | cat";
        Assert.Equal(expected, Apply(input));
    }

    [Fact]
    public void DoesNotMatchHereString()
    {
        var input = "cat <<< \"hello\"";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DoesNotMatchInputRedirect()
    {
        var input = "cat < file.txt";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoHeredoc_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void BodyWithVariables_UnquotedDelimiter()
    {
        var input = "cat <<EOF\nhello $NAME\nEOF";
        var expected = "@\"\nhello $NAME\n\"@ | cat";
        Assert.Equal(expected, Apply(input));
    }
}
