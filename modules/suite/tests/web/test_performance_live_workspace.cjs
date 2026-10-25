const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:new URL(r.request().url()).pathname==='/'?'text/html':'application/json',body:new URL(r.request().url()).pathname==='/'?html:new URL(r.request().url()).pathname==='/api/session'?JSON.stringify({authenticated:true,username:'Owner',role:'Owner',owner:true}):'{}'}));
 await page.goto('http://performance-live.invalid/');
 await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});safeDiagnosticsModeGlobal=false;liveUpdates=false;setSection('performance',false);});
 for(const section of ['performance','tickDebug','profiler','memory','optimizations','health']){
  await page.evaluate(s=>setSection(s,false),section);
  assert.equal(await page.locator(`#${section} .performanceWorkspace`).count(),1);
  assert.equal(await page.locator(`#${section} .performanceWorkspace .rowActions`).first().locator('button').count(),6);
 }
 await page.waitForFunction(()=>!performanceRefreshBusy);
 const result=await page.evaluate(async()=>{
  setSection('performance',false);const calls=[];let releases=[],active=0,max=0;
  loadPerformanceOverview=async()=>{calls.push('overview');active++;max=Math.max(max,active);await new Promise(r=>releases.push(r));active--;};
  loadPerformanceTimeline=async()=>{calls.push('timeline');active++;max=Math.max(max,active);await new Promise(r=>releases.push(r));active--;};
  loadPerformanceCenter=async()=>calls.push('HEAVY');loadPerformanceBlockActivity=async()=>calls.push('DB');
  localStorage.setItem('nsu.performance.refresh',JSON.stringify({performance:true,interval:5000}));
  performanceRefreshState.performance={};
  const pending=refreshPerformanceReadings(false);await refreshPerformanceReadings(false);await refreshPerformanceReadings(true);
  const overlap=calls.slice();for(const r of releases)r();await pending;
  safeDiagnosticsModeGlobal=true;performanceRefreshState.performance.attempted=0;await refreshPerformanceReadings(false);safeDiagnosticsModeGlobal=false;
  const afterSafe=calls.length;
  localStorage.setItem('nsu.performance.refresh',JSON.stringify({performance:false}));liveUpdates=true;await refreshPerformanceReadings(false);const afterPause=calls.length;liveUpdates=false;
  // An editing input pauses background updates without discarding the draft.
  localStorage.setItem('nsu.performance.refresh',JSON.stringify({performance:true}));$('perfWindow').focus();await refreshPerformanceReadings(false);const afterEdit=calls.length;$('perfWindow').blur();
  let nos=0;loadNos=async()=>nos++;setSection('optimizations',false);await refreshPerformanceReadings(true);
  let health=0;loadSystemHealth=async()=>health++;setSection('health',false);localStorage.setItem('nsu.performance.refresh',JSON.stringify({health:true}));await refreshPerformanceReadings(false);const autoHealth=health;await refreshPerformanceReadings(true);
  return {overlap,max,afterSafe,afterPause,afterEdit,nos,autoHealth,health};
 });
 assert.deepEqual(result.overlap,['overview','timeline']);assert.equal(result.max,2);assert.equal(result.afterSafe,2);assert.equal(result.afterPause,2);assert.equal(result.afterEdit,2);assert.equal(result.nos,1);assert.equal(result.autoHealth,0);assert.equal(result.health,1);
 await page.setViewportSize({width:390,height:844});await page.evaluate(()=>setSection('performance',false));
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'workspace fits mobile viewport');
 // Exercise the actual one-second scheduler and real fetch/render path.
 let timelineRequests=0,overviewRequests=0,heavyRequests=0,failTimeline=false;
 await page.route('**/api/performance/**',async r=>{
  const u=new URL(r.request().url());let payload={};
  if(u.pathname==='/api/performance/timeline'){timelineRequests++;if(failTimeline)return r.fulfill({status:503,contentType:'application/json',body:'{"error":"fixture busy"}'});payload={samples:[{unixMilliseconds:1000,players:3},{unixMilliseconds:7000,players:5}],samplingGaps:[]};}
  else if(u.pathname==='/api/performance/overview'){overviewRequests++;payload={available:true,current:{players:5},suiteTimings:[]};}
  else heavyRequests++;
  return r.fulfill({contentType:'application/json',body:JSON.stringify(payload)});
 });
 await page.reload();await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});safeDiagnosticsModeGlobal=false;liveUpdates=false;localStorage.setItem('nsu.performance.refresh',JSON.stringify({performance:true,interval:5000}));setSection('performance',false);performanceRefreshState.performance={};});
 await page.waitForFunction(()=>performanceTimelineData?.samples?.length===2);
 assert.ok(timelineRequests>0&&overviewRequests>0,'local auto refresh updates timeline even with global Live off');
 await page.waitForFunction(()=>document.querySelector('#performance .performanceFreshness').textContent.includes('Updated'));
 failTimeline=true;
 await page.evaluate(async()=>{await refreshPerformanceReadings(true);});
 assert.equal(await page.evaluate(()=>performanceTimelineData.samples.length),2,'failed refresh preserves last chart');
 assert.match(await page.locator('#performance .performanceFreshness').textContent(),/Refresh failed/);
 assert.deepEqual(errors,[]);console.log('PASS: six Performance workspaces, overview + timeline updates, no refresh overlap/heavy reads, Safe Diagnostics/edit/pause gates, manual health and mobile layout');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
