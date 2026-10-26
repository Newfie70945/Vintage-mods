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
# Constants that must agree across files. A mismatch here once made the live map refuse every changed-terrain update.
exporter = (root/'modules/suite/src/WebMap/WebMapTerrainExporter.cs').read_text(encoding='utf-8-sig')
bridge = (root/'modules/suite/src/WebMap/WebMapBridgeService.cs').read_text(encoding='utf-8-sig')
exporter_version = re.search(r'(?:private|internal) const int ManifestVersion = (\d+);', exporter)
assert exporter_version and 'private const int CurrentTerrainManifestVersion = WebMapTerrainExporter.ManifestVersion;' in bridge, \
    'Map service must reference the exporter cache version directly'
suite_manifest = next(m for m in manifest['mods'] if m['id'] == 'suite')
release = re.search(r'public const string Version = "([^"]+)";', (root/'modules/suite/src/SuiteRelease.cs').read_text(encoding='utf-8-sig'))
assert release and release[1] == suite_manifest['version'], 'SuiteRelease.Version differs from the manifest'
# Every WebMapConfig setting must be copied by Clone(), otherwise saving silently resets it.
start = bridge.index('public sealed class WebMapConfig'); clone_at = bridge.index('public WebMapConfig Clone()')
settings = re.findall(r'public [\w<>\[\], ]+? (\w+) \{ get; set; \}', bridge[start:clone_at])
clone_body = bridge[clone_at:bridge.index('};', clone_at)]
missing = [name for name in settings if not re.search(r'\b' + name + r'\s*=', clone_body)]
assert not missing, 'WebMapConfig.Clone() does not copy: ' + ', '.join(missing)
# State-changing dashboard routes must be in the route policy registry, which drives safe-mode blocking and cooldowns.
service = (root/'modules/suite/src/WebDashboard/WebDashboardService.cs').read_text(encoding='utf-8-sig')
registered = set(re.findall(r'new\(\) \{ Method = "(?:POST|PUT|DELETE)", Path = "([^"]+)"', service))
handled = set(re.findall(r'request\.Method == "(?:POST|PUT|DELETE)" && path == "([^"]+)"', service))
unregistered = sorted(handled - registered - {'/api/login', '/api/logout'})
assert not unregistered, 'Dashboard routes missing from the route policy registry: ' + ', '.join(unregistered)
print('Module versions, exclusions, editor fixtures, paired constants and config cloning are consistent')
