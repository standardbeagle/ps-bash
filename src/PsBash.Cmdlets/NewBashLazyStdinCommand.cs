using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The stdin of a compound pipe stage whose producer may be UNBOUNDED (<c>yes | { head -n1; }</c>): runs
/// <c>-Script</c> (the producer's emitted command) on a background runspace and returns a
/// <see cref="StdinCursor"/> that pulls its records ON DEMAND. A script block placed after a pipe only starts
/// once its upstream has finished, so it cannot consume a never-ending stream; here the producer runs
/// concurrently and the scope reads what it needs. <see cref="StdinCursor.Close"/> (the scope's
/// <c>finally</c>) stops the producer — bash's SIGPIPE.
/// <para>
/// The producer runs in its own runspace (it shares the process environment and, through <c>Set-Location</c>
/// below, the working directory, but not shell variables), so the emitter only uses this for producers whose
/// words are all literals.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.New, "BashLazyStdin")]
[OutputType(typeof(StdinCursor))]
public sealed class NewBashLazyStdinCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Script { get; set; } = "";

    protected override void EndProcessing()
    {
        var iss = InitialSessionState.CreateDefault();
#pragma warning disable IL3000 // runs only in the (non-single-file) host runspace, where the cmdlet assembly has a real path
        iss.ImportPSModule(new[] { typeof(NewBashLazyStdinCommand).Assembly.Location });
#pragma warning restore IL3000
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var cwd = SessionState.Path.CurrentFileSystemLocation.Path;
        ps.AddScript("param($d) Set-Location -LiteralPath $d; " + Script).AddArgument(cwd);

        var output = new PSDataCollection<PSObject>();
        var async = ps.BeginInvoke<PSObject, PSObject>(null, output);

        IEnumerable<object> Records()
        {
            // The collection's enumerator blocks until the next item arrives and ends when the producer completes.
            foreach (var item in output)
                yield return item?.BaseObject is string s ? s : (object?)item ?? "";
        }

        var cursor = new StdinCursor(Records());
        cursor.OnClose = () =>
        {
            try { ps.BeginStop(null, null); } catch { }
            // Dispose off-thread: Stop is asynchronous and the producer may still be mid-write.
            Task.Run(() =>
            {
                try { async.AsyncWaitHandle.WaitOne(5000); } catch { }
                try { output.Complete(); } catch { }
                try { ps.Dispose(); } catch { }
                try { runspace.Dispose(); } catch { }
            });
        };
        WriteObject(cursor);
    }
}
