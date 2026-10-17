#!/usr/bin/env python3
"""Catch fixture drift, version inconsistencies and oversized ModDB descriptions."""
from pathlib import Path
import json,re
root=Path(__file__).resolve().parents[1]
cs=(root/'suite/source/BlockLogger.WebDashboard/WebDashboardMotdScript.cs').read_text()
for key in ['Declarations','Bindings']:
    match=re.search(r'public const string '+key+r' = ("(?:[^"\\]|\\.)*");',cs)
    assert match, key
    assert json.loads(match[1])==(root/'tests/web'/('motd-editor-'+key+'.js')).read_text(), 'Embedded editor fixture drift: '+key
for name in ['suite','motd']:
    version=json.loads((root/name/'release/modinfo.json').read_text())['version']
    info=(root/name/'source/Properties/AssemblyInfo.cs').read_text()
    assert 'AssemblyVersion("'+version+'.0")' in info
for path in (root/'descriptions').glob('*.html'):
    assert path.stat().st_size<60000,path
print('Source consistency checks passed')
