#!/usr/bin/env python3
"""Opt-in integration on a copied, stopped baseline. No production performance claim.
Requires VINTAGE_STORY, DOTNET_ROOT and NSS_WEB_TEST_BASELINE. Optional NSS_TEST_PACK
adds local mod ZIPs. Build Suite, NOS and EntitySuiteProbe before running.
"""
from pathlib import Path
import http.cookiejar, json, os, secrets, shutil, subprocess, threading, time
import urllib.error, urllib.request, zipfile
repo=Path(__file__).resolve().parents[3]
suite_version=json.loads((repo/'modules/suite/modinfo.json').read_text())['version']
optimizer_version=(repo/'modules/optimizer/VERSION').read_text().strip()
optimizer_semver=json.loads((repo/'modules/optimizer/src/NewfiesOptimizer/modinfo.json').read_text())['version']
root=repo/'artifacts/validation'/('nos-entity-suite-'+str(int(time.time())))
data=root/'data';root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_WEB_TEST_BASELINE']),data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','Backups','NewfiesBlockRepair','NewfiesOptimizer'))
game=Path(os.environ['VINTAGE_STORY']);dotnet=str(Path(os.environ['DOTNET_ROOT'])/'dotnet')
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=42601,AdvertiseServer=False,Upnp=False,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save)
(data/'serverconfig.json').write_text(json.dumps(config))
mods=data/'Mods';mods.mkdir()
for source in [repo/f'artifacts/NewfiesServerEssentialsSuiteV{suite_version}.zip',repo/f'modules/optimizer/artifacts/NewfiesServerOptimizer-{optimizer_version}.zip']:
    shutil.copy2(source,mods/source.name)
if os.environ.get('NSS_TEST_PACK'):
    for source in Path(os.environ['NSS_TEST_PACK']).glob('*.zip'):shutil.copy2(source,mods/source.name)
probe=repo/'modules/optimizer/tests/EntitySuiteProbe/bin/Release/net10.0/NosEntitySuiteProbe.dll'
with zipfile.ZipFile(mods/'NosEntitySuiteProbe.zip','w',zipfile.ZIP_DEFLATED) as z:
    z.write(probe,probe.name);z.writestr('modinfo.json',json.dumps(dict(type='code',modid='nosentitysuiteprobe',name='Disposable NOS entity integration',version='1.0.0',side='server',requiredOnClient=False)))
shutil.copy2(repo/'modules/optimizer/config/optimizer.json',data/'ModConfig/optimizer.json')
# Keep external Discord delivery disabled in this disposable fixture.
for configfile in (data/'ModConfig').rglob('*Discord*.json'):
    try:
        value=json.loads(configfile.read_text())
        if isinstance(value,dict):
            value.update(Enabled=False,BotToken='',Token='')
            if isinstance(value.get('Transport'),dict):value['Transport'].update(Enabled=False,BotToken='')
            configfile.write_text(json.dumps(value))
    except (ValueError,OSError):pass
