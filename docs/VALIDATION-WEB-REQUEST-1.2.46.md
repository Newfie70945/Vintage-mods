# Web request pressure pass — Suite 1.2.46

Build: `ms11-web-request1`.

## Confirmed paths and changes

Login calls showApp, which previously started nine navigation endpoint requests concurrently, in addition to customization, the selected page and the global header. Navigation now starts at most one check at a time with 250 ms between completions. Concurrent navigation refresh calls are ignored until the active run finishes. Individual failures do not stop later checks; hiding the app stops remaining checks. This bounds background work per browser, not all HTTP traffic across clients.

The Tick Debugging badge previously called the full BuildNativeTickDebugSnapshot on the game thread. It now reads only the enabled configuration flag through an authenticated status endpoint. The full page retains its existing snapshot and controls.

Server Status previously rebuilt player summaries, analytics and daily cohorts on every read. It now uses the existing bounded, coalescing stale-while-revalidate cache with a 15-second freshness interval and a separate key for each history window. Its first load still builds on the game thread. Displayed information may be up to 15 seconds old; stale content can remain visible while refresh waits.

Password verification already runs on HTTP workers; no password work factor, authentication, authorization or CSRF protection was reduced. Root HTML already caches compressed bytes, so no duplicate compression fix was needed.

## Verification

- Executed actual embedded navigation refresh JavaScript: nine requests, peak concurrency 1, duplicate refresh suppressed, failed request continuation, logout stops remaining work, next refresh can restart.
- Existing coalesced-cache runtime tests cover cold/stale load sharing, invalidation races, failed refresh retries, pause admission and listener generations.
- Chromium Performance Center navigation regression passed.
- Official Vintage Story 1.22.7 Suite build checked separately in build output.

## Remaining optimization candidates

- Replace other sidebar full-page feeds with compact summary endpoints, especially compatibility and ChatGuard.
- Split dashboard health analysis from its lightweight header data; GetSnapshot currently copies retained samples and computes diagnostic summaries under the health lock.
- Cache serialized immutable large GET payloads to reduce repeated JSON allocation. Mutation invalidation and lifetime bounds must remain intact.
- Reduce account lock duration around password verification and config writes without introducing stale credential acceptance or lost failed-login counters.
- Profile cold page builders against a populated production-sized player/history database. Request coalescing reduces repeats but does not make one expensive builder cheap.

No production tick-time or TPS improvement is claimed from browser mocks or cache tests. Compare request main-thread work and coincident stall evidence under the same player workload after installing.
