# Newfie's Realistic Sailing 0.2.0

Server-side mod for Vintage Story 1.22.7 that makes sailing depend on real weather.

- **Sail trim and reefing.** Ride a sailboat and use `/sailing trim full|reefed|furled`. Full sail is fastest in moderate wind but becomes overpowered above ~25 kn of wind load and slows hard; reefed sails (55% area) are slower in light air but stay efficient in a gale; furled sails stop the boat. Trim is stored on the boat. Overpowered boats get a chat warning.
- **Heeling (client).** Under load the hull leans away from the wind, most on a beam reach, nil head to wind or running. If boats lean the wrong way on your install set `HeelDirection` to `-1` in the client config.
- **Leeway.** Close-hauled boats slide sideways downwind; running boats do not.
- **Squalls.** Every 5-15 minutes a squall rolls through: sailors get a warning about 25 s before, then wind builds to about 2x for 30-75 s and eases off. Squall strength is synced to clients so waves and heel match. Configure or disable in `NewfiesSailing.json` (`SquallsEnabled`, intervals, `SquallPeakMultiplier`).
- **Wind-driven speed.** Sailboat speed scales with the wind at the boat's position. Becalmed boats crawl; a fresh breeze (Force 5) gives full drive.
- **Points of sail.** Speed depends on the angle between course and wind: in irons and close-hauled are slow, a beam reach is fastest, running is moderate.
- **Gusts.** Deterministic gust and lull cycles vary the wind around its mean.
- **Storm slowdown and warnings.** Above storm force boats slow sharply, and sailors get a chat warning when the Beaufort force jumps or reaches gale.
- **Storm visuals (client).** Hulls pitch and roll with the sea state and boats throw spray from Force 3 up, growing with the wind. Cosmetic only; tune or disable in `ModConfig/NewfiesSailingClient.json` (`BobIntensity`, `BobbingEnabled`, `SplashesEnabled`). Players without the mod simply do not see them.
- **`/sailing`.** Shows wind speed in knots, direction (compass), Beaufort force, wave height and, while under way, your current point of sail.

Only entities whose code contains `boat` and one of `BoatCodeContains` (default `sail`) are affected, so rafts are untouched. Mounted players must be within 6 blocks of the boat.

## Configuration

`ModConfig/NewfiesSailing.json` is written on first start. Tune `KnotsPerWindUnit`, `Min/MaxSpeedMultiplier`, `ReferenceKnots`, `OverloadKnots`, `LeewayStrength`, `GustAmplitude`, `BoatCodeContains` and warning behaviour.

## Status

The physics model (`src/SailingModel.cs`) is unit tested in `tests/Program.cs`. The game integration was written without a Vintage Story install and has **not** been run in game; boat motion is client-predicted, so verify the speed effect on a test server and adjust `SpeedSmoothing` or boat codes if needed.
