const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const source=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPage.cs'),'utf8');
const html=process.env.NSS_UI_HTML?fs.readFileSync(process.env.NSS_UI_HTML,'utf8'):require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],external=[],saved=[];
 page.on('pageerror',e=>errors.push(e.message));
 let config={Title:'Fixture server',Subtitle:'Welcome',Content:'Original main',RulesVtml:'Original rules',Format:0,EnableRulesTab:true,AccentColor:'#ffc857',Width:980,Height:720,FutureSetting:'preserve me'},gets=0,delayRead=false,releaseRead=null,failSave=false,failRead=false,acceptDialog=false;
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());let result={};
  if(url.hostname!=='motd-fixture.invalid')external.push(request.url());
  if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
  if(url.pathname==='/api/community-motd'){
   if(request.method()==='GET'){gets++;if(failRead)return route.fulfill({status:500,contentType:'application/json',body:JSON.stringify({error:'Fixture refresh failed'})});if(delayRead){delayRead=false;await new Promise(resolve=>releaseRead=resolve)}}
   else {const posted=request.postDataJSON();if(posted.action==='save'){saved.push(posted.config);if(failSave)return route.fulfill({status:500,contentType:'application/json',body:JSON.stringify({error:'Fixture save failed'})});config=posted.config}}
   result={available:true,version:'2.1.2',message:'Fixture connected',configJson:JSON.stringify(config)};
  }
  await route.fulfill({contentType:'application/json',body:JSON.stringify(result)});
 });
 await page.addInitScript(()=>localStorage.setItem('nsu.uiScale','1.00'));
 await page.goto('http://motd-fixture.invalid/');await page.evaluate(()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:true});setSection('communityMotd',false)});await page.evaluate(()=>loadCommunityMotd(true));
