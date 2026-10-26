# Noclip correction escalation — 1.2.53

Supersedes the earlier no-automatic-kick policy for noclip **correction history**.
Raw PACKET_NOCLIP and PACKET_NOCLIP_CORRECTION findings remain review-only for generic
risk/punishment rules; a separate bounded correction-history policy handles kicks.

Default settings (Anti-Cheat > PacketGuard > Movement in the webpanel):
- PacketGuardNoclipKickAfterCorrections: true
- PacketGuardNoclipCorrectionsBeforeKick: 5 (range 2–50)
- PacketGuardNoclipCorrectionDecaySeconds: 120 (range 30–3600)
- PacketGuardNoclipCorrectionMinimumIntervalSeconds: 10 (range 5–300)

Noclip detection must be enabled and PacketGuardRejectNoclip must allow corrections.
Detection remains off by default; this change does not turn it on. Disable the kick
switch for correction-only operation. Disabled anti-cheat cannot issue this kick.
Older settings clients preserve these fields when omitted.

Each qualifying episode still needs three spaced simple-solid-geometry observations
spanning at least 1.5 seconds. Doors, chiseled/custom shapes, unknown terrain and timing
uncertainty cannot authorize correction. A failed RollbackMovement adds no strike.
A successful server teleport request resets observation confirmation and records at most
one strike per configured minimum interval. It is not a client acknowledgement that the
player has received/rendered that correction. Each recorded strike expires independently;
for example, five distinct eligible corrections inside two minutes trigger the default
kick, while expired corrections no longer contribute. Clock regression clears history.
History is per movement session, bounded to 50 timestamps, and not persisted across
reconnects. This policy kicks, never bans. Draconis/authorized-flight/admin exemptions
remain as configured. Correction audit findings include active count and policy values.

Validation scope: native fixtures/policy/browser tests, not a live player/modpack trial.

Validation passed: official 1.22.7 build (zero warnings/errors), policy tests,
24 compiled collision/flight checks, settings round-trip/preservation/failure tests
and browser coverage of 33 fields including desktop/mobile/read-only behavior.
