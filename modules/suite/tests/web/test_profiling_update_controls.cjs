const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],writes=[];
 let configured=false,patches=false,fail=false;
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>{
  const req=route.request(),url=new URL(req.url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
  if(req.method()==='POST'){
   writes.push({path:url.pathname,body:req.postDataJSON()});
   if(fail)return route.fulfill({status:503,contentType:'application/json',body:'{"error":"Save failed"}'});
   if(url.pathname==='/api/performance/automatic-profiling')configured=req.postDataJSON().enabled;
   if(url.pathname==='/api/mod-updates/compatibility')patches=req.postDataJSON().allowSameMinorGameUpdates;
  }
  const body=url.pathname==='/api/profiler'?{enabled:true,automaticIncidentProfilingConfigured:configured,samples:[]}:
   url.pathname==='/api/mod-updates'||url.pathname==='/api/mod-updates/check'?{mods:[],allowSameMinorGameUpdates:patches}:
   {success:true,message:'Saved'};
  return route.fulfill({contentType:'application/json',body:JSON.stringify(body)});
 });
 await page.goto('http://controls.invalid/');
 await page.evaluate(()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:false});setSection('profiler',false);});
 await page.evaluate(()=>loadProfiler(false));assert.equal(await page.isChecked('#profAutoCapture'),false);
 await page.evaluate(()=>{$('profAutoCapture').checked=true;return $('profAutoCapture').onchange();});
 assert.equal(configured,true);assert.equal(writes.at(-1).path,'/api/performance/automatic-profiling');assert.equal(await page.isChecked('#profAutoCapture'),true);
 fail=true;await page.evaluate(()=>{$('profAutoCapture').checked=false;return $('profAutoCapture').onchange();});assert.equal(await page.isChecked('#profAutoCapture'),true,'failed save restores prior value');fail=false;
 await page.evaluate(()=>{currentSessionRole='ReadOnly';return loadProfiler(false);});assert.equal(await page.isDisabled('#profAutoCapture'),true);
 await page.evaluate(()=>{currentSessionRole='Owner';safeDiagnosticsModeGlobal=true;return loadProfiler(false);});assert.equal(await page.isDisabled('#profAutoCapture'),true,'safe mode blocks automatic captures');
 await page.evaluate(()=>{safeDiagnosticsModeGlobal=false;setSection('mods',false);return loadMods();});
 await page.evaluate(()=>{$('modsPatchCompatibility').checked=true;return $('modsPatchCompatibility').onchange();});assert.equal(patches,true);assert(writes.some(x=>x.path==='/api/mod-updates/check'),'compatibility edit triggers new lookup');
 await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'controls fit mobile');
 assert.deepEqual(errors,[]);console.log('PASS: automatic profiler opt-in, persisted state, failure rollback, read-only/Safe Mode, compatibility opt-in, refresh and mobile');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
