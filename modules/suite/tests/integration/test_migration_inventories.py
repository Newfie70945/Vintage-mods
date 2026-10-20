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
game_port=int(os.environ.get('NSS_TEST_PORT','42598'));web_port=int(os.environ.get('NSS_TEST_WEB_PORT','42668'))
root=repo/'artifacts/validation'/('inventory-migration-1.2.44-'+str(int(time.time()))+'-'+os.environ.get('NSS_INVENTORY_MODE','recover-empty')+'-'+os.environ.get('NSS_INVENTORY_FILLED','0'))
data=root/'data';root.mkdir(parents=True)
baseline=Path(os.environ['NSS_BATCH_TEST_BASELINE']).resolve()
shutil.copytree(baseline,data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','NewfiesBlockRepair','NewfiesOptimizer'))
net=str(Path(os.environ['DOTNET_ROOT'])/'dotnet');game=Path(os.environ['VINTAGE_STORY']);env=os.environ.copy()
helper=repo/'modules/suite/tests/BlockRepairChecks/bin/Release/net10.0/BlockRepairChecks.dll'
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=game_port,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,PassTimeWhenEmpty=True,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save);(data/'serverconfig.json').write_text(json.dumps(config))
mods=data/'Mods';mods.mkdir();shutil.copy2(repo/'artifacts/NewfiesServerEssentialsSuiteV1.2.44.zip',mods)
configdir=data/'ModConfig/NewfiesServerEssentialsSuite';webfile=configdir/'NewfiesWebServer.json';web=json.loads(webfile.read_text());password=secrets.token_hex(20);web.update(Enabled=True,BindAddress='127.0.0.1',Port=web_port,AdminUsername='fixtureowner',Password=password,PasswordSalt='',PasswordHash='',SafeDiagnosticsMode=False);webfile.write_text(json.dumps(web))
features=configdir/'NewfiesServerEssentialsFeatures.json';f=json.loads(features.read_text());f['WebDashboard']=True;features.write_text(json.dumps(f))
world=data/'Saves'/save
with sqlite3.connect(world) as db:key,blob=db.execute('SELECT position,data FROM chunk ORDER BY position DESC LIMIT 1').fetchone()
(root/'original.bin').write_bytes(blob)
mode=os.environ.get('NSS_INVENTORY_MODE','recover-empty');filled=os.environ.get('NSS_INVENTORY_FILLED','0')=='1'
subprocess.run([net,str(helper),'seed-reconnect' if mode=='reconnect' else 'seed-inventory-migration',str(root/'original.bin'),str(root/'seeded.bin'),str(key),'filled' if filled else 'empty'],env=env,check=True)
seed=(root/'seeded.bin').read_bytes()
with sqlite3.connect(world) as db:db.execute('UPDATE chunk SET data=? WHERE position=?',(seed,key))
unsigned=key&((1<<64)-1);location=dict(x=(unsigned&4194303)*32+17,y=((unsigned>>54)&511)*32,z=((unsigned>>27)&4194303)*32)
lines=[];process=None

