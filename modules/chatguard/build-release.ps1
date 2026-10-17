$ErrorActionPreference = "Stop"
python (Join-Path $PSScriptRoot "../../tools/build.py") --module chatguard @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
