const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));
 await page.goto('http://sidebar.invalid/');await page.waitForLoadState('networkidle');
 const r=await page.evaluate(async()=>{
  document.body.classList.remove('loginMode');$('app').style.display='block';
  setNavStatus('health','bad','Known health issue');initNavIndicators();
  const before=[...document.querySelectorAll('#nav .navStatusDot')].every(el=>el.classList.contains('visible'));
  const health=navButton('health').querySelector('.navStatusDot').className;
  initNavIndicators();const duplicates=document.querySelectorAll('#nav [data-section="health"] .navStatusDot').length;
  const calls=[];let fail=true;api=async path=>{calls.push(path);if(path==='/api/webmap'&&fail)throw new Error('temporary outage');return {available:true,enabled:true,running:true,openCount:0,investigatingCount:0,criticalCount:0,watchCount:0,config:{enabled:true}};};
  await refreshNavIndicators(true);
  const unavailable=navButton('webmap').querySelector('.navStatusDot').title;
  calls.length=0;fail=false;await refreshNavIndicators(false);const retried=calls.includes('/api/webmap'),noExtraStatic=!calls.includes('/api/compatibility');
  const recovered=navButton('webmap').querySelector('.navStatusDot').className;
  calls.length=0;await refreshNavIndicators(false);const noRepeatedStatic=!calls.includes('/api/webmap');
  navStaticStatusChecked.set('webmap',Date.now()-300001);calls.length=0;await refreshNavIndicators(false);const scheduled=calls.includes('/api/webmap');
  const warning=document.querySelector('.bmNotice').textContent;
  return {before,health,duplicates,unavailable,retried,noExtraStatic,recovered,noRepeatedStatic,scheduled,warning};
 });
 const transport=await page.evaluate(async()=>{
  const originalFetch=window.fetch,originalTimeout=window.setTimeout;
  window.setTimeout=(fn,ms,...args)=>originalTimeout(fn,ms===30000?10:ms,...args);
  const aborted=[];let writeSignal;
  try{
   window.fetch=(path,options)=>new Promise((resolve,reject)=>{options.signal.addEventListener('abort',()=>reject(new DOMException('aborted','AbortError')),{once:true});});
   try{await apiCore('/fixture-hung-read');}catch(e){aborted.push(e.name);}
   const controller=new AbortController();const pending=apiCore('/fixture-caller-cancel',{signal:controller.signal});controller.abort();try{await pending;}catch(e){aborted.push(e.name);}
   window.fetch=async(path,options)=>{writeSignal=options.signal;return {ok:true,status:200,json:async()=>({ok:true})};};
   await apiCore('/fixture-write',{method:'POST',body:'{}'});
   return {aborted,writeHasSignal:!!writeSignal};
  }finally{window.fetch=originalFetch;window.setTimeout=originalTimeout;}
 });
 assert.deepEqual(transport.aborted,['AbortError','AbortError']);assert.equal(transport.writeHasSignal,false);
 assert(r.before);assert.match(r.health,/bad/);assert.equal(r.duplicates,1);assert.match(r.unavailable,/unavailable/);assert(r.retried);assert(r.noExtraStatic);assert.match(r.recovered,/good/);assert(r.noRepeatedStatic);assert(r.scheduled);assert.match(r.warning,/personal server/);assert.match(r.warning,/contact Newfie/);assert.match(r.warning,/use at your own risk/);assert.deepEqual(errors,[]);
 console.log('PASS sidebar waiting states, cached status replay, bounded static retries, recovery, freshness scheduling and migrator information.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
