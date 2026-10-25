"""Opt-in HTTP smoke/load test on a copied disposable official-server world.
Requires NSS_WEB_TEST_BASELINE, VINTAGE_STORY and DOTNET_ROOT; never use live data.
Credentials are random, private to ignored artifacts, and absent from the report.
"""
from pathlib import Path
import concurrent.futures, http.cookiejar, json, os, re, secrets, shutil
import sqlite3, subprocess, threading, time, urllib.error, urllib.request
repo = Path(__file__).resolve().parents[4]
suite_version = next(m['version'] for m in json.loads((repo/'modules/manifest.json').read_text())['mods'] if m['id']=='suite')
build = re.search(r'BuildSuffix = "([^"]+)"', (repo/'modules/suite/src/SuiteRelease.cs').read_text()).group(1)
root = repo / 'artifacts/validation' / ('web-snapshot-' + str(int(time.time())))
data = root / 'data'
root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_WEB_TEST_BASELINE']), data,
                ignore=shutil.ignore_patterns('Logs', 'Mods', 'Cache', 'NewfiesBlockRepair', 'NewfiesOptimizer'))
game = Path(os.environ['VINTAGE_STORY']); net = str(Path(os.environ['DOTNET_ROOT']) / 'dotnet')
game_port = int(os.environ.get('NSS_WEB_GAME_PORT', '42599'))
web_port = int(os.environ.get('NSS_WEB_HTTP_PORT', '42669'))
config = json.loads((data / 'serverconfig.json').read_text())
save = Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1', Port=game_port, AdvertiseServer=False, Upnp=False,
              ModPaths=['Mods', str(data / 'Mods')])
config['WorldConfig']['SaveFileLocation'] = str(data / 'Saves' / save)
(data / 'serverconfig.json').write_text(json.dumps(config))
mods = data / 'Mods'; mods.mkdir()
shutil.copy2(repo / ('artifacts/NewfiesServerEssentialsSuiteV' + suite_version + '.zip'), mods)
optimizer_test = os.environ.get('NSS_WEB_TEST_OPTIMIZER') == '1'
if optimizer_test:
    optimizer_version = (repo / 'modules/optimizer/VERSION').read_text().strip()
    shutil.copy2(repo / 'modules/optimizer/artifacts' / ('NewfiesServerOptimizer-' + optimizer_version + '.zip'), mods)
    optimizer_config = json.loads((repo / 'modules/optimizer/config/optimizer.json').read_text())
    optimizer_config['Profiling'].update(PhaseTimings=True, AutoStart=False)
    optimizer_config['Compatibility']['SafeMode'] = False
    (data / 'ModConfig/optimizer.json').write_text(json.dumps(optimizer_config))
folder = data / 'ModConfig/NewfiesServerEssentialsSuite'
webfile = folder / 'NewfiesWebServer.json'
web = json.loads(webfile.read_text()); password = secrets.token_hex(24)
web.update(Enabled=True, BindAddress='127.0.0.1', Port=web_port, AdminUsername='fixtureowner',
           Password=password, PasswordSalt='', PasswordHash='', SafeDiagnosticsMode=False,
           SecureCookies=False)
webfile.write_text(json.dumps(web))
performance_test = os.environ.get('NSS_WEB_TEST_PERFORMANCE') == '1'
proximity_test = os.environ.get('NSS_WEB_TEST_PROXIMITY') == '1'
if proximity_test:
    (folder / 'NewfiesChatDisplay.json').write_text(json.dumps(dict(ProximityEnabled=True,ProximityGroupName='LocalLab',ProximityRange=100,ProximityAutoJoin=True)))
f = folder / 'NewfiesServerEssentialsFeatures.json'
features = json.loads(f.read_text()); features['WebDashboard'] = True;
if performance_test:
    features['WebMap'] = True
    (folder / 'NewfiesWebMap.json').write_text(json.dumps(dict(Enabled=False)))
