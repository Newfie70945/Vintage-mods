#!/usr/bin/env python3
"""Summarize raw repeated storage workload trials without capacity extrapolation."""
from __future__ import annotations
import argparse, json, statistics
from pathlib import Path


def metric(result):
    profile=result['profile'];samples=profile['samples'];before=result['beforeGeneration'];after=result['afterGeneration']
    mib=1024**2
    return {
      'generationSeconds':result['generation']['wallMs']/1000,
      'generationCpuSeconds':(after['cpuMs']-before['cpuMs'])/1000,
      'generationAllocatedMiB':(after['allocatedBytes']-before['allocatedBytes'])/mib,
      'firstSaveMs':result['saves'][0]['totalWallMs'],
      'repeatSaveMedianMs':statistics.median(x['totalWallMs'] for x in result['saves'][1:]),
      'savePreparationMedianMs':statistics.median(x['mainPreparationWallMs'] for x in result['saves']),
      'tickAverageMs':profile['tick']['averageMs'],'tickP95Ms':profile['tick']['p95Ms'],
      'tickP99Ms':profile['tick']['p99Ms'],'tickWorstMs':profile['tick']['worstMs'],
      'effectiveTps':profile['tick']['effectiveTps'],
      'peakLoadedEntities':max(x['loadedEntities'] for x in samples),
      'capturePeakRssMiB':max(x['workingSetBytes'] for x in samples)/mib,
      'capturePeakManagedMiB':max(x['managedHeapBytes'] for x in samples)/mib,
      'workAllocatedMiB':(result['afterSaves']['allocatedBytes']-before['allocatedBytes'])/mib,
      'workCpuSeconds':(result['afterSaves']['cpuMs']-before['cpuMs'])/1000,
      'workGcPauseMs':result['afterSaves']['gcPauseMs']-before['gcPauseMs'],
      'completedSnapshotReferences':result['saves'][-1]['state']['snapshotCount'],
      'trackedAfterUnloadGc':result['afterUnloadGc']['state']['trackedAlive'],
      'trackedAfterRefreshGc':result['afterRefreshGc']['state']['trackedAlive'],
      'postUnloadGcHeapMiB':result['afterUnloadGc']['state']['heapLiveEstimateBytes']/mib,
      'postRefreshGcHeapMiB':result['afterRefreshGc']['state']['heapLiveEstimateBytes']/mib,
      'heapReleasedBySnapshotRefreshMiB':(result['afterUnloadGc']['state']['heapLiveEstimateBytes']-result['afterRefreshGc']['state']['heapLiveEstimateBytes'])/mib,
    }


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    source=json.loads((args.directory/'summary.json').read_text());runs=[]
    for result in source['results']:
        if not result['passed']:raise ValueError('Invalid run cannot enter performance summary')
        runs.append(dict(trial=result['trial'],mode=result['mode'],metrics=metric(result)))
    groups={}
    for mode in ('stock','nos','storage'):
        rows=[x['metrics'] for x in runs if x['mode']==mode]
        if not rows:continue
        groups[mode]={key:{'median':statistics.median(x[key] for x in rows),'min':min(x[key] for x in rows),'max':max(x[key] for x in rows)} for key in rows[0]}
    result={'manifest':source['manifest'],'runs':runs,'aggregate':groups,'aggregation':'Per-run metrics then median across runs. Repeat-save medians exclude the first save. Tick percentiles are original integer-ms engine work samples; forced laboratory GCs occur after capture stops.','allSavesIntegrityOk':all(x['saveIntegrity']=='ok' for x in source['results'])}
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2)+'\n')
    for mode,items in groups.items():
        print(mode,json.dumps({k:v['median'] for k,v in items.items()}))
if __name__=='__main__':main()
