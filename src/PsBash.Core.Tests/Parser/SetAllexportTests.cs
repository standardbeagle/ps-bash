using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`set -a` (allexport) is accepted: every variable already lives in the exported environment, so the
/// `set -a; . .env; set +a` idiom must not be refused as an unsupported option.</summary>
public class SetAllexportTests
{
    [Theory]
    [InlineData("set -a")]
    [InlineData("set +a")]
    [InlineData("set -o allexport")]
    [InlineData("set +o allexport")]
    [InlineData("set -ea")]
    public void Transpile_AllexportForms_Accepted(string bash) => Assert.NotNull(PsEmitter.Transpile(bash));

    [Fact] public void Transpile_SetAThenSource_KeepsTheSourceCall() => Assert.Contains("source", PsEmitter.Transpile("set -a; source .env; set +a")!, StringComparison.OrdinalIgnoreCase);
}
