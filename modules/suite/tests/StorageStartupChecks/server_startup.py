"""Opt-in integration against a COPY of a stopped, disposable 1.22.7 world.
Requires NSS_BATCH_TEST_BASELINE, VINTAGE_STORY and DOTNET_ROOT; builds Suite first.
No production paths, public listeners or external services are used.
"""
from pathlib import Path
import hashlib, http.cookiejar, json, os, secrets, shutil, subprocess, threading, time, urllib.error, urllib.request
repo=Path(__file__).resolve().parents[4]
version=next(m['version'] for m in json.loads((repo/'modules/manifest.json').read_text())['mods'] if m['id']=='suite')
root=repo/'artifacts/validation'/('storage-startup-'+version+'-'+str(int(time.time())))
data=root/'data'; root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_BATCH_TEST_BASELINE']).resolve(),data,ignore=shutil.ignore_patterns('Logs','Mods','Cache','NewfiesBlockRepair','NewfiesOptimizer'))
game=Path(os.environ['VINTAGE_STORY']);net=str(Path(os.environ['DOTNET_ROOT'])/'dotnet')
game_port=42599;web_port=42669
config=json.loads((data/'serverconfig.json').read_text());save=Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1',Port=game_port,AdvertiseServer=False,Upnp=False,VerifyPlayerAuth=True,PassTimeWhenEmpty=True,ModPaths=['Mods',str(data/'Mods')]);config['WorldConfig']['SaveFileLocation']=str(data/'Saves'/save);(data/'serverconfig.json').write_text(json.dumps(config))
(data/'Mods').mkdir();shutil.copy2(repo/'artifacts'/('NewfiesServerEssentialsSuiteV'+version+'.zip'),data/'Mods')
cdir=data/'ModConfig/NewfiesServerEssentialsSuite';password=secrets.token_hex(20)
webfile=cdir/'NewfiesWebServer.json';web=json.loads(webfile.read_text());web.update(Enabled=True,BindAddress='127.0.0.1',Port=web_port,AdminUsername='fixtureowner',Password=password,PasswordSalt='',PasswordHash='',SafeDiagnosticsMode=False);webfile.write_text(json.dumps(web))
featurefile=cdir/'NewfiesServerEssentialsFeatures.json';features=json.loads(featurefile.read_text());features.update(BlockLogger=True,WebDashboard=True);featurefile.write_text(json.dumps(features))
loggerfile=cdir/'NewfiesBlocklogger.json';logger=json.loads(loggerfile.read_text());logger.update(FullDatabaseIntegrityCheckOnStartup=False,AutoDatabaseBackupIntervalHours=0);loggerfile.write_text(json.dumps(logger))
lines=[];process=None;csrf=None;base='http://127.0.0.1:'+str(web_port)
client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def request(path,body=None):
    headers={'Origin':base,'Accept':'application/json'}
    if csrf:headers['X-CSRF-Token']=csrf
    if body is not None: headers['Content-Type']='application/json';time.sleep(1.15)
    req=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
    try:
        with client.open(req,timeout=25) as response:return response.status,json.load(response)
    except urllib.error.HTTPError as error:return error.code,json.load(error)
def auth():
    global csrf
    csrf=None;status,result=request('/api/login',{'username':'fixtureowner','password':password});assert status==200,result;csrf=result['csrfToken']
def launch():
    global process
    offset=len(lines);process=subprocess.Popen([net,str(game/'VintagestoryServer.dll'),'--dataPath',str(data),'--ip','127.0.0.1','--port',str(game_port)],cwd=game,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
    def read(proc):
        with (root/'console.log').open('a') as output:
            for line in proc.stdout: lines.append(line);output.write(line);output.flush()
    threading.Thread(target=read,args=(process,),daemon=True).start()
    deadline=time.monotonic()+180
    while not any('Dedicated Server now running on Port' in l for l in lines[offset:]):
        if process.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Server failed to start: '+str(root))
        time.sleep(.1)
    auth()
def stop():
    process.stdin.write('/stop\n');process.stdin.flush();process.wait(timeout=60);assert process.returncode==0
result={'suiteVersion':version,'officialGameVersion':'1.22.7','authenticatedGameClients':0}
try:
    launch();status,first=request('/api/blocklogger/maintenance');assert status==200 and first['available'],first
    assert first['startupValidationMode']=='Lightweight read-only probe',first
    assert 'schema and indexes' in first['startupMilliseconds'],first
    status,saved=request('/api/blocklogger/maintenance/config',{'fullIntegrityCheckOnStartup':True});assert status==200,saved
    status,draft=request('/api/blocklogger/maintenance');assert draft['fullIntegrityCheckOnStartup'] is True and draft['startupValidationMode']=='Lightweight read-only probe',draft
    stop();launch();status,full=request('/api/blocklogger/maintenance');assert full['startupValidationMode']=='Full integrity scan',full
    status,check=request('/api/health/database-integrity',{});assert status==200 and check['integrity']=='ok',check
    stop()
    dbs=list((data/'ModData').rglob('blocklogger.db'));assert len(dbs)==1,dbs
    db=dbs[0]
    for suffix in ('-wal','-shm'):
        side=Path(str(db)+suffix)
        if side.exists():side.unlink()
    bad=b'Not a SQLite database: disposable corruption fixture.';db.write_bytes(bad)
    launch();status,failed=request('/api/blocklogger/maintenance');assert status==200 and not failed['available'] and failed['startupError'],failed
    assert db.read_bytes()==bad,'Invalid database changed'
    assert not list(db.parent.glob('blocklogger-corrupt-*')),'Invalid database was quarantined'
    result.update(lightweight=first,full=full,manualIntegrity=check,failedStorage=failed,databasePreserved=True,otherSuiteServicesAvailable=True)
    stop();assert db.read_bytes()==bad
    (root/'result.json').write_text(json.dumps(result,indent=2));print('PASS official-server light/full startup, next-start config, manual check and invalid DB isolation/preservation:',root)
finally:
    if process and process.poll() is None:
        process.kill();process.wait()
