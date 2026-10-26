# Block Nuker and player occurrence reports — 1.2.53

Includes the correction/decay policy documented in NOCLIP-CORRECTION-ESCALATION-1.2.53.md.

Block Nuker controls are in PacketGuard movement settings:
- PacketGuardBlockNukerEnabled: true
- PacketGuardBlockNukerLimit: 30 (5–200), distinct client break targets in a sliding second
- PacketGuardBlockNukerKickEnabled: true
Requires ingress protection. Normal placement, block damage progress, decor removal,
creative players and configured admin exemptions do not enter this counter. Verified
against official 1.22.7: BlockPlaceOrBreak mode 0 is break, mode 1 place, mode 2 decor.
Only already-validated, loaded, in-bounds client requests are counted; secondary server-side
vein/tree breaks do not enter this path. Client-driven mass-break tools can exceed the
limit: raise it or disable this module if your server intentionally permits that behavior.

The 31st distinct request in one second is denied before native handler execution. Repeat
requests for one position do not inflate the distinct count. A single burst never kicks.
A second excessive burst at least one second later within 15 seconds can kick under
healthy server timing. Lagged/warmup timing clears confirmation while denial remains.
The queued kick rechecks connection identity, player mode/admin exemptions, current
settings, server health and queue delay. Stale callbacks cannot kick replacement sessions.
Native wire/block budgets remain in place. Raw findings cannot bypass this dedicated
confirmation path through generic punishment rules. No automatic ban is added.

Player reports:
- Each player's evidence drawer lists recorded server-occurrence totals by code, first
  recorded timestamp and latest timestamp.
- Player list also shows aggregate recorded server occurrences for comparison.
- Detection, accepted noclip correction and dedicated kick codes stay distinct.
- Counts are session-held, not persisted to disk or a rolling per-minute activity rate.
  Ingress findings are rate-limited: count means recorded reports, not every rejected
  packet, every geometry sample, or confirmed cheating. No retrospective history backfill.
- Per-player count retention is bounded to 128 codes and does not multiply current
  same-code risk findings. Existing risk decay remains independent from display totals.

Validation passed: official 1.22.7 build (zero warnings/errors), policy checks for the
30-target allowance/31st denial, independent repeat confirmation, lag exclusion,
duplicate targets and expiry; native occurrence counting without duplicate risk rows;
settings round-trip/legacy preservation/failure rollback; browser coverage of 36
fields and occurrence rendering with separate detection/correction rows. No live
malicious client, vein-tool compatibility trial or production server change performed.
