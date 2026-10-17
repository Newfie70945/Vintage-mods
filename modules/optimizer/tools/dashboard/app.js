"use strict";
const el = id => document.getElementById(id);
let evidence = null, selected = null, samples = [], points = [], busy = false, lastRead = 0;
const number = value => typeof value === "number" && Number.isFinite(value);
const fmt = (value, digits = 1) => number(value) ? value.toLocaleString(undefined, {maximumFractionDigits: digits}) : "Unavailable";
function node(tag, text, parent) {const item = document.createElement(tag); item.textContent = text; if (parent) parent.append(item); return item;}
function change(value) {return number(value) ? `${value > 0 ? "+" : ""}${fmt(value)}%` : "Unavailable";}
function selectedTrial() {
  if (!evidence) return null;
  if (evidence.kind === "newfies-optimizer-profile") return {id: "Selected profile", profiler: evidence, status: "profile only"};
  return (evidence.trials || []).find(trial => trial.id === el("trial").value) || (evidence.trials || [])[0];
}
function render() {
  selected = selectedTrial();
  const profile = selected?.profiler || {}, tick = profile.tick || {};
  samples = profile.samples || [];
  if (!Array.isArray(samples)) samples = [];
  el("context").textContent = `${evidence.gameVersion || evidence.campaign?.gameVersion || "Game version unavailable"} · ${evidence.campaign?.scenario || "Scenario unavailable"} · ${selected?.id || "No trial"}`;
  el("notice").textContent = evidence.synthetic ? "SYNTHETIC TEST FIXTURE — these values are not measured server results." : "Performance claims require matched real players, completed work, correctness and repeated trials.";
  const last = samples[samples.length - 1] || {};
  const cards = [["Tick average", tick.averageMs, "ms"], ["Tick P95 / P99", `${fmt(tick.p95Ms)} / ${fmt(tick.p99Ms)}`, "ms"],
    ["Effective TPS", tick.effectiveTps, "observed"], ["Process CPU", last.processCpuPercent, "% · may exceed 100"],
    ["Managed heap", number(last.managedHeapBytes) ? last.managedHeapBytes / 1048576 : null, "MiB"],
    ["Connected players", last.players, `verified workload peak: ${fmt(selected?.workload?.actualConnectedPeak, 0)}`],
    ["Working set", number(last.workingSetBytes) ? last.workingSetBytes / 1048576 : null, "MiB"],
    ["Loaded entities", last.loadedEntities, "entities"], ["Loaded chunks", last.loadedChunks, "chunks"],
    ["GC pause delta", last.gcPauseMsDelta, "ms · last retained sample"],
    ["Outbound delta", number(last.networkSentBytesDelta) ? last.networkSentBytesDelta / 1024 : null, "KiB · last sample"],
    ["Save duration", last.saveDurationMs, "ms · unavailable unless measured"]];
  el("metrics").replaceChildren();
  for (const [name, value, unit] of cards) {const card = node("div", "", el("metrics")); card.className="card"; node("p",name,card);node("strong",typeof value === "string" ? value : fmt(value),card);node("small",unit,card);}
  el("comparisons").replaceChildren();
  for (const item of evidence.comparisons || []) {const row=node("tr","",el("comparisons"));node("td",`${item.baseline} → ${item.candidate}`,row);node("td",item.purpose,row);
    for(const metric of ["averageMs","p95Ms","p99Ms"]){const value=item.medianOfTrialPercentChange?.[metric];const td=node("td",change(value),row);td.className=number(value)?(value<=0?"negative":"positive"):"";}
    node("td",[item.conclusion,...(item.blockingReasons||[])].join(" "),row);}
  if (!(evidence.comparisons || []).length) node("td","No controlled comparisons in this profile.",node("tr","",el("comparisons")));
  el("hotspots").replaceChildren();
  for(const hotspot of [...(profile.hotspots || [])].sort((a,b)=>(b.totalMs||0)-(a.totalMs||0)).slice(0,30)){const row=node("tr","",el("hotspots"));node("td",hotspot.name||"Unknown",row);node("td",fmt(hotspot.totalMs),row);node("td",fmt(hotspot.sampledCalls,0),row);}
  if(!(profile.hotspots || []).length)node("td","No measured hotspots available.",node("tr","",el("hotspots")));
  el("modules").replaceChildren();for(const module of profile.modules || [])node("li",`${module.name}: ${module.status} · ${module.reason || ""}`,el("modules"));
  el("limits").replaceChildren();for(const limit of [...(evidence.notes || []),...(selected?.missingMetrics || []),...(selected?.failures || []),...(profile.capabilities || []).filter(c=>c.available===false).map(c=>`${c.name}: ${c.reason}`)])node("li",limit,el("limits"));
  draw();
}
function metricValue(sample) {const key=el("metric").value,value=sample[key];if(!number(value))return null;if(key.endsWith("Bytes"))return value/1048576;if(key==="networkSentBytesDelta")return value/1024;return value;}
function draw() {
  const canvas=el("chart"),ctx=canvas.getContext("2d"),width=canvas.clientWidth,height=320,ratio=window.devicePixelRatio||1;
  canvas.width=width*ratio;canvas.height=height*ratio;ctx.scale(ratio,ratio);ctx.clearRect(0,0,width,height);
  let start=Math.min(+el("from").value,+el("to").value),end=Math.max(+el("from").value,+el("to").value);
  const view=samples.slice(Math.floor(samples.length*start/100),Math.max(Math.floor(samples.length*start/100)+1,Math.ceil(samples.length*end/100)));
  const values=view.map(metricValue).filter(number);el("chart-title").textContent=el("metric").selectedOptions[0].textContent;
  el("chart-limit").textContent=`${values.length} available / ${view.length} samples · gaps remain gaps${selected?.profiler?.dashboardHistoryDownsampled ? " · dashboard preview downsampled; full history in trial.json" : ""}`;
  points=[];el("history").replaceChildren();
  if(!values.length){ctx.fillStyle="#91a4b4";ctx.font="15px system-ui";ctx.fillText("No measurements available for this metric.",25,145);return;}
  const min=Math.min(0,...values),max=Math.max(...values,min+1),pad={left:70,right:25,top:20,bottom:40},plotW=Math.max(1,width-pad.left-pad.right),plotH=height-pad.top-pad.bottom;
  ctx.strokeStyle="#293848";ctx.fillStyle="#91a4b4";ctx.font="11px system-ui";
  for(let n=0;n<=4;n++){const y=pad.top+n*plotH/4;ctx.beginPath();ctx.moveTo(pad.left,y);ctx.lineTo(width-pad.right,y);ctx.stroke();ctx.fillText(fmt(max-n*(max-min)/4),5,y+4);}
  ctx.strokeStyle="#69d6b9";ctx.lineWidth=2;ctx.beginPath();let connected=false;
  view.forEach((sample,index)=>{const value=metricValue(sample);if(!number(value)){connected=false;return;}const x=pad.left+index*plotW/Math.max(1,view.length-1),y=pad.top+(max-value)*plotH/(max-min);if(connected)ctx.lineTo(x,y);else ctx.moveTo(x,y);connected=true;points.push({x,y,sample,value});});ctx.stroke();
  if(points.length===1){ctx.beginPath();ctx.arc(points[0].x,points[0].y,3,0,Math.PI*2);ctx.fillStyle="#69d6b9";ctx.fill();}
  ctx.fillStyle="#91a4b4";ctx.fillText("Sample order · export contains exact timestamps",pad.left,height-8);
  for(const sample of view.slice(-300)){const row=node("tr","",el("history"));node("td",sample.timestampUtc||fmt(sample.elapsedSeconds),row);node("td",fmt(metricValue(sample)),row);}
}
el("chart").addEventListener("pointermove",event=>{const rect=event.currentTarget.getBoundingClientRect(),x=event.clientX-rect.left;let best=null;for(const point of points)if(!best||Math.abs(point.x-x)<Math.abs(best.x-x))best=point;el("tooltip").textContent=best?`${best.sample.timestampUtc || `${fmt(best.sample.elapsedSeconds)} sec`} · ${fmt(best.value)}`:"No sample";});
async function refresh(){if(busy||Date.now()-lastRead<2000)return;busy=true;lastRead=Date.now();try{const response=await fetch("/api/report",{cache:"no-store"});if(!response.ok)throw Error(`Report unavailable (${response.status})`);evidence=await response.json();const value=el("trial").value;el("trial").replaceChildren();for(const trial of evidence.trials||[{id:"Selected profile"}]){const option=node("option",`${trial.id} · ${trial.status||""}`,el("trial"));option.value=trial.id;}if([...el("trial").options].some(o=>o.value===value))el("trial").value=value;render();}catch(error){el("notice").textContent=error.message;}finally{busy=false;}}
el("trial").addEventListener("change",render);el("metric").addEventListener("change",draw);for(const id of ["from","to"])el(id).addEventListener("input",draw);el("refresh").addEventListener("click",refresh);window.addEventListener("resize",draw);
el("export-csv").addEventListener("click",()=>{const fields=["timestampUtc","elapsedSeconds",el("metric").value],quote=value=>`"${String(value??"").replaceAll('"','""')}"`,csv=[fields.join(","),...samples.map(sample=>fields.map(field=>quote(sample[field])).join(","))].join("\n"),url=URL.createObjectURL(new Blob([csv],{type:"text/csv;charset=utf-8"}));const a=node("a","");a.href=url;a.download="nos-history.csv";a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
setInterval(()=>{if(!document.hidden)refresh();},5000);refresh();
