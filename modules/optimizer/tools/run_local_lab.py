#!/usr/bin/env python3
"""Run the populated private local lab using the owner's official installation."""
import argparse,json,os,subprocess,threading,time,signal,sqlite3
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--game-dir',type=Path,required=True);p.add_argument('--dotnet',default='dotnet');p.add_argument('--duration',type=int,default=0);p.add_argument('--data-dir',type=Path);p.add_argument('--optimizer',choices=['off','reference','barrel','collision'],default='off');args=p.parse_args()
if args.duration<0:p.error('--duration must be nonnegative')
root=(args.data_dir or Path(__file__).resolve().parent).resolve();game=args.game_dir.resolve();dll=game/'VintagestoryServer.dll'
if not dll.is_file():p.error('Use your own official 1.22.7 directory containing VintagestoryServer.dll')
import shutil
optimizer=root/'OptionalOptimizer/NewfiesServerOptimizer-0.20.5-preview.zip'
for current in (root/'Mods').glob('NewfiesServerOptimizer-*.zip'):current.unlink()
if args.optimizer!='off':
 shutil.copy2(optimizer,root/'Mods'/optimizer.name)
 profile={'reference':'reference.json','barrel':'barrel-candidate.json','collision':'collision-candidate.json'}[args.optimizer]
 settings=json.loads((root/'Profiles'/profile).read_text());settings['Profiling']['AutoStart']=False;(root/'ModConfig/optimizer.json').write_text(json.dumps(settings,indent=2)+'\n')
config=json.loads((root/'serverconfig.json').read_text());config.update(Ip='127.0.0.1',Port=42579,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,ModPaths=['Mods',str(root/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(root/'Saves/intensive.vcdbs');(root/'serverconfig.json').write_text(json.dumps(config,indent=2)+'\n')
process=subprocess.Popen([args.dotnet,str(dll),'--dataPath',str(root),'--ip','127.0.0.1','--port','42579'],cwd=game,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
lock=threading.Lock();events=[];stopping=False;resumed_at=None

def send(command):
 with lock:
  if process.poll() is None:process.stdin.write(command+'\n');process.stdin.flush()
def reader():
 global resumed_at
 with (root/'local-lab-console.log').open('w')as log:
  for line in process.stdout:
   events.append(line);log.write(line);log.flush();print(line,end='',flush=True)
   if 'Dedicated Server now running on Port' in line:send('/nosloadresume')
   if 'NOSLOAD_RESUMED 'in line:resumed_at=time.monotonic();send('/nosloadoptimizer')
threading.Thread(target=reader,daemon=True).start();started=time.monotonic()
try:
 while process.poll() is None:
  if resumed_at is None and time.monotonic()-started>300:raise RuntimeError('Saved workload did not resume within 300 seconds')
  if any('NOSLOAD_FAIL' in x for x in events):raise RuntimeError('Fixture reported a failure')
  if args.duration and resumed_at is not None and time.monotonic()-resumed_at>=args.duration:
   send('/nosloadstate');time.sleep(2);send('/stop');process.wait(timeout=90);break
  time.sleep(.25)
except KeyboardInterrupt:
 send('/stop');process.wait(timeout=90)
finally:
 if process.poll() is None:
  send('/stop')
  try:process.wait(timeout=90)
  except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=30)
 time.sleep(.25)
 states=[json.loads(x.split('NOSLOAD_STATE ',1)[1])for x in events if 'NOSLOAD_STATE 'in x]
 resumed=[json.loads(x.split('NOSLOAD_RESUMED ',1)[1])for x in events if 'NOSLOAD_RESUMED 'in x]
 with sqlite3.connect(f'file:{root/"Saves/intensive.vcdbs"}?mode=ro',uri=True)as db:integrity=db.execute('pragma integrity_check').fetchone()[0]
 optimizer_states=[json.loads(x.split('NOSLOAD_OPTIMIZER_STATE ',1)[1])for x in events if 'NOSLOAD_OPTIMIZER_STATE 'in x]
 result=dict(optimizerMode=args.optimizer,optimizerState=optimizer_states[-1]if optimizer_states else None,exitCode=process.returncode,resumed=resumed[-1]if resumed else None,state=states[-1]if states else None,saveIntegrity=integrity,authenticatedPlayers=0)
 (root/'LOCAL-LAB-CHECK.json').write_text(json.dumps(result,indent=2)+'\n')
 if args.duration and (not optimizer_states or optimizer_states[-1]['installed']!=(args.optimizer!='off') or not states or states[-1]['active']<2400 or states[-1]['moves']==0 or states[-1]['barrelTicks']==0 or states[-1]['firepitTicks']==0 or integrity!='ok' or process.returncode!=0):raise SystemExit('Saved workload verification failed; inspect LOCAL-LAB-CHECK.json')
