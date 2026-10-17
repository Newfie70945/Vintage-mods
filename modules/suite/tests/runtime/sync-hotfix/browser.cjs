const fs=require('node:fs');const assert=require('node:assert/strict');const {chromium}=require('playwright');
(async()=>{
 const html=fs.readFileSync(process.argv[2],'utf8');
 const player={playerUid:'fixture-player',playerName:'Fixture Player',status:'Clean',riskScore:0,clientInstalled:true,clientVersion:'1.1.27',modCount:1,reportedModCount:1,assemblyCount:1,findings:[],findingCount:0,riskFamilies:[],packageMetricsSupported:true,platform:'Linux',gameVersion:'1.22.7',lastScanUtc:Date.now(),lastHeartbeatUtc:Date.now()};
 const mod={modId:'fixturemod',version:'1.0.0',fileName:'FixtureMod.zip',sha256:'a'.repeat(64),contentSha256:'b'.repeat(64),loadedOnServer:false,modDbStatus:'Disabled',modDbMessage:'Verification disabled',isCodeMod:true};
 const overview={enabled:true,available:true,onlinePlayers:1,trackedPlayers:1,clientsConnected:1,clean:1,serverCompanionInstalled:true,serverCompanionVersionMatches:true,serverCompanionVersion:'1.1.27',expectedClientVersion:'1.1.27',protocolVersion:13,players:[player],packetGuard:{enabled:true},oreShield:{enabled:true,active:true,status:'Active',protectedOreVariants:300,clientSyncQueuedCells:24,clientSyncQueueCoalesces:20},history:{enabled:true},consequenceRules:[],forbiddenModIds:[],requiredClientModIds:[],approvedClientAssemblySha256s:[],forbiddenFingerprints:[],blacklistProfiles:[],recentConnectionDiagnostics:[],approvedClientModFingerprints:[],modDbHealth:{}};
 const detail={...player,mods:[mod],assemblies:[{assemblyName:'FixtureAssembly',version:'1.0.0',associatedModIds:['fixturemod'],sha256:'c'.repeat(64)}],scannerMetrics:[],runtimeNewAssemblies:[],evidenceCandidates:[],collectedEvidence:[],recentConnectionDiagnostics:[]};
 const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try{for(const width of [1440,390]){
  const context=await browser.newContext({viewport:{width,height:960}});await context.addInitScript(()=>{localStorage.setItem('nsu.section','anticheat');localStorage.setItem('nsu.antiCheatTab','overview');});
  const page=await context.newPage();const errors=[];const paths=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://nss.test/**',async route=>{const path=new URL(route.request().url()).pathname;paths.push(path);
   if(path==='/')return route.fulfill({contentType:'text/html',body:html});
   let body={};if(path==='/api/session')body={authenticated:true,username:'fixture',role:'Owner',owner:true,csrfToken:'fixture',expiresUtc:new Date(Date.now()+3600000).toISOString()};
   else if(path==='/api/anticheat')body=overview;
   else if(path==='/api/anticheat/client-mods')body=[{...player,mods:[mod]}];
   else if(path==='/api/anticheat/player')body=detail;
   else if(path==='/api/anticheat/status')body={enabled:true,available:true,packetGuard:{enabled:true}};
   await route.fulfill({contentType:'application/json',body:JSON.stringify(body)});
  });
  await page.goto('http://nss.test/');await page.waitForFunction(()=>document.querySelector('#acRows')?.textContent.includes('Fixture Player'));
  assert.equal(paths.filter(p=>p==='/api/anticheat/client-mods').length,0,'Overview eagerly loaded mod inventories');
  await page.locator('[data-ac-tab="clientmods"]').first().click();await page.waitForFunction(()=>document.querySelector('#acClientModsRows')?.textContent.includes('fixturemod'));
  assert.equal(paths.filter(p=>p==='/api/anticheat/client-mods').length,1);
  await page.locator('[data-ac-tab="overview"]').first().click();await page.locator('[data-ac-tab="clientmods"]').first().click();
  await page.waitForFunction(()=>document.querySelector('#acClientModsState')?.textContent.includes('player-mod rows'));
  assert.equal(paths.filter(p=>p==='/api/anticheat/client-mods').length,1,'Tab reopening ignored comparison cache');
  await page.locator('[data-ac-tab="players"]').first().click();await page.locator('#acRows [data-ac-player]').first().click();
  await page.waitForFunction(()=>document.querySelector('#acAssemblyRows')?.textContent.includes('FixtureAssembly'));
  assert(paths.includes('/api/anticheat/player'),'Player investigation did not request full details');
  assert.deepEqual(errors,[],'WebPanel browser errors');console.log(`PASS ${width}px: overview stays light; client comparisons load on demand/cache; player investigation retains assemblies; no browser exceptions.`);
  await context.close();
 }}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
