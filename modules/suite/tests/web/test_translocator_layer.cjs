const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{chromium}=require('playwright');
const source=fs.readFileSync(path.join(__dirname,'../../src/WebMap/WebMapBridgeService.cs'),'utf8');
const replacements={title:'Translocator fixture','Js(adminUrl)':'""',adminPort:'9465',PublicRefreshSeconds:'30','c.PlayerIconRefreshSeconds':'60'};
const html=source.split('return $$$$"""')[1].split('""";')[0].replace(/\{\{\{\{(.*?)\}\}\}\}/g,(_,key)=>replacements[key]);
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',r=>r.fulfill({contentType:new URL(r.request().url()).pathname==='/'?'text/html':'application/json',body:new URL(r.request().url()).pathname==='/'?html:JSON.stringify({showTranslocators:true,translocators:[],players:[],terrain:{state:'pending'},waypoints:[],activity:[],badBlockEntities:[]})}));
 await page.goto('http://translocator.invalid/');await page.waitForFunction(()=>data?.showTranslocators);
 assert.equal(await page.locator('#layerTranslocatorsRow').isVisible(),false); // panel starts closed
 await page.evaluate(()=>openPanel('layersPanel'));assert.equal(await page.locator('#layerTranslocatorsRow').isVisible(),true);
 const result=await page.evaluate(()=>{
  const a={id:'0,60,0',x:0,y:60,z:0,state:'active',hasTarget:false};let lines=0;
  const oldLine=ctx.lineTo;ctx.lineTo=()=>lines++;
  data.translocators=[a];drawTranslocators();const single=lines;lines=0;
  // Defend against a stale/bad client object claiming a missing destination.
  a.hasTarget=true;a.targetX=100;a.targetY=70;a.targetZ=100;drawTranslocators();const hidden=lines;lines=0;
  showMarker({kind:'translocator',obj:a,x:0,y:0});const unknownPopup=markerCard.textContent;
  data.translocators.push({id:'100,70,100',x:100,y:70,z:100,state:'active',hasTarget:false});a.hasTarget=false;drawTranslocators();const unlinked=lines;lines=0;a.hasTarget=true;drawTranslocators();const linked=lines;
  ctx.lineTo=oldLine;showMarker({kind:'translocator',obj:a,x:0,y:0});
  return {single,hidden,unlinked,linked,unknownPopup,popup:markerCard.textContent};
 });
 assert.equal(result.single,result.hidden,'missing endpoint cannot produce a connection line');assert.equal(result.linked,result.unlinked+1,'known endpoint produces exactly one connecting line');assert.ok(!result.unknownPopup.includes('Leads to'),'unknown destination omitted from popup');
 assert.ok(!result.popup.includes('0 uses'),'invented usage statistics removed');
 await page.evaluate(()=>{data.showTranslocators=false;updateStatus()});assert.equal(await page.locator('#layerTranslocatorsRow').isVisible(),false);
 const panel=await browser.newPage();await panel.route('**/*',r=>r.fulfill({contentType:new URL(r.request().url()).pathname==='/'?'text/html':'application/json',body:new URL(r.request().url()).pathname==='/'?require('./embedded_page.cjs')():'{}'}));
 await panel.goto('http://translocator-panel.invalid/');await panel.evaluate(()=>{showApp({username:'Owner',role:'Owner',owner:true});setSection('webMap',false);renderWebMap({showTranslocators:true,translocatorDiscoveryRadius:8,translocatorStatus:'Tracking shared discoveries'});});
 assert.equal(await panel.locator('#webMapShowTranslocators').isChecked(),true);assert.equal(await panel.locator('#webMapTranslocatorRadius').inputValue(),'8');
 await panel.evaluate(async()=>{api=async(url,options)=>{window.saved=JSON.parse(options.body);return {}};await saveWebMap();});
 assert.equal(await panel.evaluate(()=>saved.showTranslocators),true);assert.equal(await panel.evaluate(()=>saved.translocatorDiscoveryRadius),8);
 await panel.evaluate(()=>{currentSessionRole='ReadOnly';renderWebMap({})});assert.ok(await panel.locator('#webMapTranslocatorRadius').isDisabled());
 assert.deepEqual(errors,[]);console.log('PASS translocator layer visibility, missing-endpoint defense, connection display, settings roundtrip and read-only controls');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
