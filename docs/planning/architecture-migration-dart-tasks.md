# Architecture Migration Dart Tasks

This historical task list has been superseded by the host-backed architecture.

Current execution path:

```text
ps-bash launcher -> WorkerFactory -> IpcWorker -> ps-bash-host -> SdkWorker
```

The launcher no longer carries an alternate subprocess worker path. Future work
should plan and test against the host-backed path above.
