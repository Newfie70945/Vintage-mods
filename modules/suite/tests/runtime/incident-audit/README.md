Run against the built Suite and your licensed official server installation:

```sh
VINTAGE_STORY=/path/to/game NSS_ASSEMBLY=/path/to/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/incident-audit/IncidentAudit.csproj
```

Checks `IncidentAuditInterpreter` against the shipping assembly: a dominant player (and that an even split or a small window is not blamed), chunk concentration, entity lifecycle churn from logged rows or bucketed aggregates, a surge against the preceding baseline, and quiet-window labelling. The SQLite query that feeds it (`LogStorageService.QueryIncidentAuditActivityAsync`) needs a populated BlockLogger database and is not covered here.
