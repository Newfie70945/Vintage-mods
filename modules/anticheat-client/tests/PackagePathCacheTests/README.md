# Fingerprint path and retry regression checks

Run with .NET 10:

```
dotnet run --project modules/anticheat-client/tests/PackagePathCacheTests -c Release
```

Links the production fingerprint/manifest helpers. Uses temporary files and verifies equal-size/equal-timestamp files whose names differ only by case receive separate hashes on a case-sensitive filesystem. On a filesystem that aliases those names, only those case-specific checks are skipped. Exclusive file-lock fixtures verify failed reads are retried without metadata changes. Synthetic expanded/truncated manifest streams check strict declared-entry bounds. No game client, network access or anti-cheat verdict is simulated.
