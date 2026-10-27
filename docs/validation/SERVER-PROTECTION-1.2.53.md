# Server protection implementation and validation

Target: Suite 1.2.53, ms11-serverprotection1; official server 1.22.7.

| Requested area | Implementation |
|---|---|
| Mining progress | Existing server-tool/block estimates retained; foreign mining hooks and exact tools can downgrade to review. Matching start-marker timing added as advisory, never trusted proof. |
| Inventory transactions | Native permissions/quantities/revisions retained, plus bounded open-state/ordinary-slot observations and independent action budget. No duplicate crafting hooks or batch-size changes. |
| Combat timing | Request-rate observations, sustained rate confirmation and explicit per-weapon minimum intervals; default shadow mode. |
| Combat visibility | Three target-height probes, known ordinary solid geometry only, repeated observations, loaded-only and globally bounded. |
| Legitimate movement | Server-observed accepted knockback damage supplies a short speed/acceleration allowance; hard movement and collision bounds stay active. |
| Correction loops | Failed recovery streak pauses corrections with review alert; successful arrival clears failures. |
| Warnings | Rate-limited correction/expiry/kick-policy and denied-break notices. |
| Exemptions | Scoped, bounded, expiring, reasoned administrator grants with web audit and Read Only/CSRF protection. |
| Event timelines | Per-player latest 64 server events, timestamps and capped evidence strings. |
| Comparable rates | Session-average recorded events/hour after one minute; no claim of activity-normalized cheat rates. |
| Incident exports | Authenticated bounded player/evidence/settings/mod-version/build JSON. |
| Resynchronization | Targeted loaded-block/open-slot updates with per-connection/global budgets and recipient masking. |
| OreShield consistency | Missing placeholders excluded from host/protected palette selection; denied-action transport uniform across protected/ordinary cells; existing chunk/reveal/recipient tests retained. |
| Expensive actions | Separate inventory/combat ingress budgets; LOS budget and per-player sampling; bounded resync work. |
| Shadow testing | New combat/inventory observations allow actions by default; optional denials remain separate from automatic kick policies. |
| Compatibility regression | Policy/native/browser/HTTP fixtures and official-server startup/save checks; see actual outcomes below. |

Additional fix: PacketGuard save responses now carry the applied snapshot, preventing a
stale background cache refresh from making a successful settings change appear reverted.

Important limitations:
- No live Draconis/XSkills player workload, real hostile client or production false-positive
  rate has been measured. Native fixtures model compatibility boundaries, not those mods.
- No universal mining-progress proof, weapon cooldown inference or competing inventory
  transaction engine is claimed. Native rules and mod hooks remain authoritative.
- Client reports can still be fabricated by a modified client. These changes do not add
  kernel/process attestation or promise detection of hidden mods.
- Exemptions/timelines/session occurrence counters are bounded in memory, not a durable
  cross-restart statistical database. Existing history/log retention is separate.


## Validation results

- Release build against official 1.22.7: zero warnings/errors.
- Policy tests: scope/expiry bounds, correction recovery, request windows, shadow/advisory
  consequence rules, mining-marker identity/timing and prior noclip/nuker regressions.
- Compiled runtime: 17 combat/impulse/palette/mining checks plus 24 collision/flight checks;
  stock/foreign-Harmony mining classification, unknown geometry and loaded-only reads.
- Native ingress: shadow/enforced inventory/weapon behavior, exemptions, distinct evidence
  totals/timeline bounds and queued-kick context protection.
- Settings: 44 scalar fields plus tool maps/lists; legacy/Shift+L preservation, validation,
  persistence failures and independent dictionary rollback.
- Browser: display/save/drafts, textarea preservation, applied settings vs stale cache,
  desktop/mobile, Read Only controls, exemption payloads and escaped timeline/reason HTML.
- OreShield: real patched chunk masking/recipient isolation, immutable canonical payload,
  reveal/placement/vein regressions, uniform denied-cell resynchronization transport.
- Disposable official server with all seven mod IDs (Suite, five companion modules and
  NOS): live settings persistence/applied response, authenticated export lookup,
  exemption CSRF and Read Only denial, explicit world save, zero server errors, clean
  exit and SQLite world integrity `ok`. No authenticated game players were connected.

First live iteration exposed a stale cached settings read after save. The corrected
endpoint returns the applied snapshot, and the panel prioritizes it over a stale cached
read. The successful final run is the validation cited above.

Final hardening: mining estimates/grouping read the loaded world block instead of trusting a supplied selection block ID. A compiled mismatch regression verifies this.

Final packaged DLL was tested in disposable run `web-snapshot-1793061444`:
seven mod IDs loaded, zero server errors, exit 0, SQLite save integrity `ok`.
Runtime snapshot: combat Active, impulse allowance Active (2 official method hooks),
new action shadow mode true. The integration result records the installed DLL SHA-256;
it matches the packaged DLL. Optional inventory/mining observer failure paths also
passed fail-open native regressions. No production world/configuration was accessed.
