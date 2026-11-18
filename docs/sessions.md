# Named sessions

A named session is a durable `ps-bash-host` that you address by name instead of by the
launcher that happened to start it. It is the building block for remote and agent use
(B06/B07 in the [production review](production-readiness-review-2026-10-18.md)).

```bash
ps-bash session start build --idle-secs 3600   # create or reuse; stays up for 1 h of silence
PSBASH_SESSION=build ps-bash -c 'cd /src && make'   # attach from any launcher, any time
PSBASH_SESSION=build ps-bash -c 'pwd'               # same host, same runspace pool
ps-bash session list                                # named sessions: name, state, pid, start time
ps-bash session status build
ps-bash session stop build
```

## Guarantees

- **Outlives launchers.** An unnamed host ends with the shell session that spawned it. A named
  host has no such owner; only `session stop` or its idle window ends it. The default idle
  window is 24 h (`--idle-secs N` overrides; `PSBASH_HOST_IDLE_SECS` is honoured if already set).
- **Concurrent across sessions.** Each session is its own host process. Execution inside one
  host is still serialized (cwd and `$env:` are process-global), but two sessions never wait
  on each other. Use one session per independent workload or agent.
- **Name safety.** Names are 1-32 characters of `A-Z a-z 0-9 _ -`; the name becomes part of a
  socket path, so anything else is refused.
- **Per-user.** Sessions are scoped to the OS user and live under the 0700 runtime directory.

## Stateful by default

`session start` pins ONE runspace for the session's life, so shell variables, functions,
aliases, `export`s and the working directory carry from one `-c` call to the next:

```bash
PSBASH_SESSION=build ps-bash -c 'x=5; cd /src'
PSBASH_SESSION=build ps-bash -c 'echo $x; pwd'     # 5, then /src (C:/src on Windows)
```

Only exported variables reach a nested shell the session starts (v0.15.0): after `x=5`,
`sh -c 'echo $x'` prints an empty line until you `export x`.

- The launcher does not re-impose its own cwd or environment on a stateful session (it would
  undo the session's `cd`/`export`); only `PSBASH_*` control variables are forwarded.
- A command that faults does not end the session: the pinned runspace is dropped and the next
  call builds a clean one.
- `session start NAME --stateless` gives the older model: a fresh pooled runspace per command.
  `start` refuses to switch a running session between modes; `session stop` it first.
  `session list` shows each session's mode.

## Signals

`$$` in a session is the pid of its host process, which serves every command of the session.
Since v0.15.0 a signal aimed at that pid from a ps-bash shell (`sh -c "kill -USR1 $$"`,
`kill -TERM $$`) is delivered to the script the host is running. It travels as a message to the host; no OS
signal is sent:

- a `trap` for the signal runs at the script's next top-level statement;
- with no trap, the script ends with 128+N and the host stays up, except for the signals below;
- with no trap, STOP, TSTP, TTIN, TTOU and CONT are ignored, as are CHLD, URG and WINCH: a host
  command cannot be suspended.

A signal that arrives during one long command or a `( )` / `$( )` body waits for it to finish.
On Linux and macOS a real SIGUSR1 or SIGUSR2 sent to the host or the launcher by another
program is routed the same way, with one exception: on macOS an outside SIGUSR1 does not reach
a `trap … USR1` (the .NET runtime owns that signal there); SIGUSR2 does.

## Reattachable terminal

```bash
ps-bash session attach build            # start (or rejoin) the session's shell; Ctrl-] d detaches
ps-bash session attach build --read-only
ssh -t devbox ps-bash session attach build      # remote: a dropped connection loses nothing
```

`attach` starts a detached **terminal broker** (`ps-bash session serve build`, hidden) the first time.
The broker owns a PTY and the interactive shell on it, so the shell, its running programs and its
screen outlive every client. A new client gets the last 1 MiB of output replayed and the shell is nudged
to redraw (a one-row resize and back), so a full-screen program repaints. Several clients may attach at
once; `--read-only` ones watch but cannot type or resize. The broker ends when the shell exits, when
`session stop` is run, or after the idle window with nobody attached (`PSBASH_HOST_IDLE_SECS`, default 24 h).
It ignores SIGHUP, so closing the terminal that started it does not end the session. Diagnostics go to
`terminal-broker.log` in the runtime directory.

One broker holds an exclusive lease for each terminal endpoint through listener shutdown;
concurrent cold attachments cannot replace its shell or socket. Initial replay and live
writes drop a client that cannot receive within five seconds, so it cannot hold up other
clients indefinitely. All writable clients share input; use `--read-only` for observers.

The terminal broker is independent of the command host above: `-c` calls run in the stateful runspace,
the terminal runs an interactive shell. They share a name and a lifetime (`session stop` ends both),
not a runspace.

`session list` shows both halves of each name. Each line ends in `terminal=running`, `terminal=stale`
(a leftover socket nobody answers on) or `terminal=none`; a terminal created solely by `session attach`
is listed as `NAME<TAB>no-host<TAB>terminal=running`. `session status NAME` prints the command-host
status, then `terminal: running|stale|none`, and exits 0 if either half is up.
`session start --idle-secs` configures the command host. Set `PSBASH_HOST_IDLE_SECS` when
first attaching if the terminal broker should use a different idle window.

On Windows the broker runs the shell under ConPTY and is covered by the same end-to-end tests as on
Linux; the `attach` client was checked by hand on a Windows console.

## Client contract for front ends (BeagleTerm)

A client needs only: resolve the endpoint (`PSBASH_SESSION=NAME`, same rule as the launcher,
see `IpcTransportFactory.ResolveEndpoint`), speak `HostProtocol` (Health, Shutdown, command
requests), and honour the host's metadata sidecar (`<endpoint>.host.json`) for liveness.

### Terminal protocol

`<endpoint minus .sock>-t.sock`, one AF_UNIX stream, frames `[type:1][length:4 big-endian][payload]`,
payload at most 1 MiB. Client to broker: `H` hello (flags byte, bit 0 = read-only; must be first),
`I` input bytes, `R` resize (cols:2, rows:2), `Q` end the shell. Broker to client: `O` output bytes
(the first frames after hello replay the scrollback), `X` shell exited (exit code:4). Detach = close the
socket. Reference implementation: `src/PsBash.Shell/Sessions/`.
