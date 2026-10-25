#!/usr/bin/env python3
"""Repeated full-suite/modpack worldgen, active NPC AI, save and profiler-cost laboratory test. No clients or player-capacity claims."""
from __future__ import annotations
import argparse, hashlib, json, os, shutil, sqlite3, subprocess, threading, time, zipfile
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(args, mode, trial):
    out=args.output/f'{trial:02d}-{mode}'; out.mkdir()
    data=out/'data'
    shutil.copytree(args.snapshot,data,ignore=shutil.ignore_patterns('Logs','Cache','Backups','BackupSaves','Mods'))
    config=json.loads((data/'serverconfig.json').read_text())
    save=Path(config['WorldConfig']['SaveFileLocation']).name
    config.update(Ip='127.0.0.1',Port=args.port,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,
                  PassTimeWhenEmpty=True,ModPaths=['Mods',str(data/'Mods')])
    config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save)
    (data/'serverconfig.json').write_text(json.dumps(config,indent=2))
    mods=data/'Mods';mods.mkdir()
    for source in (args.suitezip,*(() if mode=='absent' else (args.optimizerzip,)),args.fixture,*args.pack.glob('*.zip')):shutil.copy2(source,mods/source.name)
    cfg=data/'ModConfig';cfg.mkdir(exist_ok=True)
    (cfg/'NewfiesWebServer.json').write_text('{"Enabled":false,"BindAddress":"127.0.0.1","Port":9465}')
    suite_cfg=cfg/'NewfiesServerEssentialsSuite';suite_cfg.mkdir(exist_ok=True)
    (suite_cfg/'NewfiesWebServer.json').write_text('{"Enabled":false,"BindAddress":"127.0.0.1","Port":9465}')
    selected_config=args.reference_config if mode=='reference' else args.optimizer_config
    optimizer=json.loads((selected_config or (Path(__file__).resolve().parents[1]/'config/optimizer.json')).read_text())
    optimizer['Profiling'].update(AutoStart=False,MaxSamples=3600,MainThreadCpu=False,SystemTimings=False,PhaseTimings=False,CallbackTimings=False)
    if mode in ('stock','storage'):
        for key,value in optimizer['EngineOptimizations'].items():
            if type(value) is bool:optimizer['EngineOptimizations'][key]=False
    if mode=='storage':
        optimizer['EngineOptimizations'].update(WorldgenRequestReuse=True,SaveSnapshotRelease=True)
    (cfg/'optimizer.json').write_text(json.dumps(optimizer,indent=2))
    # Avoid an unscheduled autosave inside the measured workload. Explicit saves
    # are identical in both arms; all other template magic numbers stay unchanged.
    magic=json.loads((data/'servermagicnumbers.json').read_text());magic['ServerAutoSave']=3600
    physics=args.reference_physics_threads if mode=='reference' and args.reference_physics_threads is not None else args.physics_threads
    if physics is not None:magic['MaxPhysicsThreads']=physics
    (data/'servermagicnumbers.json').write_text(json.dumps(magic,indent=2))
    env=os.environ.copy();env['DOTNET_ROOT']=str(args.dotnet.parent);env['NOS_LAB_NPC_COUNT']=str(args.npc_count);env['NOS_LAB_BLOCK_COUNT']=str(args.block_count);env['NOS_LAB_SPECIES']=','.join(args.species_prefix);env['NOS_LAB_CHUNK_SIDE']=str(args.chunk_side)
    process=subprocess.Popen([str(args.dotnet),str(args.game_dir/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(args.port)],cwd=args.game_dir,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
    events=[];samples=[];done=threading.Event()
    def reader():
        with (out/'console.log').open('w') as log:
            for line in process.stdout:
                stamp=time.monotonic();events.append((stamp,line.rstrip()));log.write(line);log.flush()
    def sampler():
        while not done.wait(.2):
            try:
                raw=Path(f'/proc/{process.pid}/stat').read_text();f=raw[raw.rfind(')')+2:].split()
                samples.append(dict(time=time.monotonic(),cpuSeconds=(int(f[11])+int(f[12]))/os.sysconf('SC_CLK_TCK'),rssBytes=int(f[21])*os.sysconf('SC_PAGE_SIZE')))
            except (OSError,ValueError,IndexError):pass
    threading.Thread(target=reader,daemon=True).start();threading.Thread(target=sampler,daemon=True).start()
    def wait(pattern,start=0,timeout=180):
        end=time.monotonic()+timeout
        while True:
            for item in events[start:]:
                if 'NOSLOAD_FAIL' in item[1] or 'NOSBENCH_FAIL' in item[1]:raise RuntimeError(item[1])
                if pattern in item[1]:return item
            if process.poll() is not None or time.monotonic()>end:raise RuntimeError('Missing marker: '+pattern)
            time.sleep(.02)
    def send(command):
        start=len(events);process.stdin.write(command+'\n');process.stdin.flush()
        wait('Handling Console Command '+command,start,30);return start
    def payload(tag,start):
        line=wait('NOSBENCH_'+tag+' ',start)[1]
        return json.loads(line.split('NOSBENCH_'+tag+' ',1)[1])
    def payload_load(tag,start):
        line=wait('NOSLOAD_'+tag+' ',start)[1];return json.loads(line.split('NOSLOAD_'+tag+' ',1)[1])
    def state():return payload('STATE',send('/nosbenchstate'))
    def collect():return payload('LAB_GC',send('/nosbenchgc'))
    def save_once():
        index=send('/autosavenow');t0=wait('Handling Console Command /autosavenow',index)[0]
        prep=wait('Autosave completed',index)[0];last=wait('Offthread save of savegame done.',index)[0]
        time.sleep(.25)
        return dict(totalWallMs=(max(prep,last)-t0)*1000,mainPreparationWallMs=(prep-t0)*1000,state=state())
    installed=mode!='absent'
    capturing=installed and not args.no_nos_capture
    result=dict(mode=mode,trial=trial,optimizerInstalled=installed,nosCaptureEnabled=capturing,authenticationVerification=True,authenticatedPlayers=0,columns=args.chunk_side**2,saveCycles=5,magicNumbers=magic)
    try:
        wait('Dedicated Server now running on Port',timeout=300)
        result['loadedMods']=payload_load('MODS',send('/nosloadmods'))
        expected=json.loads((args.pack/'manifest.json').read_text())
        enabled={m['id']:m['version'] for m in result['loadedMods'] if m['enabled']}
        for mod in expected:
            if mod['side'].lower()!='client' and enabled.get(mod['id'])!=mod['version']:
                raise RuntimeError('Pack member absent or disabled: '+str(mod))
        if args.keep_workload_world:result['spawnPreparationCheck']=payload_load('SPAWN_CHECK',send('/nosloadspawncheck'))
        with zipfile.ZipFile(args.optimizerzip) as archive:optimizer_id=json.loads(archive.read('modinfo.json'))['modid']
        if (optimizer_id in enabled)!=installed:raise RuntimeError('Optimizer presence does not match requested mode.')
        payload_load('HEALTH',send('/nosloadhealth'))
        result['profileCancellation']=payload_load('CANCEL_PASS',send('/nosloadcanceltest'))
        time.sleep(args.warmup)
        payload('WARM',send('/nosbenchwarm'));time.sleep(3);result['baselineGc']=collect()
        if capturing:wait('Capture started.',send('/optimizer profile start'))
        result['beforeGeneration']=state();start=time.monotonic()
        result['generation']=payload('GENERATED',send('/nosbenchgenerate'))
        if result['generation']['columns']!=args.chunk_side**2:raise RuntimeError('Fixture generated a different world size; rebuild the lab fixture.')
        result['afterGeneration']=state();result['generationObserverWallSeconds']=time.monotonic()-start
        result['blockWorkload']=payload_load('BLOCKS',send('/nosloadblocks')) if args.block_workload else None
        if args.block_workload and any(result['blockWorkload'][key]!=args.block_count for key in ('barrels','firepits')):raise RuntimeError('Fixture did not place the requested block population.')
        result['animals']=payload_load('ENTITIES',send('/nosloadentities'))
        if result['animals']['spawned']!=args.npc_count:raise RuntimeError('Fixture silently changed requested entity count.')
        time.sleep(10)
        if args.no_nos_capture:
            result['optimizerBefore']=payload_load('OPTIMIZER_STATE',send('/nosloadoptimizer'))
            if result['optimizerBefore']['profileRunning']:raise RuntimeError('Unexpected NOS capture in capture-disabled comparison.')
        result['entityBefore']=payload_load('STATE',send('/nosloadstate'))
        if result['entityBefore']['engineProfilerEnabled'] or result['entityBefore']['slowTickProfilingEnabled']:raise RuntimeError('Unexpected full engine profiling before steady workload.')
        payload_load('RESET',send('/nosloadreset'));entityStart=time.monotonic();time.sleep(args.entity_seconds)
        result['entityAfter']=payload_load('STATE',send('/nosloadstate'));result['entityWallSeconds']=time.monotonic()-entityStart;result['entityWindow']={'start':entityStart,'end':time.monotonic()}
        if args.block_workload and (result['entityAfter']['barrelTicks']<=result['entityBefore']['barrelTicks'] or result['entityAfter']['firepitTicks']<=result['entityBefore']['firepitTicks']):raise RuntimeError('Block callbacks did not advance.')
        if result['entityAfter']['active']<int(args.npc_count*.8) or result['entityAfter']['moves']<=result['entityBefore']['moves'] or result['entityAfter']['filterCalls']<=result['entityBefore']['filterCalls'] or result['entityAfter']['taskStarts']<=result['entityBefore']['taskStarts']:
            raise RuntimeError('Active/moving NPC workload not established.')
        result['profilerPairs']=[]
        for burst in range(args.profile_bursts):
            pair={}
            # Alternate order to expose population/time drift, rather than always profiling last.
            for window_mode in (('off','on') if burst%2==0 else ('on','off')):
                before=payload_load('STATE',send('/nosloadstate'))
                payload_load('RESET',send('/nosloadreset'))
                stamp=time.monotonic()
                if window_mode=='on':
                    profile=payload_load('PROFILE',send('/nosloadprofile'))
                    if not profile['success']:raise RuntimeError('Engine capture could not start: '+str(profile))
                time.sleep(4)
                after=payload_load('STATE',send('/nosloadstate'))
                pair[window_mode]=dict(before=before,after=after,wallSeconds=time.monotonic()-stamp)
            result['profilerPairs'].append(pair)
        result['engineProfile']=payload_load('PROFILE_DATA',send('/nosloadprofiledata'))
        # Give the worker a fixed settling interval, not a different delay per arm.
        time.sleep(3)
        result['saves']=[]
        for cycle in range(5):
            dirty=payload('DIRTY',send('/nosbenchdirty'))
            expected_chunks=args.chunk_side**2*result['generation'].get('verticalChunks',8)
            if dirty['chunks']!=expected_chunks:raise RuntimeError('Expected '+str(expected_chunks)+' loaded benchmark chunks: '+str(dirty))
            result['saves'].append(save_once());time.sleep(1)
        time.sleep(5);result['afterSaves']=state()
        if args.keep_workload_world:
            result['workloadSpawn']=payload_load('SPAWN',send('/nosloadsetspawn'));result['workloadSave']=save_once()
            workload=out/'workload-world.vcdbs'
            with sqlite3.connect('file:'+str(data/'Saves'/save)+'?mode=ro',uri=True) as source,sqlite3.connect(workload) as target:source.backup(target)
            result['workloadWorld']=dict(path=workload.name,sha256=sha(workload))
        if capturing:wait('Capture stopped.',send('/optimizer profile stop'))
        if not capturing:result['optimizerState']=payload_load('OPTIMIZER_STATE',send('/nosloadoptimizer'))
        if args.profile_unload:
            result['unloadProfileStart']=payload_load('PROFILE',send('/nosloadprofile'))
        unloadStart=time.monotonic();result['unloaded']=payload('UNLOADED',send('/nosbenchunload'));result['unloadObserverWallMs']=(time.monotonic()-unloadStart)*1000;time.sleep(3)
        if args.profile_unload:result['unloadProfile']=payload_load('PROFILE_DATA',send('/nosloadprofiledata'))
        if not capturing:result['optimizerAfterUnload']=payload_load('OPTIMIZER_STATE',send('/nosloadoptimizer'))
        result['afterUnloadGc']=collect()
        # Counterfactual within each process: a new snapshot should release old
        # stock snapshot references without any optimizer help.
        result['refreshSave']=save_once();time.sleep(3);result['afterRefreshGc']=collect()
        send('/stop');process.wait(timeout=60);done.set();time.sleep(.25)
        result['exitCode']=process.returncode
        errors=[line for _,line in events if '[Server Error]' in line or '[Error]' in line or 'NOSBENCH_FAIL' in line or 'Exception' in line]
        result['errors']=errors
        with sqlite3.connect('file:'+str(data/'Saves'/save)+'?mode=ro',uri=True) as db:result['saveIntegrity']=db.execute('PRAGMA integrity_check').fetchone()[0]
        result['profile']=json.loads((data/'NewfiesOptimizer/latest.json').read_text()) if capturing else None
        result['passed']=not errors and process.returncode==0 and result['saveIntegrity']=='ok' and result['entityAfter']['tickCount']>0
        if capturing and result['profile']['tick']['count']==0:raise RuntimeError('No NOS tick capture recorded.')
        wanted={'worldgenRequestReuse','saveSnapshotRelease'}
        module_rows=result['profile']['modules'] if capturing else result['optimizerState']['modules']
        module_rows=[{k.lower():v for k,v in m.items()} for m in module_rows]
        states={m['name']:m['status'] for m in module_rows if m['name'] in wanted}
        if installed and states!={name:'disabled' if mode=='stock' else 'active' for name in wanted}:raise RuntimeError('Unexpected module activation: '+str(states))
    except Exception as error:
        result.update(passed=False,failure=str(error))
        if process.poll() is None:process.terminate()
        try:process.wait(timeout=15)
        except subprocess.TimeoutExpired:process.kill();process.wait()
    finally:
        done.set();(out/'process-samples.json').write_text(json.dumps(samples,indent=2)+'\n')
        (out/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:result.get(k) for k in ('trial','mode','passed','generation','failure')}),flush=True)
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('game-dir','snapshot','output','dotnet','suitezip','optimizerzip','fixture','pack'):p.add_argument('--'+key,type=Path,required=True)
    p.add_argument('--optimizer-config',type=Path,help='Explicit optimizer configuration for the NOS arm; copied and hashed in the report.')
    p.add_argument('--reference-config',type=Path,help='Configuration for reference NOS arms, without disabling engine switches.')
    p.add_argument('--block-count',type=int,default=2000,help='Each of barrels and firepits, 1-4000 when block workload is enabled.')
    p.add_argument('--species-prefix',action='append',help='Adult entity code prefix; repeat for equal population groups.')
    p.add_argument('--keep-workload-world',action='store_true',help='Keep a consistent SQLite backup of the populated workload before forced unload.')
    p.add_argument('--no-nos-capture',action='store_true',help='Keep NOS telemetry stopped; compare actual installed optimization overhead.')
    p.add_argument('--resume',action='store_true',help='Resume complete recorded runs only when input hashes and workload parameters match.')
    p.add_argument('--profile-unload',action='store_true',help='Profile cleanup outside measured steady windows; adds diagnostic overhead to unload timings.')
    p.add_argument('--physics-threads',type=int,choices=range(1,9),help='Explicit lab native physics workers; leaves the template unchanged if omitted.')
    p.add_argument('--reference-physics-threads',type=int,choices=range(1,9),help='Explicit reference-arm workers for a labelled native settings comparison.')
    p.add_argument('--chunk-side',type=int,default=12,help='Generated chunk columns per side, 8-16 (standard 12).')
    p.add_argument('--no-block-workload',dest='block_workload',action='store_false',help='Explicitly omit barrels/firepits for a narrower experiment.')
    p.add_argument('--block-workload',action='store_true',default=True,help='Place the selected barrel and mixed burning/cold firepit workload (default 2000 each).')
    p.add_argument('--npc-count',type=int,default=6000,help='Fixture NPCs, 128-8192; no authenticated players.')
    p.add_argument('--entity-seconds',type=int,default=180);p.add_argument('--profile-bursts',type=int,default=5)
    p.add_argument('--warmup',type=int,default=15);p.add_argument('--port',type=int,default=42579)
    p.add_argument('--order',default='stock,nos,nos,stock,stock,nos')
    args=p.parse_args()
    args.species_prefix=args.species_prefix or ['game:pig-eurasian-adult-','game:sheep-','game:goat-','game:deer-']
    if not 8<=args.chunk_side<=16:p.error('--chunk-side must be 8-16')
    if not 1<=args.block_count<=4000:p.error('--block-count must be 1-4000')
    if len(args.species_prefix)>16 or any(not x or ',' in x or len(x)>128 for x in args.species_prefix):p.error('Invalid species prefix')
    if not 128<=args.npc_count<=8192:p.error('--npc-count must be between 128 and 8192')
    import math
    animal_columns=16 if args.npc_count==128 else math.ceil(math.sqrt(args.npc_count))
    animal_extent=(192 if args.block_count>100 else 64)+2*(animal_columns-1)+1
    block_extent=8+2*(math.ceil(math.sqrt(args.block_count*2))-1)+1
    if max(animal_extent,block_extent)>=args.chunk_side*32:p.error('Workload exceeds kept-loaded terrain; increase --chunk-side (standard 12).')
    for key in ('game_dir','snapshot','output','dotnet','suitezip','optimizerzip','fixture','pack'):setattr(args,key,getattr(args,key).resolve())
    if (args.output.exists() and not args.resume) or args.output.is_relative_to(args.snapshot) or args.output.is_relative_to(args.game_dir):p.error('Use a fresh output outside the game and stopped disposable snapshot.')
    if any(x.is_symlink() for x in args.snapshot.rglob('*')):p.error('Snapshot symlinks refused.')
    modes=args.order.split(',')
    if any(x not in ('stock','nos','storage','reference','absent') for x in modes):p.error('Order must contain stock, nos, reference, absent and/or storage.')
    save=Path(json.loads((args.snapshot/'serverconfig.json').read_text())['WorldConfig']['SaveFileLocation']).name
    with sqlite3.connect('file:'+str(args.snapshot/'Saves'/save)+'?mode=ro',uri=True) as db:
        if db.execute('PRAGMA integrity_check').fetchone()[0]!='ok':p.error('Snapshot integrity failed.')
    args.output.mkdir(parents=True,exist_ok=args.resume)
    manifest=dict(kind='Disposable official-server generation/save/unload A/B laboratory workload',gameVersion='1.22.7',order=modes,worldSeed=json.loads((args.snapshot/'serverconfig.json').read_text())['WorldConfig']['Seed'],worldSha256=sha(args.snapshot/'Saves'/save),artifacts={x.name:sha(x) for x in (args.suitezip,args.optimizerzip,args.fixture)},dotnet=str(args.dotnet),cpuQuota=Path('/sys/fs/cgroup/cpu.max').read_text().strip(),memoryLimit=Path('/sys/fs/cgroup/memory.max').read_text().strip(),warmupSeconds=args.warmup,entitySeconds=args.entity_seconds,profileBursts=args.profile_bursts,pack=json.loads((args.pack/'manifest.json').read_text()),limits=['No authenticated clients or bots; selected modpack is recorded, not the owner production world. Forced-active, fixture-locally-tracked real NPCs and pass-through AI filter/task-start subscribers are lab controls. Normal physics workers run; no fake client records are created.','Stock arm uses same Suite, NOS observer and fixture, with engine switches disabled.','NOS arm uses the recorded optimizer configuration; storage arm enables only WorldgenRequestReuse and SaveSnapshotRelease. Controlled fixture NPC population is recorded separately; NPC count/lifecycle still varies.','Forced compacting GC and forced unloads are test-only controls outside the measured tick capture.','Save timings are observed console-log wall intervals on local container storage, not physical disk latency.'])
    manifest['profileUnload']=args.profile_unload
    manifest['physicsThreadsOverride']=args.physics_threads
    manifest['referencePhysicsThreadsOverride']=args.reference_physics_threads
    manifest['chunkSide']=args.chunk_side
    manifest['npcCount']=args.npc_count
    manifest['nosCaptureEnabled']=not args.no_nos_capture
    manifest['blockWorkload']=args.block_workload
    manifest['blockCount']=args.block_count
    manifest['speciesPrefixes']=args.species_prefix
    manifest['keepWorkloadWorld']=args.keep_workload_world
    manifest['referenceConfigurationSha256']=sha(args.reference_config) if args.reference_config else None
    manifest['optimizerConfigurationSha256']=sha(args.optimizer_config or (Path(__file__).resolve().parents[1]/'config/optimizer.json'))
    manifest['optimizerConfiguration']=json.loads((args.optimizer_config or (Path(__file__).resolve().parents[1]/'config/optimizer.json')).read_text())
    manifest_path=args.output/'manifest.json'
    if args.resume:
        previous=json.loads(manifest_path.read_text())
        for key in ('order','worldSha256','artifacts','pack','entitySeconds','profileBursts','warmupSeconds','optimizerConfigurationSha256','profileUnload','physicsThreadsOverride','referencePhysicsThreadsOverride','chunkSide','npcCount','nosCaptureEnabled','blockWorkload','blockCount','speciesPrefixes','keepWorkloadWorld','referenceConfigurationSha256'):
            if manifest[key]!=previous.get(key):p.error('Resume inputs differ: '+key)
        manifest=previous
    (args.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    results=[]
    for trial,mode in enumerate(modes,1):
        existing=args.output/f'{trial:02d}-{mode}'/'result.json'
        if args.resume and existing.exists():
            result=json.loads(existing.read_text())
            if result.get('mode')!=mode or not result.get('passed'):p.error('Cannot reuse incomplete/failed run '+str(existing))
            print(json.dumps(dict(trial=trial,mode=mode,resumed=True)),flush=True)
        else:result=run(args,mode,trial)
        results.append(result)
        if not result.get('passed'):return 1
    (args.output/'summary.json').write_text(json.dumps(dict(manifest=manifest,results=results),indent=2)+'\n')
    return 0
if __name__=='__main__':raise SystemExit(main())
