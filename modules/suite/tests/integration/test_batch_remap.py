"""Opt-in official-server integration. Requires an existing disposable baseline world.
NSS_BATCH_TEST_BASELINE points to its data directory; VINTAGE_STORY and DOTNET_ROOT
point to official 1.22.7 and .NET 10. Never point the baseline at a running world.
The baseline is copied; all edits and credentials stay under ignored artifacts/.
Run tools/build.py --module suite and BlockRepairChecks before this script.
"""
from pathlib import Path
import http.cookiejar, json, os, secrets, shutil, sqlite3
import subprocess, threading, time, urllib.error, urllib.request
repo=Path(__file__).resolve().parents[4]
root=repo/'artifacts/validation'/('smart-remap-1.2.44-'+str(int(time.time())))
data=root/'data';root.mkdir(parents=True)
baseline=Path(os.environ['NSS_BATCH_TEST_BASELINE']).resolve()
shutil.copytree(baseline,data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','NewfiesBlockRepair','NewfiesOptimizer'))
net=str(Path(os.environ['DOTNET_ROOT'])/'dotnet');game=Path(os.environ['VINTAGE_STORY']);env=os.environ.copy()
helper=repo/'modules/suite/tests/BlockRepairChecks/bin/Release/net10.0/BlockRepairChecks.dll'
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=42598,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,PassTimeWhenEmpty=True,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save);(data/'serverconfig.json').write_text(json.dumps(config))
mods=data/'Mods';mods.mkdir();shutil.copy2(repo/'artifacts/NewfiesServerEssentialsSuiteV1.2.44.zip',mods)
configdir=data/'ModConfig/NewfiesServerEssentialsSuite';webfile=configdir/'NewfiesWebServer.json';web=json.loads(webfile.read_text());password=secrets.token_hex(20);web.update(Enabled=True,BindAddress='127.0.0.1',Port=42668,AdminUsername='fixtureowner',Password=password,PasswordSalt='',PasswordHash='',SafeDiagnosticsMode=False);webfile.write_text(json.dumps(web))
features=configdir/'NewfiesServerEssentialsFeatures.json';f=json.loads(features.read_text());f['WebDashboard']=True;features.write_text(json.dumps(f))
world=data/'Saves'/save
with sqlite3.connect(world) as db:key,blob=db.execute('SELECT position,data FROM chunk ORDER BY position DESC LIMIT 1').fetchone()
(root/'original.bin').write_bytes(blob)
subprocess.run([net,str(helper),'seed-two',str(root/'original.bin'),str(root/'seeded.bin')],env=env,check=True)
seed=(root/'seeded.bin').read_bytes()
with sqlite3.connect(world) as db:db.execute('UPDATE chunk SET data=? WHERE position=?',(seed,key))
unsigned=key&((1<<64)-1);location=dict(x=(unsigned&4194303)*32+17,y=((unsigned>>54)&511)*32,z=((unsigned>>27)&4194303)*32)
lines=[];process=None

