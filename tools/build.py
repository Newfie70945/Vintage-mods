#!/usr/bin/env python3
"""Build/package the Suite and MOTD using an owner-provided official game installation."""
from pathlib import Path
import json, os, subprocess, zipfile
root=Path(__file__).resolve().parents[1]
game=Path(os.environ.get('VINTAGE_STORY',''))
if not (game/'VintagestoryAPI.dll').is_file():
    raise SystemExit('Set VINTAGE_STORY to your official Vintage Story 1.22.7 installation.')
out=root/'artifacts';out.mkdir(exist_ok=True)
for folder,assembly in [('suite','NewfiesServerEssentialsSuite'),('motd','NewfiesCommunityMOTD')]:
    source=root/folder/'source';release=root/folder/'release'
    subprocess.run(['dotnet','build',str(source/(assembly+'.csproj')),'-c','Release','--nologo'],check=True)
    version=json.loads((release/'modinfo.json').read_text())['version']
    target=out/(assembly+'V'+version+'.zip')
    with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED) as archive:
        archive.write(source/'bin/Release/net10.0'/(assembly+'.dll'),assembly+'.dll')
        for path in sorted(release.rglob('*')):
            if not path.is_file() or path.suffix.lower() in ['.dll','.pdb','.zip']:continue
            archive.write(path,path.relative_to(release))
    print(target)
