using System.Management.Automation;
using PsBash.Core;
using PsBash.Core.Stdio;

namespace PsBash.Cmdlets;

/// <summary>
/// Live output for a runspace-pool background job (<c>cmd &amp;</c>, docs/specs/background-jobs.md §12): instead of
/// holding the job's output until <c>wait</c> collects it, every record is written to the session's
/// <see cref="OutputMux"/> as the job produces it, so it reaches the shell's stdout/stderr live and in order with
/// the foreground. The job's output and error collections are drained as they fill (nothing is retained), and a
/// write blocks while the mux applies back-pressure, which blocks the job's pipeline thread like a full pipe.
/// Driven from the module's <c>Invoke-BashBackground</c>.
/// </summary>
public static class BashJobOutput
{
    /// <summary>
    /// Start <paramref name="ps"/> (already holding the job script) with its output and error streams connected to a
    /// new channel of <paramref name="mux"/>. The returned handle is the same <c>IAsyncResult</c> <c>BeginInvoke</c>
    /// gives, so <c>wait</c>/<c>jobs</c>/<c>kill</c> treat the job as before; <c>EndInvoke</c> then returns nothing,
    /// because everything was already delivered.
    /// </summary>
    public static IAsyncResult BeginLive(PowerShell ps, OutputMux mux, string name)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ArgumentNullException.ThrowIfNull(mux);
        var channel = mux.Open(name);
        // A producer blocked on credit gives up once the job is being stopped (`kill`, runspace teardown): a
        // PowerShell.Stop() waits for the pipeline thread, which is the thread blocked here.
        Func<bool> abandoned = () => ps.InvocationStateInfo.State is not (PSInvocationState.Running or PSInvocationState.NotStarted);
        var output = new PSDataCollection<PSObject>();
        var gate = new object();
        // First writes reach the shell in launch order (BashJobRunspaces.JobTurn): this job's first record waits for the
        // earlier jobs' first writes, and the turn passes after the write, so the order of two jobs' first records is fixed.
        var turn = BashJobRunspaces.JobTurn.Of(ps);
        output.DataAdded += (_, _) =>
        {
            turn?.WaitToWrite(abandoned);
            lock (gate)
            {
                foreach (var item in output.ReadAll())
                    channel.Write(1, Text(item, mux.NewLine), abandoned);
            }
            turn?.Pass();
        };
        var errors = ps.Streams.Error;
        errors.DataAdded += (_, _) =>
        {
            turn?.WaitToWrite(abandoned);
            lock (gate)
            {
                foreach (var record in errors.ReadAll())
                    channel.Write(2, StderrRecord.Payload(record.ToString(), record.TargetObject), abandoned);
            }
            turn?.Pass();
        };
        ps.InvocationStateChanged += (_, e) =>
        {
            if (e.InvocationStateInfo.State is PSInvocationState.Completed or PSInvocationState.Failed
                or PSInvocationState.Stopped or PSInvocationState.Disconnected)
                channel.Close();
        };
        var input = new PSDataCollection<PSObject>();
        input.Complete();
        IAsyncResult started;
        try { started = ps.BeginInvoke(input, output); }
        catch
        {
            channel.Close();
            throw;
        }
        channel.MarkStarted();
        return started;
    }

    /// <summary>The exact text of one output record, as the host renders a foreground record (SdkWorker.GetOutputText).</summary>
    internal static string Text(PSObject? item, string eol)
    {
        if (item is null) return eol;
        if (item.BaseObject is ErrorRecord record) return record + eol;
        return BashRuntime.RecordBytes(item, eol);
    }
}
