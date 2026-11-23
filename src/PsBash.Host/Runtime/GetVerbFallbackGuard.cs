using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Host.Runtime;

/// <summary>
/// Stops PowerShell's default-verb retry. When a bare command word is not found, PowerShell looks it up again as
/// <c>get-WORD</c>, so the bash line <c>Package glibc</c> ran <c>Get-Package</c> (which can take minutes or prompt),
/// <c>process x</c> ran <c>Get-Process</c> and <c>module x</c> ran <c>Get-Module</c>. In bash an unknown word is
/// "command not found" (127), nothing else.
/// <para>The retry is recognisable: the pre-lookup hook sees <c>WORD</c> and then, as the very next lookup,
/// <c>get-WORD</c> (lower-case verb, the word unchanged). That second lookup is handed to the runspace's
/// command-not-found handler instead of being resolved. A <c>Get-Thing</c> the script wrote itself is never the
/// lookup right after a bare <c>Thing</c>, so it resolves as before.</para>
/// <para>Compiled delegates, not script blocks: the hooks run on every command lookup.</para>
/// </summary>
internal static class GetVerbFallbackGuard
{
    private const string RetryPrefix = "get-";

    public static void Install(CommandInvocationIntrinsics invoke)
    {
        string? lastBare = null;
        invoke.PreCommandLookupAction = (sender, e) =>
        {
            string name = e.CommandName ?? "";
            string? bare = lastBare;
            lastBare = null;
            if (bare is not null && IsRetryOf(name, bare))
            {
                invoke.CommandNotFoundAction?.Invoke(sender, e);
                return;
            }
            if (CanBeRetried(name)) lastBare = name;
        };

        // A command word that resolved to a program file the OS cannot start as it is (Windows: no PATHEXT extension;
        // Unix: no execute bit): PowerShell would hand it to the shell's "open with" association / xdg-open and report
        // success. A `#!` script (`./configure`, `PATH=_tmp:$PATH; hello`) runs through its interpreter, as the kernel
        // does for bash; any other file (`License` -> nodejs\LICENSE, `./README.md`) is bash's "Permission denied",
        // 126. ProgramFile decides, for this hook and for every cmdlet that holds a resolved file.
        invoke.PostCommandLookupAction = (sender, e) =>
        {
            RecordHashed(e);
            if (e.Command is not ApplicationInfo app) return;
            if (ProgramFile.Plan(app.Path) is { } launch)
            {
                e.CommandScriptBlock = ScriptBlock.Create(launch.StandInScript(ProgramFile.Shown(e.CommandName, app.Path)));
                return;
            }
            // An EXPORTED scalar holds a value the process environment cannot (Linux: an invalid byte): PowerShell's
            // launcher would give the program U+FFFD for it. Invoke-BashNative starts it with the exact bytes
            // (NativeRunner). Only then: every other program keeps PowerShell's launcher, and the terminal.
            if (!NativeRunner.LookingUpRaw && ProcessScalars.KeepsAnyExported)
                e.CommandScriptBlock = ScriptBlock.Create(NativeRunner.StandIn(app.Path));
        };
    }

    /// <summary>
    /// bash's hash table (<c>hash</c>): a command word without a slash that resolved to a program file through PATH is
    /// remembered with one more hit. Builtins, functions and the runtime's own commands resolve to something else and
    /// are not hashed, as bash hashes no builtin.
    /// </summary>
    private static void RecordHashed(CommandLookupEventArgs e)
    {
        if (e.Command is not ApplicationInfo app || !IsHashableName(e.CommandName)) return;
        if (PsBash.Core.CommandHashTable.InProbe) return;
        if (System.Management.Automation.Runspaces.Runspace.DefaultRunspace is not { } runspace) return;
        PsBash.Core.CommandHashTable.For(runspace).RecordUse(e.CommandName, app.Path, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>A bare command word: one with a slash (<c>./x</c>, <c>/bin/ls</c>) is run as given, never looked up or hashed.</summary>
    internal static bool IsHashableName(string? name)
        => !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\']) < 0;

    /// <summary>PowerShell retries only a word with no verb separator and no path separator.</summary>
    internal static bool CanBeRetried(string name)
        => name.Length > 0 && name.IndexOf('-') < 0 && name.IndexOf('\\') < 0;

    /// <summary>True when <paramref name="name"/> is exactly PowerShell's retry of <paramref name="bare"/>.</summary>
    internal static bool IsRetryOf(string name, string bare)
        => name.Length == bare.Length + RetryPrefix.Length
           && name.StartsWith(RetryPrefix, StringComparison.Ordinal)
           && name.EndsWith(bare, StringComparison.Ordinal);
}
