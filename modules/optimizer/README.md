# Newfie's Server Optimizer 0.2.0 preview

A server-only mod for the **official Vintage Story 1.22.7 / .NET 10 server**. This version adds real, independently controlled Harmony replacements to the profiling foundation. Patches are installed in memory during mod startup and removed on disposal; game DLL files are left intact.

## Implemented API optimizations

| Module | Change | Correctness boundary |
| --- | --- | --- |
| Online player snapshots | Builds the required fresh result array without the original LINQ iterator pipeline | Official client snapshot order, pending-player filtering and runtime array type preserved |
| Nearby player queries (opt-in) | At 128+ connected clients, reuses bounded per-thread temporary lists rather than repeatedly growing throwaway lists | Same range math, dimensions, callback order, short circuit and caller-owned results |
| Bulk stream reads | Uses a bulk byte copy for eligible reads of 256 bytes or more | Invalid ranges, aliased buffers, derived streams and unusual layouts run the original method |

Temporary player-query lists retain no player references between calls and have a 4,096-client capacity ceiling. Larger calls use the stock implementation. These modules do not change simulation ranges, AI schedules, physics rules, packet ordering, world generation or save scheduling.

## Install and controls

1. Back up, then install the single optimizer ZIP on a staging server. Clients do not need it.
2. Start the server and run `/optimizer modules` to verify which modules actually activated.
3. Review `ModConfig/optimizer.json`. Restart after changing switches.
4. Capture a representative workload with `/optimizer profile start`, `/optimizer profile stop` and `/optimizer export`.

```json
{
  "Enabled": true,
  "Compatibility": { "SafeMode": false },
  "EngineOptimizations": {
    "OnlinePlayerSnapshots": true,
    "NearbyPlayerQueries": false,
    "MinimumNearbyClients": 128,
    "BulkStreamReads": true
  }
}
```

`Enabled=false` disables the optimizer. `Compatibility.SafeMode=true` prevents the API replacement modules from installing. **Existing 0.1.0 configurations keep their previous SafeMode=true value**; explicitly set false on a staging server to try these modules. New 0.2.0 defaults use false with strict engine identity checks.

The three module switches operate independently. **Nearby-query reuse defaults off**: it cut allocation in component tests but its CPU timings were mixed, including regressions. Enable only for a controlled workload comparison. Nearby-query reuse stays unpatched below its activation threshold, and unpatches again below threshold minus 32 clients to avoid churn. Detailed profiler timing switches remain separate and default off. Stopping a profile does not disable API optimizations.

## Compatibility and fallback

The patch registry verifies the actual running game version, exact inspected official API/server assembly hashes, method layouts and Harmony owners. Altered/unrecognised builds are blocked. Stratum, Tungsten and Synergy runtime/optimizer assemblies are blocked rather than stacked without validation.

A foreign patch on a target (or the stream Position accessors) prevents that module from installing. A once-per-second monitor detects late conflicts or recorded module failures and disables/removes only our prefix. Other modules stay active. Disposal leaves foreign patches untouched. If the monitor cannot start, installed replacements are rolled back.

An exception after observable work is propagated, never replayed through stock code; the monitor then stands that module down. A failing caller-supplied query predicate can trigger this conservative stand-down. No guarantee is made about arbitrary patch interactions during the interval before the monitor runs.

## Evidence and limits

See [performance evidence](docs/performance-0.2.0.md), [external capture analysis](docs/research/supplied-probe.md), [configuration](docs/configuration.md) and [limitations](docs/limitations.md).

Component tests exercise actual official methods before and after Harmony injection. Their uninitialised engine fixtures are **not authenticated players or a multiplayer server**. Component timing/allocation results are not server TPS gains or proof of sustainable player capacity. The uploaded Stratum/Lithos capture has no matched NOS/stock baseline.

## Build

Set `VINTAGE_STORY` to a separately installed official 1.22.7 server and use a .NET 10 SDK:

```sh
python3 tools/build.py --game-dir "$VINTAGE_STORY"
```

This compiles the mod, runs C# and tooling tests, verifies official dependency hashes and packages only the mod's own DLL, manifest and guides. Game binaries are not included. The optimizer has its own build flow, separate from the regular Suite module packager.
