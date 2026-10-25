Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/slow-tick-scanner/SlowTickScanner.csproj
```

Checks `SlowTickLogScanner`, which backs the Tick Debugger's open-ended Live Logging mode: only slow-tick entries and their continuation lines are kept, polling starts at the capture's offset and waits for complete lines, a rotated or truncated log is followed from its start, and retained text is bounded to the newest entries. It does not start a server or touch the engine profiler.
