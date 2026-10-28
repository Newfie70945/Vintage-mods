"""Opt-in HTTP smoke/load test on a copied disposable official-server world.
Requires NSS_WEB_TEST_BASELINE, VINTAGE_STORY and DOTNET_ROOT; never use live data.
Credentials are random, private to ignored artifacts, and absent from the report.
"""
from pathlib import Path
import concurrent.futures, hashlib, http.cookiejar, json, os, re, secrets, shutil, zipfile
import sqlite3, subprocess, threading, time, urllib.error, urllib.request
repo = Path(__file__).resolve().parents[4]
release_modules = json.loads((repo/'modules/manifest.json').read_text())['mods']
suite_version = next(m['version'] for m in release_modules if m['id']=='suite')
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
patch_compatibility_test = os.environ.get('NSS_WEB_TEST_PATCH_COMPATIBILITY') == '1'
compatibility_modules = ('idleFirepitFastPath','collisionTraversalReuse','blockUpdateListReuse','assetCompressionCache','repulseDelegateReuse')
if patch_compatibility_test:
    assert optimizer_test and os.environ.get('NSS_WEB_TEST_SERVER_PROTECTION') == '1'
    fixtures = Path(os.environ['NOS_COMPAT_FIXTURES'])
    for filename in ('genelib_3.2.1.zip','WalkableEntities_1.0.44.zip','AttributeRenderingLibrary-v' + os.environ.get('NSS_WEB_TEST_ARL_VERSION','3.2.0') + '.zip'):
        shutil.copy2(fixtures / filename, mods)
if optimizer_test:
    optimizer_version = (repo / 'modules/optimizer/VERSION').read_text().strip()
    shutil.copy2(repo / 'modules/optimizer/artifacts' / ('NewfiesServerOptimizer-' + optimizer_version + '.zip'), mods)
    optimizer_config = json.loads((repo / 'modules/optimizer/config/optimizer.json').read_text())
    optimizer_config['Profiling'].update(PhaseTimings=True, AutoStart=False)
    optimizer_config['Compatibility']['SafeMode'] = False
    if patch_compatibility_test:
        optimizer_config['EngineOptimizations'].update(IdleFirepitFastPath=True,CollisionTraversalReuse=True,BlockUpdateListReuse=True,AssetCompressionCache=True,RepulseDelegateReuse=True)
    (data / 'ModConfig/optimizer.json').write_text(json.dumps(optimizer_config))
all_modules_test = os.environ.get('NSS_WEB_TEST_ALL_MODULES') == '1'
if all_modules_test:
    for mod in json.loads((repo / 'modules/manifest.json').read_text())['mods']:
        if mod['id'] != 'suite':
            shutil.copy2(repo / 'artifacts' / (mod['assembly'] + 'V' + mod['version'] + '.zip'), mods)
folder = data / 'ModConfig/NewfiesServerEssentialsSuite'
if all_modules_test:
    (folder / 'NewfiesServerEssentialsDiscord.json').write_text(json.dumps(dict(Enabled=False,BotToken='')))
webfile = folder / 'NewfiesWebServer.json'
web = json.loads(webfile.read_text()); password = secrets.token_hex(24)
web.update(Enabled=True, BindAddress='127.0.0.1', Port=web_port, AdminUsername='fixtureowner',
           Password=password, PasswordSalt='', PasswordHash='', SafeDiagnosticsMode=False,
           SecureCookies=False)
