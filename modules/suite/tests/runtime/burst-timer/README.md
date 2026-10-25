Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/burst-timer/BurstTimer.csproj
```

Checks `SuiteBurstTimer`, which sums many individually fast main-thread callbacks (entity spawn/despawn handlers, the OreShield column-load hook) into one `SuiteTimingRegistry` record per window. It exists because a "slow >= 1 ms" metric cannot see a mass chunk load or unload made of thousands of fast calls. The test drives the timer with an explicit clock, so it needs no running server and makes no performance claim.