await page.waitForFunction(()=>communityMotdBaseline!==null&&communityMotdData?.available,undefined,{timeout:5000});
 await page.evaluate(()=>setSection('communityMotd',false));await page.waitForTimeout(120);
 await page.locator('#communityMotdEditor').fill('Hello');await page.evaluate(()=>$('communityMotdEditor').setSelectionRange(0,5));await page.locator('[data-motd-insert="bold"]').click();
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'<strong>Hello</strong>');
 await page.locator('[data-motd-content="rules"]').click();await page.locator('#communityMotdEditor').fill('Rule draft');await page.locator('[data-motd-content="main"]').click();
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'<strong>Hello</strong>','tab changes preserve source');
 const before=gets;await page.evaluate(()=>loadCommunityMotd(false));assert.equal(gets,before,'automatic refresh paused while dirty');
 await page.locator('#communityMotdFormat').selectOption('Markdown');await page.locator('#communityMotdEditor').fill('## Heading\n**Bold** and *italic*');
 await page.waitForFunction(()=>document.querySelector('#communityMotdPreviewBody strong')?.textContent==='Heading');
 await page.locator('[data-motd-content="rules"]').click();await page.evaluate(()=>$('communityMotdEditor').setSelectionRange(0,$('communityMotdEditor').value.length));await page.locator('[data-motd-insert="bold"]').click();
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'<strong>Rule draft</strong>','secondary tabs always use VTML');
 await page.locator('[data-motd-content="main"]').click();await page.locator('#communityMotdFormat').selectOption('VTML');
 await page.locator('#communityMotdEditor').fill('<strong>Safe</strong><img src="https://bad.invalid/image" onerror="window.__motdPwn=1"><script>window.__motdPwn=1</script><iframe src="https://bad.invalid/frame"></iframe><a href="javascript:window.__motdPwn=1">Link</a><font color="#00ff00" size="999" onclick="window.__motdPwn=1">Green</font>');
 await page.waitForFunction(()=>document.querySelector('#communityMotdPreviewBody strong')?.textContent==='Safe');
 assert.equal(await page.locator('#communityMotdPreviewBody img,#communityMotdPreviewBody script,#communityMotdPreviewBody iframe,#communityMotdPreviewBody a,#communityMotdPreviewBody [onclick]').count(),0);
 assert.equal(await page.evaluate(()=>window.__motdPwn),undefined);assert.deepEqual(external,[],'preview loads no content resources');
 await page.locator('#communityMotdSave').click();await page.waitForFunction(()=>!communityMotdBusy&&!communityMotdDirty);
 assert.equal(saved.at(-1).RulesVtml,'<strong>Rule draft</strong>');assert.equal(saved.at(-1).FutureSetting,'preserve me');
 delayRead=true;await page.evaluate(()=>{void loadCommunityMotd(false)});await page.waitForFunction(()=>communityMotdLoading);await page.waitForTimeout(80);
 await page.locator('#communityMotdEditor').fill('Edited during refresh');assert.ok(releaseRead);releaseRead();await page.waitForFunction(()=>!communityMotdLoading);
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'Edited during refresh','late response cannot overwrite typing');
 page.on('dialog',dialog=>acceptDialog?dialog.accept():dialog.dismiss());await page.locator('#communityMotdRefresh').click();assert.equal(await page.locator('#communityMotdEditor').inputValue(),'Edited during refresh','cancelled refresh retains draft');
 failSave=true;await page.locator('#communityMotdSave').click();await page.waitForFunction(()=>!communityMotdBusy);assert.equal(await page.evaluate(()=>communityMotdDirty),true,'failed save retains draft');
 acceptDialog=true;await page.locator('#communityMotdResetSeen').click();await page.waitForFunction(()=>!communityMotdBusy);
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'Edited during refresh','Reset Seen retains draft');
 failRead=true;await page.locator('#communityMotdRefresh').click();await page.waitForFunction(()=>!communityMotdLoading);
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),'Edited during refresh','failed manual refresh retains draft');failRead=false;
 await page.locator('#communityMotdDiscard').click();await page.waitForFunction(()=>!communityMotdDirty);
 assert.equal(await page.locator('#communityMotdEditor').inputValue(),config.Content,'revert restores last saved source');
 failSave=false;await page.locator('[data-motd-content="announcements"]').click();assert.equal(await page.locator('#motdNewsBuilder').isVisible(),true);
 await page.locator('#motdPostTitle').fill('Maintenance <img src=x onerror=alert(1)>');await page.locator('#motdPostBody').fill('What changed\n<strong>literal markup</strong>');await page.locator('#motdPostDate').fill('2026-10-16');
 const savesBefore=saved.length;await page.locator('#motdPostInsert').click();assert.equal(saved.length,savesBefore,'Add to draft never publishes');
 assert.match(await page.locator('#communityMotdEditor').inputValue(),/&lt;img/);assert.equal(await page.locator('#communityMotdPreviewBody img').count(),0);assert.match(await page.locator('#communityMotdPreviewBody').textContent(),/2026-10-16/);
 await page.locator('[data-motd-content="updates"]').click();await page.locator('#motdPostTitle').fill('Release notes');await page.locator('#motdPostBody').fill('Fixed issue one\nImproved feature two');await page.locator('#motdPostInsert').click();await page.locator('#communityMotdSave').click();await page.waitForFunction(()=>!communityMotdBusy&&!communityMotdDirty);
 assert.match(saved.at(-1).AnnouncementsVtml,/Maintenance/);assert.match(saved.at(-1).UpdatesVtml,/Release notes/);assert.match(saved.at(-1).UpdatesVtml,/Fixed issue one/);
 await page.locator('[data-motd-content="main"]').click();assert.equal(await page.locator('#motdNewsBuilder').isVisible(),false);
 await page.locator('#communityMotdWriting').click();assert.equal(await page.locator('#communityMotdWriting').getAttribute('aria-pressed'),'true');
 await page.setViewportSize({width:390,height:844});await page.waitForTimeout(100);
 assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+2),'mobile editor stays inside viewport');
 await page.evaluate(()=>{currentSessionRole='ReadOnly';updateCommunityMotdDraft()});assert.equal(await page.locator('#communityMotdEditor').isDisabled(),true);assert.equal(await page.locator('[data-motd-insert="bold"]').isDisabled(),true);assert.equal(await page.locator('#communityMotdSave').isDisabled(),true);assert.equal(await page.locator('#motdPostInsert').isDisabled(),true);
 assert.deepEqual(errors,[]);console.log('PASS: MOTD formatting, per-tab drafts, safe live preview, background/in-flight refresh protection, complete save payload, failed save retention, cancelled discard, mobile writing mode and read-only controls');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
