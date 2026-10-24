# NOS 0.20.7 preview validation

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
