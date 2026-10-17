$ErrorActionPreference = "Stop"
python (Join-Path $PSScriptRoot "../../tools/build.py") --module discord-link @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
