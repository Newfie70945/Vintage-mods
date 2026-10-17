# Build and installation

Use the root README and `tools/build.py` for the supported repository layout. Build output and installable ZIPs go to `artifacts/`, which is ignored by Git.

The Suite references the official game's SQLite assemblies instead of shipping private copies. This avoids shared assembly identity conflicts. Discord Link restores its own Discord.Net dependencies; the packager includes managed dependencies absent from the game installation.

Back up your world and ModConfig. Install only the module ZIPs you need, remove older duplicate ZIPs, and keep server/client versions aligned where the module requires both sides. Never upload this entire source checkout to the server Mods directory.

Use a staging server and a real client before deploying changes to a heavily modded production server. Headless build and smoke checks cannot verify final GUI rendering or every third-party interaction.
