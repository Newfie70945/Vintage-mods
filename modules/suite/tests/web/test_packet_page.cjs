const assert=require('node:assert/strict'),{chromium}=require('playwright');
const html=require('./embedded_page.cjs')();
const settings={newActionShadowMode:true,combatChecksEnabled:true,inventoryChecksEnabled:true,deniedActionResyncEnabled:true,escalationWarningsEnabled:true,serverImpulseAllowanceEnabled:true,combatMaxRequestsPerSecond:25,inventoryMaxRequestsPerSecond:120,blockNukerEnabled:true,blockNukerLimit:35,blockNukerKickEnabled:false,noclipKickAfterCorrections:true,noclipCorrectionsBeforeKick:7,noclipCorrectionDecaySeconds:180,noclipCorrectionMinimumIntervalSeconds:15,ingressEnabled:false,confirmedSpeedRepeatRisk:30,kickRepeatedConfirmedSpeed:false,movementModeLimitsEnabled:true,maxHorizontalSpeed:35,groundMaxHorizontalSpeed:42,swimmingMaxHorizontalSpeed:25,airborneMaxHorizontalSpeed:38,mountProfilesEnabled:true,mountProfileEnforcementEnabled:true,unknownMountEnforcementEnabled:false,mountProfileConfirmSamples:4,mountProfileConfirmWindowMs:1500,mountRiderCorrelationEnabled:true};
for(const prefix of ['draconis','shipwright','unknownMount'])Object.assign(settings,{[prefix+'MaxHorizontalSpeed']:110,[prefix+'MaxVerticalSpeed']:75.25,[prefix+'MaxAcceleration']:500,[prefix+'MaxRiderDistance']:18,[prefix+'MaxRiderRelativeSpeed']:32});
const initial={...settings,enabled:true,movementEnabled:true,exemptAdmins:true,exemptMountedPlayers:true,tpRunesCompatibility:true,reachEnabled:true,miningEnabled:true};
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1100}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>route.fulfill({contentType:new URL(route.request().url()).pathname==='/'?'text/html':'application/json',body:new URL(route.request().url()).pathname==='/'?html:'{}'}));
 await page.goto('http://packet-fixture.invalid/');
 await page.evaluate(()=>checkSession());
 await page.evaluate(data=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:true});setSection('anticheat',false);setAntiCheatTab('packet');renderAntiCheatPacketGuard(data,{enabled:true});},initial);
 for(const [key,value] of Object.entries(settings)){
  const id='acPacket'+key[0].toUpperCase()+key.slice(1),input=page.locator('#'+id);
  assert.equal(await input.count(),1,id+' unique');
  if(typeof value==='boolean')assert.equal(await input.isChecked(),value,id);else assert.equal(Number(await input.inputValue()),value,id);
 }
 await page.evaluate(()=>{$('acPacketWeaponMinimumIntervalMs').value='{"game:sword-test":300}';$('acPacketMiningReviewOnlyTools').value='mod:specialpick';});
 const collected=await page.evaluate(()=>collectAntiCheatPacketGuard());
 assert.deepEqual(collected.weaponMinimumIntervalMs,{'game:sword-test':300});assert.deepEqual(collected.miningReviewOnlyTools,['mod:specialpick']);
 await page.evaluate(()=>{$('acPacketWeaponMinimumIntervalMs').dispatchEvent(new Event('input',{bubbles:true}));});
 await page.evaluate(data=>renderAntiCheatPacketGuard(data,{enabled:true}),initial);
 assert.match(await page.locator('#acPacketWeaponMinimumIntervalMs').inputValue(),/sword-test/,'textarea draft survives refresh');
 for(const [key,value]of Object.entries(settings))assert.equal(collected[key],value,'save payload '+key);
 await page.evaluate(()=>{$('acPacketMovementModeLimitsEnabled').checked=false;$('acPacketMovementModeLimitsEnabled').dispatchEvent(new Event('change',{bubbles:true}));});
 await page.evaluate(data=>renderAntiCheatPacketGuard(data,{enabled:true}),initial);
 assert.equal(await page.locator('#acPacketMovementModeLimitsEnabled').isChecked(),false,'checkbox drafts survive refresh');
 await page.locator('#acPacketGroundMaxHorizontalSpeed').fill('55');
 await page.getByRole('tab',{name:'Mounts & exemptions',exact:true}).click();
 await page.locator('#acPacketDraconisMaxHorizontalSpeed').fill('145');
 await page.evaluate(data=>renderAntiCheatPacketGuard(data,{enabled:true}),initial);
 assert.equal(await page.locator('#acPacketGroundMaxHorizontalSpeed').inputValue(),'55','refresh preserves movement draft');
 assert.equal(await page.locator('#acPacketDraconisMaxHorizontalSpeed').inputValue(),'145','refresh preserves mount draft');
 assert.match(await page.locator('#acPacketDraftState').textContent(),/Unsaved/);
 await page.locator('#acPacketDiscard').click();
 assert.equal(await page.locator('#acPacketDraconisMaxHorizontalSpeed').inputValue(),'110');
 await page.locator('#packetTab-mounts').focus();await page.keyboard.press('ArrowRight');
 assert.equal(await page.locator('#packetTab-actions').getAttribute('aria-selected'),'true','keyboard tabs');
 for(const key of ['movement','mounts','actions','policy','diagnostics']){
  await page.locator('#packetTab-'+key).click();
  assert.equal(await page.locator('[data-packet-section]:visible').count(),1,'one section visible');
 }
 await page.locator('#packetTab-mounts').click();
 await page.screenshot({path:'/workspace/nss-revamp/artifacts/packetpage1-desktop.png',fullPage:true});
 // Exercise the real save handler, including failed-save draft retention.
 await page.locator('#acPacketDraconisMaxHorizontalSpeed').fill('145');
 await page.locator('label').filter({has:page.locator('#acPacketIngressEnabled')}).click();
 await page.evaluate(async()=>{api=async()=>{throw Error('fixture save failure')};await saveAntiCheatPacketGuard();});
 assert.equal(await page.locator('#acPacketDraconisMaxHorizontalSpeed').inputValue(),'145');
 assert.equal(await page.evaluate(()=>packetSettingsDirty),true);
 await page.evaluate(async()=>{api=async(url,options)=>{window.packetSaved=JSON.parse(options.body);return {message:'saved',packetGuard:{...window.packetSaved}}};loadAntiCheat=async()=>{antiCheatPacketGuard={...window.packetSaved,draconisMaxHorizontalSpeed:110};renderAntiCheatPacketGuard(antiCheatPacketGuard,{enabled:true})};await saveAntiCheatPacketGuard();});
 assert.equal(await page.evaluate(()=>window.packetSaved.ingressEnabled),true,'real save transmits ingress toggle');
 assert.equal(await page.evaluate(()=>window.packetSaved.draconisMaxHorizontalSpeed),145,'real save transmits edited mount speed');
 assert.equal(await page.locator('#acPacketDraconisMaxHorizontalSpeed').inputValue(),'145','applied save snapshot wins over stale cached read');
 assert.equal(await page.evaluate(()=>packetSettingsDirty),false);
 await page.evaluate(data=>{currentSessionRole='ReadOnly';renderAntiCheatPacketGuard(data,{enabled:true});},initial);
 for(const key of Object.keys(settings))assert.equal(await page.locator('#acPacket'+key[0].toUpperCase()+key.slice(1)).isDisabled(),true,'read-only '+key);
 await page.setViewportSize({width:390,height:844});
 for(const key of ['movement','mounts','actions','policy','diagnostics']){
  await page.locator('#packetTab-'+key).click();
  const overflow=await page.evaluate(()=>{const panel=document.querySelector('[data-ac-panel=packet]');return [...panel.querySelectorAll('input')].filter(e=>e.getClientRects().length).some(e=>e.getBoundingClientRect().right>innerWidth+1)});
  assert.equal(overflow,false,key+' controls fit mobile');
 }
 await page.locator('#packetTab-mounts').click();await page.screenshot({path:'/workspace/nss-revamp/artifacts/packetpage1-mobile.png',fullPage:true});
 await page.evaluate(async()=>{const originalApi=api;api=async()=>({playerUid:'occurrence-fixture',playerName:'Fixture',findings:[],mods:[],assemblies:[],serverOccurrences:[{code:'PACKET_NOCLIP',count:7,firstSeenUtc:1000,lastSeenUtc:2000},{code:'PACKET_NOCLIP_CORRECTION',count:2,firstSeenUtc:1500,lastSeenUtc:2000}]});try{await loadAntiCheatPlayer('occurrence-fixture');}finally{api=originalApi;}});
 assert.match(await page.locator('#acOccurrenceSummary').textContent(),/PACKET_NOCLIP_CORRECTION/);
 assert.equal(await page.locator('#acOccurrenceSummary tbody tr').count(),2,'separate detection/correction rows');
 assert.equal(await page.locator('#acExemptionGrant').isDisabled(),true,'read-only cannot grant exemptions');
 assert.equal(await page.locator('#acExemptionRevoke').isDisabled(),true,'read-only cannot revoke exemptions');
 await page.evaluate(async()=>{const original=api;currentSessionRole='Owner';api=async(path,options)=>{if(path==='/api/anticheat/exemption'){window.exemptionPayload=JSON.parse(options.body);return{message:'Granted'};}return {playerUid:'occurrence-fixture',playerName:'Fixture',findings:[],mods:[],assemblies:[],observationElapsedSeconds:3600,recordedServerOccurrencesPerHour:7,recentServerEvents:[{utc:2000,code:'SERVER_ACTION_COMBAT_WALL',summary:'Review <img src=x onerror=alert(1)>',evidence:'<script>throw 1</script>'}],protectionExemptions:[{scope:'Movement',reason:'<img src=x onerror=alert(1)>',grantedBy:'admin',expiresUtc:3000}]};};try{$('acExemptionReason').value='Known mount compatibility';$('acExemptionSeconds').value='300';await editAntiCheatExemption(false);}finally{api=original;}});
 assert.equal(await page.evaluate(()=>window.exemptionPayload.seconds),300,'grant sends bounded duration');
 assert.equal(await page.locator('#acExemptionList img').count(),0,'exemption reasons are escaped');
 assert.equal(await page.locator('#acServerEventTimeline script').count(),0,'timeline evidence is escaped');
 assert.match(await page.locator('#acActivityRate').textContent(),/7.0 recorded reports/);


 assert.deepEqual(errors,[]);console.log('PASS: 44 scalar fields plus tool policies display/collect, real save, drafts/failed saves/discard, keyboard tabs, read-only access, desktop/mobile layouts.');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
