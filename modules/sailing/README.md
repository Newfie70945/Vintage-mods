# Newfie's Realistic Sailing 0.5.0

Server-side mod for Vintage Story 1.22.7 that makes sailing depend on real weather.

- **Sail trim and reefing.** Ride a sailboat and use `/sailing trim full|reefed|furled`. Full sail is fastest in moderate wind but becomes overpowered above ~25 kn of wind load and slows hard; reefed sails (55% area) are slower in light air but stay efficient in a gale; furled sails stop the boat. Trim is stored on the boat. Overpowered boats get a chat warning.
- **Heeling (client).** Under load the hull leans away from the wind, most on a beam reach, nil head to wind or running. If boats lean the wrong way on your install set `HeelDirection` to `-1` in the client config.
- **Leeway.** Close-hauled boats slide sideways downwind; running boats do not.
- **Squalls.** Every 5-15 minutes a squall rolls through: sailors get a warning about 25 s before, then wind builds to about 2x for 30-75 s and eases off. Squall strength is synced to clients so waves and heel match. Configure or disable in `NewfiesSailing.json` (`SquallsEnabled`, intervals, `SquallPeakMultiplier`).
- **Temporal storms at sea.** While a temporal storm is active the wind direction wanders up to ~85 degrees, strength rises 30%, the sea runs two forces rougher, `/sailing` reports a spinning compass, boats glow with violet sparks, and a capped number of `BoarderEntityCode` entities (default drifters) crawl out of the water beside occupied boats, despawning when the storm ends. Storm state is read from the game's temporal stability system by reflection; if it cannot be read the mod logs one warning. Admins can use `/sailing teststorm` to simulate one.
- **Anchor.** `/sailing anchor` drops or raises it; the boat stops, but in wind above `AnchorHoldKnots` (35) it drags downwind.
- **Weather glass and wind vane.** `/sailing glass` shows barometric pressure, trend and forecast (pressure falls before squalls) plus the wind direction. These are commands for now; craftable items need models and recipes.
- **Wake.** Boats trail foam behind the stern, scaling with speed.
- **Wear and repair.** Hull and sail condition (0-100%, stored on the boat) wear faster in gales, when overpowered under full sail and in temporal storms. Ragged sails lose drive and a leaking hull wallows; sailors are warned at 50, 25 and 10%. `/sailing repair hull` consumes a held plank or resin, `/sailing repair sail` a held linen, flax twine or rope (`HullRepairItems`, `SailRepairItems`), restoring 25% each. Stand within 6 blocks of the boat. `/sailing` shows condition. Tune with `WearRate` or turn off with `WearEnabled`.
- **Seasonal winds.** Winter blows 25% harder, autumn 10% harder, summer 15% lighter (`SeasonalWindEnabled`).
- **Logbook.** `/sailing log` shows distance sailed, squalls and temporal storms survived, strongest wind faced and repairs made, stored on the player.
- **Wind-driven speed.** Sailboat speed scales with the wind at the boat's position. Becalmed boats crawl; a fresh breeze (Force 5) gives full drive.
- **Points of sail.** Speed depends on the angle between course and wind: in irons and close-hauled are slow, a beam reach is fastest, running is moderate.
- **Gusts.** Deterministic gust and lull cycles vary the wind around its mean.
- **Storm slowdown and warnings.** Above storm force boats slow sharply, and sailors get a chat warning when the Beaufort force jumps or reaches gale.
- **Storm visuals (client).** Hulls pitch and roll with the sea state and boats throw spray from Force 3 up, growing with the wind. Cosmetic only; tune or disable in `ModConfig/NewfiesSailingClient.json` (`BobIntensity`, `BobbingEnabled`, `SplashesEnabled`). Players without the mod simply do not see them.
- **`/sailing`.** Shows wind speed in knots, direction (compass), Beaufort force, wave height and, while under way, your current point of sail.

Only entities whose code contains `boat` and one of `BoatCodeContains` (default `sail`) are affected, so rafts are untouched. Mounted players must be within 6 blocks of the boat.

## Items

| Item | Use (right-click) | Recipe |
| --- | --- | --- |
| Weather glass | pressure, trend, forecast | glass over copper ingot |
| Wind vane | wind direction, knots, Beaufort force | feather over stick |
| Ship's anchor | drop/raise anchor on the boat you ride | 4 iron ingots (T shape) |
| Sailor's logbook | your sailing record | 4 parchment |

The matching `/sailing` commands still work but require the item in your hotbar (`RequireItemsForCommands`, creative exempt). Because the mod adds items it must be installed on clients too. Textures and shapes are simple placeholders; recipe ingredient codes (`glass-plain`, `feather`, `paper-parchment`) are unverified against the game.

## Configuration

`ModConfig/NewfiesSailing.json` is written on first start. Tune `KnotsPerWindUnit`, `Min/MaxSpeedMultiplier`, `ReferenceKnots`, `OverloadKnots`, `LeewayStrength`, `GustAmplitude`, `BoatCodeContains` and warning behaviour.

## Status

The physics model (`src/SailingModel.cs`) is unit tested in `tests/Program.cs`. The game integration was written without a Vintage Story install and has **not** been run in game; boat motion is client-predicted, so verify the speed effect on a test server and adjust `SpeedSmoothing` or boat codes if needed.