folder=data/'ModConfig/NewfiesServerEssentialsSuite';file=folder/'NewfiesWebServer.json';web=json.loads(file.read_text());password=secrets.token_hex(24)
web.update(Enabled=True,BindAddress='127.0.0.1',Port=42670,AdminUsername='fixtureowner',Password=password,PasswordSalt='',PasswordHash='',SafeDiagnosticsMode=False,SecureCookies=False);file.write_text(json.dumps(web))
f=folder/'NewfiesServerEssentialsFeatures.json';features=json.loads(f.read_text());features['WebDashboard']=True;f.write_text(json.dumps(features))
process=subprocess.Popen([dotnet,str(game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port','42601'],cwd=game,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
lines=[]
def reader():
    with (root/'console.log').open('w') as log:
        for line in process.stdout:lines.append(line);log.write(line);log.flush()
threading.Thread(target=reader,daemon=True).start()
client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=None
checks=[]
request_measurements=[]
def request(path,payload=None):
    body=json.dumps(payload).encode() if payload is not None else None
    headers={'Content-Type':'application/json'}
    if csrf:headers['X-CSRF-Token']=csrf
    req=urllib.request.Request('http://127.0.0.1:42670'+path,data=body,headers=headers)
    request_started=time.perf_counter()
    try:response=client.open(req,timeout=20)
    except urllib.error.HTTPError as error:response=error
    raw=response.read()
    request_measurements.append(dict(path=path,status=response.code,elapsedMs=round((time.perf_counter()-request_started)*1000,3),responseBytes=len(raw)))
    return response.code,json.loads(raw)
def control(operation,settings=None):
    time.sleep(1.1) # Respect the existing administrative route cooldown.
    return request('/api/optimizations/newfies',dict(operation=operation,settings=settings or {}))
def wait(marker,start=0,timeout=240):
    end=time.monotonic()+timeout
    while True:
        for line in lines[start:]:
            if marker in line:return line
        if process.poll() is not None or time.monotonic()>end:raise RuntimeError('Missing marker '+marker+'; inspect private console.log')
        time.sleep(.1)
try:
    wait('Dedicated Server now running on Port')
    status,auth=request('/api/login',dict(username='fixtureowner',password=password));assert status==200;csrf=auth['csrfToken']
    if os.environ.get('NSS_TEST_TIMELINE'):
        status,enabled=request('/api/profiler/enabled',dict(enabled=True));assert status==200
        time.sleep(2.2)
        status,page=request('/api/performance/page-snapshot?window=1800');assert status==200
        assert page['conflicts'].get('requiresManualInspection') is True
        checks.append('opening performance page does not enumerate Harmony patches')
        status,timeline=request('/api/performance/timeline?window=61');assert status==200
        assert 'samplingGaps' in timeline and 'rawSampleCount' in timeline and 'worldSaves' in timeline
        assert all(len(row)==6 for row in timeline['samples'])
        checks.append('timeline exposes compact metric points, raw gaps and save observations')
        process.stdin.write('/autosavenow\n');process.stdin.flush()
        deadline=time.monotonic()+30
        while True:
            time.sleep(1.1)
            status,saved=request('/api/performance/timeline?window=60');assert status==200
            if saved['worldSaves']:break
            if time.monotonic()>deadline:raise AssertionError('World save observation did not reach timeline')
        checks.append('actual copied-world save emits a timeline marker')
        request('/api/performance/page-snapshot?window=1800');time.sleep(1.1)
        status,warm=request('/api/performance/page-snapshot?window=1800');assert status==200
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=4) as workers:
            responses=list(workers.map(lambda _:request('/api/performance/page-snapshot?window=1800'),range(12)))
        assert all(code==200 and payload['generatedUnixMilliseconds']==warm['generatedUnixMilliseconds'] for code,payload in responses)
        checks.append('twelve warm page requests share one snapshot generation')
    if os.environ.get('NSS_TEST_ANIMAL_RULES'):
        # Use an unregistered fixture namespace; never target live/modpack creatures.
        fixture_pattern='nosvalidation:nonexistent-animal-*'
        def animal_write(path,payload):
            time.sleep(1.1)
            return request(path,payload)
        def animal_rule():
            code,result=request('/api/animals/rules');assert code==200
            return next(r for r in result['rules'] if r['pattern']==fixture_pattern)
        for mode,threshold in [('Off',1),('ProtectFromGeneration',5)]:
            code,result=animal_write('/api/animals/rules/set',dict(pattern=fixture_pattern,limit=1000000,radiusChunks=0,generationMode=mode,minimumProtectedGeneration=threshold));assert code==200 and result['success']
            row=animal_rule();assert row['generationMode']==mode and row['minimumProtectedGeneration']==threshold
        persisted=json.loads((data/'ModConfig/NewfiesServerEssentialsSuite/NewfiesAnimalLimiter.json').read_text())
        assert persisted['RuleGenerationPolicies'][fixture_pattern]['MinimumProtectedGeneration']==5
        checks.append('per-rule Off/custom generation policies saved and visible in authenticated API and disk config')
        code,result=animal_write('/api/animals/rules/set',dict(pattern=fixture_pattern,limit=999999,radiusChunks=0));assert code==200
        assert animal_rule()['generationMode']=='ProtectFromGeneration'
        checks.append('older rule updates preserve saved generation overrides')
        code,result=animal_write('/api/animals/rules/set',dict(pattern=fixture_pattern,limit=1,radiusChunks=0,generationMode='ProtectFromGeneration',minimumProtectedGeneration=0));assert code==400
        assert animal_rule()['limit']==999999
        checks.append('invalid generation threshold rejected without changing rule')
        code,result=animal_write('/api/animals/rules/set',dict(pattern=fixture_pattern,limit=999999,radiusChunks=0,generationMode='Inherit'));assert code==200
        assert animal_rule()['generationMode']=='Inherit'
        code,result=animal_write('/api/animals/rules/remove',dict(pattern=fixture_pattern));assert code==200
        persisted=json.loads((data/'ModConfig/NewfiesServerEssentialsSuite/NewfiesAnimalLimiter.json').read_text());assert fixture_pattern not in persisted['Rules'] and fixture_pattern not in persisted['RuleGenerationPolicies']
        checks.append('inherit/reset and rule deletion remove stored generation overrides')
    status,state=request('/api/optimizations/newfies');assert status==200 and state['available'];nos=state['optimizer'];assert nos['version']==optimizer_semver
    status,catalog=request('/api/server/mods');assert status==200
    loaded_mods=[{k:m.get(k) for k in ('modId','version','systems','side','core')} for m in catalog['mods']]
    settings=nos['settings'];assert settings['nearestEntitySearch'] and settings['interactableEntitySearch'] and settings['pathQueueLimitFix'] and not settings['pathMainThreadBudget']
    checks.append('new optimizer defaults visible through authenticated Suite bridge')
    assert settings['physicsPacketListReuse'] and settings['despawnPacketProjection']
    assert next(m for m in nos['modules'] if m['name']=='despawnPacketProjection')['status']=='active'
    status,despawn_off=control('set',dict(despawnPacketProjection=False));assert status==200 and despawn_off['success']
    assert next(m for m in despawn_off['optimizer']['modules'] if m['name']=='despawnPacketProjection')['status']=='disabled'
    status,despawn_on=control('set',dict(despawnPacketProjection=True));assert status==200 and despawn_on['success']
    checks.append('despawn packet optimization independently toggled through Suite')
    assert next(m for m in nos['modules'] if m['name']=='physicsPacketListReuse')['status']=='active'
    status,physics_off=control('set',dict(physicsPacketListReuse=False));assert status==200 and physics_off['success']
    assert next(m for m in physics_off['optimizer']['modules'] if m['name']=='physicsPacketListReuse')['status']=='disabled'
    status,physics_on=control('set',dict(physicsPacketListReuse=True));assert status==200 and physics_on['success']
    checks.append('physics packet reuse independently toggled through Suite')
    status,off=control('set',dict(nearestEntitySearch=False));assert status==200 and off['success']
    assert next(m for m in off['optimizer']['modules'] if m['name']=='nearestEntitySearch')['status']=='disabled'
    assert off['optimizer']['settings']['interactableEntitySearch'];checks.append('nearest search switch independently applied')
    status,on=control('set',dict(nearestEntitySearch=True));assert status==200 and on['success']
    status,invalid=control('set',dict(pathMainThreadBudgetMs=0));assert status>=400 or not invalid['success'];checks.append('invalid path budget refused')
    status,started=control('profile-start');assert status==200 and started['success'] and started['optimizer']['profiling']
    status,refused=control('capture-clear');assert status>=400 or not refused['success'];checks.append('running capture cannot be discarded')
    status,stopped=control('profile-stop');assert status==200 and stopped['success']
    status,cleared=control('capture-clear');assert status==200 and cleared['success'] and not cleared['optimizer']['captureAvailable'];checks.append('stopped capture released through Suite')
    offset=len(lines);process.stdin.write('/nosentitycheck\n');process.stdin.flush()
    line=wait('NOS_ENTITY_INTEGRATION ',offset);terrain=json.loads(line.split('NOS_ENTITY_INTEGRATION ',1)[1]);assert terrain['success'],terrain
    checks.extend(terrain['checks'])
    state=request('/api/optimizations/newfies')[1]['optimizer']
    assert not state['settings']['pathMainThreadBudget'];checks.append('probe restores original optimizer settings')
    (root/'result.json').write_text(json.dumps(dict(success=True,suiteVersion=suite_version,optimizerVersion=optimizer_semver,checks=checks,modules=state['modules'],counters=state['counters'],loadedMods=loaded_mods,packZipCount=len(list(mods.glob('*.zip'))),requestMeasurements=request_measurements,scope='Copied official server, real terrain, optional animal/timeline API checks and actual A*; no clients or production TPS claims.'),indent=2)+'\n')
    print('PASS '+str(len(checks))+' official server/Suite/real-terrain integration checks; '+str(root/'result.json'),flush=True)
finally:
    if process.poll() is None:
        process.stdin.write('/stop\n');process.stdin.flush()
        try:process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:process.kill();process.wait()
