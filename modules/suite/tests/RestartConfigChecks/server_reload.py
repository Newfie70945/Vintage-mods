#!/usr/bin/env python3
"""Two-start persistence check using a copied, stopped disposable world. No scheduled restart is triggered."""
import argparse,json,os,shutil,subprocess,threading
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for name in ['game','dotnet','snapshot','output']:p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--port',type=int,default=42624);a=p.parse_args();repo=Path(__file__).resolve().parents[4]
if a.output.exists():raise SystemExit('Output exists; refusing to overwrite')
data=a.output/'data';shutil.copytree(a.snapshot,data,ignore=shutil.ignore_patterns('Mods','Logs','Cache','Backups','BackupSaves','NewfiesBlockRepair','NewfiesServerEssentialsWebMap'))
config=json.loads((data/'serverconfig.json').read_text());config.update(Ip='127.0.0.1',Port=a.port,AdvertiseServer=False,Upnp=False,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/Path(config['WorldConfig']['SaveFileLocation']).name);(data/'serverconfig.json').write_text(json.dumps(config))
(data/'Mods').mkdir();shutil.copy2(repo/'artifacts'/('NewfiesServerEssentialsSuiteV'+next(m['version'] for m in json.loads((repo/'modules/manifest.json').read_text())['mods'] if m['id']=='suite')+'.zip'),data/'Mods')
folder=data/'ModConfig/NewfiesServerEssentialsSuite';folder.mkdir(exist_ok=True,parents=True);path=folder/'NewfiesAutomatedRestarts.json';path.write_text(json.dumps(dict(Enabled=False,WarningMinutes=[1,5,10])))
results=[]
for boot in range(2):
 env=os.environ.copy();env['DOTNET_ROOT']=str(a.dotnet.parent)
 process=subprocess.Popen([str(a.dotnet),str(a.game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(a.port)],cwd=a.game,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
 ready=threading.Event();log=(a.output/f'boot-{boot+1}.log').open('w')
 def read(proc,output,event):
  for line in proc.stdout:
   output.write(line);output.flush()
   if 'server now running' in line.lower():event.set()
 thread=threading.Thread(target=read,args=(process,log,ready),daemon=True);thread.start()
 try:
  if not ready.wait(100):raise RuntimeError('Server did not reach RunGame')
  persisted=json.loads(path.read_text());assert persisted['SchemaVersion']==1 and 'Reason' in persisted,'Configuration loader did not persist defaults'
  actual=persisted['WarningMinutes'];assert actual==[1,5,10],actual
  results.append(dict(boot=boot+1,warningMinutes=actual))
 finally:
  if process.poll() is None:
   process.stdin.write('/stop\n');process.stdin.flush()
   try:process.wait(35)
   except subprocess.TimeoutExpired:process.kill();process.wait();raise
  thread.join(5);log.close()
  assert process.returncode==0,process.returncode
(a.output/'result.json').write_text(json.dumps(dict(gameVersion='1.22.7',build='ms11-restartwarnings1',scheduledRestartsTriggered=0,boots=results),indent=2)+'\n');print('PASS custom restart warnings preserved on both official-server launches')
