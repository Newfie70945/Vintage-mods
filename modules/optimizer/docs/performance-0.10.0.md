# NOS 0.10.0 preview / Suite 1.2.48 NOS Distance1

This pass adds five real opt-in modules: distant AI task-start evaluation, distant wander checks, stationary distant repulsion scheduling, waypoint candidate-node reuse and missing-attribute removal resync suppression. Each defaults off and reports independent runtime activation/conflicts. This is partial Synergy-purpose coverage, not implementation copying or full feature/performance parity.

Validation uses the exact inspected official Vintage Story 1.22.7 assemblies, component fixtures, headless Chromium and a private copied server. Authentication remains enabled. No clients or simulated players are counted as player workload. Final validation and measurements are recorded below.

The three distance policies preserve full entity ticks, running AI, hunger, breeding, health and physics. Named/owned/bred animals, mounts, combat/special tasks, custom runtime/task/behavior implementations and unsafe conditions retain stock scheduling. Ten seconds of damage grace and a protected radius plus 16-block margin reduce delayed reactions. Playing-client movement can take up to 100 ms to refresh proximity; animal movement of eight blocks/dimension changes invalidate it immediately. No observer in the dimension means stock, not indefinite sleep.

Waypoint pooling is per exact official AStar solver and waypoint-only scope, with 4,096 candidate slots. Raw node APIs outside that scope, busy/nested/custom solvers and overflow allocate normally. Returned waypoint ownership and engine-owned search sets remain stock. Candidate-slot limits do not limit the engine's own graph or parent chains. Reused fields include parent, action, heap index, coordinates/dimension and costs. Foreign patches on the constructor/core/helpers block both components.

Removing a nonexistent attribute skips only the otherwise redundant full-tree dirty mark on an exact SyncedTreeAttribute. Existing attributes, null-key behavior and custom subclasses remain stock.

No forced GC, process-wide GC tuning, reduced chunk delivery, full entity sleep, negotiated client protocol or TCP consolidation is added. Production benefit needs matched player/chunk/entity workloads and tick p50/p95/p99, allocation, memory and behavior observations over representative sessions.

## Completed validation

- NOS: all 19 build/test suites pass, including 45 distance/attribute/node ownership checks and 46 actual-Harmony lifecycle/conflict checks.
- Suite: zero build warnings/errors; headless Chromium passes control saving, pattern arrays, refresh/failure draft preservation, Safe Mode/read-only/capture locks, older NOS behavior and mobile layout.
- Copied official server: 34 checks pass with 25 mod ZIPs, including all six Suite companions and selected Draconis, XSkills, Forgotten Armory, Floral Zones and other mods. Checks exercise independent activation/disable, JSON persistence and invalid-pattern rejection, timeline/save events, compact web snapshots, actual terrain/pathfinding and raw-path ownership. Authentication is enabled; there are no authenticated players or bots.
- Candidate counters aggregate once per search, avoiding atomic operations on every neighbor. Same-target module teardown removes only that module's exact patch methods. Tests confirm repulsion allocation reuse survives distance-policy stand-down. The all-enabled compatibility monitor fixture allocates 58,016 bytes per once-per-second check; this includes the new modules and is not a player workload.

## Repeated waypoint component comparison

The probe creates a controlled flat arena in the copied world, warms each mode and measures 5,000 official waypoint searches per mode. The same retained raw-node path remains unchanged after the repeated pooled searches and pool recreation. No build runs concurrently with these final comparisons.

| Order | Stock allocated | Pooled allocated | Stock elapsed | Pooled elapsed |
| --- | ---: | ---: | ---: | ---: |
| stock/pooled | 29,440,040 B | 4,120,496 B | 162.66 ms | 161.89 ms |
| pooled/stock | 29,440,040 B | 4,120,040 B | 103.97 ms | 152.50 ms |
| stock/pooled | 29,440,040 B | 4,120,040 B | 77.79 ms | 70.13 ms |

Allocated bytes fall by about **86% in this repeated-path fixture**. Timings are mixed: one pair is essentially equal, one pooled run is slower and one is faster. No consistent CPU speedup, lower live heap, server TPS gain or higher player capacity is established. Pooling therefore remains off by default; measure it under your actual pathfinding/allocation pressure. These short component comparisons do not replace a representative multiplayer soak.

[Sanitized server evidence](research/components-0.10.0/server-suite-validation.json) contains all checks, selected loaded module identities, request timings, exact tested assembly hashes and the three measurements. [Validation summary](research/components-0.10.0/validation-summary.json) records executed suites and the module-monitor fixture. Earlier exploratory timing pairs were noisy and exposed per-node counter overhead; they were not used to claim production gains.
