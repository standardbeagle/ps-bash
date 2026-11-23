using System.Management.Automation;
using System.Reflection;

namespace PsBash.Cmdlets;

/// <summary>
/// Is a cmdlet's stderr merged into its stdout right now? A cmdlet that spawns a child asks this, and when the
/// answer is yes it spawns the child with ONE pipe for fd 1 and fd 2 (<see cref="ChildStdioMode.MergeStderr"/>).
/// <para>
/// <b>How it is known.</b> The emitter turns every bash merge into PowerShell's <c>2&gt;&amp;1</c> on the command
/// (<c>cmd 2&gt;&amp;1</c>, <c>cmd &amp;&gt;f</c>, <c>cmd |&amp; next</c>) or on an enclosing block
/// (<c>{ ...; } 2&gt;&amp;1</c> → <c>. { ... } 2&gt;&amp;1</c>, a function call <c>f 2&gt;&amp;1</c>). PowerShell then gives the
/// command runtime either <c>ErrorMergeTo = Output</c> (its own <c>2&gt;&amp;1</c>) or an error pipe that IS its output
/// pipe (an enclosing block's). Both are internal to System.Management.Automation and read by reflection; when
/// they cannot be read the answer is no, which is the old two-pipe behavior.
/// </para>
/// <para>
/// <b>Not every PowerShell <c>2&gt;&amp;1</c> is a bash merge.</b> The emitter also routes <c>2&gt;f</c> and
/// <c>2&gt;&amp;1 &gt;/dev/null</c> through a PowerShell <c>2&gt;&amp;1</c> into an <c>Invoke-BashRedirect</c> stage that
/// separates the two again (<c>-ErrorPath f</c>, <c>-Path $null -PassErrors</c>). The stage the output pipe feeds is
/// asked (<see cref="InvokeBashRedirectCommand.KeepsStderrWithStdout"/>); its parameters are bound before any
/// command in the pipeline begins.
/// </para>
/// </summary>
public static class StderrMerge
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Lazy<Accessors?> s_accessors = new(Resolve);

    /// <summary>True when everything <paramref name="cmdlet"/> writes to stderr lands in the same stream as its stdout.</summary>
    public static bool JoinsStdout(Cmdlet cmdlet)
    {
        if (!Captured(cmdlet, out var a, out var output)) return false;
        try
        {
            // The stage the merged stream feeds may split it again.
            if (output is null || a.Downstream.GetValue(output) is not { } processor) return true;
            return a.Command.GetValue(processor) switch
            {
                InvokeBashRedirectCommand redirect => redirect.KeepsStderrWithStdout(),
                InvokeBashFdRouteCommand route => route.KeepsStderrWithStdout(),
                InvokeBashKeepStderrCommand => false,
                _ => true,
            };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="cmdlet"/>'s stderr goes down the pipeline at all: merged into its stdout
    /// (<see cref="JoinsStdout"/>) or sent somewhere else by the stage it feeds (<c>2&gt;f</c>, <c>2&gt;&amp;1 &gt;/dev/null</c>).
    /// A cmdlet that runs a program by name must then route the program's stderr through its own error stream: run by
    /// PowerShell's nested <c>&amp; $c</c>, the program writes straight to the host's stderr and the redirect never sees it.
    /// </summary>
    public static bool Redirected(Cmdlet cmdlet) => Captured(cmdlet, out _, out _);

    private static bool Captured(Cmdlet cmdlet, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Accessors? a, out object? output)
    {
        a = s_accessors.Value;
        output = null;
        if (a is null) return false;
        try
        {
            var runtime = cmdlet.CommandRuntime;
            if (runtime is null || !a.RuntimeType.IsInstanceOfType(runtime)) return false;
            output = a.OutputPipe.GetValue(runtime);
            return a.ErrorMergeTo.GetValue(runtime)?.ToString() == "Output"
                   || (output is not null && ReferenceEquals(output, a.ErrorOutputPipe.GetValue(runtime)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The cmdlet in the stage <paramref name="cmdlet"/>'s output feeds (the next stage of its pipeline), or null when
    /// it feeds no cmdlet or the SMA internals cannot be read. Read through the same pipe as <see cref="JoinsStdout"/>.
    /// </summary>
    internal static Cmdlet? NextStage(Cmdlet cmdlet)
    {
        if (s_accessors.Value is not { } a) return null;
        try
        {
            var runtime = cmdlet.CommandRuntime;
            if (runtime is null || !a.RuntimeType.IsInstanceOfType(runtime)) return null;
            if (a.OutputPipe.GetValue(runtime) is not { } output || a.Downstream.GetValue(output) is not { } processor) return null;
            return a.Command.GetValue(processor) as Cmdlet;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="from"/>'s output reaches <paramref name="to"/> down the same pipeline (through any stages
    /// between them): <paramref name="to"/> encloses <paramref name="from"/>, as a capture encloses the <c>2&gt;&amp;1</c>
    /// stage before it (BashFdTable's stage order). False when it does not, or the SMA internals cannot be read.
    /// </summary>
    internal static bool Feeds(Cmdlet from, Cmdlet to)
    {
        var stage = from;
        // A pipeline is short; the bound only keeps a cycle in unreadable internals from spinning.
        for (int i = 0; i < 64 && NextStage(stage) is { } next; i++)
        {
            if (ReferenceEquals(next, to)) return true;
            stage = next;
        }
        return false;
    }

    /// <summary>True when the SMA internals <see cref="JoinsStdout"/> reads were found (a guard test pins it).</summary>
    internal static bool CanDetect => s_accessors.Value is not null;

    private sealed record Accessors(
        Type RuntimeType,
        PropertyInfo ErrorMergeTo,
        PropertyInfo OutputPipe,
        PropertyInfo ErrorOutputPipe,
        FieldInfo Downstream,
        PropertyInfo Command);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "The SMA internals are looked up by name in the PowerShell host runspace (the non-AOT " +
            "ps-bash-host), where System.Management.Automation is never trimmed. Not found = null = two pipes.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Reflecting over members of the untrimmed SMA types above; a missing member returns null.")]
    private static Accessors? Resolve()
    {
        try
        {
            var sma = typeof(PSCmdlet).Assembly;
            var runtime = sma.GetType("System.Management.Automation.MshCommandRuntime");
            var pipe = sma.GetType("System.Management.Automation.Internal.Pipe");
            var processor = sma.GetType("System.Management.Automation.CommandProcessorBase");
            if (runtime is null || pipe is null || processor is null) return null;
            var mergeTo = runtime.GetProperty("ErrorMergeTo", Instance);
            var output = runtime.GetProperty("OutputPipe", Instance);
            var error = runtime.GetProperty("ErrorOutputPipe", Instance);
            var downstream = pipe.GetField("_downstreamCmdlet", Instance);
            var command = processor.GetProperty("Command", Instance);
            if (mergeTo is null || output is null || error is null || downstream is null || command is null) return null;
            return new Accessors(runtime, mergeTo, output, error, downstream, command);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
