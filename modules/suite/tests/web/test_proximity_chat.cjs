const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 for(const width of [1440,390,320]){
  const page=await browser.newPage({viewport:{width,height:950}}),errors=[],writes=[];
  const state={available:true,markdownEnabled:true,boldPlayerName:true,formatGeneralChat:true,proximityEnabled:false,proximityGroupName:'Local',proximityRange:100,proximityAutoJoin:true,proximityGroupId:0,proximityStatus:'Disabled',roleNameColors:{}};
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>{const path=new URL(route.request().url()).pathname;let data={};if(path==='/')return route.fulfill({contentType:'text/html',body:html});if(path==='/api/session')data={authenticated:true,username:'Owner',role:'Owner',owner:true,safeDiagnosticsMode:false,csrfToken:'fixture'};if(path==='/api/chat-display')data=state;if(path==='/api/chat-display/settings'){const body=route.request().postDataJSON();writes.push(body);Object.assign(state,body,{proximityGroupId:42,proximityStatus:'Active — 75 blocks'});data={success:true,message:'Saved',chatDisplay:state};}return route.fulfill({contentType:'application/json',body:JSON.stringify(data)});});
  await page.goto('http://local-chat.invalid/');await page.waitForFunction(()=>currentSessionOwner);await page.evaluate(()=>{setSection('chatDisplay',false);});await page.evaluate(()=>loadChatDisplay());
  assert.equal(await page.locator('#chatProximityEnabled').isChecked(),false);
  await page.locator('label:has(#chatProximityEnabled)').click();await page.fill('#chatProximityName','Nearby');await page.fill('#chatProximityRange','75');await page.locator('label:has(#chatProximityAutoJoin)').click();await page.click('#chatDisplaySave');await page.waitForFunction(()=>chatDisplayData?.proximityGroupId===42);
  assert.equal(writes.at(-1).proximityEnabled,true);assert.equal(writes.at(-1).proximityRange,75);assert.equal(writes.at(-1).proximityGroupName,'Nearby');assert.equal(writes.at(-1).proximityAutoJoin,false);assert.match(await page.textContent('#chatProximityStatus'),/Group ID 42/);
  const count=writes.length;await page.fill('#chatProximityRange','');await page.click('#chatDisplaySave');await page.waitForTimeout(100);assert.equal(writes.length,count,'empty range not submitted');await page.fill('#chatProximityRange','75');
  const box=await page.locator('#chatProximityRange').boundingBox();assert(box.width>80&&box.x+box.width<=width+1,'range fits viewport');
  await page.evaluate(()=>{currentSessionRole='ReadOnly';renderChatDisplay();});for(const id of ['chatProximityEnabled','chatProximityName','chatProximityRange','chatProximityAutoJoin','chatDisplaySave'])assert.equal(await page.locator('#'+id).isDisabled(),true);
  assert.deepEqual(errors,[]);await page.close();
 }
 console.log('PASS proximity desktop/mobile controls, save/apply roundtrip, validation and read-only access');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
