// Run the embedded public map in Chromium with HTTP fixtures.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const source=fs.readFileSync(path.join(__dirname,'../../src/WebMap/WebMapBridgeService.cs'),'utf8');
const replacements={title:'Map fixture','Js(adminUrl)':'""',adminPort:'9465',PublicRefreshSeconds:'30','c.PlayerIconRefreshSeconds':'1'};
const html=source.split('return $$$$"""')[1].split('""";')[0].replace(/\{\{\{\{(.*?)\}\}\}\}/g,(_,key)=>replacements[key]);
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});try{
 const page=await browser.newPage(); const errors=[];page.on('pageerror',e=>errors.push(e.message));
 let mapCalls=0,playerCalls=0,inflight=0,maxInflight=0,rate=1,show=true,stamp=100;
 await page.route('**/*',async route=>{
  const url=new URL(route.request().url());let payload={};
  if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
  if(url.pathname==='/api/map'){mapCalls++;payload={players:[{name:'Old fixture',x:1,z:1}],showPlayers:true,activePlayerCount:1,playerSnapshotUnixMilliseconds:1,playerRefreshSeconds:1,terrain:{state:'pending'},waypoints:[],activity:[],badBlockEntities:[]};}
  if(url.pathname==='/api/players'){playerCalls++;inflight++;maxInflight=Math.max(maxInflight,inflight);await new Promise(r=>setTimeout(r,150));payload={players:show?[{name:'Current fixture',x:stamp,z:stamp}]:[],showPlayers:show,activePlayerCount:show?1:0,playerSnapshotUnixMilliseconds:++stamp,playerRefreshSeconds:rate};inflight--;}
  await route.fulfill({contentType:'application/json',body:JSON.stringify(payload)});
 });
 await page.goto('http://map-fixture.invalid/');await page.waitForFunction(()=>data?.players?.[0]?.name==='Current fixture');
 await page.waitForTimeout(2500);assert.ok(playerCalls>=3,'one-second refresh updates players');assert.equal(mapCalls,1,'fast icons do not reload full map');assert.equal(maxInflight,1);
 await page.evaluate(()=>load(false));await page.waitForTimeout(200);assert.equal(await page.evaluate(()=>data.players[0].name),'Current fixture','old full-map response cannot roll positions back');
 rate=2;await page.waitForFunction(()=>playerRefreshMs===2000);const before=playerCalls;await page.waitForTimeout(1200);assert.equal(playerCalls,before,'changed server interval applies without reloading');
 show=false;await page.waitForFunction(()=>data?.showPlayers===false);assert.equal(await page.evaluate(()=>data.players.length),0,'privacy-off removes markers');
 assert.deepEqual(errors,[]);console.log('PASS: embedded map cadence, independent player requests, interval changes, single-flight requests, stale snapshot and privacy handling');
 const panelSource=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPage.cs'),'utf8');
 const panelHtml=require('./embedded_page.cjs')();
 const panel=await browser.newPage();await panel.route('**/*',route=>route.fulfill({contentType:new URL(route.request().url()).pathname==='/'?'text/html':'application/json',body:new URL(route.request().url()).pathname==='/'?panelHtml:'{}'}));
 await panel.goto('http://panel-fixture.invalid/');
 await panel.evaluate(()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:true});renderWebMap({playerRefreshSeconds:7});});
 assert.equal(await panel.locator('#webMapPlayerIconRefreshSeconds').inputValue(),'7');
 await panel.evaluate(async()=>{api=async(url,options)=>{if(options?.method==='POST')window.savedMapConfig=JSON.parse(options.body);return {playerRefreshSeconds:5}};$('webMapPlayerIconRefreshSeconds').value='5';await saveWebMap()});
 assert.equal(await panel.evaluate(()=>window.savedMapConfig.playerIconRefreshSeconds),5,'web panel saves configured interval');
 await panel.evaluate(()=>{currentSessionRole='ReadOnly';renderWebMap({playerRefreshSeconds:5})});
 assert.equal(await panel.locator('#webMapPlayerIconRefreshSeconds').isDisabled(),true,'read-only staff cannot change interval');
 console.log('PASS: WebPanel refresh field displays and saves interval; read-only field disabled');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
