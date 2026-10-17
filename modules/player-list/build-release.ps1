$ErrorActionPreference = "Stop"
python (Join-Path $PSScriptRoot "../../tools/build.py") --module player-list @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