webfile.write_text(json.dumps(web))
server_protection_test = os.environ.get('NSS_WEB_TEST_SERVER_PROTECTION') == '1'
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
if server_protection_test:
    features['AntiCheat'] = True
    ac_file=folder/'NewfiesAntiCheat.json'
    ac=json.loads(ac_file.read_text()) if ac_file.exists() else {}
    ac.update(Enabled=True,PacketGuardEnabled=True,PacketGuardIngressEnabled=True,ClientModDbVerificationEnabled=False)
    if patch_compatibility_test:
        ac['OreShieldEnabled'] = True
    ac_file.write_text(json.dumps(ac))
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
    if proximity_test or performance_test or optimizer_test or translocator_test or all_modules_test or server_protection_test:
        session_status, session_data, _, _ = request('/api/session')
        assert session_status == 200
        csrf_token = session_data['csrfToken']
    if server_protection_test:
        assert request('/api/anticheat/incident/export?player=missing',opener=urllib.request.build_opener())[0]==401
        code,ac,_,_=request('/api/anticheat')
        assert code==200 and ac['packetGuard']['ingressActive'],(code,ac.get('packetGuard'))
        pg=ac['packetGuard']
        assert all(k in pg for k in ('newActionShadowMode','combatChecksEnabled','inventoryChecksEnabled','deniedActionResyncEnabled','weaponMinimumIntervalMs'))
        pg.update(newActionShadowMode=True,combatChecksEnabled=True,inventoryChecksEnabled=True,weaponMinimumIntervalMs={'game:sword-test':300},miningReviewOnlyTools=['mod:specialpick'])
        code,saved,_,_=request('/api/anticheat/packetguard',pg)
        assert code==200 and saved['packetGuard']['weaponMinimumIntervalMs']=={'game:sword-test':300}
        for attempt in range(8):
            code,ac,_,_=request('/api/anticheat')
            if code==200 and ac['packetGuard']['weaponMinimumIntervalMs']=={'game:sword-test':300}:break
            time.sleep(.5)
        else:raise AssertionError('Applied protection settings never reached the cached snapshot')
        assert request('/api/anticheat/incident/export?player=missing')[0]==404
        saved_csrf=csrf_token;csrf_token=''
        assert request('/api/anticheat/exemption',{'player':'missing','scope':'Movement','seconds':60,'reason':'fixture'})[0]==403
        csrf_token=saved_csrf
        read_password=secrets.token_hex(24)
        assert request('/api/web/accounts/create',dict(username='fixtureviewer',password=read_password,role='ReadOnly'))[0]==200
        viewer=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        assert request('/api/login',dict(username='fixtureviewer',password=read_password),viewer)[0]==200
        _,viewer_session,_,_=request('/api/session',opener=viewer)
        csrf_token=viewer_session['csrfToken']
        assert request('/api/anticheat/exemption',{'player':'missing','scope':'Movement','seconds':60,'reason':'fixture'},viewer)[0]==403
        csrf_token=saved_csrf
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
        compiled_page=client.open(base+'/',timeout=20).read().decode('utf-8')
        assert 'function initTimelineInspector' in compiled_page and 'const panelEditors=new Map()' in compiled_page and '__NSS_WORKSPACE_POLISH__' not in compiled_page
        for window in (60, 300, 1800):
            for repeat in range(2):
                for name in ('overview', 'timeline'):
                    code, payload, _, _ = request('/api/performance/' + name + '?window=' + str(window))
                    assert code == 200 and isinstance(payload, dict) and 'capturedUnixMilliseconds' in payload and 'generatedUnixMilliseconds' in payload, (name, code)
        code, before_refresh, _, _ = request('/api/performance/page-snapshot?window=60')
        assert code == 200 and before_refresh['generatedUnixMilliseconds'] > 0
        time.sleep(5.2)  # Cross the page cache TTL; the FIRST next response must be current.
        code, after_refresh, _, _ = request('/api/performance/page-snapshot?window=60')
        assert code == 200 and after_refresh['generatedUnixMilliseconds'] > before_refresh['generatedUnixMilliseconds'], 'Expired Performance snapshot required a second refresh'
        for endpoint in ('/api/operations/memory', '/api/tick-debug', '/api/profiler?window=300&points=120', '/api/optimizations/newfies'):
            code, payload, _, _ = request(endpoint)
            assert code == 200 and isinstance(payload, dict), (endpoint, code)
        code, saved, _, _ = request('/api/webmap/export-schedule', dict(exportTerrainOnStart=False, automaticStartupExportEnabled=False, automaticStartupExportIntervalDays=7, useIncrementalTerrainExport=True, lockServerDuringTerrainExport=True, liveTerrainUpdatesEnabled=True, liveTerrainUpdateSeconds=90, liveTerrainMaxTilesPerPass=2))
        assert code == 200, code
        code, map_status, _, _ = request('/api/webmap')
        assert code == 200 and map_status['liveTerrainUpdatesEnabled'] and map_status['liveTerrainUpdateSeconds'] == 90 and map_status['liveTerrainMaxTilesPerPass'] == 2 and map_status['enabled'] == translocator_test
    if all_modules_test:
        code, guard, _, _ = request('/api/chatguard')
        chatguard_version = next(m['version'] for m in release_modules if m['id']=='chatguard')
        assert code == 200 and guard.get('available') and guard['version'] == chatguard_version, (code,guard)
        time.sleep(1.1)
        code, saved_guard, _, _ = request('/api/chatguard/settings',dict(Enabled=True,Mode='LogOnly'))
        assert code == 200 and saved_guard.get('success') and saved_guard['mode'] == 'LogOnly', (code,saved_guard)
        before_save=len(lines)
        process.stdin.write('/autosavenow\n');process.stdin.flush()
        until_save=time.monotonic()+30
        while not any('World saved!' in line for line in lines[before_save:]):
            if process.poll() is not None or time.monotonic()>until_save:raise AssertionError('All-module autosave did not finish')
            time.sleep(.1)
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
        if patch_compatibility_test:
            module_states = {m['name']:m for m in nos['optimizer']['modules']}
            assert all(module_states[name]['status']=='active' for name in compatibility_modules), {name:module_states[name] for name in compatibility_modules}
            assert any('Installed recipient chunk masking' in line for line in lines), 'OreShield hooks must actually be installed'
        settings = nos['optimizer']['settings']
        assert settings['droppedItemClumping'] is False, settings
        code, clumped, _, _ = request('/api/optimizations/newfies', {'operation':'set', 'settings':{'droppedItemClumping':True}})
        assert code == 200 and clumped.get('success') and clumped['optimizer']['settings']['droppedItemClumping'] is True, (code,clumped)
        assert any(m['name']=='droppedItemClumping' and m['status']=='active' for m in clumped['optimizer']['modules']), clumped
        time.sleep(1.1)
        code, unclumped, _, _ = request('/api/optimizations/newfies', {'operation':'set', 'settings':{'droppedItemClumping':False}})
        assert code == 200 and unclumped.get('success') and any(m['name']=='droppedItemClumping' and m['status']=='disabled' for m in unclumped['optimizer']['modules']), (code,unclumped)
        assert settings['barrelRecipeMemo'] is False and settings['barrelRecipeRescanEvery'] == 10, settings
        time.sleep(1.1)
        code, applied, _, _ = request('/api/optimizations/newfies', {'operation':'set', 'settings':{'barrelRecipeMemo':True,'barrelRecipeRescanEvery':5}})
        assert code == 200 and applied.get('success'), (code, applied)
        assert applied['optimizer']['settings']['barrelRecipeMemo'] is True and applied['optimizer']['settings']['barrelRecipeRescanEvery'] == 5, applied
        assert any(m['name'] == 'barrelRecipeMemo' and m['status'] == 'active' for m in applied['optimizer']['modules']), applied
        time.sleep(1.1)
        code, applied, _, _ = request('/api/optimizations/newfies', {'operation':'set', 'settings':{'barrelRecipeMemo':False,'barrelRecipeRescanEvery':10}})
        assert code == 200 and applied.get('success') and applied['optimizer']['settings']['barrelRecipeMemo'] is False, (code, applied)
        assert applied['optimizer']['settings']['blockUpdateDiagnostics'] is False, applied
        time.sleep(1.1)
        code, observed, _, _ = request('/api/optimizations/newfies', {'operation':'set','settings':{'blockUpdateDiagnostics':True,'blockUpdateDiagnosticsMs':50}})
        assert code == 200 and observed.get('success'), (code, observed)
        for name in ['blockUpdateDiagnostics','blockUpdateDiagnosticsSplit','blockUpdateDiagnosticsSync']:
            assert any(m['name'] == name and m['status'] == 'active' for m in observed['optimizer']['modules']), (name,observed)
        time.sleep(1.1)
        code, observed, _, _ = request('/api/optimizations/newfies')
        assert code == 200 and observed['optimizer']['counters']['blockUpdatePassesObserved'] > 0, observed
        time.sleep(1.1)
        code, observed, _, _ = request('/api/optimizations/newfies', {'operation':'set','settings':{'blockUpdateDiagnostics':False}})
        assert code == 200 and observed.get('success') and not observed['optimizer']['settings']['blockUpdateDiagnostics'], (code,observed)
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
                  serverProtectionChecks=server_protection_test, proximityChecks=proximity_test, performanceRefreshChecks=performance_test, optimizerChecks=optimizer_test, translocatorChecks=translocator_test, allModulesInstalled=all_modules_test,
                  note='Empty disposable server HTTP integration; no production TPS or player-scaling claim.')
    if patch_compatibility_test:
        report.update(patchCompatibilityModules=list(compatibility_modules),oreShieldHooksInstalled=True,
                      compatibilityMods={'genelib':'3.2.1','walkableentities':'1.0.44','attributerenderinglibrary':os.environ.get('NSS_WEB_TEST_ARL_VERSION','3.2.0')})
    with zipfile.ZipFile(mods/('NewfiesServerEssentialsSuiteV'+suite_version+'.zip')) as installed:
        report['suiteDllSha256']=hashlib.sha256(installed.read('NewfiesServerEssentialsSuite.dll')).hexdigest()
    if server_protection_test:
        report['serverActionStatuses']={key:ac['packetGuard'].get(key) for key in ('combatCheckStatus','impulseHookStatus','newActionShadowMode')}
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
    report['performanceFirstExpiredRefreshFresh'] = True
    report['workspaceBrowserScriptPresent'] = True
    report['performanceCaptureTimestampsPresent'] = True
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('Performance reading endpoints and opt-in live terrain settings persistence: ok', flush=True)

