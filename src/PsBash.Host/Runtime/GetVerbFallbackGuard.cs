using System.Management.Automation;

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

        // A bare word that PATH search matched to a file Windows cannot execute (`License` -> nodejs\LICENSE,
        // `README.md`): PowerShell would hand it to the shell's "open with" association, which opens a window or
        // waits for a dialog nobody can answer. bash reports a PATH entry it may not execute: Permission denied, 126.
        if (OperatingSystem.IsWindows())
        {
            invoke.PostCommandLookupAction = (sender, e) =>
            {
                if (e.Command is ApplicationInfo app && IsBareWord(e.CommandName) && !IsRunnable(app.Path))
                    e.CommandScriptBlock = ScriptBlock.Create(PermissionDeniedScript(app.Path));
            };
        }
    }

    /// <summary>The stand-in command: bash's message on stderr (never terminating) and status 126.</summary>
    internal static string PermissionDeniedScript(string path)
        => "Write-Error -Message 'bash: " + path.Replace('\\', '/').Replace("'", "''")
           + ": Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126";
    internal static bool IsBareWord(string? name)
        => !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\', ':']) < 0;

    /// <summary>A PATHEXT extension, or a <c>#!</c> script (left to the existing script handling).</summary>
    internal static bool IsRunnable(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length > 0)
        {
            foreach (var entry in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';'))
                if (ext.Equals(entry.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.ReadByte() == '#' && fs.ReadByte() == '!';
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>PowerShell retries only a word with no verb separator and no path separator.</summary>
    internal static bool CanBeRetried(string name)
        => name.Length > 0 && name.IndexOf('-') < 0 && name.IndexOf('\\') < 0;

    /// <summary>True when <paramref name="name"/> is exactly PowerShell's retry of <paramref name="bare"/>.</summary>
    internal static bool IsRetryOf(string name, string bare)
        => name.Length == bare.Length + RetryPrefix.Length
           && name.StartsWith(RetryPrefix, StringComparison.Ordinal)
           && name.EndsWith(bare, StringComparison.Ordinal);
}
