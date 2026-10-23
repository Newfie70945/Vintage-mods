const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage();await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));await page.goto('http://memory.invalid/');
 const result=await page.evaluate(()=>{
 const a={unixMilliseconds:1000,totalAllocatedMb:100,gen2Collections:2,processStartedUnixMilliseconds:50};
 const b={unixMilliseconds:11000,totalAllocatedMb:850,gen2Collections:3,processStartedUnixMilliseconds:50};
 return [memorySnapshotAllocationRate(a,b),memorySnapshotAllocationRate(b,a),memorySnapshotAllocationRate(a,a),memorySnapshotAllocationRate(a,{...b,processStartedUnixMilliseconds:51}),memorySnapshotAllocationRate(a,{...b,totalAllocatedMb:1}),memorySnapshotAllocationRate(a,{...b,gen2Collections:1}),memorySnapshotAllocationRate({...a,processStartedUnixMilliseconds:0},b)];});
 assert.deepEqual(result,[75,75,null,null,null,null,null]);
 const compact=await page.evaluate(()=>{
 const row=(start,hasTickEvidence)=>({startUnixMilliseconds:start,severity:'High',primaryCategory:'Fixture',confidence:'Low',evidence:'Synthetic evidence',nextAction:'Inspect',hasTickEvidence});
 const d={available:true,diagnosticsEnabled:true,smart:{},current:{},incidents:[row(1000,true),row(2000,false)],suiteTimings:[{name:'Web request elapsed (includes waiting)',timePerMinuteMilliseconds:999999},{name:'Web game-thread callback wall time',timePerMinuteMilliseconds:100}]};
 performanceOverviewData=d;$('perfEvidenceFilter').value='measured';renderPerformanceOverview(d);incidentCompareStarts=[1000,2000];compareSelectedIncidents();
 return {count:$('perfIncidentCount').textContent,compared:$('perfIncidentCompare').textContent,quick:$('perfQuickRead').textContent,legacy:incidentHasTickEvidence({profileEvidence:{ticks:[{}]}})};
 });
 assert.match(compact.count,/1 \/ 2/);assert.match(compact.compared,/Synthetic evidence/);assert.match(compact.quick,/callback wall time/);assert.ok(!compact.quick.includes('request elapsed'));assert.equal(compact.legacy,true);
 console.log('PASS same-run allocation rate, reversed selection, zero interval, process restart, counter reset and legacy snapshots.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
