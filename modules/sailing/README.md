# Newfie's Realistic Sailing 0.12.1

Mod for Vintage Story 1.22.7 that makes sailing depend on real weather. Everything players do is a crafted item you right-click; there are no player commands. Because it adds items, install it on the server **and** clients. Item names are translated into English, German, Spanish, French and Brazilian Portuguese; chat messages are English.

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
| Fog horn | sounds a loud blast; sailors within 250 blocks get a bearing | 5 copper ingots |
| Sailor's concertina | plays a shanty: crew morale +20 (once a minute per boat) | 2 planks, flax twine, copper ingot |
| Brass spyglass | hold to zoom in | copper, glass, copper (vertical) |
| Treasure chart | bearing and distance to the mark; stand on it to dig | found in bottles and wrecks |
| Boat nameplate | names the nearest sailboat | 2 copper ingots |
| Deep keel | upgrade: leeway x0.4, capsize risk x0.5 | 3 planks over an iron ingot |
| Heavy sailcloth | upgrade: sail wear x0.5, +5% speed | ring of flax fibres around twine |
| Hull reinforcement | upgrade: hull wear and flooding x0.5 | iron, planks |
| Oars | hold right-click aboard to row where you look (speed-limited) | plank over 2 sticks |
| Tow rope | beside a boat, then beside the towing boat, to hitch; use again to cast off | 3 flax twine, iron ingot |
| Lightning rod | upgrade: lightning strikes do no damage | 3 copper ingots (vertical) |
| Sailor's logbook | your record: distance, squalls, temporal storms, strongest wind, repairs | 4 parchment |

Models are multi-part shapes sharing one colour-swatch texture (`textures/item/palette.png`) and recipe ingredient codes are unverified against the game; edit the JSON under `assets/newfiessailing/recipes/grid` if one does not exist. The only command is the admin test switch `/sailingadmin` (needs the `controlserver` privilege), which toggles a simulated temporal storm.

## What changes at sea

