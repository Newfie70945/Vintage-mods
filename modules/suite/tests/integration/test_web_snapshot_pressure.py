"""Opt-in HTTP smoke/load test on a copied disposable official-server world.
Requires NSS_WEB_TEST_BASELINE, VINTAGE_STORY and DOTNET_ROOT; never use live data.
Credentials are random, private to ignored artifacts, and absent from the report.
"""
from pathlib import Path
import concurrent.futures, http.cookiejar, json, os, re, secrets, shutil
import subprocess, threading, time, urllib.error, urllib.request
repo = Path(__file__).resolve().parents[4]
suite_version = next(m['version'] for m in json.loads((repo/'modules/manifest.json').read_text())['mods'] if m['id']=='suite')
build = re.search(r'BuildSuffix = "([^"]+)"', (repo/'modules/suite/src/SuiteRelease.cs').read_text()).group(1)
root = repo / 'artifacts/validation' / ('web-snapshot-' + str(int(time.time())))
data = root / 'data'
root.mkdir(parents=True)
shutil.copytree(Path(os.environ['NSS_WEB_TEST_BASELINE']), data,
                ignore=shutil.ignore_patterns('Logs', 'Mods', 'Cache', 'NewfiesBlockRepair', 'NewfiesOptimizer'))
game = Path(os.environ['VINTAGE_STORY']); net = str(Path(os.environ['DOTNET_ROOT']) / 'dotnet')
game_port = int(os.environ.get('NSS_WEB_GAME_PORT', '42599'))
web_port = int(os.environ.get('NSS_WEB_HTTP_PORT', '42669'))
config = json.loads((data / 'serverconfig.json').read_text())
save = Path(config['WorldConfig']['SaveFileLocation']).name
config.update(Ip='127.0.0.1', Port=game_port, AdvertiseServer=False, Upnp=False,
              ModPaths=['Mods', str(data / 'Mods')])
config['WorldConfig']['SaveFileLocation'] = str(data / 'Saves' / save)
(data / 'serverconfig.json').write_text(json.dumps(config))
mods = data / 'Mods'; mods.mkdir()
shutil.copy2(repo / ('artifacts/NewfiesServerEssentialsSuiteV' + suite_version + '.zip'), mods)
folder = data / 'ModConfig/NewfiesServerEssentialsSuite'
webfile = folder / 'NewfiesWebServer.json'
web = json.loads(webfile.read_text()); password = secrets.token_hex(24)
web.update(Enabled=True, BindAddress='127.0.0.1', Port=web_port, AdminUsername='fixtureowner',
           Password=password, PasswordSalt='', PasswordHash='', SafeDiagnosticsMode=False,
           SecureCookies=False)
webfile.write_text(json.dumps(web))
f = folder / 'NewfiesServerEssentialsFeatures.json'
features = json.loads(f.read_text()); features['WebDashboard'] = True; f.write_text(json.dumps(features))
base = 'http://127.0.0.1:' + str(web_port)
lines = []
process = subprocess.Popen([net, str(game / 'VintagestoryServer.dll'), '--dataPath', str(data),
                            '--ip', '127.0.0.1', '--port', str(game_port)], cwd=game,
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                           text=True, bufsize=1)
def log_reader():
    with (root / 'console.log').open('w') as log:
        for line in process.stdout:
            lines.append(line); log.write(line); log.flush()
threading.Thread(target=log_reader, daemon=True).start()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def request(path, payload=None, opener=client):
    body = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(base + path, data=body, headers={'Content-Type':'application/json'})
    started = time.perf_counter()
    try:
        response = opener.open(req, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    return response.code, json.loads(raw), len(raw), (time.perf_counter()-started)*1000
try:
    until = time.monotonic() + 180
    while not any('Dedicated Server now running on Port' in line for line in lines):
        if process.poll() is not None or time.monotonic() > until:
            raise RuntimeError('Disposable server did not start; inspect private console log')
        time.sleep(.2)
    assert request('/api/header')[0] == 401
    assert request('/api/login', {'username':'fixtureowner','password':password})[0] == 200
    status, header, header_bytes, _ = request('/api/header')
    assert status == 200 and 'playersOnline' in header and 'profiler' in header
    assert not any(k in header for k in ('payload', 'bytes', 'samples', 'lastProfile'))
    status, dashboard, dashboard_bytes, _ = request('/api/dashboard')
    assert status == 200 and 'blockLogger' in dashboard and 'playersOnline' in dashboard
    with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
        results = list(pool.map(lambda _: request('/api/header'), range(32)))
    assert all(status == 200 and 'playersOnline' in payload for status,payload,_,_ in results)
    status, server_status, status_bytes, _ = request('/api/server-status?days=30')
    assert status == 200 and isinstance(server_status, dict) and 'payload' not in server_status
    status, small, _, _ = request('/api/tick-debug/status')
    assert status == 200 and set(small) == {'enabled'}
    status, web_status, _, _ = request('/api/web/status')
    assert status == 200
    diagnostics = web_status.get('diagnostics', {})
    assert diagnostics.get('snapshotCacheHits', 0) >= 16
    report = dict(gameVersion='1.22.7', suiteVersion=suite_version, build=build,
                  checks=['unauthenticated header rejected','owner login','compact header JSON',
                          'dashboard JSON preserved','32 header requests / 16 workers',
                          'server status JSON preserved','tick status only flag','cache sharing'],
                  headerBytes=header_bytes, dashboardBytes=dashboard_bytes, serverStatusBytes=status_bytes,
                  headerRequestMaxMs=round(max(r[3] for r in results),2),
                  snapshotCacheHits=diagnostics.get('snapshotCacheHits'),
                  note='Empty disposable server HTTP integration; no production TPS or player-scaling claim.')
    (root / 'result.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(report, indent=2), flush=True)
finally:
    if process.poll() is None:
        process.stdin.write('stop\n'); process.stdin.flush()
        try: process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
