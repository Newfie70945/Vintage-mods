# Suite performance audit — 1.2.56 / NOS 0.20.22-preview

## Changes implemented

**Log Intelligence reads:** reuse bounded byte/character buffers rather than allocate
new large arrays for every read. Return both arrays in `finally`, clearing their
contents so private log text is not retained in the pool. Continue reading at most
256 KiB per pass even when a pool returns a larger array. Rotation-tail comparison
uses a fixed 64-byte stack buffer. Emit the joined text directly into one owned
string, avoiding an intermediate joined string and its newline copy.

**OreShield sync:** queued dispatch is normally already grouped by chunk. Detect
that case and copy directly into the independently owned, maximum-128-entry arrays
needed for ACK/retry state. Avoid the temporary LINQ lookup, intermediate group
array and range iterators. Mixed-chunk input retains its existing grouping path.
Keep authoritative per-recipient masking, dimensions, array ownership, sequence /
revision fields and registration-before-send ordering. No new polling is added.

## Measurements

Compiled Suite 1.2.55 versus 1.2.56, three trials of 200 calls after warm-up,
.NET 10 tiered compilation disabled for this comparison. The log cases use local
cached files with UTF-8 and exception-context lines. Sync cases use the real
production send method with synthetic native world/channel/ACK fixtures; its
counts include reflection and proxy overhead. Values below are median allocated
bytes per call, not retained heap size or production TPS.

| Workload | Before | After | Allocation reduction |
| --- | ---: | ---: | ---: |
| Approx. 64 KiB log read | 601,706 B | 279,808 B | 53.5% |
| Approx. 256 KiB log read | 2,407,843 B | 1,119,270 B | 53.5% |
| Same-chunk 64-cell sync | 49,408 B | 47,288 B | 4.3% |
| Same-chunk 129-cell sync / two batches | 101,144 B | 95,294 B | 5.8% |

Log output hashes match before/after. Mixed-chunk allocation is effectively flat
in this fixture; it is retained as a compatibility path, not claimed as a gain.
CPU timings varied between runs/cases. Raw timings are included in the measurement
JSON; no consistent CPU or server TPS gain is established. Pooling trades repeated
allocation for reusable buffers (up to a 256 KiB byte buffer and approximately
1 MiB character buffer for this reader's largest read). This is not a claim that
steady-state server RAM falls by the allocation figures above.

## Other areas inspected

- PacketGuard already shares player sampling. Its TPS snapshot includes time since
  the latest tick to notice an in-progress stall; caching that value across packets
  could weaken lag safety, so it was retained.
- OreShield discovery copies isolate recipients and concurrent state. Shared mutable
  snapshots or prematurely pooled packet arrays could disclose ore or corrupt ACK
  retries, so those ownership boundaries remain.
- Web snapshots already coalesce builders and have fixed key capacity. Performance
  reads await the shared refresh. No auto-refresh, TTL or request-rate change.
- Leaderboard HTTP reads return cached snapshots; its JSON deep copy runs during
  periodic refresh, not on every HTTP request. A replacement would need full public
  visibility/privacy equivalence testing before prioritizing it.
- Database writing/recovery uses existing queues/gates. No forced GC, automatic
  compaction or durability relaxation was introduced. A maintenance gate is not
  itself proof of a game-thread stall; its caller and workload need attribution.

## Scope and remaining priorities

This is a targeted static/compiled audit of frequently called Suite code, not a
whole-modpack performance certification. Existing long production stalls were
not reproduced or proven fixed. Native block-update diagnostics and matched
workload profiling remain the next way to attribute long callbacks. Large offline
leaderboards and high-volume evidence/risk refreshes are further candidates only
if profiling shows they matter.

Detection thresholds, correction/kick policy, sampling intervals, OreShield reveal
rules and feature defaults are unchanged. This performance pass does not resolve
unverified false-positive reports or third-party chisel exceptions. NOS changes
only its Suite exact-binary compatibility entry and release metadata; client
companion mods are unchanged. See the validation report for executed checks.
