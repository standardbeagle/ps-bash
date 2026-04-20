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

        string resolvedPath = GetUnresolvedProviderPathFromPSPath(Path);

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
}
