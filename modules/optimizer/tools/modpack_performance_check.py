#!/usr/bin/env python3
"""Repeated full-suite/modpack worldgen, active NPC AI, save and profiler-cost laboratory test. No clients or player-capacity claims."""
from __future__ import annotations
import argparse, hashlib, json, os, shutil, sqlite3, subprocess, threading, time
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
    for source in (args.suitezip,args.optimizerzip,args.fixture,*args.pack.glob('*.zip')):shutil.copy2(source,mods/source.name)
    cfg=data/'ModConfig';cfg.mkdir(exist_ok=True)
    (cfg/'NewfiesWebServer.json').write_text('{"Enabled":false,"BindAddress":"127.0.0.1","Port":9465}')
    optimizer=json.loads((Path(__file__).resolve().parents[1]/'config/optimizer.json').read_text())
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
    (data/'servermagicnumbers.json').write_text(json.dumps(magic,indent=2))
    env=os.environ.copy();env['DOTNET_ROOT']=str(args.dotnet.parent)
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
    result=dict(mode=mode,trial=trial,authenticationVerification=True,authenticatedPlayers=0,columns=64,saveCycles=5,magicNumbers=magic)
    try:
        wait('Dedicated Server now running on Port',timeout=300)
        result['loadedMods']=payload_load('MODS',send('/nosloadmods'))
        expected=json.loads((args.pack/'manifest.json').read_text())
        enabled={m['id']:m['version'] for m in result['loadedMods'] if m['enabled']}
        for mod in expected:
            if mod['side'].lower()!='client' and enabled.get(mod['id'])!=mod['version']:
                raise RuntimeError('Pack member absent or disabled: '+str(mod))
        payload_load('HEALTH',send('/nosloadhealth'))
        result['profileCancellation']=payload_load('CANCEL_PASS',send('/nosloadcanceltest'))
        time.sleep(args.warmup)
        payload('WARM',send('/nosbenchwarm'));time.sleep(3);result['baselineGc']=collect()
        wait('Capture started.',send('/optimizer profile start'))
        result['beforeGeneration']=state();start=time.monotonic()
        result['generation']=payload('GENERATED',send('/nosbenchgenerate'))
        result['afterGeneration']=state();result['generationObserverWallSeconds']=time.monotonic()-start
        result['animals']=payload_load('ENTITIES',send('/nosloadentities'))
        time.sleep(10)
        result['entityBefore']=payload_load('STATE',send('/nosloadstate'))
        if result['entityBefore']['engineProfilerEnabled'] or result['entityBefore']['slowTickProfilingEnabled']:raise RuntimeError('Unexpected full engine profiling before steady workload.')
        payload_load('RESET',send('/nosloadreset'));entityStart=time.monotonic();time.sleep(args.entity_seconds)
        result['entityAfter']=payload_load('STATE',send('/nosloadstate'));result['entityWallSeconds']=time.monotonic()-entityStart;result['entityWindow']={'start':entityStart,'end':time.monotonic()}
        if result['entityAfter']['active']<100 or result['entityAfter']['moves']<=result['entityBefore']['moves'] or result['entityAfter']['filterCalls']<=result['entityBefore']['filterCalls'] or result['entityAfter']['taskStarts']<=result['entityBefore']['taskStarts']:
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
        # Give the worker a fixed settling interval, not a different delay per arm.
        time.sleep(3)
        result['saves']=[]
        for cycle in range(5):
            dirty=payload('DIRTY',send('/nosbenchdirty'))
            if dirty['chunks']!=512:raise RuntimeError('Expected 512 loaded benchmark chunks: '+str(dirty))
            result['saves'].append(save_once());time.sleep(1)
        time.sleep(5);result['afterSaves']=state()
        wait('Capture stopped.',send('/optimizer profile stop'))
        result['unloaded']=payload('UNLOADED',send('/nosbenchunload'));time.sleep(3)
        result['afterUnloadGc']=collect()
        # Counterfactual within each process: a new snapshot should release old
        # stock snapshot references without any optimizer help.
        result['refreshSave']=save_once();time.sleep(3);result['afterRefreshGc']=collect()
        send('/stop');process.wait(timeout=60);done.set();time.sleep(.25)
        result['exitCode']=process.returncode
        errors=[line for _,line in events if '[Server Error]' in line or '[Error]' in line or 'NOSBENCH_FAIL' in line or 'Exception' in line]
        result['errors']=errors
        with sqlite3.connect('file:'+str(data/'Saves'/save)+'?mode=ro',uri=True) as db:result['saveIntegrity']=db.execute('PRAGMA integrity_check').fetchone()[0]
        result['profile']=json.loads((data/'NewfiesOptimizer/latest.json').read_text())
        result['passed']=not errors and process.returncode==0 and result['saveIntegrity']=='ok' and result['profile']['tick']['count']>0
        wanted={'worldgenRequestReuse','saveSnapshotRelease'}
        states={m['name']:m['status'] for m in result['profile']['modules'] if m['name'] in wanted}
        if states!={name:'disabled' if mode=='stock' else 'active' for name in wanted}:raise RuntimeError('Unexpected module activation: '+str(states))
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
    p.add_argument('--resume',action='store_true',help='Resume complete recorded runs only when input hashes and workload parameters match.')
    p.add_argument('--entity-seconds',type=int,default=90);p.add_argument('--profile-bursts',type=int,default=5)
    p.add_argument('--warmup',type=int,default=15);p.add_argument('--port',type=int,default=42579)
    p.add_argument('--order',default='stock,nos,nos,stock,stock,nos')
    args=p.parse_args()
    for key in ('game_dir','snapshot','output','dotnet','suitezip','optimizerzip','fixture','pack'):setattr(args,key,getattr(args,key).resolve())
    if (args.output.exists() and not args.resume) or args.output.is_relative_to(args.snapshot) or args.output.is_relative_to(args.game_dir):p.error('Use a fresh output outside the game and stopped disposable snapshot.')
    if any(x.is_symlink() for x in args.snapshot.rglob('*')):p.error('Snapshot symlinks refused.')
    modes=args.order.split(',')
    if any(x not in ('stock','nos','storage') for x in modes):p.error('Order must contain stock, nos and/or storage.')
    save=Path(json.loads((args.snapshot/'serverconfig.json').read_text())['WorldConfig']['SaveFileLocation']).name
    with sqlite3.connect('file:'+str(args.snapshot/'Saves'/save)+'?mode=ro',uri=True) as db:
        if db.execute('PRAGMA integrity_check').fetchone()[0]!='ok':p.error('Snapshot integrity failed.')
    args.output.mkdir(parents=True,exist_ok=args.resume)
    manifest=dict(kind='Disposable official-server generation/save/unload A/B laboratory workload',gameVersion='1.22.7',order=modes,worldSeed=json.loads((args.snapshot/'serverconfig.json').read_text())['WorldConfig']['Seed'],worldSha256=sha(args.snapshot/'Saves'/save),artifacts={x.name:sha(x) for x in (args.suitezip,args.optimizerzip,args.fixture)},dotnet=str(args.dotnet),cpuQuota=Path('/sys/fs/cgroup/cpu.max').read_text().strip(),memoryLimit=Path('/sys/fs/cgroup/memory.max').read_text().strip(),warmupSeconds=args.warmup,entitySeconds=args.entity_seconds,profileBursts=args.profile_bursts,pack=json.loads((args.pack/'manifest.json').read_text()),limits=['No authenticated clients or bots; selected modpack is recorded, not the owner production world. Forced-active, fixture-locally-tracked real NPCs and pass-through AI filter/task-start subscribers are lab controls. Normal physics workers run; no fake client records are created.','Stock arm uses same Suite, NOS observer and fixture, with engine switches disabled.','NOS arm uses all shipped default engine switches; storage arm enables only WorldgenRequestReuse and SaveSnapshotRelease. Controlled 128 forced-active NPCs; NPC count/lifecycle still varies.','Forced compacting GC and forced unloads are test-only controls outside the measured tick capture.','Save timings are observed console-log wall intervals on local container storage, not physical disk latency.'])
    manifest_path=args.output/'manifest.json'
    if args.resume:
        previous=json.loads(manifest_path.read_text())
        for key in ('order','worldSha256','artifacts','pack','entitySeconds','profileBursts','warmupSeconds'):
            if manifest[key]!=previous[key]:p.error('Resume inputs differ: '+key)
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
