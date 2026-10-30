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
# Realistic Sailing: example configs match the C# options, translations are complete and every item has its assets.
sail=root/'modules/sailing';cfg=(sail/'src/SailingConfig.cs').read_text()
def sailing_props(cls):
 body=cfg[cfg.index('class '+cls):];nxt=body.find('\npublic sealed class',10);body=body[:nxt] if nxt>0 else body
 return set(re.findall(r'public \S+ (\w+) \{ get; set; \} =',body))
assert set(json.loads((sail/'NewfiesSailing.example.json').read_text()))==sailing_props('SailingConfig'),'sailing server example config drift'
assert set(json.loads((sail/'NewfiesSailingClient.example.json').read_text()))==sailing_props('SailingVisualsConfig'),'sailing client example config drift'
lang=sail/'assets/newfiessailing/lang';en=set(json.loads((lang/'en.json').read_text()))
for f in lang.glob('*.json'): assert set(json.loads(f.read_text()))==en,'sailing lang keys differ: '+f.name
for f in (sail/'assets/newfiessailing/itemtypes').glob('*.json'):
 item=json.loads(f.read_text());code=item['code']
 assert 'item-'+code in en and 'itemdesc-'+code in en,'sailing lang missing for '+code
 assert (sail/'assets/newfiessailing/shapes'/(item['shape']['base']+'.json')).is_file(),'sailing shape missing for '+code
 assert (sail/'assets/newfiessailing/recipes/grid'/(code+'.json')).is_file() or code in {'treasurechart'},'sailing recipe missing for '+code
assert (sail/'modicon.png').is_file()
print('Module versions, exclusions and editor fixtures are consistent')
