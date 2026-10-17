const assert=require('node:assert/strict'),{chromium}=require('playwright');
const html=process.env.NSS_UI_HTML?require('node:fs').readFileSync(process.env.NSS_UI_HTML,'utf8'):require('./embedded_page.cjs')();
const fs=require('node:fs'),path=require('node:path');const previews=process.argv[2]||'/workspace/nss-revamp/tmp/ui1-previews';fs.mkdirSync(previews,{recursive:true});
(async()=>{
 const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],reads=[],writes=[];
  page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
  const row={code:'default',name:'Survivor',description:'',privilegeLevel:0,colorHex:'#FFFFFF',nameColorHex:'',defaultGameMode:'Survival',homeLimit:null,landClaimAllowance:100,landClaimMaxAreas:3,claimMinX:5,claimMinY:5,claimMinZ:5,privileges:['chat'],policies:{},autoGrant:false,assignedPlayers:1,isDefault:true};
  const customization={surfaceTexture:'slate',textureStrength:12,showModuleIcons:true,highContrastBorders:false};
  const chat={available:true,boldPlayerName:true,formatGeneralChat:true,showRoleBeforeName:true,showTimestampBeforeMessage:true,chatFormat:'{role} {name}: {timestamp} {message}',timestampFormat:'HH:mm'};
  const policy={name:'Survivor',homeLimit:2,homeSource:'Global default',claimAreas:3,privileges:['chat'],deniedPrivileges:[],commands:[{command:'home',allowed:true,cooldownSeconds:30,source:'Command default'}]};
  const roles={available:true,policiesAvailable:true,colorsAvailable:true,useRoleNameColors:false,revision:'first',globalHomeLimit:2,knownPrivileges:['chat','controlserver','build','xskills:use'],gameModes:['Survival'],defaultCooldowns:{},roles:[row]};
  await page.route('**/*',async route=>{
   const req=route.request(),url=new URL(req.url()),path=url.pathname;
   if(path==='/')return route.fulfill({status:200,contentType:'text/html',body:html});
   let data={};if(req.method()==='POST')writes.push({path,data:req.postDataJSON()});else reads.push(path+url.search);
   if(path==='/api/customization'){if(req.method()==='POST'){Object.assign(customization,req.postDataJSON());data={success:true,customization};}else data=customization;}
   if(path==='/api/chat-display')data=chat;
   if(path==='/api/chat-display/settings'){Object.assign(chat,req.postDataJSON());data={success:true,chatDisplay:chat};}
   if(path==='/api/session')data={authenticated:false};
   if(path==='/api/operations/diagnostics'){await new Promise(r=>setTimeout(r,80));data={capturedAt:Date.now(),webPanel:{entries:4,capacity:128,keys:[{key:'live',ageMs:50}]},admin:{snapshotHeaders:96,loadedPayloads:1,sampledBytes:4096},antiCheat:{modDbEntries:3,modDbEstimatedBytes:512},logger:{pendingEvents:2},oreShield:{pendingVeinJobs:1,oldestVeinJobAgeMs:250},note:'Measured counters; not whole-heap attribution.'};}
   if(path==='/api/profiler')data={enabled:true,profileRunning:false,samples:[],modHotspots:[{modId:'zebra_mod',totalMs:20,maxSingleMs:12,percentOfProfileTickTime:60,confidence:'High'},{modId:'alpha_mod',totalMs:5,maxSingleMs:3,percentOfProfileTickTime:15,confidence:'High'}],profileSlowTicks:[{tickMs:80,topCauseSummary:'server/tick/Worldgen: long measured cause with details'}],diagnosticIncidents:[],diagnosticSummary:{},smartAnalysis:{}};
   if(path==='/api/roles')data=roles;
   if(path==='/api/roles/players')data={total:1,players:[{playerUid:'alice',playerName:'Alice',roleCode:'default',online:false}]};
   if(path==='/api/roles/preview')data={success:true,affectedPlayers:1,effective:policy};
   if(path==='/api/roles/effective')data={available:true,playerName:'Alice',effective:policy};
   if(path==='/api/roles/batch'){const body=req.postDataJSON();data=body.preview?{success:true,players:[{playerName:'Alice',effective:policy}]}:{success:true,message:'Assigned 1 of 1 players.',applied:1};}
   if(path==='/api/admin/player')data={player:{playerUid:url.searchParams.get('player'),name:url.searchParams.get('player'),online:false},inventoryStatus:{available:false,readOnly:true,message:'Load on demand'}};
   if(path==='/api/admin/investigation'){const who=url.searchParams.get('player');await new Promise(r=>setTimeout(r,100));data={capturedAt:Date.now(),player:{playerUid:who,name:who},antiCheat:{status:who,riskScore:50,decision:{speedExplanation:'Waiting for confirmed observations.',confirmedSpeedReports:1},findings:[]},role:{effective:policy},riskHistory:[{code:'SPEED',riskBefore:0,riskAfter:50}],inventorySnapshots:[],detailNote:'Loaded on demand'};}
   if(path==='/api/admin/block-history')data={history:[{timestampUtc:'2026-10-13T20:00:00Z',action:'BlockBreak',blockCode:'game:stone',x:1,y:2,z:3,dimension:'0'}]};
   return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
  });
  await page.goto('http://polish-panel.invalid/');await page.evaluate(()=>showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:true}));
  await page.evaluate(()=>{setSection('memory',false);for(let i=0;i<30;i++)loadRetentionCounters();});
  await page.waitForFunction(()=>retentionData&&!retentionBusy);assert.equal(reads.filter(p=>p==='/api/operations/diagnostics').length,1);
  assert.match(await page.textContent('#retentionCounters'),/96/);assert.match(await page.textContent('#retentionKeys'),/live/);
  await page.evaluate(()=>setSection('customization'));await page.waitForFunction(()=>suiteCustomizationData);
  assert.equal(await page.locator('body').getAttribute('data-suite-texture'),'slate');
  assert.equal(await page.locator('#nav .uiModuleIcon').count(),await page.locator('#nav .navGroupBody>button[data-section]').count());
  await page.selectOption('#suiteCustomizationTexture','brushed');await page.locator('#suiteCustomizationTextureStrength').fill('20');await page.locator('label').filter({has:page.locator('#suiteCustomizationIcons')}).click();await page.locator('label').filter({has:page.locator('#suiteCustomizationContrast')}).click();
  assert.equal(await page.locator('body').getAttribute('data-suite-texture'),'brushed');assert.equal(await page.evaluate(()=>getComputedStyle(document.querySelector('.uiModuleIcon')).display),'none');
  await page.click('#suiteCustomizationSave');await page.waitForFunction(()=>!suiteCustomizationBusy);assert.equal(customization.surfaceTexture,'brushed');assert.equal(customization.textureStrength,20);assert.equal(customization.showModuleIcons,false);assert.equal(customization.highContrastBorders,true);
  await page.evaluate(()=>{suiteCustomizationData=null;loadSuiteCustomization();});await page.waitForFunction(()=>suiteCustomizationData);assert.equal(await page.inputValue('#suiteCustomizationTexture'),'brushed');
  await page.selectOption('#suiteCustomizationTexture','none');assert.equal(await page.evaluate(()=>getComputedStyle(document.querySelector('#nav'),'::before').display),'none');
  await page.selectOption('#suiteCustomizationTexture','slate');await page.locator('label').filter({has:page.locator('#suiteCustomizationIcons')}).click();await page.locator('label').filter({has:page.locator('#suiteCustomizationContrast')}).click();await page.click('#suiteCustomizationSave');await page.waitForFunction(()=>!suiteCustomizationBusy);
  await page.evaluate(()=>setSection('chatDisplay'));await page.waitForFunction(()=>chatDisplayData);
  await page.click('#chatLayoutTimeFirst');assert.match(await page.textContent('#chatDisplayPreview'),/^Preview: \[HH:mm\] \[Player\] ExamplePlayer: Hello world$/);
  await page.fill('#chatDisplayLayout','[Global] {role} {name} {timestamp}: {message} [end]');assert.match(await page.textContent('#chatDisplayPreview'),/\[Global\] \[Player\] ExamplePlayer \[HH:mm\]: Hello world \[end\]/);
  await page.click('#chatDisplaySave');await page.waitForFunction(()=>chatDisplayData.chatFormat.startsWith('[Global]'));assert.equal(chat.chatFormat,'[Global] {role} {name} {timestamp}: {message} [end]');
  await page.evaluate(()=>{$('chatDisplayLayout').closest('.card').scrollIntoView({block:'start'});scrollBy(0,-150);$('toastHost')?.replaceChildren();});await page.waitForTimeout(200);await page.screenshot({path:path.join(previews,'chat-layout-desktop.png'),fullPage:false});
  await page.fill('#chatDisplayLayout','<img src=x onerror=alert(1)> {name}: {message}');assert.equal(await page.locator('#chatDisplayPreview img').count(),0);
  await page.fill('#chatDisplayLayout','{message}');assert.match(await page.textContent('#chatLayoutError'),/once/);
  await page.evaluate(()=>setSection('roles'));await page.waitForFunction(()=>rolesData&&!roleBusy);
  assert.equal(await page.locator('#roleEditor .uiFieldGroup').count(),4);
  assert.equal(await page.locator('#roleList .uiRoleColor').count(),1);
  assert.equal(await page.locator('#roleList .uiRoleColor').evaluate(e=>e.style.backgroundColor),'rgb(255, 255, 255)');
  await page.click('[data-ui-fold="0"]');assert.equal(await page.locator('#roleCode').isVisible(),false);await page.locator('[data-ui-fold="0"]').press('Enter');assert.equal(await page.locator('#roleCode').isVisible(),true);
  await page.click('#roleNew');await page.click('[data-ui-fold="0"]');await page.evaluate(()=>$('roleEditor').reportValidity());assert.equal(await page.locator('#roleCode').isVisible(),true,'Validation reveals required fields in collapsed groups');await page.click('[data-role-select="default"]');
  await page.selectOption('#uiPrivilegeCategory','selected');assert.match(await page.textContent('#uiPrivilegeCount'),/1 shown/);
  await page.selectOption('#uiPrivilegeCategory','administration');assert.match(await page.textContent('#uiPrivilegeCount'),/1 shown/);
  await page.selectOption('#uiPrivilegeCategory','all');
  await page.fill('#roleHomeLimit','5');assert.match(await page.textContent('#uiRoleChanges'),/Home limit/);assert.match(await page.textContent('#uiRoleChanges'),/5/);
  await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(200);await page.screenshot({path:path.join(previews,'roles-desktop.png'),fullPage:false});
  await page.click('#rolePreview');await page.waitForFunction(()=>!document.querySelector('#rolePreviewResult').hidden);assert.match(await page.textContent('#rolePreviewResult'),/Nothing has been saved/);
  assert.equal(writes.filter(x=>x.path==='/api/roles').length,0,'Preview never saves a definition');
  await page.click('#rolePlayerFind');await page.waitForFunction(()=>rolePlayersData.length===1&&!rolePlayersLoading);
  await page.check('[data-role-batch-uid="alice"]');await page.click('#roleBatchPreview');await page.waitForFunction(()=>roleBatchDraft);
  await page.click('#roleBatchApply');await page.waitForFunction(()=>!roleBusy&&!rolePlayersLoading);
  assert.deepEqual(writes.filter(x=>x.path==='/api/roles/batch').map(x=>x.data.preview),[true,false]);
  await page.evaluate(()=>setSection('admin',false));await page.evaluate(()=>selectAdminPlayer('Alice'));
  assert(reads.some(p=>p.includes('/api/admin/player?details=summary')));
  assert(!reads.some(p=>p.startsWith('/api/admin/inventory')||p.startsWith('/api/admin/block-history')),'Selecting a player does not fetch item contents or block history');
  await page.evaluate(()=>{loadPlayerInvestigation();selectAdminPlayer('Bob');});await page.waitForFunction(()=>document.querySelector('#adminPlayerTitle').textContent.includes('Bob'));
  await page.waitForTimeout(180);assert.equal(await page.textContent('#investigationBody'),'','Late Alice summary cannot repopulate Bob');
  await page.click('[data-ui-tab="player:investigation"]');await page.click('#investigationLoad');await page.waitForFunction(()=>investigationData?.player?.name==='Bob');assert.match(await page.textContent('#investigationBody'),/Recorded risk changes/);
  await page.click('#investigationBlocks');await page.waitForFunction(()=>document.querySelector('#investigationBlockRows').textContent.includes('game:stone'));
  await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(200);await page.screenshot({path:path.join(previews,'player-investigation-desktop.png'),fullPage:false});
  const inventoryReads=reads.filter(p=>p.startsWith('/api/admin/inventory')).length;
  await page.click('[data-ui-tab="player:inventory"]');assert.equal(await page.locator('#adminInventoryRefresh').isVisible(),true);
  assert.equal(reads.filter(p=>p.startsWith('/api/admin/inventory')).length,inventoryReads,'Inventory tab is a view; contents remain manual');
  await page.click('[data-ui-tab="player:history"]');await page.locator('[data-ui-tab="player:history"]').press('Home');assert.equal(await page.locator('[data-ui-tab="player:overview"]').getAttribute('aria-selected'),'true');
  await page.evaluate(()=>setSection('profiler',false));await page.evaluate(()=>loadProfiler());
  await page.click('[data-ui-tab="profiler:attribution"]');assert.equal(await page.locator('#profMods').isVisible(),true);assert.equal(await page.locator('#chartDelay').isVisible(),false);
  await page.evaluate(()=>{$('profMods').closest('table').querySelector('th').click();});
  assert.equal(await page.locator('#profMods tr:first-child td:first-child').textContent(),'alpha_mod','Names use natural text sorting');
  await page.evaluate(()=>{$('profMods').closest('table').querySelectorAll('th')[1].click();});
  assert.equal(await page.locator('#profMods tr:first-child td:first-child').textContent(),'alpha_mod','Numeric durations sort by value');
  await page.evaluate(()=>{$('profTicks').innerHTML='<tr><td>1.2s</td><td>Later</td></tr><tr><td>900ms</td><td>Earlier</td></tr>';const th=$('profTicks').closest('table').querySelector('th');delete th.dataset.sortDir;th.click();});
  assert.equal(await page.locator('#profTicks tr:first-child td:first-child').textContent(),'900ms','Mixed durations use common units');
  await page.evaluate(()=>loadProfiler());
  await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(200);await page.screenshot({path:path.join(previews,'profiler-attribution-desktop.png'),fullPage:false});
  await page.evaluate(()=>{const input=$('profMods').closest('.tableWrap').previousElementSibling.querySelector('input');input.value='zebra';input.dispatchEvent(new Event('input'));});await page.evaluate(()=>loadProfiler());
  assert.equal(await page.locator('#profMods tr:visible').count(),1,'Table filter survives refresh');
  await page.click('[data-ui-tab="profiler:overview"]');assert.equal(await page.locator('#chartDelay').isVisible(),true);
  await page.click('[data-ui-tab="profiler:lag"]');assert.equal(await page.locator('#profMods').isVisible(),false);assert.equal(await page.locator('#profIncidents').isVisible(),true);
  await page.evaluate(()=>applySuiteCustomization({theme:'custom',accentHex:'#34B7A1',backgroundHex:'#101820',panelHex:'#182C29',textHex:'#EDF5F1'}));
  assert.equal(await page.evaluate(()=>getComputedStyle($('roles')).getPropertyValue('--panel').trim()),'#182C29','Owner palette remains effective');
  for(const width of [1440,900,390,320]){
   await page.setViewportSize({width,height:1000});
   for(const section of ['admin','roles','profiler','anticheat','databaseStorage','customPatches','customization','chatDisplay']){
    await page.evaluate(s=>setSection(s,false),section);
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,section+' fits width '+width);
   }
   if(width===390){await page.evaluate(()=>setSection('roles',false));await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(200);await page.screenshot({path:path.join(previews,'roles-mobile.png'),fullPage:false});}
  }
  await page.evaluate(()=>setSection('admin',false));
  for(const width of [390,320]){await page.setViewportSize({width,height:1000});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'Investigation fits mobile');}
  await page.evaluate(()=>{currentSessionRole='ReadOnly';setSection('roles',false);renderRolePlayers();roleSync();});
  assert.equal(await page.locator('#roleBatchPreview').isDisabled(),true);assert.equal(await page.locator('[data-role-batch-uid="alice"]').isDisabled(),true);
  assert.equal(await page.locator('#uiPrivilegeCategory').isDisabled(),false,'Read-only users can filter permissions');await page.evaluate(()=>{setSection('chatDisplay',false);renderChatDisplay();});assert.equal(await page.locator('#chatDisplayLayout').isDisabled(),true);assert.equal(await page.locator('#chatLayoutDefault').isDisabled(),true);assert.deepEqual(errors,[]);console.log('PASS UI2: texture preview/save/reload/off, strength/icons/contrast, module SVGs, role color chips, custom chat layout preview/save/escaping/read-only and responsive layouts.');console.log('PASS: UI1 role grouping/draft diff/permission filters, player tabs and manual admission, profiler tabs, text/numeric sort/filter retention, theme customization and desktop/mobile layouts. PASS: bounded diagnostic capture, draft/bulk previews, on-demand investigations, stale-player response rejection, mobile layouts and read-only controls.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
