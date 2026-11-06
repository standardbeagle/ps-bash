# `producer | native` with a native that does not read all of stdin: "Broken pipe" and lost output on Unix

Status: open, known gap (v0.14.1 release run, 2026-11-06). Test: `NativeStdioBytesTests.NativeStdin_ProgramThatIgnoresStdin_DoesNotWaitForAnEndlessProducer`
(`yes | pwsh -NoProfile -c 'Write-Output ok'`), skipped off Windows until this is fixed.

## Symptom
- bash: `ok`, stderr empty (the producer dies of SIGPIPE).
- ps-bash on macOS (every run) and Ubuntu (intermittent): stdout empty, exit 0, stderr `Broken pipe`.
- ps-bash on Windows: stdout `ok`, stderr `The pipe is being closed.` (noise, output intact).

## Cause
`yes` runs in-process (`Invoke-BashYes`) and `ConvertTo-BashNativeStdin` hands PowerShell `byte[]` records, which
PowerShell's own native-command stdin writer writes to the child's pipe. When the child exits without reading, that
write fails (EPIPE); PowerShell reports the IOException and, on Unix, ends the pipeline before the child's already
captured stdout is emitted. The write belongs to PowerShell, so no cmdlet in the pipeline can swallow it.

## Fix direction
Route `producer | native` through the OS-pipe bridge `NativeStdinBridge` already uses for shared stdin: the native
inherits a real pipe as its process stdin (PowerShell then writes nothing), and a pump thread of ours moves the
producer's records into it and treats EPIPE as "reader gone". Needs the upstream records exposed to the pump
(they are thread-bound in a PowerShell pipeline), so it is a redesign of how a native stage gets its input, with
the byte-exactness guarantees of `ConvertTo-BashNativeStdin` kept. Part of the larger memory/robustness agenda.
