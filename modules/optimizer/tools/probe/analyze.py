#!/usr/bin/env python3
"""Inspect Lithos Probe schema 2 captures locally; never treat one capture as an A/B trial."""
from pathlib import Path
import argparse,gzip,hashlib,json,collections,statistics
MAX=64*1024*1024

def analyze(path):
 data=path.read_bytes()
 if len(data)>MAX:raise ValueError('Compressed input exceeds 64 MiB')
 if data[:2]==b'\x1f\x8b':
  with gzip.open(path,'rb') as f:raw=f.read(MAX+1)
 else:raw=data
 if len(raw)>MAX:raise ValueError('Decompressed capture exceeds 64 MiB')
 d=json.loads(raw)
 if d.get('kind')!='lithos-probe' or d.get('schema')!=2:raise ValueError('Expected Lithos Probe schema 2')
 profile=d.get('profile',{});counts=collections.Counter();maincounts=collections.Counter()
 for thread in profile.get('threads',[]):
  pending=[(x,0) for x in thread.get('children',[])];visited=0
  while pending:
   node,depth=pending.pop();visited+=1
   if depth>512 or visited>1000000:raise ValueError('Profile tree exceeds inspection bounds')
   name=node.get('full',node.get('name','unknown'));samples=node.get('selfManaged',node.get('self',0))
   counts[name]+=samples
   if 'ServerMain.Process' in thread.get('name',''):maincounts[name]+=samples
   pending.extend((x,depth+1) for x in node.get('children',[]))
 tiers=d.get('series',{}).get('tiers',[]);series={}
 if tiers:
  tier=min(tiers,key=lambda x:x.get('spanSeconds',999999));times=tier.get('times',[])
  for name,values in tier.get('values',{}).items():
   numbers=[x for x in values if isinstance(x,(int,float)) and not isinstance(x,bool)]
   if numbers:series[name]={'samples':len(numbers),'last':numbers[-1],'min':min(numbers),'max':max(numbers),'median':statistics.median(numbers)}
 return {'sourceSha256':hashlib.sha256(data).hexdigest(),'kind':'nos-external-probe-analysis','sourceKind':d['kind'],'generatedAt':d.get('generatedAt'),
  'server':d.get('server'),'mods':d.get('mods'),'census':d.get('census'),'windows':d.get('windows'),
  'samplingDurationSeconds':profile.get('durationSeconds'),'managedSamples':profile.get('managedSamples'),
  'stratumRuntimeInStacks':any('StratumServer' in k for k in counts),'topSelfManagedSamples':[{'method':k,'samples':v} for k,v in counts.most_common(20)],
  'topMainThreadSelfManagedSamples':[{'method':k,'samples':v} for k,v in maincounts.most_common(20)],'seriesSummary':series,
  'limits':['Third-party supplied capture; workload and hardware not reproduced here.','Sample counts are not method call counts or exclusive CPU durations.','No matched official-server baseline or NOS run exists in this file.','This capture cannot establish relative performance between NOS and Stratum.']}
if __name__=='__main__':
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('capture',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
 report=analyze(args.capture);args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps({'census':report['census'],'windows':report['windows'],'stratumRuntimeInStacks':report['stratumRuntimeInStacks']},indent=2))
