# Newfie's Realistic Sailing 0.7.0

Mod for Vintage Story 1.22.7 that makes sailing depend on real weather. Everything players do is a crafted item you right-click; there are no player commands. Because it adds items, install it on the server **and** clients.

## Items

| Item | Right-click | Recipe |
| --- | --- | --- |
| Weather glass | pressure, trend and forecast (falls before squalls, swings wildly in temporal storms) | glass over copper ingot |
| Wind vane | wind direction, knots, Beaufort force | feather over stick |
| Ship's compass | full report: wind, sea state, point of sail, sail/hull condition, season, storms (spins in temporal storms) | copper ingots around glass |
| Rigging line | while riding a sailboat, cycle sails full, reefed, furled | 3 flax twine |
| Ship's anchor | while riding, drop or raise the anchor | 4 iron ingots (T shape) |
| Shipwright's repair kit | near a sailboat, patch the worse of hull or sails (+25%); 24 uses | plank, flax twine, 2 copper ingots |
| Bailing bucket | within 6 blocks of a boat, bail out 30% of the water; 40 uses | 5 planks (bucket shape) |
| Recovery balloon | within 10 blocks of a capsized or swamped boat: rights and refloats it; single use | 6 flax fibres over flax twine |
| Sailor's logbook | your record: distance, squalls, temporal storms, strongest wind, repairs | 4 parchment |

Models are multi-part shapes sharing one colour-swatch texture (`textures/item/palette.png`) and recipe ingredient codes are unverified against the game; edit the JSON under `assets/newfiessailing/recipes/grid` if one does not exist. The only command is the admin test switch `/sailingadmin` (needs the `controlserver` privilege), which toggles a simulated temporal storm.

## What changes at sea

- **Wind-driven speed.** Sailboat speed follows the wind at the boat's position. Becalmed boats crawl; a fresh breeze (Force 5) gives full drive.
- **Points of sail.** Beam reach is fastest; head to wind and running are slow.
- **Sail trim.** Full sail is fastest in moderate wind but overpowered above ~25 kn of load; reefed (55% area) is safer in a gale; furled loses way. Overpowered boats get a chat warning.
- **Heeling (client) and leeway.** Hulls lean away from the wind under load; close-hauled boats slide sideways. Set `HeelDirection` to `-1` in the client config if the lean looks wrong.
- **Gusts and squalls.** Every 5-15 minutes a squall hits: a warning ~25 s before, wind ~2x for 30-75 s. Synced to clients.
- **Seasonal winds.** Winter +25%, autumn +10%, summer -15%.
- **Temporal storms at sea.** Wandering wind, +30% strength, two forces rougher sea, violet sparks, and capped drifter boarders (`BoarderEntityCode`) that despawn afterwards. State is read from the game by reflection; one warning is logged if it cannot be read.
- **Wear.** Hull and sail condition (stored on the boat) wear faster in gales, when overpowered and in temporal storms; worn boats are slower. Warnings at 50/25/10%.
- **Swamping.** Boats with a hull under 50% leak, and Force 7+ seas slop water aboard. Waterlogged boats slow to a quarter speed and warn at 60% and 100%. Bail with the bucket or patch the hull.
- **Capsizing.** Badly overloaded full sail (about 1.4x the overload limit), worst on a beam reach, can roll the boat over: riders are thrown into the water, the hull sits upside down and cannot sail. Reefed or furled sail is immune. A recovery balloon rights and refloats it (water set to 35%, hull at least 25%).
- **Anchor.** Holds against `AnchorHoldKnots` (35) of wind, drags beyond.
- **Storm visuals (client).** Bobbing hulls, spray from Force 3, stern wake foam. Tune in `ModConfig/NewfiesSailingClient.json`.

Only entities whose code contains `boat` and one of `BoatCodeContains` (default `sail`) are affected; rafts are left alone.

## Configuration

`ModConfig/NewfiesSailing.json` is written on first start. Tune `KnotsPerWindUnit`, `Min/MaxSpeedMultiplier`, `ReferenceKnots`, `OverloadKnots`, `LeewayStrength`, squall and temporal-storm settings, `WearRate`, `AnchorHoldKnots` and `BoatCodeContains`.

## Status

The physics model (`src/SailingModel.cs`) is unit tested in `tests/Program.cs`. The game integration was written without a Vintage Story install and has **not** been run in game; boat motion is client-predicted, so verify on a test server.
