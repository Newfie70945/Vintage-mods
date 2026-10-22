#!/usr/bin/env python3
"""Opt-in integration on a copied, stopped baseline. No production performance claim.
Requires VINTAGE_STORY, DOTNET_ROOT and NSS_WEB_TEST_BASELINE. Optional NSS_TEST_PACK
adds local mod ZIPs. Build Suite, NOS and EntitySuiteProbe before running.
"""
from pathlib import Path
import http.cookiejar, json, os, secrets, shutil, subprocess, threading, time
import urllib.error, urllib.request, zipfile
repo=Path(__file__).resolve().parents[3]
root=repo/'artifacts/validation'/('nos-entity-suite-'+str(int(time.time())))
data=root/'data';root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_WEB_TEST_BASELINE']),data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','Backups','NewfiesBlockRepair','NewfiesOptimizer'))
game=Path(os.environ['VINTAGE_STORY']);dotnet=str(Path(os.environ['DOTNET_ROOT'])/'dotnet')
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=42601,AdvertiseServer=False,Upnp=False,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save)
(data/'serverconfig.json').write_text(json.dumps(config))
mods=data/'Mods';mods.mkdir()
for source in [repo/'artifacts/NewfiesServerEssentialsSuiteV1.2.46.zip',repo/'modules/optimizer/artifacts/NewfiesServerOptimizer-0.7.0-preview.zip']:
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
def request(path,payload=None):
    body=json.dumps(payload).encode() if payload is not None else None
    headers={'Content-Type':'application/json'}
    if csrf:headers['X-CSRF-Token']=csrf
    req=urllib.request.Request('http://127.0.0.1:42670'+path,data=body,headers=headers)
    try:response=client.open(req,timeout=20)
    except urllib.error.HTTPError as error:response=error
    return response.code,json.loads(response.read())
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
    status,state=request('/api/optimizations/newfies');assert status==200 and state['available'];nos=state['optimizer'];assert nos['version']=='0.7.0'
    status,catalog=request('/api/server/mods');assert status==200
    loaded_mods=[{k:m.get(k) for k in ('modId','version','systems','side','core')} for m in catalog['mods']]
    settings=nos['settings'];assert settings['nearestEntitySearch'] and settings['interactableEntitySearch'] and settings['pathQueueLimitFix'] and not settings['pathMainThreadBudget']
    checks.append('new optimizer defaults visible through authenticated Suite bridge')
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
    (root/'result.json').write_text(json.dumps(dict(success=True,suiteVersion='1.2.46',optimizerVersion='0.7.0',checks=checks,modules=state['modules'],counters=state['counters'],loadedMods=loaded_mods,packZipCount=len(list(mods.glob('*.zip'))),scope='Copied official server, real terrain and actual A*; no clients or production TPS claims.'),indent=2)+'\n')
    print('PASS '+str(len(checks))+' official server/Suite/real-terrain integration checks; '+str(root/'result.json'),flush=True)
finally:
    if process.poll() is None:
        process.stdin.write('/stop\n');process.stdin.flush()
        try:process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:process.kill();process.wait()
