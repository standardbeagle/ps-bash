using PsBash.Core.Runtime;

namespace PsBash.Host.Shell;

/// <summary>One resolvable command from the live runspace: its <see cref="Name"/> and <see cref="Kind"/> label.</summary>
internal readonly record struct CommandNameEntry(string Name, string Kind);

/// <summary>
/// Background-maintained snapshot of the command names resolvable in the live runspace — cmdlets,
/// functions, filters, and aliases from every loaded module, plus anything the session has defined
/// (a <c>function foo {}</c> or <c>Set-Alias</c> the user just ran). The synchronous type-ahead
/// command panel reads <see cref="Names"/> / <see cref="DescribeKind"/> with NO runspace round-trip,
/// so it stays inside the keystroke budget. <see cref="RefreshAsync"/> repopulates it OFF the
/// keystroke path: once when the worker becomes ready (preload), and again after each executed
/// command that may have added a function / alias / imported a module. Never throws — advisory.
/// </summary>
internal sealed class CommandNameCache
{
    // Two volatile snapshots swapped atomically on refresh; readers take whichever they see. Both
    // are immutable once published, so a synchronous reader never observes a half-built collection.
    private volatile IReadOnlyList<string> _names = [];
    private volatile IReadOnlyDictionary<string, string> _kindByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Single-flight + coalesce: 0 = idle, 1 = a refresh is running. A request that arrives while one
    // runs sets _pending so exactly one more refresh runs afterward — so a command that defines a
    // function still gets picked up even if its refresh raced an in-flight one.
    private int _refreshing;
    private volatile bool _pending;

    /// <summary>The current command-name snapshot (ordinal-sorted). Lock-free; safe on the keystroke path.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>
    /// A human label for a cached command's kind ("PowerShell cmdlet" / "function" / "alias" / …),
    /// or null when <paramref name="name"/> is not a known PowerShell command. Case-insensitive,
    /// matching PowerShell command resolution.
    /// </summary>
    public string? DescribeKind(string name)
        => _kindByName.TryGetValue(name, out var kind) ? kind : null;

    /// <summary>
    /// Query the live runspace for resolvable command names and publish a fresh snapshot. Bounded by
    /// <paramref name="ct"/>; single-flighted (a concurrent call coalesces into one trailing refresh).
    /// Never throws.
    /// </summary>
    public async Task RefreshAsync(IWorker? worker, CancellationToken ct = default)
    {
        if (worker is not { HasExited: false })
            return;

        // If a refresh is already running, mark that another is wanted and return — the running
        // one will re-run once on completion, coalescing a burst of triggers into one trailing query.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) == 1)
        {
            _pending = true;
            // The owner may release between our failed CAS above and this store. Re-try the
            // CAS once: if it now succeeds we run the request ourselves rather than lose it
            // (the owner already passed its own _pending check). Still owned → the owner (or
            // its post-release guard below) will observe our _pending, so returning is safe.
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) == 1)
                return;
        }

        // We own the flag. Drain, release, then guard the release race: a request that set
        // _pending AFTER the inner loop's last check but BEFORE we cleared _refreshing would
        // otherwise be dropped (the lost-wakeup the single-shot version had). The outer loop
        // re-acquires and re-drains in that case; if another caller grabbed the flag meanwhile
        // it owns the pending work, so we stop.
        do
        {
            try
            {
                do
                {
                    _pending = false;
                    await QueryAndPublishAsync(worker, ct).ConfigureAwait(false);
                }
                while (_pending && worker is { HasExited: false } && !ct.IsCancellationRequested);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }
        while (_pending
               && worker is { HasExited: false } && !ct.IsCancellationRequested
               && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0);
    }

    private async Task QueryAndPublishAsync(IWorker worker, CancellationToken ct)
    {
        try
        {
            // Tab-separate name and kind; one line per command. Aliases/functions defined this
            // session are included because Get-Command reflects the live session state.
            const string expr =
                "Get-Command -CommandType Cmdlet,Function,Filter,Alias,Script -All -ErrorAction SilentlyContinue " +
                "| ForEach-Object { \"$($_.Name)`t$($_.CommandType)\" }";

            var raw = await worker.QueryAsync(expr, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
                return;

            var names = new SortedSet<string>(StringComparer.Ordinal);
            var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in raw.Split('\n'))
            {
                var line = entry.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                var tab = line.IndexOf('\t');
                var name = (tab >= 0 ? line[..tab] : line).Trim();
                if (name.Length == 0)
                    continue;
                var type = tab >= 0 ? line[(tab + 1)..].Trim() : string.Empty;
                names.Add(name);
                // First kind wins on a name collision (Get-Command -All can list shadowed entries).
                if (!kinds.ContainsKey(name))
                    kinds[name] = DescribeCommandType(type);
            }

            _names = [.. names];
            _kindByName = kinds;
        }
        catch (OperationCanceledException)
        {
            // Bounded by ct — leave the previous snapshot in place.
        }
        catch (Exception)
        {
            // Worker busy / unavailable — completion is advisory; keep the last good snapshot.
        }
    }

    internal static string DescribeCommandType(string type) => type switch
    {
        "Cmdlet" => "PowerShell cmdlet",
        "Function" => "PowerShell function",
        "Filter" => "PowerShell filter",
        "Alias" => "PowerShell alias",
        "Script" or "ExternalScript" => "PowerShell script",
        _ => "PowerShell command",
    };
}
