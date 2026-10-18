"""Randomized comparison against the pre-Perf3 recovery SQL, with no saved world."""
from pathlib import Path
import json,re,random,sqlite3
root=Path(__file__).resolve().parent
source=(root.parent/'src/Services/LogStorageService.cs').read_text()
section=source.split('QueryRollbackRecoveryAsync',1)[1].split('public Task<List<ContainerSnapshotRecord>>',1)[0]
current={name:re.search(name+r'\.CommandText\s*=\s*@"(.*?)";',section,re.S).group(1) for name in ['cmd','snapshots']}
baseline=json.loads((root/'recovery_sql_baseline.json').read_text())
randomizer=random.Random(49281)
for fixture in range(30):
 c=sqlite3.connect(':memory:')
 for table in ['events','snapshots']:c.executescript(re.search(r'CREATE TABLE IF NOT EXISTS '+table+r' \(.*?\);',source,re.S).group(0))
 for index in ['idx_events_world_dim_pos_time','idx_events_world_time','idx_events_player_uid_time','idx_events_player_name_time','idx_snapshots_world_dim_pos_time']:
  match=re.search(r'CREATE INDEX IF NOT EXISTS '+index+r'\s.*?;',source,re.S)
  assert match,index
  c.executescript(match.group(0))
 for i in range(300):
  actor=randomizer.choice(['a','b','']);dim=randomizer.choice(['0','overworld','1']);world=randomizer.choice(['w','w','other']);x=randomizer.randrange(5);timestamp=randomizer.randrange(500);action=randomizer.choice([1,2,3,4,5,6,9])
  meta=json.dumps({'sourcePlayerUid':randomizer.choice(['a','b',''])}) if i%3 else '{malformed'
  c.execute('INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,player_name,world_id,dimension,x,y,z,block_code,meta_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)',(i+1,timestamp,'',action,actor,'name',world,dim,x,100,0,'game:stone',meta))
 for i in range(80):
  c.execute('INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,player_uid,player_name,inventory_id,reason,slot_count,non_empty_slot_count,total_item_count,slots_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',(i+1,randomizer.randrange(500),'',randomizer.choice(['w','other']),randomizer.choice(['0','overworld','1']),randomizer.randrange(5),100,0,'game:chest','a','name','chest','open',16,0,0,'[]'))
 for mode in [0,1,2]:
  args={'world':'w','from':100,'to':300,'uid':'a','area':int(mode!=0),'dimension':'0','cx':2,'cy':100,'cz':0,'minX':0,'maxX':4,'minY':98,'maxY':102,'minZ':-2,'maxZ':2,'r2':4}
  if mode==2:args.update(minX=2,maxX=2,minY=100,maxY=100,minZ=0,maxZ=0,r2=0)
  for name in current:assert c.execute(current[name],args).fetchall()==c.execute(baseline[name],args).fetchall(),(fixture,mode,name)
 c.close()
print('PASS: 30 seeded mixed-actor/world/dimension fixtures; optimized player/area event and snapshot queries exactly match pre-Perf3 rows, values and ordering.')