if optimizer_test:
    capture = json.loads((data / 'NewfiesOptimizer/latest.json').read_text())
    module_states = {m['name']:m['status'] for m in capture['modules']}
    assert module_states.get('phaseTimings') == 'captured', module_states
    assert module_states.get('chunkLifecycleTimings') == 'captured', module_states
    targets = {h['name'] for h in capture['hotspots']}
    assert {'ServerSystemSupplyChunks.mainThreadLoadChunkColumn','ServerSystemUnloadChunks.UnloadChunkColumns','ServerSystemEntitySimulation.SendEntityDespawns'} <= targets, targets
    report.update(optimizerVersion=capture['optimizerVersion'], lifecycleTimingsCaptured=True, barrelRecipeMemoRoundtrip=True, blockUpdateDiagnosticsRoundtrip=True)
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('NOS phase and three lifecycle targets captured; shutdown removed hooks: ok', flush=True)

if translocator_test:
    stored = json.loads((folder / 'NewfiesWebMap.json').read_text())
    assert stored['ShowTranslocators'] and stored['TranslocatorDiscoveryRadius'] == 9
    report.update(translocatorSettingsPersisted=True)
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print('Translocator public feed, live toggle and world-scoped settings persistence: ok',flush=True)

if all_modules_test:
    expected = [json.loads((repo / m['path'] / 'modinfo.json').read_text())['modid'] for m in json.loads((repo/'modules/manifest.json').read_text())['mods']]
    if optimizer_test:
        expected.append(json.loads((repo/'modules/optimizer/src/NewfiesOptimizer/modinfo.json').read_text())['modid'])
    if patch_compatibility_test:
        expected.extend(('genelib','walkableentities','attributerenderinglibrary'))
    native_list = next(line for line in lines if 'Mods, sorted by dependency:' in line)
    assert all(mod in native_list for mod in expected), native_list
    assert not any(re.search(r'\[(?:Server )?(?:Error|Fatal)\]',line) for line in lines), 'Server error present; inspect private console log'
    report.update(allModulesLoaded=expected,allModuleWorldSaveCompleted=True,serverErrors=0)
    (root/'result.json').write_text(json.dumps(report,indent=2)+'\n')
    print('All expected native mod IDs loaded; explicit save finished with no server errors',flush=True)
