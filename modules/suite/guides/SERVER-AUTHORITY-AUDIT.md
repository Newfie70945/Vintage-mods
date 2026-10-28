# Server-authoritative protection audit — Suite 1.2.55

## Confirmed and fixed: concealed ore IDs in compressed palettes

The previous chunk rebuild first inserted all source IDs into a native
ChunkDataLayer, then overwrote hidden ore cells with host rock. Native SetUnsafe
retains palette entries after overwrite, and Compress serializes that palette.
Decoded cells were concealed, but the palette still disclosed concealed mineral
variants. This reveals chunk-level mineral presence; the audit does not establish
that the palette alone recovers exact deposit coordinates or the world seed.

A new native-wire regression failed against Suite 1.2.54 with:
`Concealed-only ore variant survived in the outgoing wire palette.`

Suite 1.2.55 masks the private unpacked array before constructing the output layer.
Only the recipient's final view enters the fresh palette. Canonical packet bytes
and the authoritative world are unchanged. Existing visible/discovered and
player-placed ore remain visible. No new poll, world scan or second chunk traversal
is introduced; both old and new paths fill the output layer once. No production
performance improvement is claimed.

## Other audit findings and limits

- Native recipient chunk sends, single updates, bulk updates, denied-action
  corrections, discovery sync, ACKs and resync requests were inspected. Client
  ACKs do not grant discoveries; resyncs use retained server-owned discoveries.
  Existing tests cover recipient/cache isolation, loaded-only handling and
  dimension-aware discovery/placed-ore persistence.
- OreShield deliberately excludes non-cube, emitting, block-entity-bearing or
  host-incompatible ore. Its protected/skipped counters and sampled skipped codes
  must be checked with the actual modpack. This is a coverage limit, not a promise
  that all modded ores are concealed. Unknown ores should be individually supported
  after checking geometry, behavior and host compatibility.
- Native ServerMapRegion.ToPacket does not send its OreMaps dictionary. It does
  send geology/climate/landform maps, generated structures and arbitrary ModData.
  OreShield does not scrub arbitrary third-party region data/custom channels.
  Seed privacy covers only its documented native fields and remains default-off.
  No third-party ore/geology mod packet inventory was available for this audit.
- Packet ingress has bounded resource controls and structural checks. Reach and
  movement use server context, confirmations and timing exclusions. Current tests
  cover teleport/dimension transitions, lag bursts, complex collision geometry,
  duplicate targets and review-only observations. These checks do not establish
  that every modded movement or mining mechanic is classified correctly.
- New combat/inventory checks default to shadow mode. Their presence does not mean
  requests are denied. Weapon cadence requires explicit server tool-code policy;
  unknown custom inventory/mining semantics retain native/mod handling rather
  than asserting cheating. Enabling broad enforcement without modpack testing
  could reject legitimate actions. No live policy was changed.
- Client-originated mining-start markers are explicitly advisory, not proof of
  completed work. Foreign mining patches downgrade inferred timing reliability.
- Review-only observations are excluded from automatic punishment. Stale client
  verification remains a separate previously reported policy gap, not fixed by
  this packet leak repair. A clean client report still cannot attest completeness.

## Scope

Static source/native 1.22.7 inspection and compiled regression/native disposable
server checks. No suspected bypass source, real player clients, live configuration
or production modpack was available. This is not a certification of all packet
paths or an independently reproduced client-report bypass. No movement thresholds,
consequence rules or shadow-mode defaults were changed.
