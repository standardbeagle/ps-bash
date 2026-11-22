using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Pathname expansion of one word, emitted by the transpiler as <c>@(ConvertTo-BashGlob &lt;pattern&gt;)</c> for
/// every command operand, <c>for</c> list item and array element that contains an unquoted glob character.
///
/// <para>bash expands <c>echo *.txt</c> in the SHELL, after word splitting and before the command runs, so the
/// command only ever sees file names. The mapped cmdlets used to be handed the literal pattern and expand it
/// themselves (some), and <c>echo</c>, <c>printf</c>, user functions, native programs, <c>for</c> lists and
/// arrays never did, so <c>echo *</c> printed <c>*</c>. See emitter-strategy.md "Pathname expansion".</para>
///
/// <para>The pattern text is bash's dialect: <c>\</c> escapes (the emitter escapes quoted <c>*</c> / <c>?</c> /
/// <c>[</c> / <c>\</c>), <c>/</c> separates components. Output: the matches as written (relative for a relative
/// pattern), sorted; <c>nullglob</c> gives nothing for no match, <c>failglob</c> an error and status 1, and by
/// default the pattern itself (escapes removed) is the one word, as bash keeps it.</para>
///
/// <para>The mapped cmdlets that expand their own operands (cat, ls, grep, ...) still do so for what they
/// receive, so a matched NAME that itself contains <c>* ? [</c> is globbed once more by them (a file called
/// <c>a[1]</c> next to <c>a1</c>): rare, and the same as for a quoted operand today. Their dialects differ
/// (cat/wc only see <c>*</c> and <c>?</c>, ls also <c>[</c>), so no single escaping could serve them all.</para>
/// </summary>
[Cmdlet(VerbsData.ConvertTo, "BashGlob")]
[OutputType(typeof(string))]
public sealed class ConvertToBashGlobCommand : BashCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowNull]
    [AllowEmptyString]
    public string? Pattern { get; set; }

    /// <summary><c>set -f</c> / <c>set -o noglob</c>: patterns stay literal.</summary>
    internal static bool IsNoGlob(SessionState session) => session.PSVariable.GetValue("global:__BashNoglob") is true;

    protected override void ProcessRecord()
    {
        string pattern = Pattern ?? string.Empty;
        if (IsNoGlob(SessionState) || !BashGlob.HasPattern(pattern))
        {
            WriteObject(BashGlob.Unescape(pattern));
            return;
        }

        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var matches = BashGlob.Expand(pattern, cwd, InvokeBashShoptCommand.IsEnabled("dotglob"),
            InvokeBashShoptCommand.IsEnabled("globstar"), InvokeBashShoptCommand.IsEnabled("nocaseglob"),
            skipDots: InvokeBashShoptCommand.IsEnabled("globskipdots"));
        if (matches.Count == 0)
        {
            if (InvokeBashShoptCommand.IsEnabled("failglob")) FailGlob(this, BashGlob.Unescape(pattern));
            if (!InvokeBashShoptCommand.IsEnabled("nullglob"))
                WriteObject(BashGlob.Unescape(pattern));
            return;
        }

        foreach (var m in matches)
            WriteObject(m);
    }

    /// <summary>The <c>failglob</c> error: status 1, the command ends, and <c>$global:__BashFailGlob</c> is set so a
    /// <c>for</c> list that failed keeps status 1 though its body never ran (PsBuild.ZeroIterationStatus).</summary>
    internal static void FailGlob(PSCmdlet cmdlet, string word)
    {
        FileSystemHelpers.SetLastExitCode(cmdlet, 1);
        cmdlet.SessionState.PSVariable.Set("global:__BashFailGlob", true);
        cmdlet.ThrowTerminatingError(new ErrorRecord(
            new InvalidOperationException(ShellDiagnostic.Prefix(cmdlet.SessionState) + "no match: " + word),
            "NoGlobMatch", ErrorCategory.ObjectNotFound, word));
    }
}
