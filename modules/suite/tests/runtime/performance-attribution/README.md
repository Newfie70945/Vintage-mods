Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/performance-attribution/PerformanceAttribution.csproj
```

Checks the shipping assembly's handler matching, unknown and ambiguous ownership, engine attribution, and paired slow-tick/cause-history clearing. The web counterpart is `node modules/suite/tests/web/test_performance_timeline.cjs` (Playwright and Chromium required).
