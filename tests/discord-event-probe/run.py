#!/usr/bin/env python3
"""Offline Discord queue integration probe. Requires a stopped disposable world, no bot credentials."""
import argparse,json,os,shutil,subprocess,threading,time,zipfile
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--port',type=int,default=42612);p.add_argument('--game',type=Path,required=True);p.add_argument('--dotnet',required=True);p.add_argument('--snapshot',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
a=p.parse_args();root=Path(__file__).resolve().parents[2]
if a.output.exists():raise SystemExit('Output already exists; refusing to overwrite')
a.output.mkdir(parents=True)
results=[]
for index,mode in enumerate(['standalone','suite']):
    out=a.output/mode;data=out/'data'
    shutil.copytree(a.snapshot,data,ignore=shutil.ignore_patterns('Mods','Logs','Cache','Backups','BackupSaves','ModConfig','NewfiesServerEssentialsWebMap'))
    config=json.loads((data/'serverconfig.json').read_text());config.update(Ip='127.0.0.1',Port=a.port+index,AdvertiseServer=False,Upnp=False,ModPaths=['Mods',str(data/'Mods')])
    config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/Path(config['WorldConfig']['SaveFileLocation']).name)
    (data/'serverconfig.json').write_text(json.dumps(config,indent=2));(data/'Mods').mkdir()
    names=['NewfiesServerEssentialsDiscordV1.0.14.zip']+(['NewfiesServerEssentialsSuiteV1.2.44.zip'] if mode=='suite' else [])
    for name in names:shutil.copy2(root/'artifacts'/name,data/'Mods'/name)
    with zipfile.ZipFile(data/'Mods'/'DiscordEventProbe.zip','w') as z:
        z.write(Path(__file__).parent/'bin/Release/net10.0/DiscordEventProbe.dll','DiscordEventProbe.dll')
        z.writestr('modinfo.json',json.dumps(dict(type='code',modid='discordeventprobe',name='Offline Discord Event Probe',version='1.0.0',side='Server',dependencies={'game':'1.22.7','newfiesserveressentialsdiscord':'1.0.14'})))
    cfg=data/'ModConfig'/'NewfiesServerEssentialsSuite';cfg.mkdir(parents=True)
    (cfg/'NewfiesServerEssentialsDiscord.json').write_text(json.dumps({'Enabled':False,'BotToken':'','Events':{'PlayerDeath':{'Enabled':False,'ChannelId':0,'Format':'☠️ **{player} died**'},'TemporalStorm':None}}))
    env=os.environ.copy();env['DOTNET_ROOT']=str(Path(a.dotnet).parent)
    proc=subprocess.Popen([a.dotnet,str(a.game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(a.port+index)],cwd=a.game,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
    ready=threading.Event();done=threading.Event();lines=[]
    log=(out/'console.log').open('w')
    def read():
        for line in proc.stdout:
            log.write(line);log.flush();lines.append(line)
            if 'server now running' in line.lower() or 'Server is now running' in line:ready.set()
            if 'NSE_DISCORD_EVENT_PROBE PASS' in line or 'NSE_DISCORD_EVENT_PROBE FAIL' in line:done.set()
    thread=threading.Thread(target=read,daemon=True);thread.start()
    try:
        if not ready.wait(100):raise RuntimeError('Server did not start: '+str(out/'console.log'))
        proc.stdin.write('/nsediscordeventprobe\n');proc.stdin.flush()
        if not done.wait(25):raise RuntimeError('Probe timed out')
        result=next(x.strip() for x in lines if 'NSE_DISCORD_EVENT_PROBE ' in x)
        results.append({'mode':mode,'result':result})
    finally:
        if proc.poll() is None:
            proc.stdin.write('/stop\n');proc.stdin.flush()
            try:proc.wait(35)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
        thread.join(5);log.close()
    if ' FAIL' in result:raise RuntimeError(result)
(a.output/'results.json').write_text(json.dumps(results,indent=2)+'\n');print(json.dumps(results,indent=2))
