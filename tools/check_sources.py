#!/usr/bin/env python3
"""Check version, fixture and listing consistency for the active author sources."""
from pathlib import Path
import json,re,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1];manifest=json.loads((root/'modules/manifest.json').read_text())
cs=(root/'modules/suite/src/WebDashboard/WebDashboardMotdScript.cs').read_text()
for key in ['Declarations','Bindings']:
 match=re.search(r'public const string '+key+r' = """\n(.*?)\n""";',cs,re.S)
 assert match and match[1]==(root/'tests/web'/('motd-editor-'+key+'.js')).read_text(),'Editor fixture drift: '+key
for item in manifest['mods']:
 path=root/item['path'];version=json.loads((path/'modinfo.json').read_text())['version']
 project=ET.parse(path/(item['assembly']+'.csproj')).getroot()
 assert version==item['version']==project.findtext('.//Version'),item['id']
optimizer = manifest['optimizer']
optimizer_root = root / optimizer['path']
assert (optimizer_root/'VERSION').read_text().strip() == optimizer['version']
version = optimizer['version'].removesuffix('-preview')
assert json.loads((optimizer_root/'src/NewfiesOptimizer/modinfo.json').read_text())['version'] == version
assert ET.parse(optimizer_root/'src/NewfiesOptimizer/NewfiesOptimizer.csproj').getroot().findtext('.//Version') == version
assert not (root/'modules/website-connector').exists(), 'Connector is maintained outside this repository'
assert 'website-connector' not in (root/'Newfies.ServerSuite.slnx').read_text()
print('Module versions, exclusions and editor fixtures are consistent')
