using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTouch</c>
/// (REFACTOR-2). Updates the access / modification timestamps of each
/// operand file, creating an empty file when the operand does not exist
/// (unless <c>-c</c> is set). Matches GNU coreutils <c>touch</c> for the
/// supported flag subset: <c>-d DATE</c> (parse date string), <c>-a</c>
/// (update access time only), <c>-m</c> (update mod time only), <c>-c</c>
/// (no-create), <c>-v</c> (verbose — no-op in the psm1 oracle, retained for
/// arg compatibility).
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reproduces its exact branches:
/// <list type="bullet">
/// <item>No operands → "missing file operand" error.</item>
/// <item><c>-d DATE</c> parses with <see cref="DateTime.TryParse(string, out DateTime)"/>;
/// on failure, emit "invalid date format" and return without touching any
/// operand (matches the psm1 oracle's early return).</item>
/// <item>Operand missing, parent missing, no <c>-c</c> → "No such file or
/// directory" error, continue with next operand.</item>
/// <item>Operand missing, parent present, no <c>-c</c> → create an empty
/// file, then set timestamps.</item>
/// <item>Operand missing with <c>-c</c> → silent skip.</item>
/// <item>Operand exists → set <see cref="FileInfo.LastWriteTime"/> unless
/// <c>-a</c>, set <see cref="FileInfo.LastAccessTime"/> unless <c>-m</c>.
/// (Default — neither <c>-a</c> nor <c>-m</c> — sets both.)</item>
/// </list>
/// <para>
/// <b>Three colliding flags</b> declared as explicit
/// <see cref="SwitchParameter"/>s: <c>-a</c> prefix-collides with the
/// catch-all <c>Arguments</c> parameter's own <c>a</c> prefix (since the
/// cmdlet's only other parameter starts with a different letter, this is
/// a soft collision but the psm1 oracle treated <c>-a</c> as a switch);
/// <c>-c</c> prefix-collides with <c>-Confirm</c>;
/// <c>-v</c> prefix-collides with <c>-Verbose</c>. <c>-d</c> and <c>-m</c>
/// stay in <c>Arguments</c> and are scanned by the manual loop.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTouch")]
[OutputType(typeof(string))]
public sealed class InvokeBashTouchCommand : PSCmdlet
{
    [Parameter] public SwitchParameter a { get; set; }
    [Parameter] public SwitchParameter c { get; set; }
    [Parameter] public SwitchParameter v { get; set; }

    // -d prefix-collides with -Debug. Declared as value-bearing string so
    // 'touch -d "2024-01-01" file' binds the date correctly. (Without this
    // declaration, -Debug consumes the bare -d as a switch and the date
    // string lands in Arguments as a bare positional, indistinguishable
    // from a file operand.)
    [Parameter] public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "touch", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "touch"))
            {
                WriteObject(line);
            }
            return;
        }

        bool accessOnly = a.IsPresent;
        bool noCreate = c.IsPresent;
        // -v was a documented switch in the psm1 oracle but had no effect
        // on output. Preserve that no-op behavior; the declared parameter
        // exists only to defuse the -Verbose prefix collision.
        _ = v;

        bool modOnly = false;
        string? dateStr = D;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];
            if (arg == "-d")
            {
                i++;
                if (i < args.Length) dateStr = args[i];
                i++;
                continue;
            }
            if (arg == "-m")
            {
                modOnly = true;
                i++;
                continue;
            }
            // -a / -c / -v are handled by the declared SwitchParameters
            // above. Tokens that look like them but reach Arguments are
            // a parse oddity — match psm1 oracle by still recognizing them.
            if (arg == "-a") { accessOnly = true; i++; continue; }
            if (arg == "-c") { noCreate = true; i++; continue; }
            if (arg == "-v") { i++; continue; }
            operands.Add(arg);
            i++;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "touch: missing file operand");
            return;
        }

        DateTime timestamp = DateTime.Now;
        if (dateStr is not null)
        {
            if (!DateTime.TryParse(dateStr, out timestamp))
            {
                FileSystemHelpers.WriteBashError(this, $"touch: invalid date format '{dateStr}'");
                return;
            }
        }

        foreach (var file in operands)
        {
            var absolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file);
            bool exists = File.Exists(absolute) || Directory.Exists(absolute);

            if (!exists)
            {
                if (noCreate) continue;

                var parent = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"touch: cannot touch '{file}': No such file or directory");
                    continue;
                }

                try
                {
                    // Empty-file create. Closing the FileStream immediately
                    // mirrors the psm1 oracle's New-Item -ItemType File.
                    using (File.Create(absolute)) { }
                }
                catch (Exception ex)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"touch: cannot touch '{file}': {ex.Message}");
                    continue;
                }
            }

            // Set timestamps. Directories use Directory.* setters; files use
            // File.* setters. Either is fine — we already filtered.
            try
            {
                if (Directory.Exists(absolute))
                {
                    if (!accessOnly) Directory.SetLastWriteTime(absolute, timestamp);
                    if (!modOnly) Directory.SetLastAccessTime(absolute, timestamp);
                }
                else
                {
                    if (!accessOnly) File.SetLastWriteTime(absolute, timestamp);
                    if (!modOnly) File.SetLastAccessTime(absolute, timestamp);
                }
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"touch: setting times of '{file}': {ex.Message}");
            }
        }
    }
}
