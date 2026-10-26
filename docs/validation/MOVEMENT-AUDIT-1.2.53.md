# Movement audit — 1.2.53

Reviewed PacketGuard movement sampling, mount transitions, spawn trust, collision probes,
flight confirmation and rollback candidate selection.

Fixed:
- Collision-validated rollback now requires an eligible recent candidate whose entire body
  probe completes in loaded terrain and has less than 0.03 blocks of penetration.
  Unknown/throwing geometry and exhausted/stale history cancel correction instead of
  selecting an unchecked fallback. Cancelled selection grants no correction grace.
- Dismount grace applies even when the general mounted-player exemption is disabled;
  mount profiles can still track mounted state in that configuration.
- Removed spawn-coordinate-only teleport authorization in both movement and shared
  transition sampling. Being near spawn is not evidence of a server-authorized teleport.
  API hooks, engine flags, lifecycle handling and registered transitions remain in place.

Compatibility: mods that directly write player coordinates for spawn teleportation without
using the server teleport API or registered trust may now produce movement findings.
Test those integrations before enabling automatic punishment.

Limits: flight remains advisory. Straight-line noclip checks cannot prove intent. Collision
validation can be explicitly disabled by configuration. This audit is not a real-player
false-positive rate measurement, nor a guarantee that all movement bypasses are closed.

Validation: official 1.22.7 build passed with zero warnings/errors; 21 compiled
collision/flight runtime checks passed, including five rollback regression checks.
PacketGuard policy suite, source consistency and whitespace checks passed.
No live-player or modded-teleport integration run was performed in this pass.

## Mounted flight follow-up
Shared mount/dismount transition grace no longer depends on the general mount exemption
switch. This covers flight review when main movement sampling is disabled. Existing
server-side MountedOn, FreeMove/NoClip, game-mode and configured admin exemptions remain.
PACKET_FLIGHT_REVIEW is advisory and excluded from automatic punishment.
Policy regression confirms ineligible flight clears accumulated hover evidence and a full
new observation window is needed after returning to eligibility. No live Draconis test
was performed; compatibility relies on the mount exposing the native MountedOn relation.
