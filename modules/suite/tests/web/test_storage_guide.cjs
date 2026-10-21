const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:390,height:844}}),errors=[],writes=[];
 page.on('pageerror',e=>errors.push(e.message));await page.route('**/*',route=>{if(route.request().method()==='POST')writes.push(route.request().url());return route.fulfill({contentType:route.request().url().endsWith('/')?'text/html':'application/json',body:route.request().url().endsWith('/')?html:'{}'});});
 await page.goto('http://guide.invalid/');await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true,safeDiagnosticsMode:false});setSection('databaseStorage',false);document.getElementById('databaseStorageGuide').open=true;});
 const text=await page.textContent('#databaseStorageGuide');for(const setting of ['RetentionDays','LogContainerSnapshots','SnapshotOnSlotChange','SnapshotDebounceMs','AggregateEntitySpawnDespawn','DatabaseBackupRetentionCount','AutoDatabaseBackupIntervalHours'])assert(text.includes(setting),setting);
 assert.match(text,/0 disables automatic expiry/);assert.match(text,/Take\/put logs alone may not reconstruct/);assert.match(text,/does not shrink.*immediately/);assert.match(text,/Never delete live WAL\/SHM/);
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false);assert.deepEqual(writes,[]);assert.deepEqual(errors,[]);console.log('PASS mobile storage guide, exact settings, recovery tradeoffs, no write actions and script errors.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
