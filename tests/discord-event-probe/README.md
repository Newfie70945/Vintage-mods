# Offline official-server event probe

This probe is **test-only** and never included in mod packages. It requires the official Vintage Story 1.22.7 SDK and a stopped disposable world snapshot. No authenticated player or Discord credentials are needed.

Build the Discord module first with `python tools/build.py --module discord-link`, then build `DiscordEventProbe.csproj` with `VINTAGE_STORY` pointing to the SDK. Build Suite too for the integrated case.

Run `python tests/discord-event-probe/run.py --game /path/to/game --dotnet /path/to/dotnet --snapshot /path/to/stopped-test-data --output /new/validation/path`. Ports default to loopback 42612–42613 and can be changed with `--port`. The output must not already exist.

The runner copies the disposable world, removes its old mod/config directories, and loads only the probe, Discord module and (in the second case) Suite. Discord is disabled with an empty token. The probe refuses to proceed if any Discord worker is running. It temporarily enables only an in-memory queue sink, invokes the actual native announcement method with a fake player, verifies exact message routing and no duplicate death event, tests native-disabled fallback, changes storm-state flags within one server-thread command to test each phase, then restores state. No bot is started and no Discord message is sent. Storm simulation does not advance during that command.

Checks include stock-template upgrade, null-route normalization, independent routes, inherited channel, phase deduplication, no stale replay after enabling, and patch teardown/re-registration. This is an integration smoke test, not a test of actual Discord delivery or every other mod combination. Production Discord channel permissions and connectivity still need to be configured by the owner.
