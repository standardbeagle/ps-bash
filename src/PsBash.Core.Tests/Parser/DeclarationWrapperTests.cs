using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`command`/`builtin` in front of a declaration builtin only bypass functions: `command export x=1` must assign
/// (not look `export` up as an external command and report "command not found").</summary>
public class DeclarationWrapperTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("command export c=1")]
    [InlineData("builtin export c=1")]
    [InlineData("command command export c=1")]
    [InlineData("builtin builtin export c=1")]
    public void Transpile_WrappedExport_AssignsLikeBareExport(string bash) => Assert.Equal(T("export c=1"), T(bash));

    [Fact] public void Transpile_CommandVExport_StaysCommandLookup() => Assert.Contains("Invoke-BashCommand", T("command -v export"));

    [Fact] public void Transpile_BuiltinDeclareArrayLiteral_IsASyntaxError() => Assert.ThrowsAny<ParseException>(() => T("builtin declare a=(x y)"));
}
