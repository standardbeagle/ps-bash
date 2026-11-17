using PsBash.Core.Parser;

namespace PsBash.Core;

/// <summary>
/// How a program FILE the shell resolved (a PATH hit, <c>./x</c>, <c>/abs/x</c>) is started: the one answer for every
/// site that holds such a file, so none hands it straight to the OS and gets it wrong (docs/specs/program-files.md).
/// <para>
/// Windows starts only a file with a PATHEXT extension. Anything else handed to <c>CreateProcess</c> fails ("not a
/// valid Win32 application"), and handed to PowerShell as a resolved command it goes to the shell's "open with"
/// association, which opens a dialog nobody answers and reports success. So a <c>#!</c> script runs through its
/// interpreter, as the kernel does for bash, and any other file is bash's "Permission denied", 126. Unix: the kernel
/// handles <c>#!</c> itself; only a file with no execute bit needs an answer (PowerShell hands it to xdg-open).
/// </para>
/// <para>Sites: the host's command-lookup hook (a word PowerShell resolves), <c>command</c>, <c>Invoke-BashNative</c>
/// and the cmdlets that run a merged program (<c>env</c>, <c>xargs</c>, <c>find -exec</c>, <c>time</c>), <c>nohup</c>, the
/// interactive shell's direct launch. Guard: <c>Core.Tests/ProgramFileGuardTests</c>.</para>
/// </summary>
public static class ProgramFile
{
    /// <summary>Shell interpreters a <c>#!</c> line may name that ps-bash itself runs.</summary>
    private static readonly HashSet<string> ShellInterpreters = new(StringComparer.Ordinal) { "sh", "bash", "dash", "ash", "ksh" };

    /// <summary>
    /// How to start <paramref name="path"/>; null when the OS starts it as it is (a PATHEXT program on Windows, a file
    /// with an execute bit on Unix, or a file that cannot be read, which then fails the way it always did).
    /// </summary>
    public static ProgramLaunch? Plan(string path)
    {
        if (!OperatingSystem.IsWindows())
            return HasExecuteBit(path) ? null : ProgramLaunch.NotExecutable(path);
        if (HasRunnableExtension(path) || !TryReadShebang(path, out string? shebang)) return null;
        return shebang is null ? ProgramLaunch.NotExecutable(path) : FromShebang(path, shebang);
    }

    /// <summary>
    /// The launch a <c>#!</c> line asks for: <c>#!/bin/sh</c>, <c>#!/usr/bin/env bash</c> run through ps-bash's
    /// <c>bash</c>; another interpreter (<c>#!/usr/bin/env python3</c>, <c>#!/usr/bin/node --flag</c>) by name with its
    /// options. <c>#!/usr/bin/env</c> naming no program cannot run: "Permission denied".
    /// </summary>
    public static ProgramLaunch FromShebang(string path, string shebangLine)
    {
        var words = new List<string>(shebangLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (words.Count > 0 && BaseName(words[0]) == "env")
        {
            words.RemoveAt(0);
            while (words.Count > 0 && words[0].StartsWith('-')) words.RemoveAt(0); // env -S, env -i ...
        }
        if (words.Count == 0) return ProgramLaunch.NotExecutable(path);
        string name = BaseName(words[0]);
        return new ProgramLaunch(path, ShellInterpreters.Contains(name), name, words.Skip(1).ToArray());
    }

    /// <summary>bash names the word as typed (<c>./README.md</c>), or for a PATH hit the file it found.</summary>
    public static string Shown(string? commandName, string path)
        => (commandName ?? "").IndexOfAny(['/', '\\']) >= 0 ? commandName! : path;

    /// <summary>Any execute bit (user, group or other); true when the mode cannot be read, so the file runs as before.</summary>
    public static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows()) return true; // no mode bits: PATHEXT and #! decide there
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>The <c>#!</c> line's text (null when the file has none); false when the file cannot be read.</summary>
    public static bool TryReadShebang(string path, out string? shebang)
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

    private static bool HasRunnableExtension(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length == 0) return false;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';'))
            if (ext.Equals(entry.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BaseName(string interpreter)
    {
        int slash = interpreter.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? interpreter : interpreter[(slash + 1)..];
    }
}

/// <summary>One answer of <see cref="ProgramFile.Plan"/>: the file cannot run, or which interpreter runs it.</summary>
public sealed class ProgramLaunch
{
    internal ProgramLaunch(string path, bool shell, string? interpreter, IReadOnlyList<string> interpreterArgs)
    {
        Path = path; Shell = shell; Interpreter = interpreter; InterpreterArgs = interpreterArgs;
    }

    internal static ProgramLaunch NotExecutable(string path) => new(path, false, null, []);

    /// <summary>The program file.</summary>
    public string Path { get; }

    /// <summary>False: bash's "Permission denied", 126 (no <c>#!</c> line, no execute bit, <c>#!/usr/bin/env</c> alone).</summary>
    public bool Runnable => Interpreter is not null;

    /// <summary>The interpreter is a shell: ps-bash runs the file, as <c>bash FILE</c> does (one nested launcher).</summary>
    public bool Shell { get; }

    /// <summary>The interpreter's name (<c>python3</c>, <c>bash</c>); null when not <see cref="Runnable"/>.</summary>
    public string? Interpreter { get; }

    /// <summary>The options the <c>#!</c> line gives the interpreter (<c>#!/bin/bash -e</c>: <c>-e</c>).</summary>
    public IReadOnlyList<string> InterpreterArgs { get; }

    /// <summary>
    /// The PowerShell that runs the file in place of the resolved command, the command's arguments arriving as
    /// <c>@args</c>: a shell's through <c>Invoke-BashBash</c>, another interpreter by name, then its options, the file and
    /// the arguments. Pipeline input is forwarded when there is any. Not runnable: bash's message naming
    /// <paramref name="shown"/> (<see cref="ProgramFile.Shown"/>) on stderr, never terminating, and status 126.
    /// </summary>
    public string StandInScript(string shown)
    {
        if (!Runnable) return PermissionDeniedScript(shown);
        var sb = new System.Text.StringBuilder(Shell ? "Invoke-BashBash" : "& " + PsBuild.SingleQuote(Interpreter!));
        foreach (var a in InterpreterArgs) sb.Append(' ').Append(PsBuild.SingleQuote(a));
        sb.Append(' ').Append(PsBuild.SingleQuote(Path)).Append(" @args");
        string run = sb.ToString();
        return PsBuild.ForwardStageInputIfAny(run);
    }

    /// <summary>
    /// The argv that starts the file as a process, before the command's own arguments: the program
    /// (<paramref name="shellExe"/>, the ps-bash launcher, for a shell; else the interpreter found on PATH, null when
    /// there is none), its options, then the file. For a site that must start a process itself (<c>nohup</c>).
    /// </summary>
    public (string? Program, List<string> Args) CommandLine(string? shellExe, IEnumerable<string> searchDirectories)
    {
        var args = new List<string>(InterpreterArgs) { Path };
        if (!Runnable) return (null, args);
        return (Shell ? shellExe : ExecutableLookup.Find(Interpreter!, searchDirectories), args);
    }

    /// <summary>The stand-in for a file that cannot run: bash's message on stderr and status 126.</summary>
    public static string PermissionDeniedScript(string shown)
        => "Write-Error -Message 'bash: " + BashPath.ToShell(shown).Replace("'", "''")
           + ": Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126";
}
