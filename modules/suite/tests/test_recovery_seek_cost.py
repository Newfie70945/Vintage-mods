"""Regression: one-cell recovery must not walk unrelated world history.
Measures SQLite VM work, independent of CI machine speed. Uses production SQL/indexes.
"""
from pathlib import Path
import argparse,json,re,sqlite3,time
parser=argparse.ArgumentParser();parser.add_argument('--baseline',type=Path);parser.add_argument('--output',type=Path);args=parser.parse_args()
source=(Path(__file__).resolve().parents[1]/'src/Services/LogStorageService.cs').read_text()
def queries(text):
 section=text.split('QueryRollbackRecoveryAsync',1)[1].split('public Task<List<ContainerSnapshotRecord>>',1)[0]
 return {name:re.search(name+r'\.CommandText\s*=\s*@"(.*?)";',section,re.S).group(1) for name in ['cmd','snapshots']}
c=sqlite3.connect(':memory:')
for table in ['events','snapshots']:c.executescript(re.search(r'CREATE TABLE IF NOT EXISTS '+table+r' \(.*?\);',source,re.S).group(0))
for index in ['idx_events_world_dim_pos_time','idx_events_world_time','idx_events_player_uid_time','idx_events_player_name_time','idx_snapshots_world_dim_pos_time']:
 c.executescript(re.search(r'CREATE INDEX IF NOT EXISTS '+index+r'\s.*?;',source,re.S).group(0))
hour=36_000_000_000;now=638_000_000_000_000_000
parameters=dict(world='w',uid='',area=1,dimension='0',cx=10,cy=100,cz=20,minX=10,maxX=10,minY=100,maxY=100,minZ=20,maxZ=20,r2=0)
# Before-window inventory baseline, a legitimate pre-window edit, target mutations,
# and a later third-party edit must ALL survive the faster read.
c.executemany("INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,world_id,dimension,x,y,z,block_code,meta_json) VALUES(?,?,'',?,'owner','w',?,10,100,20,'game:chest','{}')",[(1,now-49*hour//2,6,'0'),(2,now-3*hour//2,5,'0'),(3,now-hour,5,'overworld'),(4,now+hour,2,'0')])
c.executemany("INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,slots_json) VALUES(?,?,'','w',?,10,100,20,'game:chest','[]')",[(1,now-25*hour,'overworld'),(2,now-hour//2,'0'),(3,now+2*hour,'overworld')])
current=queries(source);small={};result={'fixture':{'unrelatedEvents':250000,'unrelatedSnapshots':100000},'runs':[]}
def run(label,sql,parameters):
 work=0
 def progress():
  nonlocal work
  work+=100
  # Bound baseline comparisons too: regressions cannot hang CI.
  return work>100000000
 c.set_progress_handler(progress,100);start=time.perf_counter()
 try:rows=c.execute(sql,parameters).fetchall()
 finally:c.set_progress_handler(None,0)
 elapsed=(time.perf_counter()-start)*1000
 return rows,{'source':label,'milliseconds':round(elapsed,3),'vmInstructionsApprox':work,'rows':len(rows)}
for name,sql in current.items():small[name]=run('small',sql,dict(parameters,**{'from':now-2*hour,'to':now}))
c.executemany("INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,world_id,dimension,x,y,z,block_code,meta_json) VALUES(?,?,'',1,'other','w','0',?,100,20,'game:stone','{}')",((100+i,now-(i%60+1)*600_000_000,1000+i) for i in range(250000)))
c.executemany("INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,slots_json) VALUES(?,?,'','w','0',?,100,20,'game:chest','[]')",((100+i,now-(i%100+1)*600_000_000,1000+i) for i in range(100000)))
for label,text in ([('baseline',args.baseline.read_text())] if args.baseline else [])+[('current',source)]:
 for hours in [2,7,24]:
  # Older lower bounds do not turn one-cell recovery into a whole-world read.
  p=dict(parameters,**{'from':now-hours*hour,'to':now})
  for name,sql in queries(text).items():
   rows,metrics=run(label,sql,p);metrics.update(query=name,durationHours=hours);result['runs'].append(metrics)
   if label=='current':
    assert {row[-1] for row in rows}==({1,2,3,4} if name=='cmd' else {1,2,3}), (name,hours,rows)
    assert metrics['vmInstructionsApprox']<25000,(name,'unrelated history increased one-cell query work',metrics)
# EXPLAIN must show bounded position seeks, not an outer world scan.
result['plans']={name:[row[3] for row in c.execute('EXPLAIN QUERY PLAN '+sql,dict(parameters,**{'from':now-2*hour,'to':now}))] for name,sql in current.items()}
if args.output:args.output.write_text(json.dumps(result,indent=2)+'\n')
for row in result['runs']:print(row)
print('PASS: single-block recovery stays bounded with 250,000 unrelated events/100,000 snapshots across 2h/7h/24h windows; all conflict/baseline rows retained.')
