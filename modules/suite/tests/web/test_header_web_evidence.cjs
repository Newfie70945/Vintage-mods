const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:r.request().url().endsWith('/')?'text/html':'application/json',body:r.request().url().endsWith('/')?html:'{}'}));
 await page.goto('http://web-evidence.invalid/');
 const result=await page.evaluate(async()=>{
  const routes=[];api=async url=>{routes.push(url);return {playersOnline:28,profiler:{enabled:true,current:{cpuPercent:42,mainThreadDelayMs:5,loadedEntities:3000},smart:{available:true,healthScore:85,capturedUnixMilliseconds:10000}},blockLogger:{pending:10},operations:{activeAlerts:2}};};
  await refreshGlobalStatus(true);
  const incident={startUnixMilliseconds:10000,endUnixMilliseconds:11000,worstDelayMs:1000,webServer:{recentSlowRequests:[
   {unixMilliseconds:8000,endUnixMilliseconds:10500,method:'GET',path:'/api/dashboard<script>',statusCode:200,totalMilliseconds:2500,mainThreadWorkMilliseconds:300,serializationMilliseconds:1.25,serializedSnapshotReused:true,responseBytes:12345,workWindows:[{startUnixMilliseconds:9900,endUnixMilliseconds:10200,wallMilliseconds:300,gcPauseMilliseconds:100}]},
   {unixMilliseconds:20000,endUnixMilliseconds:21000,path:'/unrelated'}]}};
  const rendered=incidentWebEvidenceHtml(incident);lagIncidentDrawerPayload={incident};
  const summary=lagIncidentTextSummary();
  let copied='';operationsData={};copyText=async text=>{copied=text;};await copyQuickReport();
  const reportRoute=routes.pop();
  return {routes,reportRoute,copied,players:$('globalPlayers').textContent,health:$('globalHealth').textContent,title:$('globalHealth').title,rendered,summary,empty:incidentWebEvidenceHtml({})};
 });
 assert.deepEqual(result.routes,['/api/header']);assert.equal(result.reportRoute,'/api/dashboard');assert.match(result.copied,/Players: 28/);assert.equal(result.players,'28');assert.equal(result.health,'85/100');assert.match(result.title,/Last calculated/);
 assert.match(result.rendered,/Cached bytes reused/);assert.match(result.rendered,/GC 100.0ms/);assert.match(result.rendered,/dashboard&lt;script&gt;/);assert(!result.rendered.includes('/unrelated'));assert.match(result.summary,/JSON 1.3ms, bytes reused true/);assert.match(result.empty,/No matching completed request/);assert.deepEqual(errors,[]);
 console.log('PASS lightweight header request, counters, cached score freshness, interval-filtered evidence, escaped paths, JSON/cache diagnostics and copied summary.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
