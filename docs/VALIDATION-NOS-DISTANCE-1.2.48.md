# Suite 1.2.48 · NOS Distance1

This Suite build adds controls for NOS 0.10.0-preview. All previous 1.2.48 animal-generation, timeline and memory/performance work remains. Companion MOTD, Discord, ChatGuard, Player List and Client Integrity modules are unchanged in this pass.

## Using the new controls

Install the updated Suite and NOS ZIPs on the official inspected Vintage Story 1.22.7 server and restart. Open **Optimizations → Newfies Server Optimizer**. Stop any active NOS capture before editing. Master Enable must be on and Safe Mode off for patches to activate.

The five new switches default off:

- **Space out new AI task evaluations:** less frequent new-task selection for eligible distant animals, while current tasks continue.
- **Space out distant wandering checks:** independent check interval, without skipping active task continuation.
- **Space out stationary distant repulsion:** moving or protected animals retain stock frequency.
- **Reuse waypoint-search candidate nodes:** bounded candidate slots on exact official solvers; raw paths outside that scope remain independently owned.
- **Skip missing-attribute removal resyncs:** avoid the redundant full-tree dirty mark when a nonexistent key is removed. Leave disabled if a mod relies on that call to force synchronization.

Enable one switch at a time, then **Save and Apply NOS Settings**. Review actual module states and counters. Configured on does not mean active: unsupported engine identities and conflicting patches retain stock behavior and report a reason. Settings persist in optimizer.json. Manual JSON edits require restart; web changes apply immediately.

The default include pattern `game:*` covers vanilla codes. Add the actual namespaces from your entity listing to opt other data-defined animals in. Custom entity, behavior and AI-task implementations remain exempt even when a code matches, so heavily modded servers may have many exempt animals. Empty include means no animals qualify. Named, bred, owned, mounted, hurt and other protected animals retain normal scheduling. The protection radius has a fixed 16-block margin. See the optimizer guide for exact exclusions and timing bounds.

Read-only accounts and Suite Safe Diagnostics Mode cannot alter modules. Older NOS versions leave unsupported controls disabled. Refresh preserves unsaved drafts; a failed save also preserves the draft.

## Validation and practical limits

[Optimizer validation](../modules/optimizer/docs/performance-0.10.0.md) records component, browser and copied-server checks, including repeated official A* searches and retained raw paths. The selected pack includes every Suite companion plus XSkills, Draconis, Forgotten Armory, Floral Zones and other mods. Loading a mod is not equivalent to exercising every feature, and there were no authenticated players in these laboratory runs.

Distance scheduling deliberately changes distant decision frequency. It does not skip full entity ticks or provide full entity sleep. NOS does not add forced collections, change process-wide GC settings, throttle terrain delivery or replace the official runtime. TCP batching, tracking/send-frequency policies and negotiated client delta updates remain outside this pass. No full Synergy/Tungsten parity, production TPS improvement or guaranteed heap reduction is claimed.
