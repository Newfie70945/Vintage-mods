# NOS 0.20.2 preview — native barrel slots and mixed-workload tests

The optional BarrelMatchCountGuard previously excluded the official ItemSlotBarrelInput, so real barrels fell back even for impossible ingredient counts. It now accepts that exact type after validating inherited standard Empty/Itemstack getters. Both getters participate in foreign-patch conflict checks. Custom slot types still fall back. The switch remains off by default.

A regression test reproduced the missed fast path before the change and passes after it. The native mismatch allocation fixture now uses actual barrel-input/liquid slots rather than generic ItemSlot fixtures.

## 3,000-animal mixed workload

Timed comparisons used NOS **0.20.1**, before this narrow 0.20.2 change, with the recorded production switches, Suite 1.2.49 and 25 laboratory mod ZIPs. Each mode used the same stopped world copy, 3,000 forced-active native wild pigs, 100 barrels containing rock-granite to exercise unsuccessful recipe searches, and 100 firepits (50 initialized burning, 50 cold). Movement, AI task progress and block callbacks were required. Full engine profiling was off in each 120-second steady window.

| Measurement | NOS switches off | NOS recorded switches |
|---|---:|---:|
| Mean tick ms | 47.63 | 46.22 |
| P95 tick ms | 61.00 | 61.00 |
| P99 tick ms | 69.00 | 71.00 |
| Worst tick ms | 89.00 | 119.00 |
| Observed ticks/s | 20.78 | 21.40 |
| CPU % of one core | 107.29 | 106.90 |
| Allocation MiB/s | 51.95 | 39.52 |
| GC pause ms/s | 10.70 | 9.56 |
| Peak RSS MiB | 1739.84 | 1734.36 |
| Repeated dirty save ms | 233.42 | 240.54 |

Both modes passed the activity, callback, save and SQLite integrity checks. One pair is exploratory: mean tick time was modestly lower and allocations fell about 24%, but P95 was unchanged and P99 slightly worse. This does not establish a general latency improvement. Animals and AI outcomes are not identical across modes.

**The requested 25 real bots were not run.** The available tester automates already-running official clients; this environment has no client sessions/account identities. No fake player records or authenticated-client counts were substituted. Player networking, inventory transactions and 25-player capacity remain untested.

The laboratory has a two-core CPU quota and 8 GiB memory limit. The selected pack does not reproduce the full production pack. Firepit conflict detection remains active; this is not proof that a blocked firepit fast path executed. Barrels intentionally had unmatched input; successful fermentation and active cooking require separate workloads. Fixture observation hooks and controlled active/tracked NPCs are test-only changes. Forced unload/compact GC occur outside the steady capture. The failed first setup attempt used an incorrect granite block code and was excluded from comparisons.

No production settings or worlds were changed.

Validation: all 29 automated suites passed, including 99 block callback checks. The real-slot mismatch fixture measured approximately 32 bytes per call without the count guard and effectively zero with it. [Sanitized mixed-workload data](research/mixed-workload-3000/summary.json).
