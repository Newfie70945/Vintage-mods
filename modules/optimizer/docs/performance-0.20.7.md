# NOS 0.20.7 preview — despawn packet construction

Despawn packet projection now uses indexed reads over a temporary span of the input list. The previous replacement enumerated that list three times with per-item enumerator version checks. The new path preserves the official three-pass read and allocation order. It does not merge passes, pool outgoing arrays, change despawn decisions or delay network sends. The span is never stored or written through. As with the native list/LINQ input, callers must not concurrently mutate the input list during construction.

Foreign patches on the closed native LINQ projection/array operators or the borrowed-span helper now block this replacement through the existing compatibility monitor. The independent `DespawnPacketProjection` switch and master/Safe Mode controls remain authoritative. Native behavior is retained on unsupported builds or detected conflicts; foreign patches are left installed. No new defaults, GC policies, AI scheduling, worldgen rules, save behavior or Suite protocol changes were introduced. Suite remains 1.2.50.

## Controlled component measurements

Two fresh-process comparisons each ran seven alternating rounds against the actual official 1.22.7 packet factory. Modes were native, the pinned author-owned 0.20.6 prefix and the new prefix. Tiered compilation was disabled in both comparisons so changing JIT tiers did not determine the result. Each mode warmed before measurement. These are synthetic lists, not networking, client or server tick workloads. No server benchmark was running concurrently.

Median nanoseconds per packet construction:

| Entities per packet | Official, first / repeat | NOS 0.20.6, first / repeat | NOS 0.20.7, first / repeat |
|---|---:|---:|---:|
| 1 | 317.08 / 272.88 | 58.65 / 48.83 | 53.10 / 46.62 |
| 64 | 916.25 / 842.47 | 358.39 / 302.46 | 243.52 / 199.00 |
| 512 | 5371.65 / 4891.58 | 3025.28 / 2563.83 | 2491.16 / 1942.49 |

The new path reduced this component's median construction time in both comparisons. Timings are Stopwatch elapsed time per call, rather than a whole-server CPU utilization measurement. For 64-entity batches the reduction versus 0.20.6 was approximately 32–34%; for 512 it was 18–24%. Packet allocations remain unchanged versus 0.20.6: 720, 1,720 and 8,888 bytes respectively. The older 216-byte iterator reduction versus native remains; it is not a new allocation saving in 0.20.7.

74 functional checks cover IDs/reasons/damage defaults and order, exact official serializer bytes for 0–4,096 entries, null input/item exceptions, independently owned output arrays, changes to the input between calls, parallel calls and independent disable. Engine lifecycle tests also exercise late foreign LINQ hooks and fallback without removing the foreign owner. The previous implementation is pinned under `tests/DespawnPacketTests/Reference` from our own commit 18b1449; proprietary game implementations are not redistributed.

This does not explain or fix the reported live slowdown. Despawn packet creation may be a small fraction of a real server's CPU usage. Previous matched server comparisons established no overall tick benefit from NOS. Keep NOS off on the affected live server pending a comparison with its exact modpack and configuration. No new 3,000-animal, authenticated-client or full-server performance claim is made here.

[First controlled run](research/despawn-packets-0.20.7/controlled.json) · [Independent repeat](research/despawn-packets-0.20.7/repeat.json).
