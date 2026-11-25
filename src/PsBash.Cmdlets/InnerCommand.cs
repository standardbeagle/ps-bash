using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// Runs the command a wrapper names (<c>command NAME ARGS</c>, <c>env X=1 NAME ARGS</c>, <c>flock f NAME ARGS</c>) the
/// way bash runs a command in that position. It is THE one runner for a command a cmdlet starts by name; a wrapper that
/// re-derived it lost one rule after another:
/// <list type="bullet">
/// <item>the wrapper's pipeline records are the command's stdin (<c>echo x | env FOO=1 cat</c> printed nothing: env bound
/// the records and dropped them, flock rejected them). A program is fed their BYTES, as a pipe into it is;</item>
/// <item>the nested script's error stream is merged and re-written on the wrapper's own, so the wrapper's redirect covers
/// it (<c>env ls /x 2&gt;/dev/null</c> printed ls's error anyway);</item>
/// <item>a program file the OS cannot start as it is runs through <see cref="PsBash.Core.ProgramFile"/>, a program under
/// <c>2&gt;&amp;1</c> gets one pipe for both streams (<c>docs/specs/stdio-messaging.md</c> §4), and the command's status
/// becomes the wrapper's.</item>
/// </list>
/// Functions are bypassed (resolution is alias, cmdlet, program). Directive 12: the name and arguments are bound
/// parameters of a fixed script, never part of its text.
/// </summary>
internal static class InnerCommand
{
    /// <summary>What a wrapper is: its name in messages, how it reports a name nothing answers to, and whether a bash
    /// builtin the emitter implements (<c>eval</c>, <c>cd</c>, ...) can run in it (only <c>command</c>/<c>builtin</c>: env
    /// and flock run programs).</summary>
    internal sealed record Wrapper(string Name, Action<PSCmdlet, string> NotFound, bool ShellBuiltins);