translocator_test = os.environ.get('NSS_WEB_TEST_TRANSLOCATORS') == '1'
if translocator_test:
    features['WebMap'] = True
    (folder / 'NewfiesWebMap.json').write_text(json.dumps(dict(Enabled=True,BindAddress='127.0.0.1',Port=int(os.environ.get('NSS_WEB_MAP_PORT','42994')),ShowTranslocators=True,TranslocatorDiscoveryRadius=8)))
f.write_text(json.dumps(features))
base = 'http://127.0.0.1:' + str(web_port)
lines = []
process = subprocess.Popen([net, str(game / 'VintagestoryServer.dll'), '--dataPath', str(data),
                            '--ip', '127.0.0.1', '--port', str(game_port)], cwd=game,
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                           text=True, bufsize=1)
def log_reader():
    with (root / 'console.log').open('w') as log:
        for line in process.stdout:
            lines.append(line); log.write(line); log.flush()
threading.Thread(target=log_reader, daemon=True).start()
csrf_token = ''
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def request(path, payload=None, opener=client):
    body = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(base + path, data=body, headers={'Content-Type':'application/json', **({'X-CSRF-Token':csrf_token} if csrf_token else {})})
    started = time.perf_counter()
    try:
        response = opener.open(req, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    return response.code, json.loads(raw), len(raw), (time.perf_counter()-started)*1000
try:
    until = time.monotonic() + 180
    while not any('Dedicated Server now running on Port' in line for line in lines):
        if process.poll() is not None or time.monotonic() > until:
            raise RuntimeError('Disposable server did not start; inspect private console log')
        time.sleep(.2)
    assert request('/api/header')[0] == 401
    assert request('/api/login', {'username':'fixtureowner','password':password})[0] == 200
    if proximity_test or performance_test or optimizer_test or translocator_test:
        session_status, session_data, _, _ = request('/api/session')
        assert session_status == 200
        csrf_token = session_data['csrfToken']
    if proximity_test:
        status, chat, _, _ = request('/api/chat-display')
        assert status == 200 and chat['proximityEnabled'] and chat['proximityGroupId'] > 0 and chat['proximityStatus'].startswith('Active')
        initial_group = chat['proximityGroupId']
        chat.update(proximityRange=80, proximityGroupName='NearbyLab')
        status, saved, _, _ = request('/api/chat-display/settings', chat)
        assert status == 200 and saved['chatDisplay']['proximityGroupId'] == initial_group and saved['chatDisplay']['proximityRange'] == 80, (status, saved.get('message'))
        time.sleep(1.1)
        chat = saved['chatDisplay']; chat['proximityEnabled'] = False
        status, saved, _, _ = request('/api/chat-display/settings', chat)
        assert status == 200 and not saved['chatDisplay']['proximityEnabled']
        time.sleep(1.1)
        chat = saved['chatDisplay']; chat['proximityEnabled'] = True
        status, saved, _, _ = request('/api/chat-display/settings', chat)
        assert status == 200 and saved['chatDisplay']['proximityGroupId'] == initial_group
    status, header, header_bytes, _ = request('/api/header')
    assert status == 200 and 'playersOnline' in header and 'profiler' in header
    assert not any(k in header for k in ('payload', 'bytes', 'samples', 'lastProfile'))
    status, dashboard, dashboard_bytes, _ = request('/api/dashboard')
    assert status == 200 and 'blockLogger' in dashboard and 'playersOnline' in dashboard
    with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
        results = list(pool.map(lambda _: request('/api/header'), range(32)))
    assert all(status == 200 and 'playersOnline' in payload for status,payload,_,_ in results)
    status, server_status, status_bytes, _ = request('/api/server-status?days=30')
    assert status == 200 and isinstance(server_status, dict) and 'payload' not in server_status
    status, small, _, _ = request('/api/tick-debug/status')
    assert status == 200 and set(small) == {'enabled'}
    if performance_test:
        for window in (60, 300, 1800):
            for repeat in range(2):
                for name in ('overview', 'timeline'):
                    code, payload, _, _ = request('/api/performance/' + name + '?window=' + str(window))
                    assert code == 200 and isinstance(payload, dict), (name, code)
        for endpoint in ('/api/operations/memory', '/api/tick-debug', '/api/profiler?window=300&points=120', '/api/optimizations/newfies'):
            code, payload, _, _ = request(endpoint)
            assert code == 200 and isinstance(payload, dict), (endpoint, code)
        code, saved, _, _ = request('/api/webmap/export-schedule', dict(exportTerrainOnStart=False, automaticStartupExportEnabled=False, automaticStartupExportIntervalDays=7, useIncrementalTerrainExport=True, lockServerDuringTerrainExport=True, liveTerrainUpdatesEnabled=True, liveTerrainUpdateSeconds=90, liveTerrainMaxTilesPerPass=2))
        assert code == 200, code
        code, map_status, _, _ = request('/api/webmap')
        assert code == 200 and map_status['liveTerrainUpdatesEnabled'] and map_status['liveTerrainUpdateSeconds'] == 90 and map_status['liveTerrainMaxTilesPerPass'] == 2 and map_status['enabled'] == translocator_test
    if translocator_test:
        code, map_status, _, _ = request('/api/webmap')
        assert code == 200 and map_status['showTranslocators'] and map_status['translocatorDiscoveryRadius'] == 8, map_status
        port = int(os.environ.get('NSS_WEB_MAP_PORT','42994'))
        with urllib.request.urlopen('http://127.0.0.1:' + str(port) + '/api/map',timeout=10) as response:
            public_map=json.load(response)
        assert public_map['showTranslocators'] and public_map['translocators'] == [], public_map
        private_file = data / 'NewfiesServerEssentialsWebMap/translocators-v2-private-probe.json'
        private_file.write_text('{"privateTarget":"not-public"}')
        try:
            urllib.request.urlopen('http://127.0.0.1:' + str(port) + '/' + private_file.name,timeout=10)
            raise AssertionError('Private discovery file was served publicly')
        except urllib.error.HTTPError as error:
            assert error.code == 404, error.code
        time.sleep(1.1)
        payload=dict(enabled=True,bindAddress='127.0.0.1',port=port,showTranslocators=False,translocatorDiscoveryRadius=8,liveTerrainUpdatesEnabled=performance_test,liveTerrainUpdateSeconds=90,liveTerrainMaxTilesPerPass=2)
        code, result, _, _ = request('/api/webmap/config',payload)
        assert code == 200, (code,result)
        time.sleep(1.1)
        payload.update(showTranslocators=True,translocatorDiscoveryRadius=9)
        code, result, _, _ = request('/api/webmap/config',payload)
        assert code == 200, (code,result)
        code, map_status, _, _ = request('/api/webmap')
        assert map_status['showTranslocators'] and map_status['translocatorDiscoveryRadius'] == 9, map_status
    if optimizer_test:
        code, nos, _, _ = request('/api/optimizations/newfies')
        assert code == 200 and nos.get('available'), (code, nos)
        time.sleep(1.1)
        code, started, _, _ = request('/api/optimizations/newfies', {'operation':'profile-start'})
        assert code == 200 and started.get('success') and started['optimizer']['profiling'], (code, started)
        time.sleep(3)
        code, running, _, _ = request('/api/optimizations/newfies')
        assert code == 200, (code, running)
        (root / 'optimizer-running.json').write_text(json.dumps(running, indent=2))
        time.sleep(1.1)
        code, stopped, _, _ = request('/api/optimizations/newfies', {'operation':'profile-stop'})
        assert code == 200 and stopped.get('success') and not stopped['optimizer']['profiling'], (code, stopped)
    status, web_status, _, _ = request('/api/web/status')
    assert status == 200
    diagnostics = web_status.get('diagnostics', {})
    assert diagnostics.get('snapshotCacheHits', 0) >= 16
    report = dict(gameVersion='1.22.7', suiteVersion=suite_version, build=build,
                  checks=['unauthenticated header rejected','owner login','compact header JSON',
                          'dashboard JSON preserved','32 header requests / 16 workers',
                          'server status JSON preserved','tick status only flag','cache sharing'],
                  headerBytes=header_bytes, dashboardBytes=dashboard_bytes, serverStatusBytes=status_bytes,
                  headerRequestMaxMs=round(max(r[3] for r in results),2),
                  snapshotCacheHits=diagnostics.get('snapshotCacheHits'),
                  proximityChecks=proximity_test, performanceRefreshChecks=performance_test, optimizerChecks=optimizer_test, translocatorChecks=translocator_test,
                  note='Empty disposable server HTTP integration; no production TPS or player-scaling claim.')
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(report, indent=2), flush=True)
finally:
    if process.poll() is None:
        process.stdin.write('/stop\n'); process.stdin.flush()
        try: process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()

