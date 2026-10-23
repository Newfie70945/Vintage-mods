const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));await page.goto('http://logevidence.invalid/');
 await page.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});logIntelligenceData={issues:[{title:'Fixture error',category:'Runtime',impact:'High',occurrences:50000,recentOccurrences:2,recentRatePerMinute:0.4,recentObservedSeconds:300,origin:'Example.<img src=x onerror=alert(1)>.Run',likelyModName:'Vintage Story native build logger',samples:[]}]};renderLogIntelligence();showLogIssue(0);});
 assert.match(await page.locator('#logMetrics').textContent(),/Historical occurrences/);
 assert.match(await page.locator('#logIssueRows').textContent(),/50,000 total2 observed in 5m/);
 assert.match(await page.locator('#logIssueDetail').textContent(),/0.4\/min/);
 assert.match(await page.locator('#logIssueDetail').textContent(),/Archived and pre-start entries do not count/);
 assert.equal(await page.locator('#logIssueDetail img').count(),0,'stack origin escaped');
 await page.evaluate(()=>{logIntelligenceData.issues[0].origin='';logIntelligenceData.issues[0].recentRatePerMinute=null;showLogIssue(0);});
 assert.match(await page.locator('#logIssueDetail').textContent(),/legacy aggregate/);assert.match(await page.locator('#logIssueDetail').textContent(),/warming up/);
 assert.deepEqual(errors,[]);console.log('PASS historical/recent counts, coverage, rate warmup, legacy context and origin escaping.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
