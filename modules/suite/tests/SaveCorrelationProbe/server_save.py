"""Opt-in copied official 1.22.7 server test. Deliberately stalls ONE save.
No authenticated clients; not a performance benchmark and never use a live world.
Build Suite and SaveCorrelationProbe first. Requires the same environment variables
as StorageStartupChecks/server_startup.py. OperationsCenter/WebDashboard stay off.
"""
from pathlib import Path
import json,os,shutil,subprocess,threading,time,zipfile
repo=Path(__file__).resolve().parents[4];root=repo/'artifacts/validation'/('save-correlation-'+str(int(time.time())));data=root/'data';root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_BATCH_TEST_BASELINE']).resolve(),data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','NewfiesBlockRepair','NewfiesOptimizer'))
game=Path(os.environ['VINTAGE_STORY']);net=str(Path(os.environ['DOTNET_ROOT'])/'dotnet');port=42600
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=port,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,PassTimeWhenEmpty=True,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save);(data/'serverconfig.json').write_text(json.dumps(config))
mods=data/'Mods';mods.mkdir();shutil.copy2(repo/'artifacts/NewfiesServerEssentialsSuiteV1.2.45.zip',mods)
with zipfile.ZipFile(mods/'SaveCorrelationProbe.zip','w',zipfile.ZIP_DEFLATED) as z:
 z.write(repo/'modules/suite/tests/SaveCorrelationProbe/bin/Release/net10.0/SaveCorrelationProbe.dll','SaveCorrelationProbe.dll');z.writestr('modinfo.json',json.dumps(dict(type='code',modid='nsssavecorrelationprobe',name='Disposable Save Correlation Probe',version='1.0.0',side='server',requiredOnClient=False)))
cdir=data/'ModConfig/NewfiesServerEssentialsSuite'
p=cdir/'NewfiesServerEssentialsFeatures.json';f=json.loads(p.read_text());f.update(PerformanceDiagnostics=True,OperationsCenter=False,WebDashboard=False);p.write_text(json.dumps(f))
p=cdir/'NewfiesBlocklogger.json';c=json.loads(p.read_text());c.update(ProfilerEnabled=True,AutoDatabaseBackupIntervalHours=0);p.write_text(json.dumps(c))
lines=[];proc=subprocess.Popen([net,str(game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(port)],cwd=game,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
def read():
 with (root/'console.log').open('w') as out:
  for line in proc.stdout:lines.append(line);out.write(line);out.flush()
threading.Thread(target=read,daemon=True).start()
def wait(marker,offset=0,timeout=180):
 deadline=time.monotonic()+timeout
 while not any(marker in l for l in lines[offset:]):
  if proc.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Missing '+marker+'; see '+str(root))
  time.sleep(.1)
 return next(l for l in lines[offset:] if marker in l)
def command(cmd,marker):
 offset=len(lines);proc.stdin.write(cmd+'\n');proc.stdin.flush();return wait(marker,offset,30)
try:
 wait('Dedicated Server now running on Port')
 # Build a normal >60-sample baseline and pass the 45-second startup suppression.
 command('/nsssaveprobe','SAVE_PROBE_ARMED')
 deadline=time.monotonic()+65
 while time.monotonic()<deadline:
  if proc.poll() is not None:raise RuntimeError('Test server exited')
  time.sleep(.2)
 command('/autosavenow','World saved!')
 deadline=time.monotonic()+15;result=None
 while time.monotonic()<deadline:
  report=command('/nsssaveprobereport','SAVE_PROBE_REPORT ');result=json.loads(report.split('SAVE_PROBE_REPORT ',1)[1])
  if any(i['WorldSaveWindowMs']>=150 for i in result['incidents']):break
  time.sleep(.3)
 assert result and result['windows']>0,result
 matches=[i for i in result['incidents'] if i['WorldSaveWindowMs']>=150];assert matches,result
 assert any('World save' in g['PrimaryCategory'] for g in result['smart']['IncidentGroups']),result
 assert any(s['Category']=='WorldSave' for s in result['smart']['Signals']),result
 assert all('correlation, not proven cause' in i['Evidence'] for i in matches)
 (root/'result.json').write_text(json.dumps(dict(officialGameVersion='1.22.7',authenticatedGameClients=0,artificialSaveStallMs=1800,operationsCenter=False,webDashboard=False,result=result),indent=2))
 print('PASS real GameWorldSave subscription, delayed sampler overlap, incident evidence and grouped/smart save correlation with OperationsCenter/WebDashboard disabled:',root)
 proc.stdin.write('/stop\n');proc.stdin.flush();proc.wait(timeout=60);assert proc.returncode==0
finally:
 if proc.poll() is None:proc.kill();proc.wait()
