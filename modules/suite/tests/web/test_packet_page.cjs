const assert=require('node:assert/strict'),{chromium}=require('playwright');
const html=require('./embedded_page.cjs')();
const settings={noclipKickAfterCorrections:true,noclipCorrectionsBeforeKick:7,noclipCorrectionDecaySeconds:180,noclipCorrectionMinimumIntervalSeconds:15,ingressEnabled:false,confirmedSpeedRepeatRisk:30,kickRepeatedConfirmedSpeed:false,movementModeLimitsEnabled:true,maxHorizontalSpeed:35,groundMaxHorizontalSpeed:42,swimmingMaxHorizontalSpeed:25,airborneMaxHorizontalSpeed:38,mountProfilesEnabled:true,mountProfileEnforcementEnabled:true,unknownMountEnforcementEnabled:false,mountProfileConfirmSamples:4,mountProfileConfirmWindowMs:1500,mountRiderCorrelationEnabled:true};
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
 const collected=await page.evaluate(()=>collectAntiCheatPacketGuard());
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
 await page.evaluate(async()=>{api=async(url,options)=>{window.packetSaved=JSON.parse(options.body);return {message:'saved'}};loadAntiCheat=async()=>{antiCheatPacketGuard={...window.packetSaved};renderAntiCheatPacketGuard(antiCheatPacketGuard,{enabled:true})};await saveAntiCheatPacketGuard();});
 assert.equal(await page.evaluate(()=>window.packetSaved.ingressEnabled),true,'real save transmits ingress toggle');
 assert.equal(await page.evaluate(()=>window.packetSaved.draconisMaxHorizontalSpeed),145,'real save transmits edited mount speed');
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
 assert.deepEqual(errors,[]);console.log('PASS: 33 fields display/collect, real save, drafts/failed saves/discard, keyboard tabs, read-only access, desktop/mobile layouts.');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
