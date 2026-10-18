const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],writes=[];
 const optimizer={version:'0.4.0',settings:{enabled:true,safeMode:false,onlinePlayerSnapshots:true,aiFilterDispatch:true,repulseDelegateReuse:true,nearbyPlayerQueries:false,minimumNearbyClients:128,bulkStreamReads:true,assetCompressionCache:true,assetCacheMaxMiB:16},profiling:false,modules:[{name:'assetCompressionCache',status:'conflict',reason:'foreign <script>unsafe</script>'}],counters:{assetCacheHits:4,assetCacheBytes:1048576}};
 let fail=false;
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>{
  const req=route.request(),url=new URL(req.url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
  if(url.pathname==='/api/optimizations/newfies'){
   if(req.method()==='POST'){const body=req.postDataJSON();writes.push(body);if(!fail&&body.operation==='set')Object.assign(optimizer.settings,body.settings);}
   return route.fulfill({contentType:'application/json',body:JSON.stringify({available:true,success:!fail,message:fail?'Save failed':'Settings applied',optimizer})});
  }
  return route.fulfill({contentType:'application/json',body:'{}'});
 });
 await page.goto('http://nos.invalid/');
 await page.evaluate(()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:false});setSection('optimizations',false);});await page.evaluate(()=>loadNos());
 assert.match(await page.textContent('#nosModules'),/conflict/);assert.equal(await page.locator('#nosModules script').count(),0);
 await page.fill('#nosMinimumNearbyClients','200');await page.evaluate(()=>loadNos());assert.equal(await page.inputValue('#nosMinimumNearbyClients'),'200','refresh preserves draft');
 fail=true;await page.click('#nosApply');await page.waitForFunction(()=>!nosBusy);assert.equal(await page.inputValue('#nosMinimumNearbyClients'),'200','save failure preserves draft');
 fail=false;await page.click('#nosApply');await page.waitForFunction(()=>!nosBusy);assert.equal(writes.at(-1).settings.minimumNearbyClients,200);assert.equal(await page.evaluate(()=>nosDirty),false);
 await page.evaluate(()=>{safeDiagnosticsModeGlobal=true;renderNos({available:true,optimizer:nosSnapshot});});assert.equal(await page.locator('#nosApply').isDisabled(),true);const before=writes.length;await page.evaluate(()=>nosAction('set'));assert.equal(writes.length,before);
 await page.evaluate(()=>{safeDiagnosticsModeGlobal=false;currentSessionRole='ReadOnly';renderNos({available:true,optimizer:nosSnapshot});});assert.equal(await page.locator('#nosEnabled').isDisabled(),true);
 await page.evaluate(()=>{currentSessionRole='Owner';nosSnapshot.profiling=true;renderNos({available:true,optimizer:nosSnapshot});});assert.equal(await page.locator('#nosApply').isDisabled(),true);assert.equal(await page.locator('#nosProfileStop').isDisabled(),false);
 await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'controls fit mobile');
 await page.evaluate(()=>{nosSnapshot.profiling=false;delete nosSnapshot.settings.aiFilterDispatch;delete nosSnapshot.settings.repulseDelegateReuse;renderNos({available:true,optimizer:nosSnapshot});});
 assert.equal(await page.locator('#nosAiFilterDispatch').isDisabled(),true,'older NOS lacks new fields');await page.evaluate(()=>nosAction('set'));await page.waitForFunction(()=>!nosBusy);assert.equal(Object.hasOwn(writes.at(-1).settings,'aiFilterDispatch'),false,'unsupported fields are not sent to older NOS');
 await page.evaluate(()=>renderNos({available:false,message:'Install matching NOS'}));assert.equal(await page.locator('#nosControls').isHidden(),true);assert.match(await page.textContent('#nosState'),/Install matching/);
 assert.deepEqual(errors,[]);console.log('PASS: NOS module/conflict display, save/draft failure behavior, Safe Mode, read-only, capture locks, absent module and mobile layout');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
