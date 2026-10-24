#!/usr/bin/env python3
"""Summarize completed modpack laboratory runs without turning them into player-capacity claims."""
import argparse,json,statistics
from pathlib import Path


def rate(before,after,seconds,key,scale=1):
    return (after[key]-before[key])/seconds/scale


def observations(result, samples):
    a,b,s=result['entityBefore'],result['entityAfter'],result['entityWallSeconds']
    window=result['entityWindow'];peak=max(x['rssBytes']for x in samples if window['start']<=x['time']<=window['end'])
    return dict(entityPeakRssMiB=peak/1024**2,entityEndCommittedMiB=b['gcCommittedBytes']/1024**2,entityEndFragmentedMiB=b['gcFragmentedBytes']/1024**2,generationSeconds=result['generation']['wallMs']/1000,
        entityTickAverageMs=b['tickAverageMs'],entityTickP95Ms=b['tickP95Ms'],entityTickP99Ms=b['tickP99Ms'],entityTickWorstMs=b['tickWorstMs'],entityTps=b['tickCount']/s,
        entityCpuPercent=rate(a,b,s,'cpuMs',10),entityAllocMiBPerSecond=rate(a,b,s,'allocatedBytes',1024**2),entityGcPauseMsPerSecond=rate(a,b,s,'gcPauseMs'),
        entityEndRssMiB=b['rssBytes']/1024**2,entityEndHeapMiB=b['heapBytes']/1024**2,entityEndActive=b['active'],otherLoadedEntities=b['loaded']-b['spawned'],
        movements=b['moves']-a['moves'],taskStarts=b['taskStarts']-a['taskStarts'],
        firstSaveMs=result['saves'][0]['totalWallMs'],repeatSaveMs=statistics.median(x['totalWallMs']for x in result['saves'][1:]),
        savePreparationMs=statistics.median(x['mainPreparationWallMs']for x in result['saves']),
        unloadedTrackedAlive=result['afterUnloadGc']['state']['trackedAlive'],saveIntegrity=result['saveIntegrity'])