def launch():
    global process
    process=subprocess.Popen([net,str(game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(game_port)],cwd=game,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
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

base='http://127.0.0.1:'+str(web_port);client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=None

def request(path,body=None):
    headers={'Origin':base,'Accept':'application/json'}
    if csrf:headers['X-CSRF-Token']=csrf
    if body is not None:headers['Content-Type']='application/json';time.sleep(1.15)
    req=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
    try:
        with client.open(req,timeout=20) as response:return response.status,json.load(response)
    except urllib.error.HTTPError as error:return error.code,json.load(error)

def state():
    global csrf
    status,result=request('/api/block-migration')
    if status==401:
        csrf=None;status,auth=request('/api/login',{'username':'fixtureowner','password':password});assert status==200,auth;csrf=auth['csrfToken'];status,result=request('/api/block-migration')
    assert status==200,result;return result

def wait_field(field):
    until=time.monotonic()+90
    while True:
        result=state();item=result.get(field) or {}
        if item.get('state')!='running':return item,result
        if time.monotonic()>until:raise RuntimeError('Timed out waiting for '+field)
        time.sleep(.2)

result={'suiteVersion':'1.2.44','buildSuffix':'ms11-migrationcontainers1','officialGameVersion':'1.22.7','inventoryMode':mode,'initiallyOccupied':filled,'authenticatedGameClients':0}

def wait_inventory():
    until=time.monotonic()+120
    while True:
        current=state();p=current.get('preview') or {};report=p.get('inventoryReport') or {}
        if report.get('state') not in ('running','queued'):return p,report
        if time.monotonic()>until:raise RuntimeError('Inventory preparation timed out: '+str(root))
        time.sleep(.2)

def command(cmd,marker,timeout=90):
    offset=len(lines);process.stdin.write(cmd+'\n');process.stdin.flush();until=time.monotonic()+timeout
    while not any(marker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Probe timed out: '+cmd+' '+str(root))
        time.sleep(.1)
    return json.loads(next(line.split(marker,1)[1] for line in lines[offset:] if marker in line))

try:
    import zipfile
    project=repo/'modules/suite/tests/InventoryRestoreProbe'
    if os.environ.get('NSS_PROBE_PREBUILT')!='1':subprocess.run([net,'build',str(project/'InventoryRestoreProbe.csproj'),'-c','Release','--nologo'],env=env,check=True)
    with zipfile.ZipFile(mods/'InventoryRestoreProbe.zip','w',zipfile.ZIP_DEFLATED) as z:
        z.write(project/'bin/Release/net10.0/InventoryRestoreProbe.dll','InventoryRestoreProbe.dll')
        z.writestr('modinfo.json',json.dumps(dict(type='code',modid='nseinventoryrestoreprobe',name='Disposable Inventory Migration Probe',version='1.0.0',side='server',requiredOnClient=False)))
    launch();ready(0);current=state();worldid=current['world']
    history=next(data.rglob('blocklogger.db'));ticks=int(time.time()*10000000)+621355968000000000-600000000
    if mode!='reconnect':
      with sqlite3.connect(history) as h:
        h.execute('INSERT INTO snapshots(timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,slot_count,non_empty_slot_count,total_item_count,slots_json) VALUES(?, ?, ?, ?, ?, ?, ?, ?, 14, 1, 1, ?)',(ticks,'2026-10-01T00:00:00Z',worldid,'0',location['x'],location['y'],location['z'],'game:bookshelf',json.dumps([dict(SlotId=0,ItemCode='game:book-normal-brickred',StackSize=1)])))
    status,_=request('/api/block-migration',dict(operation='scan',sourceId=999999));assert status==200
    scan,_=wait_field('scan');assert scan['state']=='complete' and scan['errors']==scan['unsupported']==0,scan
    options=dict(createBackup=True,manualBackupAcknowledged=False,allowAir=False,allowValidSource=False,inventoryMode=mode,inventoryBeforeUtc='',inventoryRiskAcknowledged=True,inventoryOverwriteAcknowledged=mode=='replace')
    mappings=[dict(sourceId=999999,destination='game:bookshelf')]
    status,error=request('/api/block-migration',dict(operation='batch-preview',mappings=mappings,**(options|dict(inventoryRiskAcknowledged=False))));assert status==400 and 'Acknowledge' in error['error'],error;result['riskAcknowledgementEnforced']=True
    if mode=='replace':
        status,error=request('/api/block-migration',dict(operation='batch-preview',mappings=mappings,**(options|dict(inventoryOverwriteAcknowledged=False))));assert status==400 and 'acknowledge' in error['error'],error;result['overwriteAcknowledgementEnforced']=True
    status,plan=request('/api/block-migration',dict(operation='batch-preview',mappings=mappings,**options));assert status==200,plan
    plan,report=wait_inventory()
    if mode=='reconnect':
        assert report['state']=='not-requested',report
        assert not list((data/'NewfiesBlockRepair').glob('migration-inventories-*.db'))
        result['reconnectNeedsNoHistoryOrWholeWorldPreparation']=True
    else:assert report['state']=='complete' and report['prepared']==1,report
    expected_recovery=0 if mode=='reconnect' else (1 if mode=='replace' or not filled else 0)
    if mode!='reconnect':assert report['recovered']==expected_recovery and report['preserved']==1-expected_recovery,report
    result['preparation']=report
    mismatch=options|dict(inventoryBeforeUtc='2026-10-01T00:00:00Z')
    status,error=request('/api/block-migration',dict(operation='loaded-stage',previewId=plan['id'],**mismatch));assert status==400 and 'options changed' in error['error'],error;result['changedInventoryOptionsRefused']=True
    direct=os.environ.get('NSS_INVENTORY_DIRECT','0')=='1'
    status,loaded=request('/api/block-migration',dict(operation='rule-set',sourceId=999999,destination='game:bookshelf',**options) if direct else dict(operation='loaded-stage',previewId=plan['id'],**options))
    assert status==200 and loaded['state'] in ('preparing-inventories','backing-up','activating','active'),loaded
    original_pid=process.pid;until=time.monotonic()+120
    while True:
        active=state()['loadedMigration']
        if active['active']:break
        if active['state']=='failed' or time.monotonic()>until:raise RuntimeError('Live inventory activation failed: '+str(active))
        time.sleep(.1)
    assert process.pid==original_pid;result['activatedWithoutRestart']=True;result['automaticPreparationOnDirectRule']=direct
    with sqlite3.connect(active['plan']['backup']) as db:assert db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0]==seed;assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    position=str(location['x'])+' '+str(location['y'])+' '+str(location['z'])
    observed=command('/nseinvprobe '+position,'NSE_INV_PROBE_RESULT ');assert observed['success'] and observed['block']=='game:bookshelf' and observed['slots']==14,observed
    if mode=='reconnect' and not filled:
        assert observed['items']==[],observed
        result['emptyInventoryStaysEmptyWithoutHistory']=True
    else:
        item=observed['items'][0];assert item['quantity']==(1 if expected_recovery else 7),observed
        if expected_recovery:assert item['code']=='game:book-normal-brickred' and item['text']=='',observed
        else:assert item['text']=='Keep this exact written text',observed
    result['officialEngineLoadedInventory']=observed;active=state()['loadedMigration'];assert active['sessionInventoriesRecovered']==expected_recovery and active['sessionInventoriesPreserved']==1-expected_recovery,active
    if mode=='reconnect':
        result['liveReconnection']=command('/nselivereconnectprobe '+position,'NSE_LIVE_RECONNECT_PROBE ')
    if expected_recovery:
        probe=data/'NSSInventoryProbe';probe.mkdir(exist_ok=True);(probe/'replay.bin').write_bytes(seed)
        replay=command('/nseinventoryhookprobe replay','NSE_INVENTORY_GUARD_PROBE ');assert replay['unchanged'] and replay['noLoadMarker'];result['durableReceiptBlocksReplay']=True
        subprocess.run([net,str(helper),'seed-reconnect' if mode=='reconnect' else 'seed-inventory-migration',str(root/'original.bin'),str(probe/'changed.bin'),str(key),'empty' if filled else 'filled'],env=env,check=True)
        changed=command('/nseinventoryhookprobe changed','NSE_INVENTORY_GUARD_PROBE ');assert changed['unchanged'] and changed['noLoadMarker'];result['changedRetainedInventoryRefused']=True
    status,_=request('/api/block-migration',dict(operation='loaded-disable'));assert status==200
    stop()
    with sqlite3.connect(world) as db:after=db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0];assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    (root/'repaired.bin').write_bytes(after);subprocess.run([net,str(helper),'verify-inventory-migration',str(root/'seeded.bin'),str(root/'repaired.bin'),str(plan['mappings'][0]['target'])],env=env,check=True)
    result['normalSaveHealthyCellsAndIntegrityVerified']=True;result['backupPreservedOriginal']=True
    (root/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2));print('Validation output: '+str(root))
finally:
    if process is not None and process.poll() is None:
        try:stop()
        except Exception:process.terminate();process.wait(timeout=30)