def launch():
    global process
    process=subprocess.Popen([net,str(game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port','42598'],cwd=game,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
    def read(proc):
        with (root/'console.log').open('a') as log:
            for line in proc.stdout:lines.append(line);log.write(line);log.flush()
    threading.Thread(target=read,args=(process,),daemon=True).start()

def ready(offset):
    until=time.monotonic()+180
    while not any('Dedicated Server now running on Port' in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Test server failed to start; see '+str(root/'console.log'))
        time.sleep(.1)

def stop():
    process.stdin.write('/stop\n');process.stdin.flush();process.wait(timeout=60);assert process.returncode==0

base='http://127.0.0.1:42668';client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=None

def request(path,body=None):
    headers={'Origin':base,'Accept':'application/json'}
    if csrf:headers['X-CSRF-Token']=csrf
    if body is not None:headers['Content-Type']='application/json';time.sleep(1.15)
    req=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
    try:
        with client.open(req,timeout=20) as response:return response.status,json.load(response)
    except urllib.error.HTTPError as error:return error.code,json.load(error)

def state():
    status,result=request('/api/block-repair');assert status==200,result;return result

def wait_field(field):
    until=time.monotonic()+90
    while True:
        result=state();item=result.get(field) or {}
        if item.get('state')!='running':return item,result
        if time.monotonic()>until:raise RuntimeError('Timed out waiting for '+field)
        time.sleep(.2)

result={'suiteVersion':'1.2.44','buildSuffix':'ms11-smartremap1','gameVersion':'1.22.7','authenticatedGameClients':0}
try:
    launch();ready(0)
    status,auth=request('/api/login',{'username':'fixtureowner','password':password});assert status==200,auth;csrf=auth['csrfToken']
    status,_=request('/api/block-repair',dict(operation='scan'));assert status==200
    normal,_=wait_field('scan');assert normal['state']=='complete' and normal['errors']==0
    assert not any(f['id'] in (999999,999998) for f in normal['findings']);result['defaultExcludesUndefinedRawIds']=True
    status,_=request('/api/block-repair',dict(operation='scan',diagnostics=True));assert status==200
    scan,current=wait_field('scan');assert scan['state']=='complete' and scan['errors']==0
    sources={f['id']:f for f in scan['findings']};assert sources[999999]['count']==sources[999998]['count']==1
    result['scannedChunks']=scan['chunks'];assert sources[999999]['positions'][0]['x']==location['x']
    # Insert exact-coordinate placement evidence only into this disposable history.
    history=next(data.rglob('blocklogger.db'));ticks=int(time.time()*10000000)+621355968000000000-10000000
    with sqlite3.connect(history) as db:
        for dx,code in [(0,'game:rock-granite'),(1,'game:rock-basalt')]:
            db.execute('INSERT INTO events(timestamp_ticks,timestamp_utc,action,world_id,dimension,x,y,z,block_code) VALUES(?, ?, 2, ?, ?, ?, ?, ?, ?)',(ticks,'2026-10-18T00:00:00Z',current['world'],'0',location['x']+dx,location['y'],location['z'],code))
    status,_=request('/api/block-repair',dict(operation='mapping-advice',sources=[999999,999998]));assert status==200
    advice,_=wait_field('mappingAdvice');assert advice['state']=='complete',advice
    candidates={a['sourceId']:a for a in advice['findings']};assert candidates[999999]['candidates'][0]['code']=='game:rock-granite';assert candidates[999998]['candidates'][0]['code']=='game:rock-basalt';result['exactPositionRecommendations']=True
    mappings=[dict(sourceId=999999,destination=str(candidates[999999]['candidates'][0]['id'])),dict(sourceId=999998,destination='game:rock-basalt')]
    options=dict(allowAir=False,allowValidSource=False,createBackup=True,manualBackupAcknowledged=False)
    status,error=request('/api/block-repair',dict(operation='batch-preview',mappings=[mappings[0],mappings[0]],**options));assert status==400;result['duplicateSourceRejected']=True
    status,error=request('/api/block-repair',dict(operation='batch-preview',mappings=[dict(sourceId=999999,destination='game:water-still-7')],**options));assert status==400;result['fluidMismatchRejected']=True
    status,search=request('/api/block-repair',dict(operation='search',query='bookshelf'));assert status==200
    containers=[entry for entry in search['results'] if entry['entityClass']];assert containers,'Official fixture needs a bookshelf block entity'
    status,error=request('/api/block-repair',dict(operation='batch-preview',mappings=[dict(sourceId=999999,destination=str(containers[0]['id']))],**options));assert status==400;result['containerTargetRejected']=True
    status,plan=request('/api/block-repair',dict(operation='batch-preview',mappings=mappings,**options));assert status==200,plan;assert len(plan['mappings'])==2 and plan['count']==2
    status,error=request('/api/block-repair',dict(operation='batch-stage',previewId=plan['id'],mappings=[mappings[0],dict(sourceId=999998,destination='game:rock-granite')],**options));assert status==400 and 'changed' in error['error'];assert state()['pending'] is None;result['changedDestinationsRejected']=True
    status,error=request('/api/block-repair',dict(operation='stage',previewId=plan['id'],sourceId=plan['source'],destination=str(plan['target']),**options));assert status==400;result['singleStageCannotConfirmBatch']=True
    status,staged=request('/api/block-repair',dict(operation='batch-stage',previewId=plan['id'],mappings=mappings,**options));assert status==200 and staged['state']=='pending',staged
    stop();offset=len(lines);launch();ready(offset)
    report=json.loads((data/'NewfiesBlockRepair/last-repair.json').read_text());assert report['State']=='complete' and report['ChangedBlocks']==2 and report['ChangedChunks']==1,report
    with sqlite3.connect(world) as db:after=db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0];result['worldIntegrity']=db.execute('PRAGMA integrity_check').fetchone()[0]
    assert result['worldIntegrity']=='ok';(root/'repaired.bin').write_bytes(after)
    with sqlite3.connect(report['Backup']) as db:assert db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0]==seed;result['backupIntegrity']=db.execute('PRAGMA integrity_check').fetchone()[0]
    subprocess.run([net,str(helper),'verify-batch',str(root/'seeded.bin'),str(root/'repaired.bin'),str(plan['mappings'][0]['target']),str(plan['mappings'][1]['target'])],env=env,check=True)
    result.update(changedBlocks=report['ChangedBlocks'],changedChunks=report['ChangedChunks'],mappings=report['Mappings'],stagedPlanConsumed=not (data/'NewfiesBlockRepair/pending.json').exists(),all32768CellsAndOpaqueFieldsVerified=True,automaticBackupPreservedOriginalChunk=True)
    stop()
    # A changed source count must reject the entire plan before backup or edits.
    with sqlite3.connect(world) as db:db.execute('UPDATE chunk SET data=? WHERE position=?',(seed,key))
    failed=dict(staged);failed={k[0].upper()+k[1:]:v for k,v in failed.items()};failed['Id']=secrets.token_hex(16)
    failed['Mappings']=[{k[0].upper()+k[1:]:v for k,v in entry.items()} for entry in staged['mappings']];failed['Mappings'][0]['Count']+=1
    (data/'NewfiesBlockRepair/pending.json').write_text(json.dumps(failed))
    offset=len(lines);launch();ready(offset)
    refusal=json.loads((data/'NewfiesBlockRepair/last-repair.json').read_text());assert refusal['State']=='failed' and 'counts changed' in refusal['Message'],refusal
    with sqlite3.connect(world) as db:assert db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0]==seed
    result['changedCountsRejectWholePlanBeforeWrites']=True
    stop();(root/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2));print('Validation output: '+str(root))
finally:
    if process is not None and process.poll() is None:
        try:stop()
        except Exception:process.terminate();process.wait(timeout=30)
