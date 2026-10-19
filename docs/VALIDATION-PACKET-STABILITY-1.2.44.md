# Suite 1.2.44 — PacketStability1 validation

Build: `newfies-server-essentials-suite-1.2.44-ms11-packetstability1`. Official inspected runtime: Vintage Story 1.22.7, .NET 10. Companion versions are unchanged.

## Findings and changes

- A first exhausted ingress budget previously produced a High-severity `PACKET_FLOOD`, eligible for finding-code consequences. Existing wire/position/action/settings limits still reject excess traffic immediately. Punishment eligibility now requires continuous over-budget observations on one budget for at least three seconds, at least ten observations, no gap over 1.5 seconds, and reliable server timing. Queued diagnostics delayed over one second cannot turn into confirmed floods. Existing diagnostic cooldowns still apply, so three seconds is a minimum confirmation duration, not a promised kick time.
- Right-click reach was intentionally allowed for mod compatibility, but its finding could still match kick rules. Use observations are now review-only across risk, correlation, player warnings and finding-code consequences. Break reach requires repeated distinct targets over at least 750 ms, with at least three strikes and healthy timing, before it is punishment-eligible. Existing direct rejection of invalid/out-of-range actions remains separate.
- The same target's use/break callbacks no longer count as independent targets. Reach strikes cannot be primed during lag/warmup and carried into healthy timing. Reporting is coalesced to five seconds, with an immediate update when a previously advisory sequence becomes confirmed. Target retention is bounded at 128 signatures and reuses pruning scratch.
- Cube/history failures now consult bounded server-defined selection geometry. Client hit positions cannot enlarge reach. In-range actions skip the extra geometry reads. Missing/invalid geometry, non-finite server positions and invalid picking ranges are not proof of reach abuse; unknown ping uses bounded reconciliation instead of NaN arithmetic.
- TPS snapshots account for elapsed time since the last actual tick, including a current stall before its next callback. The existing conservative recovery smoothing remains.
- Behavior findings reuse validated client-policy evidence instead of running the complete mod/assembly evaluator twice per report. Client scans and policy changes still perform full evaluations. Per-family temporary dictionaries are reused. Advisory repeats cannot erase or refresh the lifetime of earlier confirmed evidence of the same code.
- Uncertain mining and unverified teleport observations are excluded consistently from automatic evidence paths. Malformed packets, unauthorized targets and confirmed evidence remain protected.

## Verification

- Release compilation against official 1.22.7 assemblies: zero warnings/errors.
- `tests/packetguard/PacketIngressPolicyTests.csproj` links production helpers and verifies raw protocol/resource bounds; burst/refill/clock behavior; sustained versus single-tick floods; quiet-gap and unreliable-timing resets; reach action/distinct-target/duration gates; review metadata parsing; current-stall timing; unknown ping; and oversized/invalid selection geometry. These portable checks also run in GitHub Actions.
- `tests/runtime/advisory/AdvisoryRuntimeSmoke.csproj` loads the actual compiled Suite and official dependencies. It checks risk, incident and finding-code consequence exclusion for advisory/legacy floods and reach, confirms actionable paths remain live, tests both family and legacy fast risk updates, and verifies advisory repeats cannot replace or refresh confirmed evidence. The actual ingress budget rejects excess in a 10,000-frame fixture and coalesces it to one advisory callback; malformed frame lengths remain rejected. Disabled OreShield startup performs no world access, virtualizer creation, hook installation or queued work.
- `tests/runtime/interaction/InteractionRuntimeSmoke.csproj` uses actual official player/entity, block and selection types with API doubles. It verifies normal block/BE/hand/damage packets and rejection of distant/cross-dimension/out-of-world/unloaded targets; oversized server selection boxes; ignored spoofed hit positions; no extra shape query on ordinary in-range actions; invalid position/geometry and unknown-ping handling; current-stall detection; discarded lag-primed strikes; retained healthy repeated-break enforcement; and right-click target/evidence isolation.
- Existing official-runtime ingress parser/Harmony, native hotbar/backpack, confidence-repeat, OreShield packet/vein and 29-field settings round-trip checks passed. General repository checks, post-builder checks, source consistency and whitespace checks passed.

All runtime fixtures here are detached objects/API doubles; no authenticated game clients, production world or production kick was exercised. Test probes/fixtures are excluded from the compiled mod ZIP.

## Isolated cost comparison

The production `RebuildSessionRiskFromCurrentEvidence` hot path was measured against the preceding LiveMigration1 DLL. Both began from the same fully evaluated client evidence, with 200 synthetic mod rows, 100 warmups and 1,000 measured calls. Reflection-call arguments were reused. Risk outputs matched before/after in both scoring modes.

| Scoring | Allocated bytes/call before | After | Time before | After |
|---|---:|---:|---:|---:|
| Legacy | 63,600 | 440 | 105.3 µs | 1.41 µs |
| Family (default) | 66,240 | 416 | 187.5 µs | 2.44 µs |

This isolates repeated risk recalculation, not the entire finding/report pipeline, network handling, tick time, memory footprint or production lag. Timing is machine-dependent. Raw JSON is included in the release bundle.

Reproduce with `VINTAGE_STORY`, `NSS_ASSEMBLY` and the .NET 10 executable on PATH:

```sh
python tools/build.py --module suite
dotnet run --project modules/suite/tests/packetguard/PacketIngressPolicyTests.csproj -c Release
dotnet run --project modules/suite/tests/runtime/advisory/AdvisoryRuntimeSmoke.csproj -c Release
dotnet run --project modules/suite/tests/runtime/interaction/InteractionRuntimeSmoke.csproj -c Release
NSS_FAMILY_RISK=1 dotnet run --project modules/suite/tests/runtime/anticheat-cost/AntiCheatCostSmoke.csproj -c Release
```

For the baseline cost, point `NSS_ASSEMBLY` at the preceding DLL and use the same fixture/scoring mode.

## Practical limits

The user reported mostly packet-flood and reach kicks but supplied no specific kick/finding evidence row or tick capture. These changes address verified code paths that could cause those symptoms; they do not establish the exact cause of the reported production kicks or all remaining lag. Sustained legitimate traffic above configured budgets can still require operator tuning. Keep the `budget`, confirmation, action, geometry and TPS evidence from any recurrence. Historical logged kicks are not rewritten. [Migration functionality and its validation](VALIDATION-BLOCK-MIGRATION-1.2.44.md) remain included.
