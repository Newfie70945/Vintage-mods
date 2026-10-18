"""Synthetic recovery query benchmark and EXPLAIN plans; no live world or save."""
from pathlib import Path
import argparse,json,re,sqlite3,time
parser=argparse.ArgumentParser();parser.add_argument('--baseline',type=Path);parser.add_argument('--output',type=Path);args=parser.parse_args()
current=(Path(__file__).resolve().parents[1]/'src/Services/LogStorageService.cs').read_text()
def queries(source):
 section=source.split('QueryRollbackRecoveryAsync',1)[1].split('public Task<List<ContainerSnapshotRecord>>',1)[0]
 return {name:re.search(name+r'\.CommandText\s*=\s*@"(.*?)";',section,re.S).group(1) for name in ['cmd','snapshots']}
c=sqlite3.connect(':memory:')
for table in ['events','snapshots']:
 c.executescript(re.search(r'CREATE TABLE IF NOT EXISTS '+table+r' \(.*?\);',current,re.S).group(0))
for index in ['idx_events_world_dim_pos_time','idx_events_world_time','idx_events_player_uid_time','idx_events_player_name_time','idx_snapshots_world_dim_pos_time']:
 match=re.search(r'CREATE INDEX IF NOT EXISTS '+index+r'\s.*?;',current,re.S)
 if match:c.executescript(match.group(0))
# 50,000 events over 500 cells, with retained later edits and pre-window snapshots.
c.executemany('INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,player_name,world_id,dimension,x,y,z,block_code,meta_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)',((i+1,1000+i,'',1,'target' if i%10==0 else 'other','name','w','0' if i%2 else 'overworld',i%500,100,0,'game:stone','{}') for i in range(50000)))
c.executemany('INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,player_uid,player_name,inventory_id,reason,slot_count,non_empty_slot_count,total_item_count,slots_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',((i+1,900+i//500,'','w','0',i%500,100,0,'game:chest','target','name','chest','open',16,0,0,'[]') for i in range(2000)))
a={'world':'w','from':1000,'to':2000,'target':'target','uid':'target','area':0,'dimension':'0','cx':0,'cy':100,'cz':0,'minX':-30,'maxX':30,'minY':70,'maxY':130,'minZ':-30,'maxZ':30,'r2':900}
result={'fixture':{'events':50000,'snapshots':2000,'cells':500},'runs':[]}
references={}
for label,source in ([('baseline',args.baseline.read_text())] if args.baseline else [])+[('current',current)]:
 for mode in ['player','area','block']:
  parameters=dict(a,area=mode!='player')
  if mode=='block':parameters.update(minX=0,maxX=0,minY=100,maxY=100,minZ=0,maxZ=0,r2=0)
  for name,sql in queries(source).items():
   plan=[row[3] for row in c.execute('EXPLAIN QUERY PLAN '+sql,parameters)]
   started=time.perf_counter();rows=c.execute(sql,parameters).fetchall();elapsed=time.perf_counter()-started
   key=(mode,name)
   ids=[row[-1] for row in rows]
   if label=='baseline':references[key]=ids
   elif references:assert references[key]==ids,f'{mode}/{name}: query changed retained history or order'
   result['runs'].append({'source':label,'mode':mode,'query':name,'rows':len(rows),'milliseconds':round(elapsed*1000,2),'plan':plan})
if args.output:args.output.write_text(json.dumps(result,indent=2))
for run in result['runs']:print(f"{run['source']} {run['mode']} {run['query']}: {run['rows']} rows, {run['milliseconds']} ms")
print('PASS retained history/order comparison' if references else 'PASS synthetic fixture queries')
