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
            InvokeCommand.InvokeScript(
                useLocalScope: false,
                dotSource,
                input: null,
                args: null);
        }
        else
        {
            if (Arguments != null && Arguments.Length > 0)
            {
                var items = string.Join(", ", Arguments.Select(a => $"'{a.Replace("'", "''")}'"));
                var setPositional = ScriptBlock.Create($"$global:BashPositional = @({items})");
                InvokeCommand.InvokeScript(
                    useLocalScope: false,
                    setPositional,
                    input: null,
                    args: null);
            }
            else
            {
                var clearPositional = ScriptBlock.Create("$global:BashPositional = $null");
                InvokeCommand.InvokeScript(
                    useLocalScope: false,
                    clearPositional,
                    input: null,
                    args: null);
            }

            string content;
            using (var reader = new StreamReader(resolvedPath, System.Text.Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(content))
                return;

            var result = BashTranspiler.Transpile(content, TranspileContext.Eval);
            if (string.IsNullOrEmpty(result))
                return;

            var sb = ScriptBlock.Create(result);
            InvokeCommand.InvokeScript(
                useLocalScope: false,
                sb,
                input: null,
                args: null);
        }
    }

    private string ResolveSourcePath(string rawPath)
    {
        if (Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") == "1")
        {
            if (OperatingSystem.IsWindows() && rawPath.StartsWith("/tmp/", StringComparison.Ordinal))
            {
                return System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    rawPath[5..].Replace('/', System.IO.Path.DirectorySeparatorChar));
            }

            if (OperatingSystem.IsWindows()
                && rawPath.Length >= 3 && rawPath[0] == '/' && rawPath[2] == '/'
                && char.IsAsciiLetter(rawPath[1]))
            {
                return $"{char.ToUpperInvariant(rawPath[1])}:\\{rawPath[3..].Replace('/', '\\')}";
            }
        }

        return GetUnresolvedProviderPathFromPSPath(rawPath);
    }

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
