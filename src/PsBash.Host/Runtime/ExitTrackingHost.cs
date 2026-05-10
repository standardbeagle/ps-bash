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

    /// <summary>
    /// Concrete-typed accessor used by SdkWorker to wire the formatter
    /// forwarder. Distinct name avoids hiding the base PSHost.UI property.
    /// </summary>
    public ExitTrackingHostUI HostUI => _ui;
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
    // Forwarder set by SdkWorker per-invocation so PowerShell's Out-Default
    // (which renders unhandled pipeline objects via the formatter and writes
    // the resulting text to host UI) reaches the same output sink as the
    // BashText/string streaming path. When null, Write/WriteLine are silent —
    // matching the historical no-op behavior so callers that don't opt in
    // don't suddenly leak formatter output to stdout.
    private Action<string>? _writeForwarder;

    public void SetWriteLineForwarder(Action<string>? forwarder)
    {
        _writeForwarder = forwarder;
    }

    public override PSHostRawUserInterface? RawUI => null;

    public override string ReadLine() => throw new NotSupportedException();
    public override System.Security.SecureString ReadLineAsSecureString() => throw new NotSupportedException();

    // PowerShell's formatter calls Write for partial-line output (e.g. when
    // rendering wide tables or applying ANSI color sequences before the line
    // terminator) and WriteLine for the line break. Buffer Write calls so a
    // single visual line surfaces to the forwarder as one callback rather than
    // a stream of fragments.
    private readonly System.Text.StringBuilder _lineBuffer = new();

    public override void Write(string value)
    {
        // Without a forwarder the buffer would grow unbounded across the
        // shared runspace's lifetime; drop fragments to match the historical
        // silent-host behavior outside an active SdkWorker invocation.
        if (_writeForwarder is null) return;
        if (string.IsNullOrEmpty(value)) return;
        _lineBuffer.Append(value);
    }

    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
        => Write(value);

    public override void WriteLine(string value)
    {
        if (_writeForwarder is null)
        {
            _lineBuffer.Clear();
            return;
        }
        var line = _lineBuffer.Length > 0
            ? _lineBuffer.ToString() + (value ?? string.Empty)
            : (value ?? string.Empty);
        _lineBuffer.Clear();
        _writeForwarder(line);
    }

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
