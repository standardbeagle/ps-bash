using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>history</c> builtin of a non-interactive shell (scripts, <c>-c</c>): the per-shell list in
/// <c>$global:BashHistory</c>, rules in <see cref="BashHistoryBuiltins.History"/>. Before it existed,
/// PowerShell's own <c>history</c> alias (Get-History) ran and printed its parameter-binding errors.
/// The interactive shell answers a plain <c>history</c> line itself (<c>PsBash.Host.Shell.HistoryBuiltin</c>).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHistory")]
public sealed class InvokeBashHistoryCommand : PSCmdlet
{
    // Decoys for DIRECT calls (`Invoke-BashHistory -c`): these letters prefix common parameters or -Arguments.
    // Transpiled scripts never bind them: the emitter single-quotes every dash word (OrderedArgCommands).
    [Parameter] public SwitchParameter A { get; set; }
    [Parameter] public SwitchParameter C { get; set; }
    [Parameter] public string? D { get; set; }
    [Parameter] public SwitchParameter P { get; set; }
    [Parameter] public SwitchParameter W { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = new List<string>();
        if (A) args.Add("-a");
        if (C) args.Add("-c");
        if (D is not null) { args.Add("-d"); args.Add(D); }
        if (W) args.Add("-w");
        if (P) args.Add("-p");
        args.AddRange(Arguments ?? []);
        if (FileSystemHelpers.TryHandleVersion(this, "history", [.. args])) return;
        int status = BashHistoryBuiltins.History(BashHistoryState.Load(this), args, BashHistoryState.CreateIo(this));
        FileSystemHelpers.SetLastExitCode(this, status);
    }
}

/// <summary>
/// The <c>fc</c> builtin of a non-interactive shell: <c>fc -l</c> lists, <c>fc -s [pat=rep] [cmd]</c> re-runs an
/// entry (echoed to stderr, run as bash through the transpiler like <c>eval</c>). Editing in <c>$FCEDIT</c> is
/// not supported. Before it existed, PowerShell's <c>fc</c> alias (Format-Custom) ran.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFc")]
public sealed class InvokeBashFcCommand : PSCmdlet
{
    // Decoy for a DIRECT `Invoke-BashFc -e vi` (-e is ambiguous between -ErrorAction and -ErrorVariable).
    [Parameter] public string? E { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = new List<string>();
        if (E is not null) { args.Add("-e"); args.Add(E); }
        args.AddRange(Arguments ?? []);
        if (FileSystemHelpers.TryHandleVersion(this, "fc", [.. args])) return;
        int status = BashHistoryBuiltins.Fc(BashHistoryState.Load(this), args, BashHistoryState.CreateIo(this), out var command);
        FileSystemHelpers.SetLastExitCode(this, status);
        if (command is null) return;
        // The re-run command is bash text; it runs in the caller's session like eval, streaming to this pipeline.
        InvokeCommand.InvokeScript(
            "param($s) $p = $null; "
            + "try { $p = [PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile($s, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval) } "
            + "catch { Write-Error ('ps-bash: fc: ' + $(if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message })) -ErrorAction Continue; $global:LASTEXITCODE = 2; return }; "
            + "$global:LASTEXITCODE = 0; Invoke-Expression $p",
            false, PipelineResultTypes.Output, null, command);
    }
}

/// <summary>Load / IO glue shared by <see cref="InvokeBashHistoryCommand"/> and <see cref="InvokeBashFcCommand"/>.</summary>
internal static class BashHistoryState
{
    internal static BashHistoryList Load(PSCmdlet cmdlet)
    {
        var value = cmdlet.SessionState.PSVariable.GetValue("global:BashHistory");
        if (value is PSObject pso) value = pso.BaseObject;
        if (value is BashHistoryList list) return list;
        // A subshell restore hands back a plain list copy (BashShellState.CloneMutable); adopt its entries.
        var fresh = new BashHistoryList();
        if (value is System.Collections.IEnumerable items and not string)
            foreach (var item in items) fresh.Add(item?.ToString() ?? "");
        cmdlet.SessionState.PSVariable.Set("global:BashHistory", fresh);
        return fresh;
    }

    internal static BashHistoryBuiltins.Io CreateIo(PSCmdlet cmdlet) => new(
        line => cmdlet.WriteObject(line),
        line => FileSystemHelpers.WriteStderr(cmdlet, line),
        path =>
        {
            var full = FileSystemHelpers.ProviderPath(cmdlet, path);
            if (!File.Exists(full)) return null;
            try { return BashFileSystem.ReadLines(full).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        },
        (path, lines, append) =>
        {
            var full = FileSystemHelpers.ProviderPath(cmdlet, path);
            try
            {
                using var w = new StreamWriter(full, append, PsBash.Core.RawBytes.Encoding);
                foreach (var l in lines) { w.Write(l); w.Write('\n'); }
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return false; }
        },
        // HISTFILE unset: readline's default, $HOME/.history (bash -c 'history -w' creates it).
        BashVariableStore.Get("HISTFILE") ?? (BashVariableStore.Get("HOME") is { Length: > 0 } home ? home.TrimEnd('/', '\\') + "/.history" : null));
}
