# Ore Shield discovery performance pass

## OreFix1: concealment and packet preparation

Vintage Story 1.22.7's `collectChunk` caches one vanilla packet per chunk/entity mode and reuses it across recipients within a send tick. OreFix1 shallow-copies that packet for each recipient before changing `Blocks`; the compressed concealed baseline remains shared through the existing mask cache. Nested wrappers deduplicate only the same recipient's private packet. A packet rewrapped for a different recipient starts from its untouched canonical source. No recipient's discoveries can change another recipient's packet.

This fixes the previous once-per-object marker, which skipped later recipients and could transmit an earlier player's discoveries. The canonical source now also makes the original working version's decoded-packet scan safe again: a cold candidate scan reads the existing 32,768-element decoded array instead of locking and reading the live chunk 32,768 times. The exceptional fail-closed rebuild still uses live authoritative chunk data.

The official-runtime test under `tests/runtime/oreshield` patches the real `collectChunk`, uses real compressed chunk data and wire serialization, and checks buried/cave ore, player-placed exemptions, known/unknown recipients in both orders, repeat sends, nested wrappers, immutable canonical bytes, cached payload reuse and cache-disabled masking. The previous NativeFix1 binary fails the cross-recipient concealment case. The supplied working 1.2.36 binary passes the core concealment reference case. No running world or measured server-load benchmark is used.

## Bounded discovery work

The periodic discovery scanner now resumes a per-player chunk sweep instead of collecting and sorting every exposed ore within the full discovery radius on each movement update. Chunks near the player are visited first. Entries within each chunk continue across passes, including edits between passes.

The preparation limits apply to each visibility tick:

| Work | Limit |
| --- | --- |
| Players selected for scanning | 8, rotating through online players |
| Chunk windows examined per selected player | 64 |
| Exposed cells evaluated per selected player | 1,024 |
| Missing chunk indexes built across all players | 2 |
| LOS checks across all players | `min(OreShieldMaxLosChecksPerPass * 4, 2048)` |
| Touching chunk faces refreshed after column loads | 4 |
| Pending boundary refreshes | 4,096, deduplicated |

The configured per-player LOS ceiling still applies. A LOS check can test the target center and its faces; the counter is a candidate-level check, not a count of underlying engine rays. Discovery candidate lists are bounded by the allocated LOS ceiling. Exposed-cell indexes use lazy 4 KiB bitsets, with 128-entry reads rather than whole-chunk array copies. The discovery-cell telemetry count is maintained during mutations, avoiding a full scan of all player discovery history whenever the dashboard requests a snapshot. Packet masking audit counters are aggregated per chunk rather than taking the shared state lock for each counter increment.

Column loading queues exposure-index updates. Each update examines only the touching face's 1,024 cells. If the queue fills, the affected index is removed and rebuilt by the normal scanner or packet path; refresh work is never silently dropped while retaining a stale index.

These budgets can delay newly visible ore during cold-cache scans, rapid movement or heavy player/column load. Player mining still authorizes the six face-adjacent protected ore cells through the existing authoritative break path. Whole-vein reveal remains an optional setting with its existing limit. Previously discovered cells and player-placed exemptions retain their existing behavior.

All ore disclosure still requires the existing server authorization rules. Discovery preparation never edits or weakens the synchronous fail-closed chunk mask. Ore Shield sync protocol 3, capability and client build marker are unchanged, so the supplied Client Integrity sync remains compatible.

The Ore Shield web snapshot now reports `VisibilityChunksVisited`, `VisibilityCandidatesEvaluated`, `VisibilityIndexBuilds`, `VisibilityBudgetDeferrals`, `BoundaryRefreshesPending`, `BoundaryRefreshCellsEvaluated`, `BoundaryRefreshOverflowInvalidations`, and their fixed limits. Deferrals count budget-limited passes/player selection; a growing boundary backlog indicates queued exposure work. These limits bound preparation units, not elapsed milliseconds. Initial masking/compression, index construction, block events and client resync remain additional work, and server-world load testing is required to measure actual CPU/latency improvements.

Run the standalone production-helper regression checks with:

```sh
dotnet run --project tests/oreshield/OreShieldWorkTests.csproj -c Release
```

The checks cover dense/sparse exposed-index windows, edits between ticks, nearest-first sweep coverage, resets on chunk/dimension changes, and a shared work ceiling even when many players request their minimum allocation.
