# Patch Generator

Open Patch Generator in the WebPanel. Use an Owner account to save or stage changes; other authenticated accounts can inspect definitions and loaded assets.

1. Choose Recipes, Items or Blocks and search a mod/file name such as `pickaxe`. Pick a loaded asset. If the file contains several recipes, select the definition you want to change.
2. Choose a recipe helper or Edit an existing property. The ingredient picker shows keys and codes. The property picker shows real paths and current values, including variant/material-specific values. Numbers, strings and booleans use normal controls; objects/arrays use JSON.
3. Generate & Preview. Check the asset, exact property path and current/new values. Editing the form requires a new preview.
4. Save Disabled Draft to keep a proposal, or Stage for Next Restart to include it in the managed content mod. Staging does not restart the server. Restart to apply enabled changes.
5. Use the Managed Content Patches table to edit, disable or delete definitions. Disable/delete also needs a restart to remove a loaded effect. Advanced JSON and runtime editors are expandable below.

For recipe balancing, start with output or ingredient quantity. When replacing a recipe code, preserve compatible item/block types and wildcard/variant mappings; the existing `type` property can be changed with the property editor when switching between an item and a block.

For item/block balancing, select an existing property such as durability, attack power, stack size, nutrition or resistance. A `ByType` mapping may control individual materials rather than a single global number. The generator exposes these actual paths and does not guess a mod's custom balance rules.

Server-only recipe patches normally use Server side. Item/block changes default to Server and clients: download the enabled patch pack and distribute it to clients, then restart their game. A custom server Mods directory may need the downloaded pack installed manually. Keep one copy of `newfiessuitecontentpatches` on the server, not the generated folder plus a duplicate downloaded ZIP.

Download Patch JSON exports the current preview. Download Enabled Patch Pack exports the managed content mod with enabled definitions. Asset/patch ordering and custom mod code can affect the final result: verify the game behavior and startup patch logs after restarting.