if process.returncode != 0:
    raise RuntimeError(f'Disposable server did not stop cleanly: {process.returncode}')
with sqlite3.connect('file:' + str(data / 'Saves' / save) + '?mode=ro', uri=True) as database:
    integrity = database.execute('PRAGMA integrity_check').fetchone()[0]
assert integrity == 'ok', integrity
report.update(exitCode=process.returncode, saveIntegrity=integrity)
(root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
print('Clean shutdown and save integrity: ok', flush=True)

if proximity_test:
    groups = json.loads((data / 'Playerdata/playergroups.json').read_text())
    values = groups.values() if isinstance(groups, dict) else groups
    owned = [g for g in values if g.get('OwnerUID') == 'newfiesserveressentialssuite:proximity']
    assert len(owned) == 1 and owned[0]['Name'] == 'NearbyLab' and not owned[0].get('ChatHistory')
    stored = json.loads((folder / 'NewfiesChatDisplay.json').read_text())
    assert stored['ProximityEnabled'] and stored['ProximityGroupId'] == initial_group and stored['ProximityRange'] == 80
    report.update(proximityGroupPersisted=True, proximityHistoryEntries=0)
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('Owned proximity group/settings persisted with zero history entries', flush=True)

if performance_test:
    stored = json.loads((folder / 'NewfiesWebMap.json').read_text())
    assert stored['LiveTerrainUpdatesEnabled'] and stored['LiveTerrainUpdateSeconds'] == 90 and stored['LiveTerrainMaxTilesPerPass'] == 2 and stored['Enabled'] == translocator_test
    report['liveTerrainSettingsPersisted'] = True
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('Performance reading endpoints and opt-in live terrain settings persistence: ok', flush=True)

if optimizer_test:
    capture = json.loads((data / 'NewfiesOptimizer/latest.json').read_text())
    module_states = {m['name']:m['status'] for m in capture['modules']}
    assert module_states.get('phaseTimings') == 'captured', module_states
    assert module_states.get('chunkLifecycleTimings') == 'captured', module_states
    targets = {h['name'] for h in capture['hotspots']}
    assert {'ServerSystemSupplyChunks.mainThreadLoadChunkColumn','ServerSystemUnloadChunks.UnloadChunkColumns','ServerSystemEntitySimulation.SendEntityDespawns'} <= targets, targets
    report.update(optimizerVersion=capture['optimizerVersion'], lifecycleTimingsCaptured=True)
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('NOS phase and three lifecycle targets captured; shutdown removed hooks: ok', flush=True)

if translocator_test:
    stored = json.loads((folder / 'NewfiesWebMap.json').read_text())
    assert stored['ShowTranslocators'] and stored['TranslocatorDiscoveryRadius'] == 9
    report.update(translocatorSettingsPersisted=True)
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('Translocator public feed, live toggle and world-scoped settings persistence: ok',flush=True)
