Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/incident-recurrence/IncidentRecurrence.csproj
```

Checks `IncidentRecurrenceAnalyzer` against the shipping assembly: steady-cadence detection (and that irregular gaps or same-episode fragments are not reported as a cadence), world-save overlap, repeated measured mod ownership (low-confidence ownership is ignored), repeated assessments ("Insufficient evidence" never counts) and the worsening-trend rule.
