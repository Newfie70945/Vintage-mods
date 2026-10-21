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
root=repo/'artifacts/validation'/('loaded-migration-1.2.44-'+str(int(time.time())))
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
subprocess.run([net,str(helper),'seed-live',str(root/'original.bin'),str(root/'seeded.bin')],env=env,check=True)
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

def wait_active():
    until=time.monotonic()+120
    while True:
        loaded=state()['loadedMigration']
        if loaded['active']:return loaded
        if loaded['state']=='failed' or time.monotonic()>until:raise RuntimeError('Live activation failed: '+str(loaded))
        time.sleep(.1)

result={'suiteVersion':'1.2.44','buildSuffix':'ms11-migrationbypass1','officialGameVersion':'1.22.7','authenticatedGameClients':0}
try:
    # Compile and package only the disposable test probe, never a production ZIP.
    import zipfile
    project=repo/'modules/suite/tests/LoadedMigrationProbe'
    if os.environ.get('NSS_PROBE_PREBUILT')!='1':subprocess.run([net,'build',str(project/'LoadedMigrationProbe.csproj'),'-c','Release','--nologo'],env=env,check=True)
    with zipfile.ZipFile(mods/'LoadedMigrationProbe.zip','w',zipfile.ZIP_DEFLATED) as z:
        z.write(project/'bin/Release/net10.0/LoadedMigrationProbe.dll','LoadedMigrationProbe.dll')
        z.writestr('modinfo.json',json.dumps(dict(type='code',modid='nseloadedmigrationprobe',name='Disposable Loaded Migration Probe',version='1.0.0',side='server',requiredOnClient=False)))
    launch();ready(0)
    status,auth=request('/api/login',{'username':'fixtureowner','password':password});assert status==200,auth;csrf=auth['csrfToken']
    status,recording=request('/api/blocklogger/recording');assert status==200 and recording['featureEnabled'] and recording['ready'],recording
    original_recording={k:recording['settings'][k] for k in ['logContainerSnapshots','snapshotOnSlotChange','snapshotDebounceMs']}
    status,recording=request('/api/blocklogger/recording',dict(logContainerSnapshots=True,snapshotOnSlotChange=True,snapshotDebounceMs=4000));assert status==200 and recording['snapshotRecovery'],recording
    assert recording['settings']['snapshotDebounceMs']==4000
    saved_logger=json.loads((configdir/'NewfiesBlocklogger.json').read_text());assert saved_logger['LogContainerSnapshots'] and saved_logger['SnapshotOnSlotChange']
    status,rejected=request('/api/blocklogger/recording',dict(logContainerSnapshots='yes'));assert status==400,rejected
    status,recording=request('/api/blocklogger/recording',original_recording);assert status==200,recording
    result['liveRecordingSettingsPersistAndRejectInvalidTypes']=True
    status,_=request('/api/health/safe-mode',dict(enabled=True));assert status==200
    status,safe_denial=request('/api/blocklogger/recording',dict(logContainerSnapshots=True));assert status==429 and safe_denial.get('safeDiagnosticsMode') is True and 'safe diagnostics' in safe_denial.get('error','').lower(),safe_denial
    status,_=request('/api/health/safe-mode',dict(enabled=False));assert status==200
    result['recordingWritesBlockedBySafeDiagnosticsMode']=True
    status,_=request('/api/block-migration',dict(operation='scan'));assert status==200
    scan,current=wait_field('scan');assert scan['state']=='complete' and scan['errors']==scan['unsupported']==0,scan
    ids={f['id']:f for f in scan['findings']};assert ids[999999]['count']==ids[999998]['count']==1
    options=dict(createBackup=True,manualBackupAcknowledged=False,allowAir=False,allowValidSource=False)
    mappings=[dict(sourceId=999999,destination='game:rock-granite'),dict(sourceId=999998,destination='game:rock-basalt')]
    status,plan=request('/api/block-migration',dict(operation='batch-preview',mappings=mappings,**options));assert status==200,plan
    status,error=request('/api/block-migration',dict(operation='loaded-stage',previewId=plan['id'],createBackup=False,manualBackupAcknowledged=True));assert status==400,error
    status,loaded=request('/api/block-migration',dict(operation='loaded-stage',previewId=plan['id'],**options));assert status==200 and loaded['state'] in ('backing-up','activating','active'),loaded
    assert state()['preview'] is None;result['reviewedMappingAndBackupOptionsBound']=True
    original_pid=process.pid;active=wait_active();assert process.pid==original_pid
    result['activatedWithoutRestart']=True;backup=active['plan']['backup'];assert Path(backup).is_file()
    with sqlite3.connect(backup) as db:assert db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0]==seed;assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    offset=len(lines);process.stdin.write('/nsemigrationprobe '+str(location['x'])+' '+str(location['y'])+' '+str(location['z'])+'\n');process.stdin.flush()
    until=time.monotonic()+90;marker='NSE_LOADED_MIGRATION_PROBE '
    while not any(marker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Normal chunk-load probe timed out: '+str(root))
        time.sleep(.1)
    observed=json.loads(next(line.split(marker,1)[1] for line in lines[offset:] if marker in line));assert observed['firstCode']=='game:rock-granite' and observed['secondCode']=='game:rock-basalt',observed
    active=state()['loadedMigration'];assert active['sessionBlocksChanged']==2 and active['sessionChunksChanged']==1 and active['sessionChunksRefused']==0,active
    result.update(normalLoaderObservedDestinations=observed,changedChunks=active['sessionChunksChanged'],changedReferences=active['sessionBlocksChanged'])
    # Test the actual looked-at/held adapter using a detached administrator,
    # not an authenticated network client. Two occurrences share the third ID.
    status,_=request('/api/block-migration',dict(operation='scan'));assert status==200
    assert state()['scan']['state']=='running';result['heldCommandIssuedDuringWholeWorldScan']=True
    held_backups={p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}
    offset=len(lines);process.stdin.write('/nseheldmigrationprobe '+str(location['x']+2)+' '+str(location['y'])+' '+str(location['z'])+'\n');process.stdin.flush()
    until=time.monotonic()+30;heldmarker='NSE_HELD_MIGRATION_PROBE '
    while not any(heldmarker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Held adapter probe timed out: '+str(root))
        time.sleep(.1)
    held=json.loads(next(line.split(heldmarker,1)[1] for line in lines[offset:] if heldmarker in line));assert held['heldQuantity']==7 and held['healthyRejected'],held
    assert all(held[k] for k in ['readOnlyPreview','singlePlainRepair','sameIdNeighbourUnchanged','metadataRefused','singleConfirmationRejected']),held
    result['heldPreviewAndSingleRepair']=dict((k,held[k]) for k in ['readOnlyPreview','singlePlainRepair','sameIdNeighbourUnchanged','metadataRefused','singleConfirmationRejected'])
    staged=wait_active();assert len(staged['plan']['mappings'])==3 and process.pid==original_pid,staged;result['heldRuleAutomaticallyActiveWithoutRestart']=True
    assert 'no world scan' in held['message'];result['heldRuleDoesNotWaitForRunningScan']=True
    assert {m['source'] for m in staged['plan']['mappings']}=={999999,999998,999997};result['heldRulePreservesOtherRulesAndHeldStack']=True;result['healthyLookedAtSourceRefused']=True
    assert held['unacknowledgedRejected'] and held['noAutomaticBackup'],held
    until=time.monotonic()+45
    while True:
        active=state()['loadedMigration']
        if active['loadedSweepBlocksChanged']>=2:break
        if time.monotonic()>until:raise RuntimeError('Already-loaded terrain was not migrated: '+str(active))
        time.sleep(.1)
    assert active['loadedSweepBlocksChanged']==2 and active['loadedSweepBlocksSkipped']==0,active
    assert {p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}==held_backups
    result['heldNoBackupRequiresAcknowledgementAndCreatesNoBackup']=True
    result['alreadyLoadedBothMatchingBlocksMigratedWithoutReload']=True
    # Save normally before explicitly reloading this disposable fixture column.
    process.stdin.write('/autosavenow\n');process.stdin.flush();until=time.monotonic()+90
    while True:
        with sqlite3.connect(world) as db:saved=db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0]
        if saved!=seed:break
        if time.monotonic()>until:raise RuntimeError('Normal autosave did not persist the fixture column')
        time.sleep(.2)
    unload_offset=len(lines)
    process.stdin.write('/nseunloadfixture '+str(location['x']//32)+' '+str(location['z']//32)+'\n');process.stdin.flush()
    until=time.monotonic()+30
    while not any('NSE_FIXTURE_UNLOADED' in line for line in lines[unload_offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Fixture column unload was not acknowledged')
        time.sleep(.1)
    result['fixtureReloadedAfterNormalAutosave']=True
    offset=len(lines);process.stdin.write('/nsemigrationprobe '+str(location['x']+2)+' '+str(location['y'])+' '+str(location['z'])+'\n');process.stdin.flush()
    until=time.monotonic()+90
    while not any(marker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Held-rule load probe timed out: '+str(root))
        time.sleep(.1)
    both=json.loads(next(line.split(marker,1)[1] for line in lines[offset:] if marker in line));assert both['firstCode']==both['secondCode']=='game:rock-granite',both
    active=state()['loadedMigration'];assert active['sessionBlocksChanged']+active['loadedSweepBlocksChanged']==4 and active['sessionChunksRefused']==0,active;result['heldRuleMigratesBothMatchingOccurrences']=True
    offset=len(lines);process.stdin.write('/nsechiselmigrationprobe '+str(location['x']+8)+' '+str(location['y'])+' '+str(location['z'])+'\n');process.stdin.flush()
    until=time.monotonic()+90;chiselmarker='NSE_CHISEL_MIGRATION_PROBE '
    while not any(chiselmarker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Chisel migration probe timed out: '+str(root))
        time.sleep(.1)
    result['chiseledMaterials']=json.loads(next(line.split(chiselmarker,1)[1] for line in lines[offset:] if chiselmarker in line))
    status,disabled=request('/api/block-migration',dict(operation='loaded-disable'));assert status==200 and not disabled['active'] and not disabled['enabled'],disabled
    result['disableImmediate']=True
    # Direct web-editor save: no scan or automatic backup, then pause/resume
    # using the stored backup policy. Use the same mappings already verified.
    backups_before_manual_rules={p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}
    for entry in mappings:
        status,direct=request('/api/block-migration',dict(operation='rule-set',sourceId=entry['sourceId'],destination=entry['destination'],createBackup=False,manualBackupAcknowledged=True,inventoryMode='preserve'))
        assert status==200,direct
        ready_rules=wait_active();assert ready_rules['plan']['createBackup'] is False and ready_rules['plan']['manualBackupAcknowledged'] is True
        assert ready_rules['plan']['backup']=='',ready_rules['plan']
        assert {p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}==backups_before_manual_rules
    before_resume={p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}
    status,paused=request('/api/block-migration',dict(operation='loaded-disable'));assert status==200 and not paused['active']
    status,resumed=request('/api/block-migration',dict(operation='loaded-resume'));assert status==200,resumed
    resumed=wait_active();assert resumed['plan']['createBackup'] is False and resumed['plan']['manualBackupAcknowledged'] is True
    assert {p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}==before_resume
    status,_=request('/api/block-migration',dict(operation='loaded-disable'));assert status==200
    result['directRuleSaveAndResumeRetainManualBackupPolicy']=True
    offset=len(lines);process.stdin.write('/nsemigrationcancelprobe\n');process.stdin.flush();until=time.monotonic()+20;cancelmarker='NSE_LIVE_CANCEL_PROBE '
    while not any(cancelmarker in line for line in lines[offset:]):
        if process.poll() is not None or time.monotonic()>until:raise RuntimeError('Queued activation cancellation probe timed out')
        time.sleep(.1)
    result['disabledQueuedActivationCannotReactivate']=True
    stop()
    with sqlite3.connect(world) as db:
        after=db.execute('SELECT data FROM chunk WHERE position=?',(key,)).fetchone()[0];assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    (root/'repaired.bin').write_bytes(after)
    # Official normal saving may refresh light/entity fields; verify every solid
    # cell, including all healthy neighbors, rather than assuming those bytes freeze.
    subprocess.run([net,str(helper),'verify-live',str(root/'seeded.bin'),str(root/'repaired.bin'),str(plan['mappings'][0]['target']),str(plan['mappings'][1]['target'])],env=env,check=True)
    result['normalSavePersistedAll32768CellsVerified']=True
    f['BlockLogger']=False;features.write_text(json.dumps(f))
    offset=len(lines);launch();ready(offset);off=state()['loadedMigration'];assert not off['active'] and not off['enabled'];result['disabledStateSurvivesRestart']=True
    status,off_recording=request('/api/blocklogger/recording');assert status==200 and off_recording['featureEnabled'] is False and off_recording['recording'] is False,off_recording
    status,_=request('/api/blocklogger/recording',dict(enabled=True));assert status!=200,'disabled module accepted a recording write'
    result['disabledModuleRecordingStatusRemainsReadable']=True
    # Hold the backup predecessor to reproduce an indefinitely queued backup.
    offset=len(lines);process.stdin.write('/nseholdbackupprobe\n');process.stdin.flush();until=time.monotonic()+15
    while not any('NSE_BACKUP_HELD' in line for line in lines[offset:]):
        assert time.monotonic()<until,'Hold probe timed out';time.sleep(.1)
    backups_before_bypass={p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}
    status,queued=request('/api/block-migration',dict(operation='rule-set',sourceId=999997,destination='game:rock-granite',inventoryMode='preserve',createBackup=True));assert status==200,queued
    queued=state()['loadedMigration'];assert queued['state']=='backing-up' and not queued['active'],queued
    expected_rules=[(m['source'],m['target']) for m in queued['plan']['mappings']]
    status,denied=request('/api/block-migration',dict(operation='loaded-enable-no-backup',manualBackupAcknowledged=False));assert status==400,denied
    assert state()['loadedMigration']['plan']['createBackup'] is True
    status,bypass=request('/api/block-migration',dict(operation='loaded-enable-no-backup',manualBackupAcknowledged=True));assert status==200,bypass
    assert bypass['active'] and bypass['enabled'] and bypass['plan']['createBackup'] is False and bypass['plan']['manualBackupAcknowledged'] is True,bypass
    assert [(m['source'],m['target']) for m in bypass['plan']['mappings']]==expected_rules
    offset=len(lines);process.stdin.write('/nsereleasebackupprobe\n');process.stdin.flush();until=time.monotonic()+15
    while not any('NSE_BACKUP_RELEASED' in line for line in lines[offset:]):
        assert time.monotonic()<until,'Release probe timed out';time.sleep(.1)
    time.sleep(.5);assert state()['loadedMigration']['active']
    assert {p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}==backups_before_bypass
    result['backupBypassRequiresAcknowledgementAndDoesNotWaitForOldWorker']=True;result['backupBypassPreservesEverySavedMapping']=True;result['staleBackupCompletionCannotRestoreBackupPolicy']=True
    stop();offset=len(lines);launch();ready(offset);persisted=state()['loadedMigration']
    assert persisted['active'] and persisted['enabled'] and persisted['plan']['createBackup'] is False and persisted['plan']['manualBackupAcknowledged'] is True,persisted
    assert [(m['source'],m['target']) for m in persisted['plan']['mappings']]==expected_rules
    assert {p.name for p in (data/'NewfiesBlockRepair').glob('world-before-loaded-*.vcdbs')}==backups_before_bypass
    result['enabledManualBackupRulesSurviveRestartWithoutAnotherBackup']=True
    stop();(root/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2));print('Validation output: '+str(root))
finally:
    if process is not None and process.poll() is None:
        try:stop()
        except Exception:process.terminate();process.wait(timeout=30)
