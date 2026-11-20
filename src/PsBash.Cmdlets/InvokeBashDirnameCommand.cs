using System.Management.Automation;
using PsBash.Cmdlets.Args;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDirname</c> function
/// (REFACTOR-2 Phase 1). Strips the last path component from each operand,
/// matching GNU <c>dirname</c>.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact path normalization (backslash -> slash, trailing slash trim) and
/// the three-way directory decision: empty -> "/", no slash -> ".", slash at
/// index 0 -> "/", otherwise the substring up to the last slash. The psm1
/// function takes <em>all</em> arguments as path operands (it has no flags), so
/// this cmdlet does the same.
///
/// Output model: emits a bare <see cref="string"/> per operand, identical to
/// the psm1 <c>New-BashObject -TypeName 'PsBash.TextOutput'</c> fast path. The
/// <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function via
/// InvokeCommand. No ScriptBlock construction on the hot path -> AOT-safe.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDirname")]
[OutputType(typeof(string))]
public sealed class InvokeBashDirnameCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "dirname", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "dirname"))
            {
                WriteObject(line);
            }
            return;
        }

        // GNU getopt_long: an unknown option is a usage error (status 1 + Try line), not a NAME.
        // REGRESSION: `dirname --bogus` printed `.`, status 0.
        var parsed = ArgParser.Parse(args, DirnameSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "dirname", parsed)) return;
        bool zero = parsed.Has("zero");
        var operands = parsed.Operands();
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "dirname");
            return;
        }

        foreach (var path in operands)
        {
            var dir = DirectoryPart(BashPath.ToShell(path));
            WriteObject(zero ? NulRecords.Record(dir) : dir);
        }
    }

    private static readonly OptSpecSet DirnameSpec = new(
        new[] { new OptSpec("zero", 'z', "zero") }, allowAbbrev: true, gnuInfoOptions: true);

    /// <summary>
    /// gnulib <c>mdir_name</c>: the name minus its last component and the slashes before it; <c>.</c> when nothing
    /// is left, <c>/</c> for the root (<c>a//b</c> → <c>a</c>, <c>/usr/bin/</c> → <c>/usr</c>, <c>""</c> → <c>.</c>).
    /// </summary>
    internal static string DirectoryPart(string f)
    {
        int prefix = f.Length > 0 && f[0] == '/' ? 1 : 0;
        int i = 0;
        while (i < f.Length && f[i] == '/') i++;
        int baseIdx = i;
        bool sawSlash = false;
        for (; i < f.Length; i++)
        {
            if (f[i] == '/') sawSlash = true;
            else if (sawSlash) { baseIdx = i; sawSlash = false; }
        }
        int len = baseIdx;
        while (prefix < len && f[len - 1] == '/') len--;
        return len == 0 ? "." : f[..len];
    }
}