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

    protected override void ProcessRecord()
    {
        if (string.IsNullOrEmpty(Path))
            return;

        string resolvedPath = ResolveSourcePath(Path);

        if (!System.IO.File.Exists(resolvedPath))
        {
            if (TryCreateOptionalSnapshot(resolvedPath, Path))
                return;

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
            return;
        }

        if (System.IO.Path.GetExtension(resolvedPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var dotSource = ScriptBlock.Create($". '{resolvedPath.Replace("'", "''")}'");
            WriteAll(InvokeCommand.InvokeScript(
                useLocalScope: false,
                dotSource,
                input: null,
                args: null));
        }
        else
        {
            // bash: `source f a b` sets $1.. to a b for the file, then RESTORES the caller's; with no
            // arguments the caller's positional parameters stay visible. This used to clear them
            // (`set -- a b; . ./lib.sh; echo $1` printed nothing) and never restored after args.
            bool withArgs = Arguments is { Length: > 0 };
            object? savedPositional = SessionState.PSVariable.GetValue("global:BashPositional");
            if (withArgs)
                SessionState.PSVariable.Set("global:BashPositional", Arguments!.Cast<object>().ToArray());
            try
            {
                SourceBashFile(resolvedPath);
            }
            finally
            {
                if (withArgs)
                    SessionState.PSVariable.Set("global:BashPositional", savedPositional);
            }
        }
    }

    /// <summary>
    /// Every record the sourced code produced, to this cmdlet's output. <c>InvokeScript</c> RETURNS the
    /// output instead of writing it, and it was discarded: a sourced file's `echo` printed nothing.
    /// </summary>
    private void WriteAll(System.Collections.ObjectModel.Collection<PSObject> output)
    {
        foreach (var record in output)
            WriteObject(record);
    }

    private void SourceBashFile(string resolvedPath)
    {
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
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
            return;

        var result = BashTranspiler.Transpile(content, TranspileContext.Eval);
        if (string.IsNullOrEmpty(result))
            return;

        var sb = ScriptBlock.Create(result);
        WriteAll(InvokeCommand.InvokeScript(
            useLocalScope: false,
            sb,
            input: null,
            args: null));
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
