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
        var source = new ProducerSource(output);
        var async = ps.BeginInvoke<PSObject, PSObject>(null, output);

        var cursor = new StdinCursor(source);
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

    /// <summary>
    /// The producer's output collection as a cancellable record source: a wait for the next item can be
    /// abandoned (the pump feeding a native program's stdin stops when the program exits) without taking it.
    /// Items are removed as they are read; the source ends once the producer completed and nothing is left.
    /// </summary>
    private sealed class ProducerSource : IStdinRecordSource
    {
        private readonly PSDataCollection<PSObject> _output;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Queue<object> _ready = new();

        internal ProducerSource(PSDataCollection<PSObject> output)
        {
            _output = output;
            _output.DataAdded += (_, _) => Signal();
            _output.Completed += (_, _) => Signal();
        }

        private void Signal()
        {
            try { _signal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        }

        public bool TryReadNext(CancellationToken ct, out object? record)
        {
            while (true)
            {
                if (_ready.TryDequeue(out record)) return true;
                // Read IsOpen BEFORE draining: an item added just before completion is still collected.
                bool open = _output.IsOpen;
                foreach (var item in _output.ReadAll())
                    _ready.Enqueue(item?.BaseObject is string s ? s : (object?)item ?? "");
                if (_ready.Count > 0) continue;
                if (!open) return false;
                // Every add/complete releases the semaphore, so an item that lands between ReadAll and here
                // is not missed.
                _signal.Wait(ct);
            }
        }

        public void Dispose() { }
    }
}