    /// <summary>Run <c>argv[0]</c> with <c>argv[1..]</c>, fed <paramref name="stdin"/> (the wrapper's pipeline records;
    /// empty = it inherits the caller's stdin).</summary>
    internal static void Run(PSCmdlet cmdlet, Wrapper wrapper, string[] argv, List<object> stdin)
    {
        // command command x, command builtin x: the wrappers only bypass functions, so peel them.
        while (argv.Length > 1 && argv[0] is "command" or "builtin" && !argv[1].StartsWith('-')) argv = argv[1..];
        var invoke = cmdlet.InvokeCommand;
        string capture = ErrorCapture(cmdlet);
        // A builtin the emitter implements (`command eval …`, `builtin set -- a`, `command exec cmd`) has no cmdlet to
        // resolve: it runs as its static command, the operands already expanded (BashBuiltins.RunAsSource, the same
        // runner Resolve-BashCommand uses for `$cmd`).
        if (wrapper.ShellBuiltins && BashBuiltins.RunAsSource.Contains(argv[0]))
        {
            var callArgs = new object[argv.Length];
            callArgs[0] = ScriptBlock.Create(PsBuild.BuiltinAsSourceScript(argv[0]));
            for (int k = 1; k < argv.Length; k++) callArgs[k] = argv[k];
            // Dot-sourced: the caller's scope, as the literal builtin runs (`command eval 'x=1'` sets x there). 2>&1 and
            // re-written: the nested script's error stream is not this cmdlet's, so `command . 2>/dev/null` printed anyway.
            const string asSource = "$__psbash_cr = $args[0]; $__psbash_ca = @($args | Select-Object -Skip 1); ";
            var runner = ScriptBlock.Create(asSource + (stdin.Count > 0 ? "$input | " : "") + ". $__psbash_cr @__psbash_ca" + capture);
            foreach (var o in invoke.InvokeScript(false, runner, stdin.Count > 0 ? stdin : null, callArgs))
            {
                if (o?.BaseObject is ErrorRecord er) FileSystemHelpers.WriteErrorRecord(cmdlet, er);
                else cmdlet.WriteObject(o);
            }
            FileSystemHelpers.AdoptLastExitCode(cmdlet);
            return;
        }
        var name = argv[0];
        // `command :`, `command false`, a bare `command builtin`: Resolve-BashCommand's own trivial blocks
        // (BashBuiltins.CallStackBuiltins); there is no command of that name to find. (`env true`: the program.)
        if (name is ":" or "true" || name == "builtin" && wrapper.ShellBuiltins) { FileSystemHelpers.SetLastExitCode(cmdlet, 0); return; }
        if (name == "false") { FileSystemHelpers.SetLastExitCode(cmdlet, 1); return; }
        CommandInfo? target = null;
        try
        {
            // A builtin is its run-time command first (`command bind`, `command enable -n x`): Invoke-Bash<Name>, a cmdlet
            // or a module function, which the bare name finds only when an alias maps it (BashBuiltins, the one table).
            var found = invoke.InvokeScript(
                "param($n, $b) if ($b) { $f = Get-Command ('Invoke-Bash' + [WildcardPattern]::Escape($n)) -CommandType Cmdlet,Function " +
                "-ErrorAction SilentlyContinue | Select-Object -First 1; if ($f) { return $f } }; " +
                "Get-Command ([WildcardPattern]::Escape($n)) -CommandType Alias,Cmdlet,Application,ExternalScript " +
                "-ErrorAction SilentlyContinue | Select-Object -First 1", name, BashBuiltins.Names.Contains(name) && name != "[");
            foreach (var r in found)
            {
                if (r?.BaseObject is CommandInfo ci) { target = ci; break; }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
        }

        if (target == null)
        {
            wrapper.NotFound(cmdlet, name);
            return;
        }

        // A program file the OS cannot start as it is (`command ./configure`, a #! script on PATH): Get-Command hands
        // back the raw ApplicationInfo, so the lookup hook's stand-in never applied; ProgramFile gives the same one.
        object invokeTarget = target;
        if (target is ApplicationInfo file && PsBash.Core.ProgramFile.Plan(file.Path) is { } launch)
            invokeTarget = ScriptBlock.Create(launch.StandInScript(PsBash.Core.ProgramFile.Shown(name, file.Path)));

        // `env X=1 htop`, `command vim`: a program this command runs is on the host's terminal when the command itself
        // writes to the host (HostTerminal) - a bare statement, nothing piped in, stderr not merged.
        if (invokeTarget is ApplicationInfo onTerminal && stdin.Count == 0 && !StderrMerge.JoinsStdout(cmdlet)
            && !InvokeBashNativeCommand.IsBatchFile(onTerminal.Path)
            && HostTerminalLookup.TryRun(cmdlet, name, onTerminal.Path, argv.Skip(1).ToArray()))
            return;

        // `command prog 2>&1`: a program gets ONE pipe for stdout and stderr (docs/specs/stdio-messaging.md §4). In a
        // background job always through RunMerged: `& $c` would give it the process environment, not the job's scalars.
        if (invokeTarget is ApplicationInfo app && !InvokeBashNativeCommand.IsBatchFile(app.Path)
            && (StderrMerge.JoinsStdout(cmdlet) || PsBash.Core.ScalarOverlay.Current is not null))
        {
            try
            {
                FileSystemHelpers.SetLastExitCode(cmdlet, InvokeBashNativeCommand.RunMerged(cmdlet, app.Path, argv.Skip(1),
                    inheritStdin: true, input: stdin.Count > 0 ? stdin : null));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(cmdlet, $"{wrapper.Name}: {name}: {ex.Message}");
            }
            return;
        }

        // Fixed body: $args[0] is the resolved command, the rest is splatted. No user text is ever
        // part of the script.
        const string prologue =
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; ";
        // With upstream input the inner command must be fed it explicitly (`$input` is the list
        // handed to InvokeScript); without, it inherits the caller's stdin untouched.
        // `[` is called by that name, spelled in the call: Invoke-BashTest knows its bracket form (the closing `]`)
        // from the text it was invoked by, and `& $c` reached it as plain `test` ("too many arguments").
        // 2>&1: the nested script's error stream is not this cmdlet's, so `command ls /x 2>/dev/null` printed the error
        // anyway; merged here, each ErrorRecord is re-written on this command's error stream below.
        string call = (name == "[" ? "& '[' @rest" : "& $c @rest") + capture;
        var invokeBody = stdin.Count > 0 ? prologue + "$input | " + call : prologue + call;
        var invokeArgs = new object[argv.Length];
        invokeArgs[0] = invokeTarget;
        for (int k = 1; k < argv.Length; k++) invokeArgs[k] = argv[k];

        // A program is fed the records' BYTES, as a pipe into it is (ConvertTo-BashNativeStdin): PowerShell's native
        // pipe formats any other object, so `printf 'a\n' | command cat` gave cat a BashText table.
        var input = invokeTarget is ApplicationInfo ? NativeStdinBytes(stdin) : stdin;

        try
        {
            var output = stdin.Count > 0
                ? invoke.InvokeScript(invokeBody, false, PipelineResultTypes.None, input, invokeArgs)
                : invoke.InvokeScript(invokeBody, invokeArgs);
            foreach (var item in output)
            {
                if (item?.BaseObject is ErrorRecord er) FileSystemHelpers.WriteErrorRecord(cmdlet, er);
                else cmdlet.WriteObject(item);
            }
            FileSystemHelpers.AdoptLastExitCode(cmdlet);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(cmdlet, $"{wrapper.Name}: {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The suffix that captures a nested script's error stream so <paramref name="cmdlet"/> can re-write each record on
    /// its own (<see cref="FileSystemHelpers.WriteErrorRecord"/>, so the cmdlet's redirect covers it). A bare
    /// <c>2&gt;&amp;1</c> is a bash merge only when the cmdlet's own stderr joins its stdout; otherwise
    /// <see cref="InvokeBashKeepStderrCommand"/> tells the nested command so (<see cref="StderrMerge"/>), and a child it
    /// spawns keeps its stderr apart: <c>env sh -c 'echo e &gt;&amp;2' 2&gt;/dev/null</c> printed e as stdout.
    /// </summary>
    internal static string ErrorCapture(PSCmdlet cmdlet) =>
        StderrMerge.JoinsStdout(cmdlet) ? " 2>&1" : " 2>&1 | Invoke-BashKeepStderr";

    /// <summary>The records as the bytes a program reads (<see cref="NativeStdinBridge.RecordBytes"/>), one byte[] each.</summary>
    internal static List<object> NativeStdinBytes(List<object> records)
    {
        var bytes = new List<object>(records.Count);
        foreach (var r in records)
        {
            var b = r is PSObject { BaseObject: byte[] raw } ? raw : r as byte[] ?? NativeStdinBridge.RecordBytes(r);
            if (b.Length > 0) bytes.Add(b);
        }
        return bytes;
    }
}
