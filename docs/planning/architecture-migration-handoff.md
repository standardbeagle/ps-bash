# Architecture Migration Handoff

This handoff has been superseded by the completed host migration.

Current execution path:

```text
ps-bash launcher -> WorkerFactory -> IpcWorker -> ps-bash-host -> SdkWorker
```

The runtime module is loaded inside the host process. The launcher communicates
with that host over the protocol implemented by `IpcWorker` and the host server.
