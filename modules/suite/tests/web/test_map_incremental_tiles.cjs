const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{chromium}=require('playwright');
const source=fs.readFileSync(path.join(__dirname,'../../src/WebMap/WebMapBridgeService.cs'),'utf8');
const replacements={title:'Map fixture','Js(adminUrl)':'""',adminPort:'9465',PublicRefreshSeconds:'30','c.PlayerIconRefreshSeconds':'60'};
const html=source.split('return $$$$"""')[1].split('""";')[0].replace(/\{\{\{\{(.*?)\}\}\}\}/g,(_,key)=>replacements[key]);
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 let generation=100,since=0,changes=[];
 await page.route('**/*',r=>{const u=new URL(r.request().url());return r.fulfill({contentType:u.pathname==='/'?'text/html':'application/json',body:u.pathname==='/'?html:JSON.stringify(u.pathname==='/api/map'?{players:[],waypoints:[],activity:[],badBlockEntities:[],terrain:{state:'ready'},terrainManifest:{generatedUnixMilliseconds:generation,changedTilesSinceUnixMilliseconds:since,changedTiles:changes,tileCount:1,tileSize:128,minWorldX:0,minWorldZ:0,maxWorldXExclusive:128,maxWorldZExclusive:128}}:{})});});
 await page.goto('http://incremental-map.invalid/');await page.waitForFunction(()=>data?.terrainManifest?.generatedUnixMilliseconds===100);
 await page.evaluate(()=>{requestDraw=()=>{};tileCache.clear();window.kept={};tileCache.set('normal/1/0',kept);tileCache.set('normal/0/0',{});tileCache.set('sepia/0/0',{});tileCache.set('normal/lod/2/0/0',{});tileCache.set('normal/lod/2/2/0',{});ox=55;oz=77;scale=.2;followUid='';});
 generation=200;since=100;changes=['0,0'];await page.evaluate(()=>load(false));
 const selective=await page.evaluate(()=>({keys:[...tileCache.keys()],kept:tileCache.get('normal/1/0')===kept,camera:[ox,oz,scale]}));
 assert.deepEqual(selective.keys,['normal/1/0','normal/lod/2/2/0']);assert.equal(selective.kept,true);assert.deepEqual(selective.camera,[55,77,.2]);
 generation=400;since=300;changes=['2,0'];await page.evaluate(()=>load(false));assert.equal(await page.evaluate(()=>tileCache.size),0,'missed revision safely invalidates cache');
 assert.deepEqual(errors,[]);console.log('PASS: incremental publication invalidates changed base/style/ancestor tiles, keeps unrelated images and camera, safely refreshes after missed revisions');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
