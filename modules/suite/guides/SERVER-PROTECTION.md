# Server-side protection

Suite 1.2.53 · ms11-serverprotection1 · official Vintage Story 1.22.7.

Replace the old Suite ZIP and keep your existing configuration files. Example JSON is
a reference, not a replacement for your live settings.

Open **Anti-Cheat → PacketGuard → Movement → Server action safety**.
New combat and inventory checks start in **observation-only** mode. Leave that on while
reviewing your server's tools and mods. Turning it off permits the new checks to deny
actions; it does not add combat/inventory automatic kicks. Existing movement, reach,
mining, Block Nuker and noclip policies keep their own switches.

## New combat and inventory checks

New heuristics apply to survival players and respect configured admin exemptions.
Malformed-packet and wire resource safety remain independent.

Combat observes attack request rates and repeated attacks through ordinary solid walls.
Wall evidence requires three sampled target heights, loaded terrain, matching dimensions
and ordinary unit-cube collision shapes. Doors, chiseling, block entities/behaviors, custom block types, partial
shapes, unsupported ranges and unknown terrain cannot establish wall evidence. Mounts
and configured administrator exemptions apply. Wall observations are repeated before
any configured denial. Line-of-sight work is bounded globally and per player.

Weapon minimum intervals are an optional JSON object of exact codes and milliseconds,
for example `{"game:sword-test":300}`. The example is illustrative: select real codes
and test them. Defaults are empty because no universal weapon cooldown is assumed.
Intervals must be 50–5000ms; up to 128 entries are allowed. Three rapid observations are
required. These limits are explicit server policy, not proof of a weapon's true cooldown.

Combat and inventory each have a separate token budget (default 20 and 100 requests/sec,
with three seconds of burst capacity). Budget excess is observed in shadow mode and
can be denied when shadow mode is off and server timing is reliable.

Inventory checks inspect server-known inventory availability/open state and ordinary
slot bounds. Native tabbed, custom and special-slot behavior is retained. Source-stack
quantities, destination capacity, permissions, item attributes, crafting outputs and
revision resynchronization remain native: the Suite does not replay crafting hooks,
cache inventory results, split batches or add a competing item-conservation ledger.

## Mining compatibility and movement allowances

Existing tool-aware mining estimates use the loaded server block, current resistance, tool speed/tier/material
and block/collectible behaviors. Foreign Harmony patches to breaking/speed/resistance
methods now downgrade inferred timing to uncertain. Exact tool codes can also be added
to the review-only list for cadence compatibility; reach and Block Nuker remain independent. New start/completion telemetry pairs a
matching damage-request marker with the server's current tool/block and timestamp.
That marker comes from the client, so it never proves completed work and never by
itself authorizes denial or punishment. Secondary server-side vein/tree breaks are
excluded from the distinct client-request Block Nuker counter.

Accepted server damage with positive knockback supplies a 750ms allowance for ordinary
speed/acceleration analysis. It does not authorize arbitrary displacement or disable
noclip geometry checks. Flight review excludes this short impulse period. The panel
shows whether the official damage hooks actually installed; unavailable hooks fail
open without changing damage results. Unusual launch mods can use a temporary scoped
exemption while compatibility is investigated.

## Corrections, warnings and recovery

Noclip remains off by default. When enabled, its configured correction/kick policy is
independent of new-action shadow mode. Simple-solid evidence must repeat before a
correction request. A later server sample must observe the requested destination before
a correction counts. Default kick policy: five independently spaced corrections,
at least ten seconds apart; each expires after two minutes. Failed corrections never
add a kick strike. Three failed recovery attempts in 30 seconds suspend further noclip
corrections for 60 seconds and record an alert. Successful recovery clears the failure
streak. This prevents repeated failed-teleport loops without granting permanent trust.

Escalation warnings are independently enabled by default and rate-limited. They show
active noclip correction counts, expiry, kick policy and blocked-break explanations.
Uncertain combat/mining observations do not produce player cheat accusations.

Denied loaded blocks and known open inventory slots can be resynchronized without
editing the world. This work is limited per connection and globally. OreShield-enabled
block resynchronization uses one uniform, recipient-masked sync transport for both
ordinary and protected cells. It cannot fall back to a raw ore-bearing update when
OreShield is unavailable. Unsupported/unloaded cells are skipped, not force-loaded.
This is targeted correction, not a universal reconstruction of all mod GUI/fluid state.

## Player reports and temporary exemptions

Open a player → **Anti-Cheat investigation**:

- Recorded occurrences distinguish detection, correction and kick codes. Rates are
  session averages after at least one minute, not a count of every packet or proof of
  cheating. Active players and longer sessions naturally have different totals.
- The latest 64 server events retain timestamps, summaries and bounded evidence.
- Download incident JSON to include the player snapshot, settings, installed mod
  versions and Suite/API build identity. It is authenticated and can contain sensitive
  player/mod information; review before sharing. No credentials or full config export
  are included by this endpoint.
- Administrators/owners can grant or revoke Movement, Mining, Combat, Inventory or
  BlockNuker exemptions. Each needs a reason and expires within one hour. Read Only
  accounts cannot change them. Grants are audited; malformed-packet safety and unrelated
  scopes remain active. Exemptions are memory-held, not persisted across an Anti-Cheat module/server restart.

## Limits and rollout

Test doors, chiseling, mounts/dismounts, knockback, translocators, mining bonuses and
client-driven mass-break tools on a disposable world before enabling new denials. Keep
shadow mode on for a representative player workload and compare evidence with actual
activity. This release does not prove that client reports are truthful or detect every
hidden Harmony mod. Real-server behavior checks stay independent of clean client claims.

### Vanilla gliders

The official `game:glider` must be equipped in a backpack slot and actively gliding to select the glider movement envelope. It allows up to 120 horizontal blocks/second, including two seconds of landing grace while still equipped. Takeoff and landing start fresh speed/acceleration windows. Normal gliding inside that envelope does not accumulate walking-speed/acceleration findings. Teleport limits, loaded-terrain noclip checks and action/packet checks still run. A glide flag alone or a glider carried inside a bag does not qualify. This generous compatibility envelope is not a guarantee of legitimate movement.

Speed observations marked `speedEscalationEligible=False`, or older rows without timing eligibility, stay visible for review but add no generic risk, correlation or automatic policy consequences. The repeated-speed bonus applies only to eligible findings. This closes a path where a risk-based rule could punish a timing-degraded observation despite the dedicated speed-kick gate rejecting it.
