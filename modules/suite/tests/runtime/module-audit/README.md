# Audit safety regressions

These checks now exercise the repaired production assembly using temporary files and injected failures. The original unsafe behavior is recorded in `AUDIT-1.2.41-Audit1.md` and `MODULE-AUDIT-1.2.41-CHECKS.txt` as historical evidence.

Set `VINTAGE_STORY` to the official 1.22.7 installation and `NSS_ASSEMBLY` to the built Suite DLL. Build `ModuleAuditProbes.csproj`, then run its DLL with .NET 10. It verifies failed home reads/writes, recovery originals/future schemas, report caps/paging, bounded UTF-8 log reads/rotation, dependency versions, ZIP paths and queued-write retries. It starts no game world and makes no network requests.

`reports-browser.cjs` uses Playwright/Chromium with a captured `WebDashboardPage.Html` fixture to check desktop/mobile paging, escaped notes, stale responses and read-only/protected-storage controls. API responses are local fixtures.
