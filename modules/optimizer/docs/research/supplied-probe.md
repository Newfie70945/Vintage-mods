# Supplied Lithos Probe capture

Source: owner-supplied `lithosprobe-20260930-125523.json.gz`; SHA-256 and extracted metrics are in `supplied-probe-summary.json`. The raw uploaded capture is not redistributed in this repository.

The capture reports game 1.22.7, .NET 10.0.12, Debian 13, 32 logical processors, server GC, 2,000 players, 512 loaded chunks and 2,463 entities at capture time. StratumServer frames are present even though the mod list contains only vanilla modules and Lithos Probe.

The final 60-second window reports **10.43 TPS**, mean tick work **95.16 ms**, P95 **225.99 ms**, maximum **564.86 ms**. The 5-minute window reports 22.20 TPS; its longer period includes lower-load stages, so it is not interchangeable with the final-minute result. Only one of the three referenced captures was uploaded here.

The time series reaches roughly **1,478 MB/s reported allocation**, **19,612 MB managed heap** and **22,017 MB working set** during the ramp. Those are workload samples, not retained-object or leak proof. The CPU metric’s normalisation should be verified before comparing it with a hosting panel.

Main-thread and worker stacks point toward player/entity tracking and interaction checks, spawn work, compression, allocation/GC and synchronization. A prominent `EntityPlayer.IsInteractable` frame can include inlined callees; its sample count is not a getter invocation count. The official `PlayerByUid` is already a dictionary lookup, so this frame is not evidence for inventing a linear-search fix.

With only 512 loaded chunks, this capture does not isolate terrain generation as the principal bottleneck. A stronger next profiling target is `PhysicsManager.UpdateTrackedEntityLists`: determine per-player traversal cost, candidate density and tracking churn on the official server before designing a spatial-index change. Visibility, dimensions, removals and packet-order equivalence need scenario tests.

This is third-party evidence of an extreme workload. There is no matched official-server baseline, NOS run, hardware control or reproduced bot workload in this file. It cannot support a relative performance claim or a 2,000-player capacity claim for NOS.

Re-analyse a local capture with:

```sh
python3 tools/probe/analyze.py /path/to/capture.json.gz --output /path/to/summary.json
```
