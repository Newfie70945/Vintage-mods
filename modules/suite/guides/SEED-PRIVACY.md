# Client seed privacy preview — Suite 1.2.54

This opt-in server feature replaces seed values in outgoing native packets on
official Vintage Story 1.22.7 dedicated servers. It does not change the real world
seed, terrain generation, server weather objects or saved region weather bytes.
It is disabled by default and requires a restart to enable, disable or reconfigure.

After starting Suite once, edit
`ModConfig/NewfiesServerEssentialsSuite/NewfiesSeedPrivacy.json`:

```json
{
  "Enabled": true,
  "PublicSeed": 0
}
```

Choose a stable public seed different from the real seed. Restart and use
`/seedprivacy` from the console or an account with `controlserver` privilege.
Check that the status says **Active**. Unsupported game versions, unavailable
weather mappings, an equal public/real seed or an existing foreign serializer
patch leave privacy **NOT ACTIVE** and emit a warning. Such startup refusal does
not stop the server or prevent normal client connections.

## Coverage

- Native identification packet seed.
- Native weather channel RNG seed fields.
- Native map-region `weatherState` RNG seed fields inside mod data.

Only outgoing copies are edited. Other protobuf fields, including unknown fields,
are preserved. When an active sanitizer rejects a malformed sensitive packet, it
throws before serialization instead of sending original data or an old reusable
buffer. Native caller exception handling applies; a failed send can interrupt a
client's update or connection. Repeated rejection warnings require investigation.
There are no polling timers or extra network requests. Enabled mode adds a small
packet check and allocates rewritten copies for the covered packet types; no
multiplayer overhead benchmark has been established.

## Compatibility and limits

Treat this as a client compatibility preview. The client uses the public seed for
some calendar, weather and visual calculations. Automated native packet tests do
not establish that these look correct in a real client. Trial it on a copied world
with your modpack, checking joining/rejoining, weather transitions, calendar,
regional travel and saving/restarting before wider use. Disable and restart if
client behavior is affected; this does not require changing the saved world seed.

This covers the inspected native paths, not arbitrary third-party mod messages,
commands, web panels or downloads. It cannot revoke a real seed previously learned
by players or guarantee protection against seed inference from generated terrain.
Keep OreShield enabled for its separate ore-data protection. Seed privacy alone
does not establish that an ore prediction mod is ineffective.
