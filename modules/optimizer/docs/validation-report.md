# NOS 0.20.11 / Suite 1.2.52 validation

All 31 automated suites passed against official Vintage Story 1.22.7. Builds completed with zero warnings/errors. Browser control tests passed, including older-version support and mobile layout. Native-method allocation checks, weak-cache collection, live state/range mutation and exact runtime patch-composition checks passed.

Two 180-second populated-server runs (3,000 animals, 500 barrels, 500 firepits and 23 selected mod ZIPs) passed save/unload/clean-shutdown/integrity checks. Allocation rate fell while fewer ticks completed; tick latency worsened in this pair; both new options remain experimental and **off**. No general latency benefit is claimed. [Measurements, controls and limitations](performance-0.20.11.md).

## Previous validation

### NOS 0.20.10 preview validation

All 31 required automated suites passed with none skipped on official Vintage Story 1.22.7. Build: zero warnings/errors. Recipe/query coverage: 10,068 checks including 10,000 native conformance cases; block callback coverage: 109; engine lifecycle/conflict coverage: 138. Direct comparisons against the actual previous release and a separate installed-module server check passed; saved-world integrity and clean shutdown passed. [Exact measurements and limitations](performance-0.20.10.md).

The separately compiled lab fixture adds a recipe-matching stress command using scratch stacks only. It is test code, not a production mod. The discarded barrel experiments are absent from the release. Suite remains 1.2.51.

## Prior validation

### NOS 0.20.9 preview validation

All 31 automated validation suites completed with none skipped against official Vintage Story 1.22.7. The block callback suite passed 106 checks, including the exact public Steep & Brew 0.7.1 postfix, unchanged mismatch results, its tea/decoction restrictions, and rejection of an unknown postfix using the same owner string. Existing dependency conflict and engine lifecycle checks remain enabled.

Suite 1.2.50 verification scheduling tests exercise extracted production methods: failed-write preservation/retry, 100 concurrent updates, burst coalescing, final shutdown flush, 40-session staggered scheduling, mid-pass arrivals, callback-registration failure and shutdown no-ops. Consequence tests pass for kick deduplication, stronger bans, expiry and failed-action retry. The Suite builds without warnings. These are functional/component tests, not a new timed full-server comparison; component timings in this validation run are not controlled production performance evidence.

[Changes and limits](performance-0.20.9.md).

## Previous validation

### NOS 0.20.8 preview validation

The current pass adds opt-in full-batch crafting candidate acceleration and the Suite 1.2.50 control. Native four-stack outputs/consumption/callbacks match, and two alternating comparisons demonstrate lower elapsed work and allocation in a large synthetic recipe index. [Current measurements, exact scope and limitations](performance-0.20.8.md). All 31 current automated validation suites completed with none skipped. Crafting coverage includes 62 functional checks; engine lifecycle/conflict coverage includes 136 checks. The updated Suite builds without warnings and its extracted web script passes Node syntax validation. Full current validation results are recorded in the build manifest.

## Prior validation

The 0.20.7 build completed all 30 automated validation suites with no skipped suites against the inspected official Vintage Story 1.22.7 assemblies. Module/version checks and archive integrity checks also pass. The source and compiled preview retain the same configuration defaults; Suite remains 1.2.50.

Current coverage includes 74 despawn packet checks (including official serializer byte equality) and 130 engine lifecycle/conflict checks. Two isolated seven-round comparisons against native and our pinned 0.20.6 prefix reproduce lower packet-construction time with unchanged output allocation. This is a component result; it is not evidence of improved production TPS or resolution of the reported live regression. [0.20.7 results and scope](performance-0.20.7.md).

## Prior validation retained for context

The full build and validation manifest records the executed automated suites against the inspected official Vintage Story 1.22.7 assemblies. A new native-versus-patched barrel recipe mutation regression reproduces the pre-fix mismatch and passes with the fix. No optimizer scheduling defaults changed.

Six dedicated-server comparison arms ran with NOS 0.20.0, the recorded capture switches, Suite 1.2.49 and 25 mod ZIPs. All passed save and SQLite integrity checks. Four arms used 128 active native NPCs; two exploratory arms used 1,024. These are not authenticated player workloads and do not reproduce the full production pack. The narrow 0.20.1 compatibility fix was made after those timed comparisons; their timings are not relabelled as 0.20.1 measurements.

Measured allocation fell, but tick latency and CPU did not improve. No general smoother-server claim is supported. See [measurements and limitations](performance-0.20.1.md). Production foreign Harmony conflicts remain blocked; those guards were not bypassed.

Raw uploaded logs and private configuration are not included in source or release archives.

The 0.20.2 pass adds actual native barrel-slot regression/allocation coverage and a 3,000-animal / 200-block-entity comparison. See [results and missing 25-client workload](performance-0.20.2.md). Timing measurements use 0.20.1; the optional count guard was off there.

0.20.3 adds direct single-target AI delegate dispatch, with native-versus-patched subscription-mutation checks and focused official-target timings. See [measurements](performance-0.20.3.md); no server-wide speedup is inferred.

0.20.4 adds allocation-free first encounters for basic scheduler exemptions and stale-delay clearing regression tests. [Measurements and limits](performance-0.20.4.md). This is not a production memory-leak fix or a new server throughput result.

0.20.5 adds native-versus-patched custom-slot mutation coverage, native getter conflict guards and an intensive 3,000-animal / 1,000-block-entity test harness. [Setup, results and limits](performance-0.20.5.md). Exact owner PC ModsByServer files remain unavailable until attached.

Final intensive validation: six matched comparison arms passed activity/callback/save/integrity checks. Full optimizer absence was verified, with NOS capture stopped in both presence-comparison arms. No tick benefit was established; production advice is to leave NOS off pending exact modpack/configuration investigation. The populated-world restart check passed with NOS absent; the test fixture reloads and reactivates saved tagged animals without duplication.

0.20.6 bounds and reuses re-entrant search contexts with native-result, epoch, exception, thread and reference-clearing regression checks. A new alternating old/current official-target benchmark measures nested allocation reduction with essentially unchanged CPU cost. [Results and limitations](performance-0.20.6.md). No new server-wide gain is established.

0.20.7 preserves native despawn packet bytes while reducing the previous prefix's iteration overhead. Two isolated alternating comparisons show lower component construction time with unchanged packet allocation. Expanded engine tests cover foreign LINQ hook fallback. [Measurements and scope](performance-0.20.7.md). No new live-server or multiplayer throughput gain is established.
