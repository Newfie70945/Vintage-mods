# Guard escalation audit — 1.2.53

Fixed findings:
1. Noclip strike accounting used successful teleport API return, not observed arrival.
   Requests now stage a bounded pending correction. A later sample (75ms–3s) must
   observe the same entity/dimension within 0.75 blocks of the requested destination.
   A matching arrival counts once; timeout, invalid coordinates, entity/dimension change,
   mount or unreliable timing/disabled protection cannot count. This is server-observed
   arrival, not proof the client rendered or acknowledged the correction.
2. Block Nuker evidence could survive player/entity/dimension/limit changes on a reused
   connection. Context changes reset its window; queued kicks recheck context generation,
   current limit, entity, dimension, admin/mode, health and queue delay. Old callbacks
   cannot clear a newer context's pending flag. One kick per context is scheduled.
3. Denial evidence strings were formatted before report cooldown suppression. They are
   now constructed only for a report or a queued kick, keeping saturated denial cheaper.

Existing settings remain: five observed noclip corrections at least 10s apart, each
expires after 120s; Block Nuker allows 30 distinct client break targets/second and kicks
only on independently spaced repeated healthy over-limit bursts. Raw findings remain
excluded from generic first-finding punishment. No bans are introduced.

Further priorities, not implemented here:
- Recoverable block/inventory resynchronization after denied actions, tested against
  modded tools and their prediction behavior.
- Moderation warnings and configurable per-player exemptions with expiry/reason/audit.
- Per-hour comparison rates and recent-event timelines; current occurrence totals are
  session counts and rate-limited reports, not all packets or confirmed cheating.
- Real-player regression trials around doors, chiseling, slopes, mount transitions,
  delayed teleports, mining bonuses and packet delivery after server stalls.
- Tool-aware mining-progress validation using server-owned inputs without assuming
  vanilla timing for XSkills or modded mining tools.

Validation passed: official 1.22.7 Release build (zero warnings/errors), policy tests
covering pending-arrival timing/timeout/identity/dimension/eligibility and context reset,
24 compiled collision/flight checks, native ingress/occurrence regressions and compiled
queued-kick tests verifying limit changes and context generations. Source consistency
and whitespace checks passed. No live modpack multiplayer session was run.
