using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// What the shell prints, and the status it leaves, for a PowerShell parameter-binding error (<c>tr -O a b</c>: the
/// binder fails the statement before the cmdlet runs). The wording is GNU getopt's (oracle:
/// RuntimeCommandParityDifferentialTests.BinderRejectedFlag_IsAUsageErrorWithItsStatus); this pins the mapping the
/// host applies (SdkWorker) for the cases the oracle script does not reach.
/// </summary>
public class UsageErrorsTests
{
    private const string AmbiguousText = "Parameter cannot be processed because the parameter name 'O' is ambiguous.";

    [Theory]
    [InlineData("Invoke-BashTr", "AmbiguousParameter", "O", "tr: invalid option -- 'O'\nTry 'tr --help' for more information.", 1)]
    [InlineData("Invoke-BashSeq", "NamedParameterNotFound", "Q", "seq: invalid option -- 'Q'\nTry 'seq --help' for more information.", 1)]
    [InlineData("Invoke-BashGrep", "AmbiguousParameter", "O", "grep: invalid option -- 'O'\nUsage: grep [OPTION]... PATTERNS [FILE]...\nTry 'grep --help' for more information.", 2)]
    [InlineData("Invoke-BashCd", "AmbiguousParameter", "O", "cd: invalid option -- 'O'\nTry 'cd --help' for more information.", 2)]
    public void BindingFailure_SingleLetter_IsGetoptsInvalidOption(string cmdlet, string errorId, string name, string message, int status)
        => Assert.Equal((message, status), UsageErrors.BindingFailure(cmdlet, errorId, name, AmbiguousText));

    [Fact]
    public void BindingFailure_OtherBindingError_KeepsTheTextAndFails()
        => Assert.Equal(("Missing an argument for parameter 'W'.", 1),
            UsageErrors.BindingFailure("Invoke-BashPing", "MissingArgument", "W", "Missing an argument for parameter 'W'."));

    [Fact]
    public void BindingFailure_NotABashCommand_KeepsTheTextAndStatusIsOne()
        => Assert.Equal((AmbiguousText, 1), UsageErrors.BindingFailure("Get-ChildItem", "AmbiguousParameter", "O", AmbiguousText));
}
