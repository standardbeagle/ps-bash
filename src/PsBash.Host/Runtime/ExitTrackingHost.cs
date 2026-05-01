using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;

namespace PsBash.Host.Runtime;

/// <summary>
/// Minimal PSHost that captures the exit code when a script calls <c>exit N</c>.
/// PowerShell SDK routes <c>exit N</c> through <see cref="SetShouldExit"/> rather
/// than throwing ExitException at the .NET level, so a custom host is the correct
/// mechanism for capturing the exit code in-process.
/// </summary>
internal sealed class ExitTrackingHost : PSHost
{
    private readonly ExitTrackingHostUI _ui = new();

    public int ExitCode { get; private set; }
    public bool ShouldExit { get; private set; }

    public void Reset() { ExitCode = 0; ShouldExit = false; }

    public override Guid InstanceId { get; } = Guid.NewGuid();
    public override string Name => "PsBash";
    public override Version Version => new(1, 0);
    public override PSHostUserInterface UI => _ui;
    public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;

    public override void SetShouldExit(int exitCode) { ExitCode = exitCode; ShouldExit = true; }
    public override void EnterNestedPrompt() { }
    public override void ExitNestedPrompt() { }
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class ExitTrackingHostUI : PSHostUserInterface
{
    public override PSHostRawUserInterface? RawUI => null;

    public override string ReadLine() => throw new NotSupportedException();
    public override System.Security.SecureString ReadLineAsSecureString() => throw new NotSupportedException();

    public override void Write(string value) { }
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }
    public override void WriteLine(string value) { }
    public override void WriteDebugLine(string message) { }
    public override void WriteErrorLine(string value) => Console.Error.WriteLine(value);
    public override void WriteProgress(long sourceId, ProgressRecord record) { }
    public override void WriteVerboseLine(string message) { }
    public override void WriteWarningLine(string message) { }

    public override Dictionary<string, PSObject> Prompt(
        string caption, string message, Collection<FieldDescription> descriptions)
        => throw new NotSupportedException();

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName)
        => throw new NotSupportedException();

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        => throw new NotSupportedException();

    public override int PromptForChoice(
        string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
        => throw new NotSupportedException();
}
