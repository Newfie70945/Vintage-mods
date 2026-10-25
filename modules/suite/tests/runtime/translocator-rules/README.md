# Translocator privacy and runtime regressions

Build the Suite against official Vintage Story 1.22.7, then set `NSS_ASSEMBLY`, `VINTAGE_STORY` and the .NET 10 environment and run:

```
dotnet run --project modules/suite/tests/runtime/translocator-rules -c Release
```

Exercises the compiled registry and tracker against the official API and real static-translocator block entities, with controlled players, loaded-chunk/accessor proxies and a temporary private store. Checks undiscovered coordinates are absent from JSON, separately discovered endpoints, repairs, removal, world/schema separation, retention limits, bounded round-robin reads, no remote target reads, placement/interaction, failure isolation, disabled tracking, async persistence/reload and listener cleanup. No authenticated player or production tick-time claim is implied.
