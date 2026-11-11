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

        // A command word that resolved to a file Windows cannot execute (no PATHEXT extension): PowerShell would hand
        // it to the shell's "open with" association, which opens a window or a "Pick an app" dialog nobody answers,
        // and reports success. A `#!` script (`./configure`, `PATH=_tmp:$PATH; hello`) runs through its interpreter,
        // as the kernel does for bash; any other file (`License` -> nodejs\LICENSE, `./README.md`) is bash's
        // "Permission denied", 126.
        if (OperatingSystem.IsWindows())
        {
            invoke.PostCommandLookupAction = (sender, e) =>
            {
                if (e.Command is not ApplicationInfo app || HasRunnableExtension(app.Path)) return;
                if (!TryReadShebang(app.Path, out string? shebang)) return;
                e.CommandScriptBlock = ScriptBlock.Create(shebang is null
                    ? PermissionDeniedScript(Shown(e.CommandName, app.Path))
                    : ShebangScript(app.Path, shebang));
            };
        }
        else
        {
            // Unix: PowerShell hands a file without an execute bit (`./doc` after `chmod -x doc`) to xdg-open and
            // reports success; bash says "Permission denied", 126.
            invoke.PostCommandLookupAction = (sender, e) =>
            {
                if (e.Command is not ApplicationInfo app || HasExecuteBit(app.Path)) return;
                e.CommandScriptBlock = ScriptBlock.Create(PermissionDeniedScript(Shown(e.CommandName, app.Path)));
            };
        }
    }

    /// <summary>bash names the word as typed (<c>./README.md</c>), or for a PATH hit the file it found.</summary>
    private static string Shown(string? commandName, string path)
        => (commandName ?? "").IndexOfAny(['/', '\\']) >= 0 ? commandName! : path;

    /// <summary>Any execute bit (user, group or other); true when the mode cannot be read, so the file runs as before.</summary>
    internal static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows()) return true; // no mode bits: PATHEXT and #! decide there
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Shell interpreters a <c>#!</c> line may name that ps-bash itself runs.</summary>
    private static readonly HashSet<string> ShellInterpreters = new(StringComparer.Ordinal) { "sh", "bash", "dash", "ash", "ksh" };

    /// <summary>
    /// The stand-in command for a <c>#!</c> script: <c>#!/bin/sh</c>, <c>#!/usr/bin/env bash</c> run through ps-bash's
    /// <c>bash</c>; another interpreter (<c>#!/usr/bin/env python3</c>, <c>#!/usr/bin/node --flag</c>) runs by name with
    /// its options, then the file, then the command's arguments. Pipeline input is forwarded when there is any.
    /// </summary>
    internal static string ShebangScript(string path, string shebangLine)
    {
        var words = new List<string>(shebangLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (words.Count > 0 && BaseName(words[0]) == "env")
        {
            words.RemoveAt(0);
            while (words.Count > 0 && words[0].StartsWith('-')) words.RemoveAt(0); // env -S, env -i ...
        }
        if (words.Count == 0) return PermissionDeniedScript(path);
        string name = BaseName(words[0]);
        var sb = new System.Text.StringBuilder(ShellInterpreters.Contains(name) ? "Invoke-BashBash" : "& " + PsBash.Core.Parser.PsBuild.SingleQuote(name));
        for (int i = 1; i < words.Count; i++) sb.Append(' ').Append(PsBash.Core.Parser.PsBuild.SingleQuote(words[i]));
        sb.Append(' ').Append(PsBash.Core.Parser.PsBuild.SingleQuote(path)).Append(" @args");
        string run = sb.ToString();
        return "$__psbash_in = @($input); if ($__psbash_in.Count -gt 0) { $__psbash_in | " + run + " } else { " + run + " }";
    }

    private static string BaseName(string interpreter)
    {
        int slash = interpreter.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? interpreter : interpreter[(slash + 1)..];
    }

    private static bool HasRunnableExtension(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length == 0) return false;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';'))
            if (ext.Equals(entry.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The <c>#!</c> line's text (null when the file has none); false when the file cannot be read.</summary>
    internal static bool TryReadShebang(string path, out string? shebang)
    {
        shebang = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[512];
            int n = fs.Read(buf, 0, buf.Length);
            if (n < 2 || buf[0] != '#' || buf[1] != '!') return true;
            int end = Array.IndexOf(buf, (byte)'\n', 2, n - 2);
            if (end < 0) end = n;
            shebang = System.Text.Encoding.Latin1.GetString(buf, 2, end - 2).Trim();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The stand-in command: bash's message on stderr (never terminating) and status 126.</summary>
    internal static string PermissionDeniedScript(string path)
        => "Write-Error -Message 'bash: " + path.Replace('\\', '/').Replace("'", "''")
           + ": Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126";
    /// <summary>A PATHEXT extension, a <c>#!</c> script (run by <see cref="ShebangScript"/>), or a file it cannot read.</summary>
    internal static bool IsRunnable(string path)
        => HasRunnableExtension(path) || !TryReadShebang(path, out string? shebang) || shebang is not null;

    /// <summary>PowerShell retries only a word with no verb separator and no path separator.</summary>
    internal static bool CanBeRetried(string name)
        => name.Length > 0 && name.IndexOf('-') < 0 && name.IndexOf('\\') < 0;

    /// <summary>True when <paramref name="name"/> is exactly PowerShell's retry of <paramref name="bare"/>.</summary>
    internal static bool IsRetryOf(string name, string bare)
        => name.Length == bare.Length + RetryPrefix.Length
           && name.StartsWith(RetryPrefix, StringComparison.Ordinal)
           && name.EndsWith(bare, StringComparison.Ordinal);
}
