const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));await page.goto('http://render.invalid/');
 // Settle the initial unauthenticated session read before installing the fixture
 // session; otherwise its late response can hide the whole app during reveal.
 await page.waitForLoadState('networkidle');
 await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});setSection('dashboard',false);performanceTimelineData={samplingGaps:[],samples:[{unixMilliseconds:1000,players:2},{unixMilliseconds:7000,players:4},{unixMilliseconds:13000,players:3}]};renderPerformanceTimeline();});
 await page.evaluate(()=>setSection('performance',false));await page.waitForFunction(()=>document.querySelector('#perfTimelineState').textContent.includes('3 displayed'));
 assert.ok(await page.locator('#perfTimelineCanvas').evaluate(c=>c.width>500),'revealing a previously hidden chart redraws at its visible width');
 await page.setViewportSize({width:700,height:1000});await page.waitForTimeout(180);const wide=await page.locator('#perfTimelineCanvas').evaluate(c=>c.width);await page.setViewportSize({width:390,height:844});await page.waitForTimeout(180);assert.ok(await page.locator('#perfTimelineCanvas').evaluate((c,w)=>c.width<w,wide),'container resize redraws canvas');
 const result=await page.evaluate(async()=>{
  const pending={},calls=[];api=url=>{calls.push(url);if(url.startsWith('/api/performance/page-snapshot'))return new Promise((resolve,reject)=>pending[url]={resolve,reject});return Promise.resolve({});};loadPerformanceBlockActivity=async()=>{};
  const payload=players=>({overview:{available:true,current:{players},suiteTimings:[]},timeline:{samplingGaps:[],samples:[{unixMilliseconds:1000,players},{unixMilliseconds:7000,players}]},entityScan:{available:false},entityGrowth:{rows:[]},environment:{history:[]},providers:{providers:[]},conflicts:{requiresManualInspection:true,message:'Run manual inspector'}});
  $('perfWindow').value='1800';const old=loadPerformanceCenter(true);await loadPerformanceCenter(false);$('perfWindow').value='60';const recent=loadPerformanceCenter(true);pending['/api/performance/page-snapshot?window=60'].resolve(payload(7));await recent;pending['/api/performance/page-snapshot?window=1800'].resolve(payload(42));await old;
  const kept=performanceTimelineData.samples[0].players;calls.length=0;const failed=loadPerformanceCenter(true);const error=new Error('busy');error.status=503;pending['/api/performance/page-snapshot?window=60'].reject(error);await failed;
  const requests=calls.filter(x=>x.startsWith('/api/performance/'));
  renderPerformanceOverview({available:true,suiteTimings:[{name:'Web request elapsed (includes waiting)',timePerMinuteMilliseconds:9000},{name:'Web game-thread callback wall time',timePerMinuteMilliseconds:1500}]});
  return {kept,requests,manual:$('perfConflictSummary').textContent,first:$('perfSuiteTimings').rows[0].textContent,waitClass:$('perfSuiteTimings').rows[1].cells[2].className};
 });
 assert.equal(result.kept,7);assert.equal(result.requests.length,1,'busy responses must not trigger a multi-endpoint fallback burst');assert.match(result.manual,/Manual/);assert.match(result.first,/callback wall time/);assert.equal(result.waitClass,'muted');assert.deepEqual(errors,[]);
 console.log('PASS hidden/revealed and resized chart recovery, stale range responses, no busy fallback storm, manual inspector state and wait/execution distinction.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
