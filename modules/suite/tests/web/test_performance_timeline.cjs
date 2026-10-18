const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>route.fulfill({contentType:route.request().url().endsWith('/')?'text/html':'application/json',body:route.request().url().endsWith('/')?html:'{}'}));
 await page.goto('http://performance.invalid/');
 const result=await page.evaluate(()=>{
  const ranges=Array.from($('perfWindow').options,x=>Number(x.value));
  const initial=performanceWindowSeconds();
  const labels=[];const original=CanvasRenderingContext2D.prototype.fillText;
  CanvasRenderingContext2D.prototype.fillText=function(text,...args){labels.push(text);return original.call(this,text,...args);};
  performanceTimelineData={samples:[{unixMilliseconds:1000,players:2},{unixMilliseconds:2000,players:12}]};renderPerformanceTimeline();
  CanvasRenderingContext2D.prototype.fillText=original;
  let calls=0;loadPerformanceCenter=async()=>{calls++;};$('perfWindow').value='3600';$('perfWindow').onchange();
  const hour=performanceWindowLabel();$('perfTimelineRange').click();
  renderTickDebug({enabled:true,profiler:{},profileSlowTicks:[{unixMilliseconds:1000,tickMs:250,topCauseSummary:'<script>bad</script>'}],profileSlowCauses:[{marker:'mod:ai',count:8,worstTickMs:250,modId:'example',confidence:'High'}]});
  return {ranges,initial,labels,hour,next:performanceWindowSeconds(),calls,text:$('tickDebugHistory').textContent,script:!!$('tickDebugHistory').querySelector('script')};
 });
 assert.deepEqual(result.ranges,[60,300,900,1800,3600]);assert.equal(result.initial,1800);assert(result.labels.includes('Players'));assert(result.labels.includes('12'));assert.equal(result.hour,'1-hour');assert.equal(result.next,60);assert.equal(result.calls,2);assert.match(result.text,/mod:ai/);assert.match(result.text,/example/);assert.match(result.text,/250.0ms/);assert.equal(result.script,false);assert.deepEqual(errors,[]);
 console.log('PASS: player chart, 1m–1h ranges/default, shared range controls, cause counts, slow ticks and escaped markers');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
