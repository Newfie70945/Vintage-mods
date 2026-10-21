"""Exercise production retention SQL against late-imported expired history.
Counts SQLite VM work rather than relying on CI wall-clock timing.
"""
from pathlib import Path
import re,sqlite3
source=(Path(__file__).resolve().parents[1]/'src/Services/LogStorageService.cs').read_text()
section=source.split('public Task<int> PruneAsync',1)[1].split('public ',1)[0]
queries=re.findall(r'CommandText = @"(DELETE FROM .*?)";',section,re.S)
assert len(queries)==3
for sql in queries:
 table=re.search(r'DELETE FROM (\w+)',sql)[1];time='window_end_ticks' if table=='entity_activity' else 'timestamp_ticks'
 db=sqlite3.connect(':memory:');db.execute(f'CREATE TABLE {table}(id INTEGER PRIMARY KEY,{time} INTEGER)');db.execute(f'CREATE INDEX idx_time ON {table}({time} DESC)')
 # Recent low IDs precede a late import of expired rows. ID order must not
 # require walking the retained history to find the expired time range.
 db.executemany(f'INSERT INTO {table} VALUES(?,?)',((i,1000) for i in range(1,50001)))
 db.executemany(f'INSERT INTO {table} VALUES(?,?)',((i,1) for i in range(50001,51001)))
 steps=[0]
 def progress():steps[0]+=1;return 0
 db.set_progress_handler(progress,1)
 old=sql.replace(f'ORDER BY {time}', 'ORDER BY id');db.execute('SAVEPOINT baseline');db.execute(old,{'cutoff':10});old_work=steps[0];db.execute('ROLLBACK TO baseline');db.execute('RELEASE baseline')
 steps[0]=0;db.execute(sql,{'cutoff':10});new_work=steps[0];db.set_progress_handler(None,0)
 limit=int(re.search(r'LIMIT (\d+)',sql)[1]);assert db.execute(f'SELECT count(*) FROM {table} WHERE {time}>=10').fetchone()[0]==50000
 assert db.execute(f'SELECT count(*) FROM {table} WHERE {time}<10').fetchone()[0]==1000-limit
 assert new_work < old_work/5,(table,new_work,old_work)
 print(f'PASS {table}: {old_work:,} → {new_work:,} SQLite VM steps; retained rows intact, deletion limit {limit} respected')
 db.close()
