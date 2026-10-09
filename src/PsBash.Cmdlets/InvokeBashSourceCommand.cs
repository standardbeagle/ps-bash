using System.Management.Automation;
using PsBash.Core.Transpiler;

namespace PsBash.Cmdlets;

/// <summary>
/// Reads a bash script file, transpiles it, and sources it into the caller's scope.
/// If the file has a .ps1 extension it is dot-sourced natively.
/// Positional arguments are passed through $global:BashPositional.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSource")]
public sealed class InvokeBashSourceCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string? Path { get; set; }

    [Parameter(Position = 1, ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Return the code to source as a script block instead of running it — the transpiler emits
    /// <c>. $(Invoke-BashSource -AsScriptBlock f …)</c> so the CALLER dot-sources it and its output
    /// streams. Run here, <c>InvokeScript</c> can only hand the output back once the file finished.
    /// </summary>
    [Parameter(DontShow = true)]
    public SwitchParameter AsScriptBlock { get; set; }

    private static int _positionalSaves;

    protected override void ProcessRecord()
    {
        var code = Prepare();
        if (AsScriptBlock)
        {
            WriteObject(code ?? ScriptBlock.Create(""));
            return;
        }
        if (code is not null)
        {
            // InvokeScript RETURNS the output instead of writing it; it was discarded, so a
            // sourced file's `echo` printed nothing.
            foreach (var record in InvokeCommand.InvokeScript(useLocalScope: false, code, input: null, args: null))
                WriteObject(record);
        }
    }

    /// <summary>The code that sources <see cref="Path"/> in the caller's scope, or null when there is
    /// nothing to run (missing or unreadable file — error written, status 1 — or an empty one).</summary>
    private ScriptBlock? Prepare()
    {
        if (string.IsNullOrEmpty(Path))
            return null;

        string resolvedPath = ResolveSourcePath(Path);

        if (!System.IO.File.Exists(resolvedPath))
        {
            if (TryCreateOptionalSnapshot(resolvedPath, Path))
                return null;

            WriteError(new ErrorRecord(
                new System.IO.FileNotFoundException($"ps-bash: {Path}: No such file or directory"),
                "FileNotFound",
                ErrorCategory.ObjectNotFound,
                Path));
            // bash: source /nonexistent => exit 1. WriteError emits the
            // diagnostic but doesn't set $LASTEXITCODE on its own, so the
            // launcher process would still return 0 to the caller. Push a
            // non-zero exit code into the global so the outer eval picks
            // it up.
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return null;
        }

        if (System.IO.Path.GetExtension(resolvedPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            return ScriptBlock.Create($". '{resolvedPath.Replace("'", "''")}'");

        string content;
        try
        {
            content = BashFileSystem.ReadAllTextRaw(resolvedPath);
        }
        catch (IOException ex)
        {
            WriteError(new ErrorRecord(
                ex,
                "SourceReadFailed",
                ErrorCategory.ReadError,
                resolvedPath));
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return null;
        }

        if (string.IsNullOrWhiteSpace(content))
            return null;

        var result = BashTranspiler.Transpile(content, TranspileContext.Eval);
        if (string.IsNullOrEmpty(result))
            return null;

        // bash: `source f a b` sets $1.. to a b for the file, then RESTORES the caller's; with no
        // arguments the caller's positional parameters stay visible. This used to clear them
        // (`set -- a b; . ./lib.sh; echo $1` printed nothing) and never restored after args. The
        // saved value gets its own global — a nested `source x args` runs in the same scope.
        if (Arguments is { Length: > 0 })
        {
            string saved = "__psbash_srcpos" + Interlocked.Increment(ref _positionalSaves);
            SessionState.PSVariable.Set("global:" + saved, SessionState.PSVariable.GetValue("global:BashPositional"));
            SessionState.PSVariable.Set("global:BashPositional", Arguments.Cast<object>().ToArray());
            result = "try {\n" + result + "\n} finally { $global:BashPositional = $global:" + saved
                + "; Remove-Variable -Name " + saved + " -Scope Global -ErrorAction SilentlyContinue }";
        }
        return ScriptBlock.Create(result);
    }
    // The shared runtime path policy (ProviderPath → RuntimePath.Map). This used to carry its own
    // copy — /tmp → GetTempPath (not $env:TEMP) and /c/ — only under PSBASH_UNIX_PATHS=1, so
    // `. $d/f` (d=/tmp) and `cat $d/f` could name different files.
    private string ResolveSourcePath(string rawPath) => FileSystemHelpers.ProviderPath(this, rawPath);

    private bool TryCreateOptionalSnapshot(string resolvedPath, string rawPath)
    {
        var fileName = System.IO.Path.GetFileName(rawPath);
        if (!fileName.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!fileName.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(resolvedPath, string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
