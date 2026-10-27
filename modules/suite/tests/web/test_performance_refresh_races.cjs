const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));
 await page.goto('http://refresh-races.invalid/');
 const result=await page.evaluate(async()=>{
  showApp({username:'Owner',role:'Owner',owner:true});safeDiagnosticsModeGlobal=false;liveUpdates=false;setSection('performance',false);
  const payload=n=>({overview:{available:true,current:{players:n},suiteTimings:[]},timeline:{samplingGaps:[],samples:[{unixMilliseconds:1000,players:n},{unixMilliseconds:7000,players:n}]},entityScan:{available:false},entityGrowth:{rows:[]},environment:{history:[]},providers:{providers:[]},conflicts:{requiresManualInspection:true}});
  const held={};api=url=>new Promise((resolve,reject)=>held[url]={resolve,reject});loadPerformanceBlockActivity=async()=>{};
  $('perfWindow').value='1800';const full=loadPerformanceCenter(true);const buttonBusy=$('perfRefresh').disabled;
  const recent=loadPerformanceCenter(false);
  held['/api/performance/overview?window=1800'].resolve(payload(7).overview);held['/api/performance/timeline?window=1800'].resolve(payload(7).timeline);await recent;
  held['/api/performance/page-snapshot?window=1800'].resolve(payload(42));await full;
  const retained={overview:performanceOverviewData.current.players,timeline:performanceTimelineData.samples[0].players};
  // Same-range concurrent reads must also reject late older overview responses.
  let oldResolve,newResolve;
  api=()=>new Promise(r=>oldResolve=r);const old=loadPerformanceOverview(false);
  api=()=>new Promise(r=>newResolve=r);const newer=loadPerformanceOverview(false);
  newResolve(payload(9).overview);await newer;oldResolve(payload(99).overview);await old;
  const finalOverview=performanceOverviewData.current.players;
  api=async()=>{throw new Error('fixture busy')};await loadPerformanceTimeline(false);
  renderPerformanceTimeline();const errorText=$('perfTimelineState').textContent;
  recordPerformanceResponse('/api/performance/page-snapshot?window=1800',new Error('old snapshot failed'));
  recordPerformanceResponse('/api/performance/overview?window=1800',null);
  recordPerformanceResponse('/api/performance/timeline?window=1800',null);
  const recovered=!performanceRefreshState.performance.error;
  const calls=[];api=async url=>{calls.push(url);return url.includes('/overview')?payload(11).overview:payload(11).timeline;};
  await $('perfRefresh').onclick();const manualKept=performanceTimelineData.samples[0].players;
  return {retained,buttonBusy,buttonReleased:!$('perfRefresh').disabled,finalOverview,errorText,recovered,calls,manualKept,kept:retained.timeline};
 });
 assert.deepEqual(result.retained,{overview:7,timeline:7});assert.equal(result.finalOverview,9);assert.equal(result.buttonBusy,true);assert.equal(result.buttonReleased,true);assert.equal(result.kept,7);assert.equal(result.recovered,true);assert.equal(result.manualKept,11);assert.equal(result.calls.length,2);assert.ok(result.calls.every(x=>/\/api\/performance\/(overview|timeline)\?/.test(x)),'Refresh Overview must not query block history or full page snapshots');assert.match(result.errorText,/keeping previous graph/);assert.deepEqual(errors,[]);
 console.log('PASS: late full-page/live and same-range overview responses cannot replace newer readings; refresh controls settle and failed graph reads preserve data.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
