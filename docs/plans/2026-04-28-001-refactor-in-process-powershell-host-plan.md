# Refactor To Host-Backed PowerShell Runtime

This migration plan has been superseded by the implemented host-backed runtime.

Current execution path:

```text
ps-bash launcher -> WorkerFactory -> IpcWorker -> ps-bash-host -> SdkWorker
```

`ps-bash` remains the launcher. `ps-bash-host` owns the PowerShell runspace and
loads the runtime module. New design and test plans should target this single
supported path.
