# Host terminal

An embedding that runs a ps-bash session in-process (BeagleTerm's session server, a GUI) gives every program
**pipes**. A program that needs a terminal (an editor, a pager, `htop`, `git commit`'s editor, `clear`) then fails
("TERM environment variable not set") or hangs. A host that owns a real terminal can lend it to ps-bash. A program
that would write straight to the host then runs on that terminal, as it would in bash.

Code: `Transpiler/HostTerminal.cs` (the API), `Cmdlets/HostTerminalLookup.cs` (the hook and the run). The call sites
are `InvokeBashNativeCommand.TryRunOnHostTerminal` and `InnerCommand` (for `env`, `command` and `time`).
Tests: `Cmdlets.Tests/HostTerminalTests`.

## API (host side)

- `IHostTerminal.Run(HostTerminalLaunch, CancellationToken) → int?`.
  - It runs the program with its stdin, stdout and stderr on the terminal, as that terminal's foreground job.
  - It blocks until the program ends, then returns the exit status (128+N for a death by signal N).
  - It returns **null** to decline. ps-bash then starts the program on pipes as before.
  - The token is cancelled when the user stops the command (Ctrl+C); the host ends the program and returns.
  - A thrown exception means the host could not start the program: `NAME: message`, status 126.
- `HostTerminalLaunch(Path, Arguments, WorkingDirectory, Environment)`.
  - `Arguments` is the argv after the program name.
  - `Environment` is COMPLETE: the process environment, the session's exports and a job's scalars. A null value is a
    variable the program must not have.
- `HostTerminal.Register(runspace, terminal)` and `Unregister(runspace)`.
  - Registrations are keyed by the runspace object in a `ConditionalWeakTable`, so a closed runspace drops its own.
  - `PsBash.Transpiler` does not reference SMA.
- After importing the module, the host also calls `[PsBash.Cmdlets.HostTerminalLookup]::Install($ExecutionContext.InvokeCommand)`
  once per runspace. Registering alone installs nothing.

## Which programs go to the terminal

A program runs on the terminal only when ALL of these hold:

- its runspace registered a terminal, and the terminal does not decline;
- it is a command word resolved to a program file, not a batch file and not a `ProgramFile` stand-in (`#!` on
  Windows, no x-bit);
- it reads no pipeline input (`MyInvocation.ExpectingInput` is false);
- its output goes straight to the host (`HostTerminalLookup.WritesToHost`): the pipe has the host's external writer
  and no downstream command, so the output is not captured, piped or redirected;
- its stderr is not merged into stdout (`StderrMerge.JoinsStdout`).

So `vim x`, the `htop` in `clear && htop`, and every program of a loop that writes to the screen go to the terminal.
`$(git log)`, `$x = git branch`, the `ls` in `ls | less`, `prog > f`, `prog 2>&1` and `'in' | prog` keep their pipes.
In bash those programs are not on the terminal either.

## How a program reaches the check

- **Bash-mode and PowerShell-mode lines alike.** `Install` adds a `PostCommandLookupAction`.
  - It first runs any action installed before it (a host's own, such as BeagleTerm's session-environment binder), so
    installation order does not matter.
  - It then swaps an `ApplicationInfo` that is still one for a stand-in script block that calls
    `Invoke-BashNative -HostTerminal -- PATH @argv`.
  - Piped input goes through a steppable pipeline and never reaches the terminal.
  - A failing status also fails the command for PowerShell's `$?` (`HostTerminalLookup.FailCommand`), so
    `vim x && next` behaves as it does with the program itself.
- **`Invoke-BashNative`** looks past the hook (`JobProgramLookup.Raw`; `JobProgramLookup.IsRaw` tells the hook to
  stand aside).
- **`env X=1 htop`, `command vim`, `time make`** run their program by name through `InnerCommand`, which makes the
  same check.

The stand-in script block for a path is created once and cached process-wide (`s_standIns`). It is reused across
runspaces, and each run executes in the runspace that looked it up (guarded by
`TheSameProgramInTwoRunspaces_SetsEachRunspacesOwnStatus`).

## Cost

- A runspace without a registered terminal pays one `ConditionalWeakTable` lookup per program lookup.
- A runspace with one pays one `ProgramFile.Plan` per program lookup (a `stat` and, on Unix, a read of the `#!` line).
- A program that goes to the terminal pays one `WritesToHost` check (SMA members resolved once, then field reads),
  one environment copy, and a 100 ms timer that passes a stop on to the host.
- None of this is on the path of the standalone ps-bash host, which registers no terminal.
