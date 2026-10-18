from pathlib import Path
import sqlite3,re,json
s=(Path(__file__).resolve().parents[1]/'src/Services/LogStorageService.cs').read_text()
section=s.split('QueryRollbackRecoveryAsync',1)[1].split('public Task<List<ContainerSnapshotRecord>>',1)[0]
sql={name:re.search(name+r'\.CommandText\s*=\s*@"(.*?)";',section,re.S).group(1) for name in ['resolve','cmd','snapshots']}
c=sqlite3.connect(':memory:')
for name in ['events','snapshots']:
 c.executescript(re.search(r'CREATE TABLE IF NOT EXISTS '+name+r' \(.*?\);',s,re.S).group(0))
for index in ['idx_events_world_dim_pos_time','idx_events_world_time','idx_events_player_uid_time','idx_events_player_name_time','idx_snapshots_world_dim_pos_time']:
 match=re.search(r'CREATE INDEX IF NOT EXISTS '+index+r'\s.*?;',s,re.S)
 assert match,index
 c.executescript(match.group(0))
def event(i,t,uid='a',name='ExactName',world='w',dim='0',x=1,action=1,meta=None,y=100,z=1):
 c.execute('INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,player_name,world_id,dimension,x,y,z,block_code,meta_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)',(i,t,'',action,uid,name,world,dim,x,y,z,'game:stone',json.dumps(meta or {})))
def snapshot(i,t,dim='0',x=1):
 c.execute('INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,player_uid,player_name,inventory_id,reason,slot_count,non_empty_slot_count,total_item_count,slots_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',(i,t,'','w',dim,x,100,1,'game:chest-east','a','ExactName','chest','open',16,0,0,'[]'))
event(1,100);event(2,101);event(3,101);event(4,130,uid='other',name='Other')
event(5,115,world='elsewhere');event(6,115,uid='a',name='ExactName2',x=99)
event(7,120,uid='b',name='Different',action=9,meta={'sourcePlayerUid':'a','sourcePlayerName':'ExactName','entityId':'123'})
event(8,120,uid='a',name='ExactName',dim='overworld')
event(9,70,uid='other',name='Other');event(10,50,uid='other',name='Other')
snapshot(1,60);snapshot(2,95);snapshot(3,115);snapshot(4,200,dim='overworld');snapshot(5,190,x=400)
a={'world':'w','from':100,'to':120,'target':'ExactName','uid':'a','area':0,'dimension':'0','cx':0,'cy':0,'cz':0,'minX':0,'maxX':0,'minY':0,'maxY':0,'minZ':0,'maxZ':0,'r2':0}
assert [r[0] for r in c.execute(sql['resolve'],a)]==['a']
assert list(c.execute(sql['resolve'],dict(a,target='Exact'))) == []
assert list(c.execute(sql['resolve'],dict(a,target="%' OR 1=1 --"))) == []
r=c.execute(sql['cmd'],a).fetchall();ids=[x[12] for x in r]
assert 4 in ids,'later other edit must be retained for conflict detection'
assert 5 not in ids,'cross-world rows must be excluded'
assert ids.index(3)<ids.index(2),'timestamp ties use descending database IDs'
assert 7 in ids,'killer-attributed death must be available'
assert 8 in ids,'legacy overworld representation must be matched'
assert 9 not in ids and 10 not in ids,'history before latest pre-window baseline must be excluded'
event(11,97,uid='other',name='Other')
assert 11 in [x[12] for x in c.execute(sql['cmd'],a)],'pre-window delta after baseline is needed for reconstructing inventory'
sn=c.execute(sql['snapshots'],a).fetchall();assert {x[17] for x in sn}=={2,3,4},[x[17] for x in sn]
print('PASS: actual SQL exact identity/injection resistance, world isolation, ID order, late conflicts, killer attribution, legacy dimensions and pre-window baselines.')

# Area scope is applied to affected-cell selection, with later conflicts retained at those cells.
c.execute('DELETE FROM events');c.execute('DELETE FROM snapshots')
event(20,105,uid='a',x=10,z=10);event(21,110,uid='b',x=10,z=10)
event(22,150,uid='later',x=10,z=10)
event(23,110,x=15,z=10);event(24,110,x=16,z=10)
event(25,110,x=14,z=14);event(26,110,dim='1',x=10,z=10)
event(27,110,world='other',x=10,z=10);event(28,110,dim='overworld',x=10,z=10)
event(29,110,uid='',action=9,x=10,z=10,meta={'entityId':'99'})
event(30,110,action=9,x=16,z=10);event(31,90,x=10,z=10)
# A pre-window inventory baseline and an intervening legitimate delta remain available.
snapshot(10,80,x=10);c.execute('UPDATE snapshots SET z=10 WHERE id=10')
event(32,95,uid='owner',action=6,x=10,z=10)
snapshot(11,115,x=10);c.execute('UPDATE snapshots SET z=10 WHERE id=11')
snapshot(12,115,x=16)
area=dict(a,area=1,uid='',cx=10,cy=100,cz=10,minX=5,maxX=15,minY=95,maxY=105,minZ=5,maxZ=15,r2=25)
ids={row[12] for row in c.execute(sql['cmd'],area)}
assert {20,21,22,23,28,29,31,32}.issubset(ids),ids
assert not ids.intersection({24,25,26,27,30}),ids
assert {row[17] for row in c.execute(sql['snapshots'],area)}=={10,11}
# No actor resolution is needed; unknown and empty actors are still recoverable area activity.
event(33,110,uid='',x=11,z=10)
assert 33 in {row[12] for row in c.execute(sql['cmd'],area)}
assert list(c.execute(sql['cmd'],dict(area,cx=1000,minX=995,maxX=1005)))==[]
print('PASS: area SQL all actors, spherical boundaries, dimensions/world, natural deaths, pre-window baselines and retained post-window conflicts.')

# Radius zero is the single-block scope, applied before history caps; never a radius-one alias.
exact=dict(area,minX=10,maxX=10,minY=100,maxY=100,minZ=10,maxZ=10,r2=0)
ids={row[12] for row in c.execute(sql['cmd'],exact)}
assert {20,21,22,28,29,31,32}.issubset(ids), ids
assert not ids.intersection({23,24,25,26,27,30,33}), ids
assert {row[17] for row in c.execute(sql['snapshots'],exact)}=={10,11}
assert not list(c.execute(sql['cmd'],dict(exact,dimension='2')))
print('PASS: exact single-cell SQL excludes neighboring blocks/dimensions/worlds and retains all actors, inventory baselines and later conflicts.')
