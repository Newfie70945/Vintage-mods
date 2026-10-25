Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/slow-tick-analyzer/SlowTickAnalyzer.csproj
```

Checks `SlowTickAnalyzer`, which classifies each profiled slow tick (single dominant cause, several contributors, GC pause, mostly unmarked time, spread) and writes the plain-language reason shown in the Tick Debugger. Also checks that low-confidence mod ownership is never named, that a GC pause inside a marker is not blamed on that marker alone, and the per-owner roll-up and headline. The GC pause measurement itself needs a live engine profile and is not covered here.
