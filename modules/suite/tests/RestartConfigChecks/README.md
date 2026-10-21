# Restart warning persistence regression

Build Suite first using the official 1.22.7 SDK, then run:

`VINTAGE_STORY=/path/to/game dotnet run --project modules/suite/tests/RestartConfigChecks/RestartConfigChecks.csproj -c Release`

The executable uses the shipping Suite config type and SDK Newtonsoft.Json to check subset/default/empty/null/invalid warning lists, five save/load cycles and population of an existing config. Before the fix, the first test reproduces the reported reset to all eight defaults.

For the actual API load/store path, `server_reload.py --game /path/to/game --dotnet /path/to/dotnet --snapshot /stopped/disposable/world-data --output /new/test/output` copies the stopped test world and launches the official server twice, checking the stored warning list after each startup. It disables advertising and the restart schedule, binds loopback, and stops normally. No scheduled restart is triggered. Existing output paths are refused.