- **Apparent wind.** The wind felt on deck is the true wind minus the boat's own velocity, so running before the wind feels lighter and beating feels stronger and further forward. Drive, overload, capsize risk and heel use it; sea state uses the true wind. `MotionToKnots` (default 60) converts the game's motion units to knots and may need tuning.
- **Wind shadow.** Land or trees upwind blanket the wind: sail close under an island and you lose up to 80% of it.
- **Shoals and groundings.** Shallow water scrapes the hull and slows the boat; running aground nearly stops it. A deep keel draws more water, so it scrapes sooner.
- **Tacking and gybing.** Going about through the wind costs momentum (worse in light air). An accidental gybe in a fresh breeze whips the boom across, damaging the sails and the crew's nerve.
- **Sea breeze and whitecaps.** Winds run about 12% stronger by day and lighter at night; from Force 4 white horses appear on the sea around you.
- **Wind-driven speed.** Sailboat speed follows the wind at the boat's position. Becalmed boats crawl; a fresh breeze (Force 5) gives full drive.
- **Points of sail.** Beam reach is fastest; head to wind and running are slow.
- **Sail trim.** Full sail is fastest in moderate wind but overpowered above ~25 kn of load; reefed (55% area) is safer in a gale; furled loses way. Overpowered boats get a chat warning.
- **Heeling (client) and leeway.** Hulls lean away from the wind under load; close-hauled boats slide sideways. Set `HeelDirection` to `-1` in the client config if the lean looks wrong.
- **Gusts and squalls.** Every 5-15 minutes a squall hits: a warning ~25 s before, wind ~2x for 30-75 s. Synced to clients.
- **Seasonal winds.** Winter +25%, autumn +10%, summer -15%.
- **Temporal storms at sea.** Wandering wind, +30% strength, two forces rougher sea, violet sparks, and capped drifter boarders (`BoarderEntityCode`) that despawn afterwards. State is read from the game by reflection; one warning is logged if it cannot be read.
- **Wear.** Hull and sail condition (stored on the boat) wear faster in gales, when overpowered and in temporal storms; worn boats are slower. Warnings at 50/25/10%.
- **Fog banks.** Every 10-25 minutes a fog bank lasts 2-4 minutes: visibility falls to roughly 40 blocks, the wind drops by up to 40% and sailors are warned. The ship's compass report and weather glass mention it. The client eases an ambient fog in while you are on or near a boat.
- **Rougher seas.** Boats roll and pitch harder (client `SeaRoughness` 1.5); beating into Force 5+ seas costs up to 40% speed; from Force 7 rogue waves slam boats under way, shipping water, damaging the hull, checking speed and lurching the hull with a spray burst.
- **Crew morale.** Each boat has morale: fair weather lifts it, gales, storms, leaks and fog sap it, and a low morale slows the boat by up to 12%. Shanties raise it; capsizes and rogue waves hurt it.
- **Sound.** Generated effects for the fog horn, wind gusts, hull creaks, wave slams, balloon hiss and the shanty (`assets/newfiessailing/sounds`).
- **Drifting wrecks.** In fog, wrecks drift out of the mist near sailors. Right-click one once for rusty gears, twine, planks and the odd ingot.
- **Tides and currents.** The tide turns about every 12.4 game hours; a current swings back and forth along a locally varying axis and pushes boats that have a rider (unattended boats stay put unless `CurrentsAffectEmptyBoats`). The anchor holds against it. The ship's compass report shows the tide state and current.
- **Bottles and charts.** Messages in bottles drift on the wind; open one for a note and often a treasure chart. Wrecks can hold charts too. Follow the chart's bearing and distance, stand on the mark and right-click to dig up loot.
- **Names and upgrades.** Nameplates give a boat a name used in messages and reports. A deep keel, heavy sailcloth and a reinforced hull are one-off upgrades that change leeway, capsize risk, wear, flooding and speed.
- **Lightning.** Squall peaks, temporal storms and Force 9+ gales can bring strikes: scorched sails and shaken crew unless a lightning rod is fitted.
- **Whirlpools.** In temporal storms a whirlpool can open 45-70 blocks from a sailor. It spirals nearby boats inward for 90 seconds and floods and damages any that reach the centre. Sail, oars or the anchor (half the pull) help escape.
- **Rowing, towing, crowding.** Oars let a boat move in a calm or fog. A tow rope hitches a swamped or disabled boat behind a healthy one. More than two riders slows a boat by 6% each, to a minimum of 70%.
- **Night glow.** After dark the wake sparkles with phosphorescent specks.
- **Swamping.** Boats with a hull under 50% leak, and Force 7+ seas slop water aboard. Waterlogged boats slow to a quarter speed and warn at 60% and 100%. Bail with the bucket or patch the hull.
- **Capsizing.** Badly overloaded full sail (about 1.4x the overload limit), worst on a beam reach, can roll the boat over: riders are thrown into the water, the hull sits upside down and cannot sail. Reefed or furled sail is immune. A recovery balloon launches three real balloon entities (red, yellow, blue) that rise above the boat; after 3 seconds the hull is righted and refloated (water set to 35%, hull at least 25%), then the balloons hover and drift away.
- **Anchor.** Holds against `AnchorHoldKnots` (35) of wind, drags beyond.
- **Storm visuals (client).** Bobbing hulls, spray from Force 3, stern wake foam. Tune in `ModConfig/NewfiesSailingClient.json`.

Only entities whose code contains `boat` and one of `BoatCodeContains` (default `sail`) are affected; rafts are left alone.

## Configuration

Copy `NewfiesSailing.example.json` / `NewfiesSailingClient.example.json` from the download for every option with its default (the files are also generated automatically in `ModConfig` on first start).

`ModConfig/NewfiesSailing.json` is written on first start. Tune `KnotsPerWindUnit`, `Min/MaxSpeedMultiplier`, `ReferenceKnots`, `OverloadKnots`, `LeewayStrength`, squall and temporal-storm settings, `WearRate`, `AnchorHoldKnots` and `BoatCodeContains`.

## Testing

See [the test checklist](../../docs/SAILING-TEST-CHECKLIST.md). Floating markets are not implemented, and `HeaveEnabled` (vertical swell) is experimental.

## Source layout

- `src/SailingModel.cs` – pure maths, unit tested.
- `src/SailingModSystem*.cs` – server logic, split into `Weather`, `Boats` and `Items` partial files.
- `src/SailingVisuals.cs`, `SpyglassZoom.cs` – client-only visuals, sounds and zoom.
- `src/Entity*.cs`, `ItemSailingTool.cs` – balloons, drifting finds, whirlpools and the tool item.
- `assets/newfiessailing` – items, entities, shapes, textures, sounds, recipes and translations.

## Status

The physics model (`src/SailingModel.cs`) is unit tested in `tests/Program.cs`. The game integration was written without a Vintage Story install and has **not** been run in game; boat motion is client-predicted, so verify on a test server.
