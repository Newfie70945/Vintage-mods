# Review of Claude's optimizer and MOTD branches

Reviewed immutable branch tips:

- `claude/optimizer-mod-audit-m5la11` at `12f9521` (three commits).
- `claude/motd-mod-gui-8g2605` at `0554fde` (two commits).
- `suite-performance-improvements` through `7f561bf` (eight commits); its first three are also present on `ccr-1fb2c0f4-jz5owu` at `3b1154e`. This branch appeared during the review and was audited separately.

Selected reviewed fixes were integrated into main with original commit authors preserved. The branch refs were not modified. Existing Performance workspace/live-map changes remain in main. This is a source integration, not a Mod DB release or a change to production server settings.

## Findings corrected before publishing

**P2: first-line links lost their destination.** `MotdDiscordProvider.SplitTitle` removed every short first line from the body after converting it to plain text. A one-line `[Join site](https://example.com/join)` post consequently produced title `Join site` and an empty body, dropping its clickable destination. Linked first lines now remain in the body.

**P2: first-line block formatting was removed before parsing.** A post beginning with an opening code fence produced title `` ``` `` and sent the remaining lines to the markdown converter without their opening fence. `**raw**` became bold text instead of code. Opening fences now retain the complete post under a fallback title. First list entries and quotations likewise retain their formatting in the body. Ordinary heading titles still avoid duplication.

The compiled provider is exercised by optional `NSS_DISCORD_ASSEMBLY` checks in `tests/discord-events`, covering masked/bare links, opening fences, ordinary headings, lists and quotes.

## Accepted optimizer safeguards

- Unpatch only entries actually installed; do not detour untouched stock methods during teardown.
- Verify the official VSSurvivalMod identity for modules that depend on it.
- Defer native reflection/delegate binding until guarded use.
- Recognize the optimizer and its profiler as cooperating Harmony owners; preserve observer patches during engine disposal.
- Report assembly-derived optimizer version; observe shutdown by reference; cache failed native thread-ID discovery rather than retrying every tick.

All independently configured module defaults remain unchanged. These are correctness/compatibility fixes, not evidence of a production TPS gain.

The collision-dimension question in the original audit was checked against the official 1.22.7 methods. Both inspected `GenerateCollisionBoxList` implementations leave that parameter unused; position dimensions are initialized elsewhere. No collision algorithm change was justified by that finding.

## Validation

- Official Vintage Story 1.22.7 assemblies and .NET 10; optimizer, MOTD and Discord Link compile with zero warnings/errors.
- 156 actual-Harmony lifecycle/conflict checks, including observer installation, late conflict checks and disposal preservation.
- Path-queue and profiler regression projects pass.
- 42 shared formatter/parser checks and 30 Discord event/compiled-provider checks pass.
- Full optimizer build/test driver passed all 34 component/Python test suites; heavy modpack/server probe projects are excluded by that driver's existing policy.
- `tools/check_sources.py` and `git diff --check` pass.

Tests use official engine contracts and fixtures. They do not establish production performance, multiplayer capacity or compatibility with every third-party mod.

## Additional Suite branch

Accepted `d43f3bc` (AFK member lookup caching), `3b1154e` (ore-mask coordinate lookup without per-cell BlockPos allocation), `7b9701d` (shared compiled whitespace regex/static timestamp formats), and `032c8a0` (recurring incident patterns). The AFK cache stores metadata, not player objects; values are still read live. The ore lookup retains the same coordinate/dimension semantics.

**P2 corrected: recurring-mod claims trusted historical confidence labels.** A `LikelyModId` with `Medium`/`High` confidence alone is not retained profiler evidence. The new analyzer now also requires a matching non-core profiler tick and a meaningful measured-work score. Tests cover high-confidence historical labels without any retained tick and ensure they do not become a measured-mod pattern.

Four commits are intentionally **not included**:

- **`5f89e3e` — automatic player activity audit enrichment.** Its ten-second `AwaitWithTimeout` only limits waiting. The underlying synchronous SQLite aggregations have no native progress/interrupt budget, and the worker flag is released when waiting times out. A slow query can keep running while later maintenance passes start more work. Time-window clamping does not bound the number of rows in a busy server's ten-minute baseline. Use a dedicated, single-reader, cancellable query runner with a real SQLite deadline and bounded/opt-in background enrichment before integrating.
- **`800ba87` — default offline discovery eviction.** The visibility tick sorts all offline records and can delete every eligible player's full discovery map in one pass, under the shared lock and without a per-tick chunk/time budget. This may reduce retained memory but introduces a new unbounded game-thread cleanup path. Require incremental cleanup before enabling it by default. The existing discovery retention policy is unchanged in main.

- **`59a4cad` — open-ended live slow-tick logging.** `BuildNativeTickDebugSnapshot` calls `SlowTickLogScanner.Poll()` on the game thread, synchronously reading/decoding/parsing up to 512 KiB per status request. A byte cap does not give a disk-latency or tick-time bound. Long-running logging can also outproduce the polling reader, so the displayed entries may trail the current slowdown. Move file work to a bounded background reader and publish cached snapshots before integrating.
- **`7f561bf` — hot-tick cause classification/200-row retention.** `CaptureProfilerTick` attaches the increase in process-wide `GC.GetTotalPauseDuration()` since the previous sampling callback to `FrameProfiler.PrevRootEntry`. That delta covers callback-to-callback time, not an independently timestamped overlap with that previous root; a collection before the current callback can be assigned to the wrong tick. The analyzer then labels it as a confirmed GC overlap/primary GC cause. Keep this out until pause timing is aligned to the root interval, or clearly treat it as nearby-window correlation rather than confirmed tick attribution. More retained ticks alone is reasonable, but does not validate the attribution change bundled with it.

Review cutoff is the immutable `7f561bf` tip. Future commits on Claude's active branches are outside this recorded review.

Additional validation: Suite builds against official 1.22.7; compiled-assembly AFK cache/missing-member/live-value tests, recurring-pattern tests, OreShield packet tests and Performance navigation/live-refresh browser regressions.
