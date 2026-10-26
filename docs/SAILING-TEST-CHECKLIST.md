# Realistic Sailing – in-game test checklist

The sailing module was written without access to Vintage Story, so none of it has been run in game. Work through this on a test server (creative, then survival) and note what fails. Logs go to `Logs/server-main.txt`; search for `[NewfiesSailing]`.

## Load
- [ ] Server starts with the mod and no `Failed to load` messages for items, entities, recipes or sounds.
- [ ] Missing recipe ingredient codes (`glass-plain`, `feather`, `paper-parchment`, `flaxtwine`, `flaxfibers`, `plank-*`, `ingot-*`, `gear-rusty`) are logged; edit `assets/newfiessailing/recipes/grid` and `EntityDriftingWreck.Loot` as needed.
- [ ] `ModConfig/NewfiesSailing.json` and `NewfiesSailingClient.json` are written.
- [ ] Creative inventory shows all 11 items with icons that look sensible.

## Basic sailing
- [ ] Sailboats are detected: code contains `sail` (change `BoatCodeContains` if the vanilla boat code differs).
- [ ] Ship's compass shows wind, Beaufort force, point of sail and condition.
- [ ] Boat speed changes with heading to the wind. If the server speed scaling is ignored (client prediction), note it.
- [ ] Rigging line cycles full, reefed, furled; speed and heel respond.
- [ ] Heel direction: if the boat leans into the wind set `HeelDirection` to `-1` in the client config.
- [ ] Hull bob/roll visible; adjust `SeaRoughness`.

## Weather
- [ ] Squall warning, wind surge and end message appear.
- [ ] Fog bank: fog thickens near a boat, eases out, wind falls, wreck may spawn.
- [ ] Temporal storm (real, or admin `/sailingadmin`): violet sparks, wandering wind, boarders, compass jitter.

## Damage and recovery
- [ ] Overpowered full sail in a gale wears sails; repair kit restores 25% per use and loses durability.
- [ ] Leaky hull or rough sea floods the boat; bailing bucket drains it.
- [ ] Capsize happens only when badly overloaded; riders are thrown off; hull flips; boat cannot sail.
- [ ] Recovery balloon: three balloons appear, hull rights after 3 s, balloons float away. Check balloon positions are smooth on the client.
- [ ] Rogue wave lurch, spray and slam sound at Force 7+.

## Crew, sound and extras
- [ ] Concertina plays the shanty and raises morale (once a minute per boat); morale warnings at 30% and 10%.
- [ ] Fog horn plays and other sailors receive a bearing message.
- [ ] Wind gust and creak sounds play when aboard in rough weather.
- [ ] Drifting wreck can be right-clicked once for loot, then disappears.
- [ ] `HeaveEnabled` (experimental): turn on and check for jitter; leave off if it fights buoyancy.
- [ ] Logbook shows distance, squalls, storms, wind, repairs, capsizes, rescues, wrecks.

## Added in 0.10.0
- [ ] Spyglass: hold right-click zooms; release restores the field of view (check the log for `Spyglass zoom failed`).
- [ ] Tide: compass report shows tide state and current; boats with a rider drift with it, anchored boats do not. Tune `CurrentPush`.
- [ ] Bottles appear on water near sailors, can be opened once, and give a note plus often a chart.
- [ ] Chart: bearing/distance reads sensibly; standing within 6 blocks and right-clicking digs loot and consumes the chart.
- [ ] Nameplate names a nearby boat; the name appears in capsize/swamp/rogue wave messages and the compass report.
- [ ] Upgrades: each can be fitted once per boat; effects are noticeable (keel less leeway, cloth slower sail wear, reinforcement slower flooding).
