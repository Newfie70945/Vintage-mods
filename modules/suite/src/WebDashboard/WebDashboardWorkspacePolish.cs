namespace BlockLogger.WebDashboard;

internal static class WebDashboardWorkspacePolish
{
    public const string Script = """
// Presentation-only tools: no timers, polling, scans or automatic writes.
function stablePanel(id){
  const host=$(id);const cache=stablePanel.cache||(stablePanel.cache=new WeakMap());
  if(!host)return {set html(value){}};
  if(!cache.has(host))cache.set(host,{last:null,set html(value){
    value=String(value??'');if(value===this.last)return;this.last=value;
    if(host.tagName!=='TBODY'){host.innerHTML=value;return;}
    const template=document.createElement('template');template.innerHTML='<table><tbody>'+value+'</tbody></table>';
    const incoming=[...template.content.querySelector('tbody').children],existing=new Map();
    const key=(r,i)=>r.dataset.panelKey||r.dataset.perfIncident||r.dataset.rowKey||String(i);
    [...host.children].forEach((r,i)=>existing.set(key(r,i),r));
    const scroll=host.closest('.tableWrap'),top=scroll?.scrollTop,left=scroll?.scrollLeft;
    incoming.forEach((r,i)=>{const old=existing.get(key(r,i));
      // Labels and sort adornments are presentation state, not row data.
      const signature=r.innerHTML,marks=stablePanel.rowMarkup||(stablePanel.rowMarkup=new WeakMap());let row=old;
      if(!row||marks.get(row)!==signature){row=r;marks.set(row,signature);if(old)old.remove();}
      if(host.children[i]!==row)host.insertBefore(row,host.children[i]||null);
      existing.delete(key(r,i));
    });
    for(const row of existing.values())row.remove();
    const sort=host.closest('table')?.querySelector('thead th[data-sort-dir]');
    if(sort?.onclick){const direction=sort.dataset.sortDir;sort.dataset.sortDir=direction==='asc'?'desc':'asc';sort.onclick();}
    if(scroll){scroll.scrollTop=top;scroll.scrollLeft=left;}
  }});
  return cache.get(host);
}

function panelDialog({title='Confirm action',message='',details='',input=false,value='',requiredText='',danger=false,accept='Continue'}={}){
  const run=()=>new Promise(resolve=>{
    const prior=document.activeElement,dialog=document.createElement('dialog');dialog.className='panelActionDialog';
    dialog.setAttribute('aria-label',title);
    dialog.innerHTML='<form method="dialog"><h3></h3><p></p><pre class="panelChangePreview"></pre><label class="panelDialogInput">Response <input autocomplete="off"></label><div class="rowActions"><button value="cancel">Cancel</button><button value="apply" class="primary">Continue</button></div></form>';
    dialog.querySelector('h3').textContent=title;dialog.querySelector('p').textContent=message;
    const cancel=dialog.querySelector('[value=cancel]');cancel.type='button';cancel.onclick=()=>dialog.close('cancel');
    const pre=dialog.querySelector('pre');pre.textContent=details;pre.hidden=!details;
    const field=dialog.querySelector('input'),label=dialog.querySelector('label'),yes=dialog.querySelector('[value=apply]');
    label.hidden=!input;field.value=value;yes.textContent=accept;yes.classList.toggle('danger',danger);
    if(requiredText){label.firstChild.textContent='Type '+requiredText+' to continue ';yes.disabled=field.value!==requiredText;field.oninput=()=>yes.disabled=field.value!==requiredText;}
    let complete=false;const finish=answer=>{if(complete)return;complete=true;dialog.remove();if(prior?.isConnected)prior.focus({preventScroll:true});resolve(answer);};
    dialog.addEventListener('close',()=>finish(dialog.returnValue==='apply'?(input?field.value:true):(input?null:false)),{once:true});
    dialog.addEventListener('cancel',()=>{dialog.returnValue='cancel';});
    dialog.querySelector('form').addEventListener('submit',e=>{if(e.submitter?.value==='apply'&&requiredText&&field.value!==requiredText)e.preventDefault();});
    document.body.append(dialog);dialog.showModal();(input?field:dialog.querySelector('[value=cancel]')).focus();
  });
  const next=(panelDialog.pending||Promise.resolve()).then(run);panelDialog.pending=next.catch(()=>{});return next;
}
function panelConfirm(message,options={}){return panelDialog({...options,message});}
function panelPrompt(message,value='',options={}){return panelDialog({...options,message,input:true,value});}

const panelEditors=new Map();
function editorControls(editor){return [...editor.root.querySelectorAll(editor.selector||'input[id],select[id],textarea[id]')].filter(e=>!['password','hidden','file','button','submit'].includes(e.type)&&!e.dataset.panelIgnore);}
function editorValues(editor){return Object.fromEntries(editorControls(editor).map(e=>[e.id,e.type==='checkbox'?e.checked:e.value]));}
function restoreEditor(editor,values){for(const e of editorControls(editor))if(Object.hasOwn(values,e.id)){if(e.type==='checkbox')e.checked=values[e.id];else if(e.value!==String(values[e.id]))e.value=values[e.id];}}
function editorLabel(id){const el=$(id);return (document.querySelector('label[for="'+CSS.escape(id)+'"]')?.textContent||el?.closest('.field')?.querySelector('label')?.textContent||el?.closest('label')?.textContent||id).trim().replace(/\s+/g,' ').slice(0,110);}
function editorChanges(editor){const current=editorValues(editor);return Object.entries(current).filter(([k,v])=>editor.baseline&&v!==editor.baseline[k]);}
function syncPanelEditor(editor){
  editor.dirty=!!editor.baseline&&editorChanges(editor).length>0;
  editor.bar.hidden=!editor.dirty;editor.bar.querySelector('[data-draft-state]').textContent=editor.busy?(editor.reviewing?'Reviewing changes…':'Saving…'):editorChanges(editor).length+' unsaved change(s)';
  for(const b of editor.bar.querySelectorAll('button'))b.disabled=editor.busy||currentSessionRole==='ReadOnly'||editor.save.disabled;
}
function registerPanelEditor({id,title,root,selector,save,route,applies,nativeDiscard=null,match=null,nativeDirty=null}){
  const node=typeof root==='string'?document.querySelector(root):root,button=$(save);if(!node||!button)return;
  const editor={id,title,root:node,selector,save:button,route,applies,baseline:null,revision:0,dirty:false,busy:false,nativeDiscard,match,nativeDirty};
  const bar=document.createElement('div');bar.className='panelDraftBar';bar.hidden=true;bar.setAttribute('role','status');
  bar.innerHTML='<strong data-draft-state></strong><span></span><button class="primary">Review & save</button><button>Discard edits</button>';
  bar.querySelector('span').textContent=title+' · '+applies;node.prepend(bar);editor.bar=bar;panelEditors.set(id,editor);
  const changed=e=>{if(!editorControls(editor).includes(e.target)||currentSessionRole==='ReadOnly')return;editor.revision++;syncPanelEditor(editor);};
  node.addEventListener('input',changed,true);node.addEventListener('change',changed,true);
  const original=button.onclick;
  button.onclick=async event=>{
    if(editor.busy||button.disabled||currentSessionRole==='ReadOnly')return;
    const changes=editorChanges(editor);if(!changes.length){if(!editor.baseline)return toast('Load settings before saving.','error');return;}
    const lines=changes.map(([k,v])=>editorLabel(k)+': '+(/password|token|secret|key/i.test(k)?'[redacted]':JSON.stringify(editor.baseline[k])+' → '+JSON.stringify(v))).join('\n');
    editor.busy=true;editor.reviewing=true;syncPanelEditor(editor);
    try{
      if(!await panelDialog({title:'Review '+title,message:applies+' The server validates the submitted values.',details:lines,accept:'Save changes'}))return;
      editor.reviewing=false;syncPanelEditor(editor);
      if(currentSessionRole==='ReadOnly'||button.disabled)return;
      await original?.call(button,event);
    }finally{editor.busy=false;editor.reviewing=false;syncPanelEditor(editor);}
  };
  bar.querySelector('button').onclick=()=>button.click();
  bar.querySelectorAll('button')[1].onclick=()=>{restoreEditor(editor,editor.baseline||{});editor.dirty=false;editor.revision++;if(editor.nativeDiscard)$(editor.nativeDiscard)?.click();syncPanelEditor(editor);};
}
function preservePanelEditors(ids,render){
  const editors=ids.map(id=>panelEditors.get(id)).filter(Boolean);for(const e of editors)if(e.nativeDirty)e.dirty=e.nativeDirty()&&!!e.baseline&&editorChanges(e).length>0;const drafts=editors.map(e=>e.dirty?editorValues(e):null);const active=document.activeElement,selection=active&&editors.some(e=>e.root.contains(active))?{node:active,start:active.selectionStart,end:active.selectionEnd}:null;
  const finish=()=>editors.forEach((e,i)=>{if(drafts[i]){if(!e.nativeDirty)e.baseline=editorValues(e);restoreEditor(e,drafts[i]);}else e.baseline=editorValues(e);syncPanelEditor(e);});
  const finishWithFocus=()=>{finish();if(selection?.node.isConnected&&!selection.node.disabled){selection.node.focus({preventScroll:true});if(typeof selection.start==='number')try{selection.node.setSelectionRange(selection.start,selection.end);}catch{}}};const result=render();if(result?.then)return result.finally(finishWithFocus);finishWithFocus();return result;
}

const panelFreshness=new Map();
function notePanelResponse(path,data){
  const key=path.split('?')[0];if(!key.startsWith('/api/performance/')&&!['/api/tick-debug','/api/profiler','/api/operations/memory'].includes(key))return;
  const explicit=Number(data?.capturedUnixMilliseconds||data?.current?.unixMilliseconds||data?.sample?.unixMilliseconds||data?.profiler?.current?.unixMilliseconds||0);
  const samples=data?.samples||data?.timeline?.samples||data?.analysis?.samples||[];
  const observed=explicit||Number(samples[samples.length-1]?.unixMilliseconds||data?.snapshot?.unixMilliseconds||0);
  panelFreshness.set(key,{received:Date.now(),observed,profileObserved:Number(data?.lastProfile?.completedUnixMilliseconds||data?.modAttribution?.profileCompletedUnixMilliseconds||data?.profiler?.lastProfile?.completedUnixMilliseconds||0),generated:Number(data?.generatedUnixMilliseconds||0)});
  if(key==='/api/performance/page-snapshot'){notePanelResponse('/api/performance/overview',data.overview);notePanelResponse('/api/performance/timeline',data.timeline);}
  requestAnimationFrame(updatePanelFreshness);
}
function updatePanelFreshness(){
  const panels={perfTimelineCanvas:'/api/performance/timeline',perfQuickRead:'/api/performance/overview',perfSuiteTimings:'/api/performance/overview',perfIncidents:'/api/performance/overview',perfEntityGrowth:'/api/performance/entity-growth',memoryChart:'/api/operations/memory',tickDebugHistory:'/api/tick-debug',profIncidents:'/api/profiler'};
  for(const [id,path] of Object.entries(panels)){
    const host=$(id)?.closest('.card'),header=host?.querySelector('.cardHeader');if(!header)continue;
    let label=header.querySelector('.panelFreshness');if(!label){label=document.createElement('span');label.className='small panelFreshness';header.append(label);}
    const reading=panelFreshness.get(path);let text='Not received';
    if(reading){const since=Math.max(0,Math.floor((Date.now()-reading.received)/1000)),profilePanel=path==='/api/profiler'||path==='/api/tick-debug',captured=profilePanel?reading.profileObserved:reading.observed;text=captured?(profilePanel?'Latest profile ':'Sample ')+new Date(captured).toLocaleTimeString()+' · '+Math.max(0,Math.floor((Date.now()-captured)/1000))+'s old':profilePanel?'Profile capture time unavailable':'Capture time unavailable';label.title='Response received '+since+'s ago. A received response is not proof that a new sample was collected.';}
    if(label.textContent!==text)label.textContent=text;
  }
}

const timelineInspection={start:null,end:null,pinned:null,hover:null,hidden:new Set()};
function resetTimelineInspection(){timelineInspection.start=timelineInspection.end=timelineInspection.pinned=null;timelineInspection.hover=null;requestPerformanceTimelineRender();}
function inspectedTimelineSamples(){const rows=performanceTimelineData?.samples||[];return rows.filter(s=>Number.isFinite(Number(s.unixMilliseconds))&&(timelineInspection.start===null||Number(s.unixMilliseconds)>=timelineInspection.start)&&(timelineInspection.end===null||Number(s.unixMilliseconds)<=timelineInspection.end)).sort((a,b)=>Number(a.unixMilliseconds)-Number(b.unixMilliseconds));}
function nearestTimelineSample(time){return inspectedTimelineSamples().reduce((best,s)=>!best||Math.abs(Number(s.unixMilliseconds)-time)<Math.abs(Number(best.unixMilliseconds)-time)?s:best,null);}
function timelineReadout(sample){if(!sample)return 'No sample at this time. Sampling gaps are not interpolated.';return 'Recorded sample '+new Date(Number(sample.unixMilliseconds)).toLocaleString()+' · '+[['Delay','mainThreadDelayMs','ms'],['CPU','cpuPercent','%'],['Players','players',''],['Entities','loadedEntities',''],['Heap','managedHeapMb','MB']].map(([label,key,unit])=>label+': '+(sample[key]==null?'unavailable':Number(sample[key]).toLocaleString(undefined,{maximumFractionDigits:1})+unit)).join(' · ');}
function syncTimelineInspection(){const label=$('timelineInspectorReadout');if(!label)return;const t=timelineInspection.pinned??timelineInspection.hover;const row=t===null?null:inspectedTimelineSamples().find(s=>Number(s.unixMilliseconds)===Number(t));label.textContent=t===null?'Hover for values; click to pin. Arrow keys move the pinned sample.':(timelineInspection.pinned!==null?'Pinned · ':'')+timelineReadout(row);$('timelineZoomState').textContent=timelineInspection.start===null?'Rolling view':'Zoomed locally · Reset to follow new samples';}
function zoomTimeline(factor){const raw=(performanceTimelineData?.samples||[]).filter(s=>Number.isFinite(Number(s.unixMilliseconds))).map(s=>Number(s.unixMilliseconds));if(raw.length<2)return;const low=Math.min(...raw),high=Math.max(...raw);const span=((timelineInspection.end??high)-(timelineInspection.start??low))*factor;const center=timelineInspection.pinned??((timelineInspection.start??low)+(timelineInspection.end??high))/2;if(span>=high-low){timelineInspection.start=timelineInspection.end=null;}else{timelineInspection.start=Math.max(low,Math.min(high-span,center-span/2));timelineInspection.end=timelineInspection.start+span;}requestPerformanceTimelineRender();}
function initTimelineInspector(){
 const canvas=$('perfTimelineCanvas');if(!canvas)return;canvas.tabIndex=0;canvas.setAttribute('aria-label','Performance graph. Click to pin a sample; left and right arrows move between samples.');
 const tools=document.createElement('div');tools.id='timelineInspector';tools.innerHTML='<div class="rowActions"><button data-zoom="in">Zoom in</button><button data-zoom="out">Zoom out</button><button data-zoom="reset">Reset view</button><button data-zoom="unpin">Unpin</button><span id="timelineZoomState" class="small"></span></div><div class="timelineTraceChoices"></div><p id="timelineInspectorReadout" class="small"></p>';
 canvas.closest('.timelineWrap').before(tools);
 const traces=[['Delay','mainThreadDelayMs'],['CPU','cpuPercent'],['Players','players'],['Entities','loadedEntities'],['Heap','managedHeapMb']];
 for(const [name,key] of traces){const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.checked=true;check.onchange=()=>{if(!check.checked&&timelineInspection.hidden.size===4){check.checked=true;return;}if(check.checked)timelineInspection.hidden.delete(key);else timelineInspection.hidden.add(key);requestPerformanceTimelineRender();};label.append(check,document.createTextNode(name));tools.querySelector('.timelineTraceChoices').append(label);}
 tools.querySelector('[data-zoom=in]').onclick=()=>zoomTimeline(.5);tools.querySelector('[data-zoom=out]').onclick=()=>zoomTimeline(2);tools.querySelector('[data-zoom=reset]').onclick=resetTimelineInspection;tools.querySelector('[data-zoom=unpin]').onclick=()=>{timelineInspection.pinned=null;requestPerformanceTimelineRender();};
 let pointerFrame=0,pointerX=0;
 const at=x=>{const rows=inspectedTimelineSamples(),rect=canvas.getBoundingClientRect();if(!rows.length||rect.width<=87||x<75||x>rect.width-12)return null;return nearestTimelineSample(Number(rows[0].unixMilliseconds)+(x-75)/(rect.width-87)*(Number(rows.at(-1).unixMilliseconds)-Number(rows[0].unixMilliseconds)));};
 canvas.addEventListener('mousemove',e=>{pointerX=e.clientX-canvas.getBoundingClientRect().left;if(pointerFrame)return;pointerFrame=requestAnimationFrame(()=>{pointerFrame=0;timelineInspection.hover=at(pointerX)?.unixMilliseconds??null;syncTimelineInspection();});});
 canvas.addEventListener('mouseleave',()=>{timelineInspection.hover=null;syncTimelineInspection();});
 canvas.addEventListener('click',e=>{const x=e.clientX-canvas.getBoundingClientRect().left;if(performanceIncidentHitAreas.some(h=>Math.abs(h.x-x)<=10))return;timelineInspection.pinned=at(x)?.unixMilliseconds??null;requestPerformanceTimelineRender();});
 canvas.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','Escape'].includes(e.key))return;e.preventDefault();if(e.key==='Escape'){timelineInspection.pinned=null;}else{const rows=inspectedTimelineSamples();let i=rows.findIndex(s=>Number(s.unixMilliseconds)===Number(timelineInspection.pinned));if(i<0)i=rows.length-1;const row=rows[Math.max(0,Math.min(rows.length-1,i+(e.key==='ArrowLeft'?-1:1)))];timelineInspection.pinned=row?.unixMilliseconds??null;}requestPerformanceTimelineRender();});
 syncTimelineInspection();
}

let panelIncidentRows=[],panelIncidentGrouping=true;
function incidentGroupKey(x){return JSON.stringify([x.playerUid||x.playerName||'',x.code||'',x.subject||'']);}
function panelIncidentDetails(rows){return rows.map(x=>'<article class="panelIncidentEvent"><b>'+esc(acDate(x.unixMilliseconds))+'</b> · '+esc(x.playerName||'')+'<p>'+esc(x.summary||'')+'</p><b>Recorded consequence / rule:</b> '+esc(x.consequence||'None recorded')+'<br><b>Risk:</b> '+Number(x.riskBefore||0)+' → '+Number(x.riskAfter||0)+'<br><b>Why:</b> '+esc(x.why||'No explanation recorded')+'<br><b>Next:</b> '+esc(x.recommendedAction||'Review evidence')+'<pre>'+esc(x.evidence||'No evidence text recorded')+'</pre></article>').join('');}
function renderAntiCheatIncidentGroups(rows){
 panelIncidentRows=rows.slice(0,250);const host=$('acHistoryRows');if(!host)return;
 const open=new Set([...host.querySelectorAll('details[open]')].map(d=>d.dataset.incidentGroup));
 const groups=new Map();panelIncidentRows.forEach(x=>{const key=panelIncidentGrouping?incidentGroupKey(x):String(x.id||x.unixMilliseconds)+incidentGroupKey(x);if(!groups.has(key))groups.set(key,[]);groups.get(key).push(x);});
 renderAntiCheatIncidentGroups.groups=groups;
 const markup=[...groups].map(([key,events])=>{events.sort((a,b)=>Number(b.unixMilliseconds)-Number(a.unixMilliseconds));const x=events[0],consequences=[...new Set(events.map(e=>e.consequence).filter(Boolean))],first=events.at(-1);return '<tr data-panel-key="'+esc(key)+'"><td>'+esc(acDate(x.unixMilliseconds))+(events.length>1?'<div class="small">First '+esc(acDate(first.unixMilliseconds))+'</div>':'')+'</td><td><b>'+esc(x.playerName||'')+'</b><div class="small">'+esc(x.playerUid||'')+'</div></td><td>'+acSeverityBadge(Math.max(...events.map(e=>Number(e.severity||0))))+'</td><td>'+esc(x.confidence||'Low')+'<div class="small">Latest observation</div></td><td><code>'+esc(x.code||'')+'</code><div class="small">'+events.length+' report(s) in this loaded result</div></td><td>'+Number(x.riskBefore||0)+' → '+Number(x.riskAfter||0)+'</td><td>'+Number(x.x||0).toFixed(1)+', '+Number(x.y||0).toFixed(1)+', '+Number(x.z||0).toFixed(1)+' · dim '+Number(x.dimension||0)+'</td><td><details data-incident-group="'+esc(key)+'" '+(open.has(key)?'open':'')+'><summary>Evidence & decision history ('+events.length+')</summary><div class="panelIncidentDetails">'+(open.has(key)?panelIncidentDetails(events):'')+'</div></details></td><td>'+esc(consequences.length?consequences.join('\n'):'None recorded')+'</td></tr>';}).join('');
 stablePanel('acHistoryRows').html=markup||'<tr><td colspan="9">No persistent reports match this filter.</td></tr>';
 enhanceTableByBodyId('acHistoryRows');
 const state=$('acHistoryGroupingState');if(state)state.textContent=groups.size+' group(s) / '+panelIncidentRows.length+' loaded reports · counts are limited to this result, not lifetime totals';
}
function initIncidentGrouping(){const toolbar=$('acHistoryLoad')?.parentElement;if(!toolbar)return;const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.checked=true;check.onchange=()=>{panelIncidentGrouping=check.checked;renderAntiCheatIncidentGroups(panelIncidentRows);};label.append(check,document.createTextNode('Group repeated findings'));const state=document.createElement('span');state.id='acHistoryGroupingState';state.className='small';toolbar.append(label,state);
 document.addEventListener('toggle',e=>{const d=e.target;if(!d.matches?.('details[data-incident-group]')||!d.open)return;const events=renderAntiCheatIncidentGroups.groups?.get(d.dataset.incidentGroup)||[];d.querySelector('.panelIncidentDetails').innerHTML=panelIncidentDetails(events);},true);
}

function responsivePanelTables(){for(const id of ['acHistoryRows','perfIncidents','profIncidents','profMods','tickDebugContributors']){const body=$(id),table=body?.closest('table');if(!table)continue;table.classList.add('panelResponsiveTable');const headers=[...table.querySelectorAll(':scope > thead th')].map(h=>h.textContent.replace(/↕/g,'').trim());for(const row of body.children)[...row.children].forEach((td,i)=>{td.dataset.label=headers[i]||'';});}}
function initPerformanceTriage(){const button=document.createElement('button');button.id='perfSecondaryPanels';let expanded=false;try{expanded=localStorage.getItem('nsu.performance.secondaryExpanded')==='true';}catch{};button.dataset.expanded=String(expanded);button.textContent=expanded?'Collapse secondary panels':'Show secondary panels';button.onclick=()=>{expanded=!expanded;button.dataset.expanded=String(expanded);button.textContent=expanded?'Collapse secondary panels':'Show secondary panels';try{localStorage.setItem('nsu.performance.secondaryExpanded',String(expanded));}catch{};for(const card of $('performance').querySelectorAll('[data-panel-triage-applied]'))setWorkspaceCardCollapsed(card,!expanded,false);requestPerformanceTimelineRender();};$('performance')?.querySelector(':scope > .toolbar')?.append(button);}
function applyPerformanceTriage(){const page=$('performance');if(!page)return;const secondary=new Set(['perfEntityTypes','perfHeatmap','perfEntityGrowth','perfChunks','perfBlockActivity','perfEnvironmentHistory','perfProviders','perfConflicts'].map(id=>$(id)?.closest('.card')).filter(Boolean));const titles=/Entity Type Hotspots|Chunk Pressure Heatmap|Entity Growth|Block Activity|Environment|Providers|Harmony|Conflict/i;for(const card of page.querySelectorAll('.workspaceCard')){const title=card.querySelector(':scope > .cardHeader h3')?.textContent||'';if((!secondary.has(card)&&!titles.test(title))||card.dataset.panelTriageApplied)continue;card.dataset.panelTriageApplied='1';if(!Object.hasOwn(workspaceCollapsedCards,card.dataset.workspaceCardKey))setWorkspaceCardCollapsed(card,$('perfSecondaryPanels')?.dataset.expanded!=='true',false);}}

// Native editors keep their existing protections. Extend the same behavior to
// configuration pages which previously overwrote controls on each refresh.
registerPanelEditor({id:'login',title:'Login Protection',root:'#loginProtection',selector:'input[id^="lp"]',save:'loginProtSave',route:'/api/loginprotection/config',applies:'Applies immediately to protection settings.'});
registerPanelEditor({id:'restarts',title:'Restart schedule',root:$('restartSave')?.closest('section'),selector:'#restartEnabled,#restartPrimaryTime,#restartTimes,#restartWarnings,#restartReason,#restartGameAnnouncements,#restartDiscordAnnouncements',save:'restartSave',route:'/api/restarts/settings',applies:'Recalculates the recurring schedule immediately; times are UTC.'});
registerPanelEditor({id:'map',title:'Web Map settings',root:$('webMapSave')?.closest('.card'),save:'webMapSave',route:'/api/webmap/config',applies:'Updates map settings; listener changes may rebind the map listener. Terrain choices apply to the next export.'});
registerPanelEditor({id:'mapSchedule',title:'Terrain export schedule',root:$('webMapSaveExportSchedule')?.closest('.card'),save:'webMapSaveExportSchedule',route:'/api/webmap/export-schedule',applies:'Applies to subsequent scheduled terrain passes; saving does not start an export.'});
registerPanelEditor({id:'packet',title:'Packet protection',root:'[data-ac-panel="packet"]',selector:'input[id^="acPacket"],select[id^="acPacket"],textarea[id^="acPacket"]',save:'acPacketSave',nativeDiscard:'acPacketDiscard',nativeDirty:()=>packetSettingsDirty,route:'/api/anticheat/packetguard',applies:'Applies live to subsequent checks; exemptions and enforcement remain server validated.'});
registerPanelEditor({id:'recording',title:'BlockLogger recording',root:$('blRecordingSave')?.closest('.card'),selector:'input[id^="blRec"],textarea[id^="blRec"]',save:'blRecordingSave',nativeDiscard:'blRecordingDiscard',nativeDirty:()=>blRecordingDirty,route:'/api/blocklogger/recording',applies:'Applies to future activity; historical missing snapshots cannot be recreated.'});
registerPanelEditor({id:'nos',title:'Optimizer settings',root:$('nosApply')?.closest('.card'),selector:Object.values(nosFields).map(id=>'#nos'+id).join(','),save:'nosApply',nativeDiscard:'nosDiscard',nativeDirty:()=>nosDirty,route:'/api/optimizations/newfies',match:options=>{try{return JSON.parse(options.body||'{}').operation==='set';}catch{return false;}},applies:'Applies eligible runtime modules live; capture locks, conflict checks and safe fallbacks remain enforced.'});
const originalPacketRenderer=renderAntiCheatPacketGuard;renderAntiCheatPacketGuard=(...args)=>preservePanelEditors(['packet'],()=>originalPacketRenderer(...args));
const originalRecordingRenderer=renderBlockLoggerRecording;renderBlockLoggerRecording=(...args)=>preservePanelEditors(['recording'],()=>originalRecordingRenderer(...args));
const originalNosRenderer=renderNos;renderNos=(...args)=>preservePanelEditors(['nos'],()=>originalNosRenderer(...args));
const originalLoginRenderer=renderLoginProtectionConfig;renderLoginProtectionConfig=(...args)=>preservePanelEditors(['login'],()=>originalLoginRenderer(...args));
const originalRestartRenderer=renderRestartSchedule;renderRestartSchedule=(...args)=>preservePanelEditors(['restarts'],()=>originalRestartRenderer(...args));
const originalMapRenderer=renderWebMap;renderWebMap=(...args)=>preservePanelEditors(['map','mapSchedule'],()=>originalMapRenderer(...args));
const originalPanelApi=api;api=async function(path,options={}){
 const editors=[...panelEditors.values()].filter(e=>e.route===path&&(!e.match||e.match(options))&&String(options.method||'GET').toUpperCase()!=='GET');
 const revisions=editors.map(e=>e.revision),submitted=editors.map(e=>editorValues(e));
 const result=await originalPanelApi(path,options);
 if(result?.success!==false)editors.forEach((e,i)=>{e.baseline=submitted[i];if(e.revision===revisions[i])e.dirty=false;syncPanelEditor(e);});
 notePanelResponse(path,result);return result;
};
const originalWorkspaceSync=syncWorkspacePage;syncWorkspacePage=(...args)=>{const result=originalWorkspaceSync(...args);responsivePanelTables();applyPerformanceTriage();return result;};
const originalPerformanceSync=syncPerformanceWorkspace;syncPerformanceWorkspace=(...args)=>{const result=originalPerformanceSync(...args);if(performancePages[currentSection]&&!document.hidden)updatePanelFreshness();return result;};
window.addEventListener('beforeunload',e=>{if([...panelEditors.values()].some(x=>x.dirty)||packetSettingsDirty||nosDirty||blRecordingDirty){e.preventDefault();e.returnValue='';}});
initTimelineInspector();initIncidentGrouping();initPerformanceTriage();responsivePanelTables();applyPerformanceTriage();
const panelPolishStyle=document.createElement('style');panelPolishStyle.textContent=`
.panelActionDialog{width:min(650px,94vw);max-height:85vh;overflow:auto;background:#111c25;color:#dce8ee;border:1px solid #37505f;border-radius:14px;padding:24px;box-shadow:0 24px 80px #0009}.panelActionDialog::backdrop{background:#000a}.panelActionDialog p{white-space:pre-wrap;line-height:1.6}.panelActionDialog input{width:100%;margin:10px 0}.panelActionDialog .rowActions{justify-content:flex-end;margin-top:18px}.panelChangePreview{max-height:40vh;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;background:#0a131b;padding:12px;border-radius:8px}.panelDraftBar{position:sticky;top:8px;z-index:8;display:flex;align-items:center;gap:10px;flex-wrap:wrap;background:#172c32;border:1px solid #398389;border-radius:10px;padding:12px;margin-bottom:12px}.panelDraftBar[hidden]{display:none}.panelDraftBar span{flex:1;min-width:150px;font-size:12px}.panelFreshness{margin-left:auto;font-size:11px;color:#9eb4bf}.timelineTraceChoices{display:flex;flex-wrap:wrap;gap:14px;margin-top:10px}.timelineTraceChoices label{display:flex;align-items:center;gap:6px}#timelineInspectorReadout{min-height:2.8em;line-height:1.6;overflow-wrap:anywhere}.panelIncidentEvent{border-top:1px solid #2b3c48;padding:12px 0}.panelIncidentEvent pre{white-space:pre-wrap;overflow-wrap:anywhere;max-height:220px;overflow:auto}.panelIncidentDetails{max-height:480px;overflow:auto;min-width:180px}#acHistoryRows details summary{cursor:pointer;color:#92d3d3}
@media(max-width:700px){table.panelResponsiveTable{min-width:0!important;display:block!important;width:100%!important}table.panelResponsiveTable>thead{display:none}table.panelResponsiveTable>tbody{display:block}table.panelResponsiveTable>tbody>tr{display:block;border:1px solid #2a3b46;border-radius:10px;margin:10px 0;padding:10px;background:#101c24}table.panelResponsiveTable>tbody>tr>td{display:block!important;max-width:100%;white-space:normal!important;overflow-wrap:anywhere;border:0!important;padding:6px!important}table.panelResponsiveTable>tbody>tr>td:before{content:attr(data-label);display:block;color:#91a8b5;font-size:11px;font-weight:600;margin-bottom:3px}.panelFreshness{width:100%;margin:4px 0}.panelDraftBar{top:0}.panelDraftBar button{flex:1}.panelActionDialog{padding:16px}.panelIncidentDetails{min-width:0}}
`;document.head.append(panelPolishStyle);
""";
}
