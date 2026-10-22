const assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs');
const source=fs.readFileSync(require('node:path').join(__dirname,'../../src/WebDashboard/WebDashboardPage.cs'),'utf8');
const start=source.indexOf('let navIndicatorRefreshInFlight=false;'),end=source.indexOf('\nfunction syncNavGroups',start);
(async()=>{
 let pending=[],calls=[],active=0,peak=0,visible=true;
 const context=vm.createContext({Date,Promise,Number,String,setTimeout:fn=>{fn();},lastNavIndicatorRefresh:0,
 $:id=>id==='app'?{style:{display:visible?'block':'none'}}:null,
 setReportsNavState(){},setModUpdatesNavState(){},setAntiCheatNavState(){},setNavStatus(){},setCompatibilityNavState(){},
 api:url=>{calls.push(url);active++;peak=Math.max(peak,active);return new Promise((resolve,reject)=>pending.push(ok=>{active--;ok?resolve({enabled:true}):reject(Error('offline'));}));}});
 vm.runInContext(source.slice(start,end),context);
 const run=vm.runInContext('refreshNavIndicators(true)',context);
 await vm.runInContext('refreshNavIndicators(true)',context);
 assert.equal(calls.length,1,'overlapping refresh must not start another batch');
 for(let i=0;i<9;i++){assert.equal(pending.length,1);pending.shift()(i!==2);for(let j=0;j<12;j++)await Promise.resolve();}
 await run;assert.equal(calls.length,9);assert.equal(peak,1);assert(calls.includes('/api/tick-debug/status'));assert(!calls.includes('/api/tick-debug'));
 const logout=vm.runInContext('refreshNavIndicators(true)',context);visible=false;pending.shift()(true);await logout;assert.equal(calls.length,10,'logout must stop remaining jobs');
 visible=true;const retry=vm.runInContext('refreshNavIndicators(false)',context);assert.equal(calls.length,11,'gate must release after logout');visible=false;pending.shift()(true);await retry;
 console.log('PASS: nine checks, peak concurrency one, refresh coalescing, failure continuation, logout cancellation and restart.');
})().catch(e=>{console.error(e);process.exitCode=1;});
