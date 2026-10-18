#!/usr/bin/env python3
"""Render recorded laboratory measurements; never infer player capacity."""
from __future__ import annotations
import argparse,json,statistics
from pathlib import Path


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('aggregate',type=Path);p.add_argument('--markdown',type=Path,required=True);p.add_argument('--chart',type=Path,required=True);args=p.parse_args()
    d=json.loads(args.aggregate.read_text());a=d['aggregate'];stock=a['stock'];nos=a['nos']
    rows=[('Peak incidental loaded entities','peakLoadedEntities','entities'),('Generation, 64 columns','generationSeconds','s'),('Generation CPU work','generationCpuSeconds','CPU s'),('Generation allocations','generationAllocatedMiB','MiB'),('First dirty save','firstSaveMs','ms'),('Repeated dirty saves','repeatSaveMedianMs','ms'),('Save main-thread preparation','savePreparationMedianMs','ms'),('Tick work p95','tickP95Ms','ms'),('Tick work p99','tickP99Ms','ms'),('Worst tick work','tickWorstMs','ms'),('Effective TPS','effectiveTps','ticks/s'),('Peak measured RSS','capturePeakRssMiB','MiB'),('Peak measured managed heap','capturePeakManagedMiB','MiB'),('Workload allocations','workAllocatedMiB','MiB'),('GC pause during workload','workGcPauseMs','ms'),('Snapshot entries after completed save','completedSnapshotReferences','references'),('Tracked chunks after unload + lab GC','trackedAfterUnloadGc','chunks'),('Tracked chunks after next save + lab GC','trackedAfterRefreshGc','chunks'),('Live heap after unload + lab GC','postUnloadGcHeapMiB','MiB'),('Heap released by next snapshot + lab GC','heapReleasedBySnapshotRefreshMiB','MiB')]
    def fmt(value):return f"{value['median']:.2f} ({value['min']:.2f}–{value['max']:.2f})"
    lines=['# NOS 0.5.0 generation/save performance test','', 'Official Vintage Story 1.22.7, Suite 1.2.48, shipping NOS 0.5.0 preview. Three runs per arm, identical stopped-world copies and seed, fixed 64-column generation, five dirty saves of 512 target chunks, explicit unload and snapshot-refresh checks. Values below are per-run medians with the observed run range; repeated-save numbers first take the median of saves 2–5 in each run.','', '| Measurement | Stock NOS target methods | NOS defaults | Unit |','| --- | ---: | ---: | --- |']
    for label,key,unit in rows:lines.append(f'| {label} | {fmt(stock[key])} | {fmt(nos[key])} | {unit} |')
    lines+=['','## Interpretation boundaries','', 'The stock arm retains the same Suite, NOS observer and test fixture, with NOS engine switches disabled. The NOS arm uses shipping default engine switches. This workload exercises terrain generation, saving and unloading; it does not establish entity, network or real-player performance.','', 'Natural entity spawning remained enabled, so incidental entity counts varied even with the same world copy and fixed chunk requests. This can affect serialization and tick timings; the table reports those counts. Generation and save timing ranges must be assessed alongside individual trials. Three repetitions, shared host scheduling, file caches and container storage cannot establish a small production speedup. Tick work is the official integer-millisecond pre-sleep sample. Save wall times are observed from console events; main-thread preparation and worker disk writes overlap.','', 'Explicit compacting GCs occur before capture and after forced unloads to measure live retention. They are test-only controls, not NOS behavior or production recommendations. Retained-object counts are not retained bytes; RSS includes native memory, committed heap and reusable pools. Memory after the next snapshot also includes unrelated engine/cache activity.','', f"Environment: CPU quota/period {d['manifest']['cpuQuota']} microseconds; memory cgroup limit {int(d['manifest']['memoryLimit'])/2**30:.0f} GiB. The shared host was not reserved exclusively. World-generation and physics thread counts match the supplied stopped template. All recorded saves passed SQLite integrity checks: {d['allSavesIntegrityOk']}. No authenticated clients or bots were used.", '', 'The raw JSON contains per-run observations and exact world/artifact hashes. The runner and fixture are included in source for reproduction. Production TPS and player-capacity claims require long real-player captures on the target modpack.','']
    args.markdown.parent.mkdir(parents=True,exist_ok=True);args.markdown.write_text('\n'.join(lines))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    panels=[('Generation wall time','generationSeconds','seconds'),('Repeated dirty save','repeatSaveMedianMs','milliseconds'),('Tick work p99','tickP99Ms','milliseconds'),('Peak measured RSS','capturePeakRssMiB','MiB'),('Generation allocations','generationAllocatedMiB','MiB'),('Tracked chunks after unload + lab GC','trackedAfterUnloadGc','chunks')]
    fig,axes=plt.subplots(2,3,figsize=(14,8));colors=['#78828c','#137e79']
    for ax,(title,key,unit) in zip(axes.flat,panels):
        for idx,mode in enumerate(('stock','nos')):
            vals=[r['metrics'][key] for r in d['runs'] if r['mode']==mode];med=statistics.median(vals)
            ax.bar(idx,med,width=.58,color=colors[idx],alpha=.55)
            jitter=[(i-(len(vals)-1)/2)*.08 for i in range(len(vals))]
            ax.scatter([idx+j for j in jitter],vals,c=colors[idx],s=38,zorder=3,edgecolors='black',linewidths=.4)
        ax.set_title(title,fontsize=11);ax.set_ylabel(unit);ax.set_xticks([0,1],['Stock methods','NOS 0.5.0']);ax.set_ylim(bottom=0);ax.grid(axis='y',alpha=.2);ax.set_axisbelow(True);ax.spines[['top','right']].set_visible(False)
    fig.suptitle('NOS generation/save laboratory comparison — three runs per arm',fontsize=15)
    fig.text(.5,.015,'Bars: medians. Dots: individual runs. Same Suite and observer in both arms. No clients/bots; no production TPS claim.\nForced GC applies only to the retention check and is excluded from tick capture.',ha='center',fontsize=10)
    fig.tight_layout(rect=(0,.07,1,.95));args.chart.parent.mkdir(parents=True,exist_ok=True);fig.savefig(args.chart,dpi=160);plt.close(fig)
if __name__=='__main__':main()
