const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];let writes=0,serverSeconds=60,failSave=false,performanceReads=0;
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',async r=>{const u=new URL(r.request().url());let data={};
  if(u.pathname==='/')return r.fulfill({contentType:'text/html',body:html});
  if(/^\/api\/performance\/(overview|timeline|page-snapshot)$/.test(u.pathname))performanceReads++;
  if(u.pathname==='/api/session')data={authenticated:true,username:'Owner',role:'Owner',owner:true};
  if(u.pathname==='/api/loginprotection/config'){
   if(r.request().method()==='POST'){writes++;if(failSave)return r.fulfill({status:503,contentType:'application/json',body:'{"error":"fixture refused save"}'});serverSeconds=JSON.parse(r.request().postData()).protectionSeconds;data={success:true};}
   else data={available:true,config:{enabled:true,protectionSeconds:serverSeconds,protectOnJoin:true},activeProtected:0};
  }
  if(u.pathname==='/api/loginprotection/status')data={lines:['ready']};
  return r.fulfill({contentType:'application/json',body:JSON.stringify(data)});
 });
 await page.goto('http://workspace-polish.invalid/');
 await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});liveUpdates=false;safeDiagnosticsModeGlobal=false;setSection('loginProtection',false);});
 await page.evaluate(()=>loadLoginProtection(false));
 assert.equal(await page.evaluate(()=>panelEditors.size),7);
 await page.fill('#lpSeconds','120');
 assert.match(await page.locator('#loginProtection .panelDraftBar').textContent(),/unsaved/);
 await page.evaluate(()=>renderLoginProtectionConfig({available:true,config:{enabled:true,protectionSeconds:90}}));
 assert.equal(await page.inputValue('#lpSeconds'),'120','refresh must preserve draft');
 await page.click('#loginProtSave');await page.locator('.panelActionDialog').waitFor();
 assert.match(await page.locator('.panelChangePreview').textContent(),/90.*120/);
 assert.equal(writes,0,'preview must not write');await page.click('.panelActionDialog [value=cancel]');assert.equal(writes,0);
 await page.click('#loginProtSave');await page.click('.panelActionDialog [value=apply]');
 await page.waitForFunction(()=>!panelEditors.get('login').dirty&&!panelEditors.get('login').busy);assert.equal(writes,1);assert.equal(serverSeconds,120);
 failSave=true;await page.fill('#lpSeconds','150');await page.click('#loginProtSave');await page.click('.panelActionDialog [value=apply]');await page.waitForFunction(()=>!panelEditors.get('login').busy);assert.equal(await page.inputValue('#lpSeconds'),'150');assert.equal(await page.evaluate(()=>panelEditors.get('login').dirty),true);
 await page.locator('#loginProtection .panelDraftBar button').last().click();assert.equal(await page.inputValue('#lpSeconds'),'120');assert.equal(await page.evaluate(()=>panelEditors.get('login').dirty),false);
 await page.evaluate(()=>{currentSessionRole='ReadOnly';$('loginProtSave').click();});assert.equal(await page.locator('.panelActionDialog').count(),0);assert.equal(writes,2);await page.evaluate(()=>currentSessionRole='Owner');
 const inFlight=await page.evaluate(async()=>{const saved=api;let resolveConfig;api=path=>path.endsWith('/config')?new Promise(r=>resolveConfig=r):Promise.resolve({lines:['ready']});const read=loadLoginProtection(false);$('lpSeconds').value='160';$('lpSeconds').dispatchEvent(new Event('input',{bubbles:true}));resolveConfig({available:true,config:{enabled:true,protectionSeconds:110}});await read;api=saved;return {value:$('lpSeconds').value,dirty:panelEditors.get('login').dirty};});assert.deepEqual(inFlight,{value:'160',dirty:true});await page.locator('#loginProtection .panelDraftBar button').last().click();
 const dialogs=await page.evaluate(async()=>{const answer=panelPrompt('<script>unsafe</script>','',{title:'Typed action',requiredText:'SAVE'});await new Promise(r=>setTimeout(r,0));const d=document.querySelector('dialog');const blocked=d.querySelector('[value=apply]').disabled;d.querySelector('input').value='SAVE';d.querySelector('input').dispatchEvent(new Event('input'));d.querySelector('[value=apply]').click();return {blocked,answer:await answer,unsafe:!!d.querySelector('script')};});
 assert.deepEqual(dialogs,{blocked:true,answer:'SAVE',unsafe:false});
 const beforeChartReads=performanceReads;
 const chart=await page.evaluate(()=>{
  setSection('performance',false);performanceTimelineData={samplingGaps:[],samples:[{unixMilliseconds:1000,players:1,cpuPercent:10},{unixMilliseconds:2000,players:2,cpuPercent:20},{unixMilliseconds:3000,players:3,cpuPercent:30}]};resetTimelineInspection();renderPerformanceTimeline();
  timelineInspection.pinned=2000;zoomTimeline(.5);renderPerformanceTimeline();const narrowed=inspectedTimelineSamples().length;
  resetTimelineInspection();renderPerformanceTimeline();notePanelResponse('/api/performance/timeline',{capturedUnixMilliseconds:1000,samples:[{unixMilliseconds:1000}]});updatePanelFreshness();
  return {narrowed,all:inspectedTimelineSamples().length,age:document.querySelector('#perfTimelineCanvas').closest('.card').querySelector('.panelFreshness').textContent};
 });assert.ok(chart.narrowed<chart.all);assert.match(chart.age,/Sample/);
 await page.locator('#perfTimelineCanvas').focus();await page.keyboard.press('ArrowLeft');await page.waitForFunction(()=>document.querySelector('#timelineInspectorReadout').textContent.includes('Pinned'));assert.match(await page.locator('#timelineInspectorReadout').textContent(),/Pinned/);await page.keyboard.press('Escape');assert.equal(performanceReads,beforeChartReads,'local chart inspection must not issue server reads');
 await page.evaluate(()=>{setSection('anticheat',false);setAntiCheatTab('history');});await page.waitForTimeout(150);
 const group=await page.evaluate(()=>{
 const base={playerUid:'p1',playerName:'<script>player</script>',code:'PACKET_SPEED_HACK',subject:'p1',severity:4,confidence:'Low',evidence:'mode=vanilla-glider;<svg onload=bad>',consequence:'risk: Kick — fixture reason',riskBefore:0,riskAfter:50};
  renderAntiCheatIncidentGroups([{...base,id:1,unixMilliseconds:1000},{...base,id:2,unixMilliseconds:2000},{...base,id:3,unixMilliseconds:3000,playerUid:'p2',subject:'p2'}]);
  const before=$('acHistoryRows').firstElementChild;renderAntiCheatIncidentGroups(panelIncidentRows);return {groups:$('acHistoryRows').rows.length,same:before===$('acHistoryRows').firstElementChild,text:$('acHistoryRows').textContent,unsafe:!!$('acHistoryRows').querySelector('script,svg')};
 });assert.equal(group.groups,2);assert.equal(group.same,true);assert.equal(group.unsafe,false);assert.match(group.text,/2 report/);assert.match(group.text,/risk: Kick/);
 await page.locator('#acHistoryRows details').first().locator('summary').click();await page.waitForFunction(()=>document.querySelector('#acHistoryRows details[open] .panelIncidentEvent'));assert.match(await page.locator('#acHistoryRows details[open]').textContent(),/mode=vanilla-glider/);
 const keyed=await page.evaluate(()=>{const row=$('acHistoryRows').firstElementChild;row.querySelector('details').open=false;const changed=panelIncidentRows.map(x=>x.id===3?{...x,riskAfter:25}:x);renderAntiCheatIncidentGroups(changed);return {count:$('acHistoryRows').rows.length,kept:row===$('acHistoryRows').firstElementChild};});assert.equal(keyed.count,2,'keyed updates must not duplicate replaced rows');assert.equal(keyed.kept,true,'unchanged group should retain its node');
 const sorted=await page.evaluate(()=>{const table=$('acHistoryRows').closest('table'),header=table.querySelector('thead th');header.click();header.click();const key=$('acHistoryRows').firstElementChild.dataset.panelKey;renderAntiCheatIncidentGroups(panelIncidentRows.map(x=>x.id===1?{...x,riskAfter:60}:x));return {direction:header.getAttribute('aria-sort'),kept:key===$('acHistoryRows').firstElementChild.dataset.panelKey};});assert.equal(sorted.direction,'descending');assert.equal(sorted.kept,true,'refresh preserves selected table sort');
 await page.setViewportSize({width:390,height:844});await page.waitForTimeout(100);assert.equal(await page.locator('#acHistoryRows').first().evaluate(e=>getComputedStyle(e.closest('table')).display),'block');assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'mobile layout overflows');
 assert.deepEqual(errors,[]);console.log('PASS seven-editor draft/preview/cancel/save/failure/read-only flows, typed safe dialogs, local chart zoom/pin/keys, capture freshness, incident grouping/full evidence, keyed node retention and mobile cards.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