def summary(values):
    return dict(median=statistics.median(values),minimum=min(values),maximum=max(values),runs=len(values))


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);args=p.parse_args()
    directory=args.directory;raw=json.loads((directory/'summary.json').read_text());results=raw['results']
    if not results or any(not r.get('passed')for r in results):raise ValueError('Only completed passing comparisons may be summarized.')
    measured={r['trial']:observations(r,json.loads((directory/f"{r['trial']:02d}-{r['mode']}"/'process-samples.json').read_text()))for r in results}
    if not {'stock','nos'}.issubset({r['mode']for r in results}):raise ValueError('This report requires both stock and NOS arms; setup-only runs are not comparisons.')
    groups={}
    for mode in sorted(set(r['mode']for r in results)):
        rows=[measured[r['trial']]for r in results if r['mode']==mode]
        groups[mode]={key:summary([r[key]for r in rows])for key in rows[0]if isinstance(rows[0][key],(float,int))}
    profiler={}
    for mode in ('off','on'):
        per_run=[]
        for r in results:
            windows=[pair[mode]for pair in r['profilerPairs']]
            per_run.append({
                'tickAverageMs':statistics.median(w['after']['tickAverageMs']for w in windows),
                'tickP95Ms':statistics.median(w['after']['tickP95Ms']for w in windows),
                'allocationMiBPerSecond':statistics.median(rate(w['before'],w['after'],w['wallSeconds'],'allocatedBytes',1024**2)for w in windows),
                'cpuPercent':statistics.median(rate(w['before'],w['after'],w['wallSeconds'],'cpuMs',10)for w in windows)})
        profiler[mode]={key:summary([r[key]for r in per_run])for key in per_run[0]}
    analysis=dict(groups=groups,profilerWindows=profiler,perRun=[dict(trial=r['trial'],mode=r['mode'],**measured[r['trial']])for r in results],
        scope='Disposable official server, identical stopped seed/world copies, same full Suite and selected mods in both arms; NOS engine switches off versus shipping defaults. No authenticated clients or player-capacity evidence.',
        caveats=['Two runs per arm are descriptive observations, not significance or production guarantees.',
          'Tick work is the official integer-millisecond pre-sleep sample, observed once per tick. It is not the frame-profiler root duration and cannot resolve small sub-millisecond differences.',
          'NPCs are forced-active and fixture-locally-tracked for stock physics without clients; pass-through AI subscribers are test controls. Modules not exercised by this workload require their separate native-method fixtures; activation alone is not measured benefit.',
          'World/NPC randomness, shared 2-vCPU container scheduling, caches and incidental entities remain confounders.',
          'Profiler on windows are four seconds containing one requested three-second automatic-style capture and its finalization; off windows are four seconds without it. They alternate order, and are much more frequent than the production 300-second cooldown.',
          'No authenticated players are connected; online gameplay, webpanel polling, client graphics, network delivery and every loaded feature are not exercised.',
          'Compacting GC/unload calls are retention measurements outside the steady phase, not optimizer behavior.'])
    (directory/'analysis.json').write_text(json.dumps(analysis,indent=2)+'\n')
    def show(group,key):
        x=group[key];return f"{x['median']:.2f} ({x['minimum']:.2f}–{x['maximum']:.2f})"
    metrics=[('Generation, 64 columns','generationSeconds','s'),('Steady tick average','entityTickAverageMs','ms'),('Steady tick p95','entityTickP95Ms','ms'),('Steady tick p99','entityTickP99Ms','ms'),('Worst steady tick','entityTickWorstMs','ms'),('Effective steady ticks/s','entityTps','ticks/s'),('Other loaded entities at steady end','otherLoadedEntities','entities'),('Steady CPU','entityCpuPercent','% of one core'),('Steady allocation','entityAllocMiBPerSecond','MiB/s'),('Steady GC pause','entityGcPauseMsPerSecond','ms/s'),('Steady peak RSS','entityPeakRssMiB','MiB'),('Steady end RSS','entityEndRssMiB','MiB'),('Steady end GC committed','entityEndCommittedMiB','MiB'),('Steady end GC fragmented','entityEndFragmentedMiB','MiB'),('Steady end managed estimate','entityEndHeapMiB','MiB'),('First dirty save','firstSaveMs','ms'),('Repeated dirty saves','repeatSaveMs','ms'),('Save main-thread preparation','savePreparationMs','ms'),('Tracked chunk references after unload + lab GC','unloadedTrackedAlive','chunks')]
    optimizerArtifact=next((name for name in raw['manifest']['artifacts'] if name.startswith('NewfiesServerOptimizer-')), 'Unknown optimizer')
    version=optimizerArtifact.removeprefix('NewfiesServerOptimizer-').removesuffix('.zip')
    settingsLabel='NOS recorded settings' if 'optimizerConfiguration' in raw['manifest'] else 'NOS defaults'
    lines=[f'# NOS {version} selected-modpack laboratory comparison','',f"{len(results)} passing runs; order: {', '.join(raw['manifest']['order'])}. {raw['manifest']['entitySeconds']} seconds of steady moving-NPC AI/physics per run. Each value is the median of per-run observations, followed by the observed range. This is not a player-capacity benchmark.",'',f'| Measurement | NOS switches off | {settingsLabel} | Unit |','| --- | ---: | ---: | --- |']
    for title,key,unit in metrics:lines.append(f"| {title} | {show(groups['stock'],key)} | {show(groups['nos'],key)} | {unit} |")
    if raw['manifest'].get('blockWorkload'):
        lines+=['','## Mixed block workload','','Each arm placed 100 mismatch-input barrels and 100 firepits (50 initialized burning, 50 cold). These observations do not establish successful fermentation or active cooking performance.','', '| Arm | Barrel callbacks in steady window | Firepit callbacks in steady window |','| --- | ---: | ---: |']
        for r in results:
            a,b=r['entityBefore'],r['entityAfter']
            lines.append(f"| {r['trial']} {r['mode']} | {b['barrelTicks']-a['barrelTicks']} | {b['firepitTicks']-a['firepitTicks']} |")
    lines+=['','## Engine-profiler cost windows','','Per-run medians across paired windows, then median/range across all runs. Each on window contains a 3s capture within about 4s, including completion work. This measures the current optimized Suite profiler, not the old implementation.','','| Measurement | Off windows | Capture windows |','| --- | ---: | ---: |']
    for title,key in [('Tick average, ms','tickAverageMs'),('Tick p95, ms','tickP95Ms'),('Allocation, MiB/s','allocationMiBPerSecond'),('CPU, % of one core','cpuPercent')]:lines.append(f"| {title} | {show(profiler['off'],key)} | {show(profiler['on'],key)} |")
    if any('validationCorrection'in r for r in results):lines+=['','Validation bookkeeping note: the first stock run completed its workload and integrity checks, but an inner profiler-window variable shadowed the arm identifier in the terminal module-state assertion. The checker was fixed before the NOS runs. The original terminal record is retained, and the recorded stock state/exit/integrity/error/tick/activity evidence was independently revalidated without changing any measured values. See validationCorrection and result.original-terminal.json.']
    if (directory/'environment-interruption.json').exists():lines+=['','Environment note: execution reconnected after the third completed trial and interrupted the fourth launch. Only the final baseline was restarted from the stopped world copy after input-hash validation. The quota remained 2 vCPU / 8 GiB, but physical-host identity was not recorded before reconnect. Small cross-run timing/CPU differences are not evidence of a causal gain. See environment-interruption.json.']
    lines+=['','## Limits','']+['- '+c for c in analysis['caveats']]+['','All run databases passed SQLite integrity checks. See raw result JSON, process samples, enabled-mod inventory and artifact/world hashes for reproduction. No benchmark fixture belongs in a production Mods folder.','']
    (directory/'report.md').write_text('\n'.join(lines));print(json.dumps(analysis['groups'],indent=2))


if __name__=='__main__':main()
