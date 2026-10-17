// Run against HTML exported from the compiled Suite by PlayerCommandSettingsSmoke.
// Requires Playwright and Chromium; no running game server or account is used.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const html=fs.readFileSync(process.argv[2],'utf8'),out=process.argv[3]||'/tmp/nss-playercommands-previews';
fs.mkdirSync(out,{recursive:true});
const defaults={available:true,th3EssentialsDetected:true,spawnEnabled:true,spawnCooldownSeconds:60,spawnWarmupSeconds:5,spawnCancelOnMove:true,privateMessagesEnabled:true,replyEnabled:true,respectSuiteMutes:true,maxPrivateMessageLength:500,starterKitEnabled:true,starterKitProvider:'Auto',starterKitEffectiveProvider:'Th3Essentials',starterKitConfigured:true,starterKitItemCount:3,starterKitClaimedCount:8,warpsEnabled:true,warpCommandEnabled:true,warpCount:3,warpDefaultCooldownSeconds:91,warpDefaultWarmupSeconds:17,warpDefaultCancelOnMove:false,backEnabled:true,backCooldownSeconds:30,backWarmupSeconds:5,backCancelOnMove:true,backTrackDeaths:true,backTrackTeleports:true,homesEnabled:true,homeGlobalLimit:2,homeRoleLimits:{supporter:5},homePlayerLimits:{},homeCooldownSeconds:60,homeWarmupSeconds:5,homeCancelOnMove:true,t2pEnabled:true,t2pCooldownSeconds:30,t2mEnabled:true,t2mCooldownSeconds:30,rtpEnabled:true,rtpMinRadius:1000,rtpMaxRadius:10000,rtpCooldownSeconds:1800,rtpWarmupSeconds:10,rtpCancelOnMove:true,rtpCostEnabled:false,rtpCostItemCode:'game:gear-rusty',rtpCostQuantity:1,rtpMaxSearchAttempts:20,rtpAvoidWaterAndLava:true,rtpCenterOnPlayerPosition:false,teleportRolePolicies:{home:{'*':{allowed:false,cooldownSeconds:60},supporter:{allowed:true,cooldownSeconds:0},archived:{allowed:true,cooldownSeconds:120}}},availableRoles:[{code:'default',name:'Survivor',privileges:['chat']},{code:'supporter',name:'Supporter',privileges:['chat']},{code:'admin',name:'Administrator',privileges:['controlserver','chat']}]};
defaults.commands=['spawn','msg','reply','starterkit','warp','back','home','sethome','delhome','homes','t2p','t2m','rtp'].map((key,i)=>({key,command:key==='reply'?'/r <message>':`/${key}`,enabled:key!=='reply',individuallyEnabled:key!=='reply',rootOwnedBySuite:i>3,rootProvider:i>3?"Newfie's Server Essentials":'Th3Essentials',fallbackCommand:`/nse ${key}`,requiredPrivilege:'chat',description:`${key} command information <img src=x onerror="window.injected=true">`}));
const permissions={available:true,roles:defaults.availableRoles.map(r=>({...r,privilegeLevel:r.code==='admin'?100:1})),onlinePlayers:[{playerUid:'alice-uid',playerName:'Alice',roleCode:'default',permanentPrivileges:[],deniedPrivileges:[]},{playerUid:'bob-uid',playerName:'Bob',roleCode:'supporter',permanentPrivileges:['chat'],deniedPrivileges:[]}],knownPrivileges:['chat','controlserver','root']};
const warps={available:true,enabled:true,defaultCooldownSeconds:91,defaultWarmupSeconds:17,defaultCancelOnMove:false,availableRoles:['default','supporter'],availablePrivileges:['chat'],warps:[]};
(async()=>{
  const browser=await chromium.launch({executablePath:process.env.CHROMIUM||'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1100}}),errors=[],posts=[],gets=[];
    let snapshot=structuredClone(defaults),failSave=false,holdSave=null,releaseSave;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',async route=>{
      const request=route.request(),url=new URL(request.url()),endpoint=url.pathname;
      if(endpoint==='/')return route.fulfill({status:200,contentType:'text/html',body:html});
      if(request.method()==='GET'){
        gets.push(endpoint);
        const value=endpoint==='/api/session'?{authenticated:false}:endpoint==='/api/playercommands'?snapshot:endpoint==='/api/playercommands/permissions'?permissions:endpoint==='/api/warps'?warps:{};
        return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(value)});
      }
      const body=request.postDataJSON();posts.push({endpoint,body});
      if(endpoint==='/api/playercommands/settings'){
        if(holdSave)await holdSave;
        if(failSave)return route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({error:'Fixture: settings could not be persisted'})});
        snapshot={...snapshot,...body};
        return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({success:true,message:'Settings saved.',playerCommands:snapshot})});
      }
      if(endpoint==='/api/playercommands/command'){
        snapshot.commands=snapshot.commands.map(c=>c.key===body.key?{...c,enabled:body.enabled,individuallyEnabled:body.enabled}:c);
        return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({success:true,playerCommands:snapshot})});
      }
      if(endpoint==='/api/playercommands/permissions')return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({success:true,message:'Privilege updated.',permissions})});
      if(endpoint==='/api/warps/settings'){
        Object.assign(warps,{defaultCooldownSeconds:body.defaultCooldownSeconds,defaultWarmupSeconds:body.defaultWarmupSeconds,defaultCancelOnMove:body.defaultCancelOnMove});
        return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({success:true,snapshot:warps})});
      }
      return route.fulfill({status:200,contentType:'application/json',body:'{}'});
    });
    await page.goto('http://suite-preview.invalid/');
    await page.waitForFunction(()=>typeof showApp==='function');
    await page.evaluate(()=>{showApp({username:'Preview Owner',role:'Owner',owner:true,safeDiagnosticsMode:true});liveUpdates=false;setSection('playercommands');});
    await page.waitForFunction(()=>playerCommandsState?.available&&!playerCommandsLoading);
    assert.equal(await page.locator('.playerCommandEntry').count(),13);
    assert.equal(gets.filter(x=>x==='/api/playercommands/permissions').length,0,'Permissions should load on demand');
    assert.equal(gets.filter(x=>x==='/api/warps').length,0,'Directory must not request warp definitions');
    assert.equal(await page.locator('.playerCommandEntry img').count(),0,'Descriptions must be escaped');
    assert.equal(await page.evaluate(()=>window.injected),undefined);
    await page.evaluate(()=>{playerCommandsState.commands.forEach(c=>c.description=c.description.split(' <img')[0]);renderPlayerCommandDirectory();});
    await page.screenshot({path:path.join(out,'player-commands-desktop.png'),fullPage:true});
    await page.fill('#playerCommandSearch','rtp');assert.equal(await page.locator('.playerCommandEntry').count(),1);
    await page.fill('#playerCommandSearch','');await page.selectOption('#playerCommandFilter','external');assert.equal(await page.locator('.playerCommandEntry').count(),4);
    await page.selectOption('#playerCommandFilter','disabled');assert.equal(await page.locator('.playerCommandEntry').count(),1);
    await page.selectOption('#playerCommandFilter','all');
    await page.click('#playerCommandTab-teleports');await page.fill('#playerCmdSpawnCooldown','77');
    assert.equal(await page.evaluate(()=>playerCommandsDirty),true);
    assert.equal(await page.locator('#playerCommandsSave').isEnabled(),true);
    await page.screenshot({path:path.join(out,'player-teleports-desktop.png'),fullPage:true});
    await page.click('#playerCommandTab-commands');assert.equal(await page.locator('[data-player-command-toggle="spawn"]').isDisabled(),true);
    await page.evaluate(()=>{setSection('serverStatus',false);setSection('playercommands');});
    assert.equal(await page.inputValue('#playerCmdSpawnCooldown'),'77','Returning to the page must preserve edits');
    failSave=true;const before=posts.length;await page.click('#playerCommandsSave');
    await page.waitForFunction(()=>!playerCommandsBusy);
    assert.equal(posts.length,before+1);assert.equal(await page.inputValue('#playerCmdSpawnCooldown'),'77');assert.equal(await page.evaluate(()=>playerCommandsDirty),true);
    assert.equal(await page.locator('#playerCommandsSave').isEnabled(),true,'Failed save remains retryable');
    failSave=false;holdSave=new Promise(r=>releaseSave=r);await page.click('#playerCommandsSave');
    await page.waitForFunction(()=>playerCommandsBusy);
    assert.equal(await page.locator('#playerCommandsSave').isDisabled(),true);assert.equal(await page.locator('#playerCmdSpawnCooldown').isDisabled(),true);
    await page.evaluate(()=>savePlayerCommands());assert.equal(posts.length,before+2,'Concurrent saves are blocked');
    releaseSave();holdSave=null;await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    const saved=posts.filter(x=>x.endpoint==='/api/playercommands/settings').at(-1).body;
    assert.equal(saved.spawnCooldownSeconds,77);assert.deepEqual(saved.teleportRolePolicies,defaults.teleportRolePolicies,'Inherited defaults and wildcard/saved-role policies survive unrelated saves');
    assert.equal('warpDefaultCooldownSeconds' in saved,false,'Policy save must not submit unrelated warp defaults');
    await page.click('#playerCommandTab-roles');await page.selectOption('#playerCmdRoleCommand','t2p');
    assert.equal(await page.locator('#playerCmdRolePolicyRows tr').first().locator('td:visible').count(),4,'Role policy view shows one command at a time');
    const defaultMode=page.locator('[data-role-policy-mode="t2p"][data-role="default"]');
    const defaultAccess=page.locator('[data-role-policy-access="t2p"][data-role="default"]');
    assert.equal(await page.locator('[data-role-policy-access="t2p"][data-role="admin"]').isChecked(),true,'Unconfigured administrators retain privileged teleport defaults');
    assert.equal(await defaultAccess.isChecked(),false);assert.equal(await defaultAccess.isDisabled(),true);
    await defaultMode.selectOption('override');await defaultAccess.check();await page.locator('[data-role-policy-cooldown="t2p"][data-role="default"]').fill('15');
    await page.selectOption('#playerCmdRoleCommand','home');assert.equal(await page.locator('[data-role-policy-access="home"][data-role="default"]').isChecked(),false);
    await page.selectOption('#playerCmdRoleCommand','t2p');assert.equal(await defaultAccess.isChecked(),true,'Changing command filters preserves overrides');
    await page.click('#playerCommandsSave');await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    assert.deepEqual(posts.filter(x=>x.endpoint==='/api/playercommands/settings').at(-1).body.teleportRolePolicies.t2p,{default:{allowed:true,cooldownSeconds:15}});
    await defaultMode.selectOption('inherit');await page.click('#playerCommandsSave');await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    assert.equal('t2p' in posts.filter(x=>x.endpoint==='/api/playercommands/settings').at(-1).body.teleportRolePolicies,false,'Reverting to inheritance removes override');
    await page.click('#playerCmdRolePolicyReset');await page.click('#playerCommandsSave');await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    assert.deepEqual(posts.filter(x=>x.endpoint==='/api/playercommands/settings').at(-1).body.teleportRolePolicies,{},'Reset removes explicit policies instead of materializing defaults');
    await page.click('#playerCommandTab-teleports');await page.fill('#playerCmdHomeRoleLimits','supporter=nope');
    const invalidBefore=posts.length;await page.click('#playerCommandsSave');assert.equal(posts.length,invalidBefore);assert.equal(await page.inputValue('#playerCmdHomeRoleLimits'),'supporter=nope');
    await page.fill('#playerCmdHomeRoleLimits','supporter=5');await page.fill('#playerCmdRtpMinRadius','20000');await page.click('#playerCommandsSave');assert.equal(posts.length,invalidBefore);
    await page.fill('#playerCmdRtpMinRadius','1000');await page.fill('#playerCmdSpawnCooldown','88');
    await page.click('#playerCommandTab-permissions');await page.waitForFunction(()=>playerPermissionState?.onlinePlayers?.length===2);
    await page.fill('#playerPermissionSearch','alice');assert.equal(await page.locator('#playerPermPlayerRows tr:visible').count(),1);
    await page.fill('[data-perm-value="alice-uid"]','chat');await page.click('[data-perm-act="grant"][data-uid="alice-uid"]');
    await page.waitForFunction(()=>!playerPermissionBusy);assert.equal(await page.inputValue('#playerCmdSpawnCooldown'),'88','Permission mutation preserves policy draft');assert.equal(await page.evaluate(()=>playerCommandsDirty),true);
    assert.deepEqual(posts.filter(x=>x.endpoint==='/api/playercommands/permissions').at(-1).body,{playerUid:'alice-uid',action:'grant',value:'chat'});
    // Reload must respect cancellation, then explicitly discard when accepted.
    page.once('dialog',dialog=>dialog.dismiss());await page.click('#playerCommandsRefresh');assert.equal(await page.inputValue('#playerCmdSpawnCooldown'),'88');
    page.once('dialog',dialog=>dialog.accept());await page.click('#playerCommandsRefresh');await page.waitForFunction(()=>!playerCommandsLoading&&!playerCommandsDirty);
    await page.click('#playerCommandTab-commands');await page.click('[data-player-command-toggle="spawn"]');await page.waitForFunction(()=>!playerCommandsBusy&&playerCommandsState.commands[0].individuallyEnabled===false);
    assert.equal(posts.filter(x=>x.endpoint==='/api/playercommands/command').at(-1).body.key,'spawn');
    const bulkBefore=posts.length;
    await page.click('#playerCommandsDisableAll');assert.equal(posts.length,bulkBefore,'Bulk feature edits must not apply immediately');
    await page.click('#playerCommandsSave');await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    assert.equal(posts.at(-1).body.spawnEnabled,false);assert.equal(await page.evaluate(()=>playerCommandsState.commands[0].individuallyEnabled),false,'Bulk feature changes retain individual switches');
    await page.click('#playerCommandsEnableAll');await page.click('#playerCommandsSave');await page.waitForFunction(()=>!playerCommandsBusy&&!playerCommandsDirty);
    // Keyboard tabs and mobile layout: horizontal scrolling stays inside data tables.
    await page.focus('#playerCommandTab-commands');await page.keyboard.press('ArrowRight');assert.equal(await page.locator('#playerCommandTab-teleports').getAttribute('aria-selected'),'true');
    await page.evaluate(()=>$('toastHost').replaceChildren());
    await page.setViewportSize({width:390,height:844});
    for(const tab of ['commands','teleports','community','roles','permissions']){
      await page.evaluate(t=>setPlayerCommandTab(t),tab);await page.evaluate(()=>scrollTo(0,0));
      await page.waitForTimeout(80);
      const width=await page.evaluate(()=>({document:document.documentElement.scrollWidth,viewport:innerWidth}));
      const clipped=await page.evaluate(()=>[...document.querySelectorAll('#playercommands .toggleLine')].filter(e=>e.getClientRects().length&&e.scrollWidth>e.clientWidth+2).map(e=>e.textContent.trim()));
      const switches=await page.evaluate(()=>[...document.querySelectorAll('#playercommands .suiteSwitchTrack')].filter(e=>e.getClientRects().length).map(e=>e.getBoundingClientRect().width));
      assert.ok(switches.every(w=>w<=52),'Switch tracks must retain their compact width');
      assert.deepEqual(clipped,[],`${tab}: toggle text must wrap inside its card`);
      assert.ok(width.document<=width.viewport+2,`${tab} overflows mobile viewport: ${JSON.stringify(width)}`);
      await page.screenshot({path:path.join(out,`player-${tab}-mobile.png`),fullPage:true});
    }
    // Read-only and disabled module behavior must hold even for programmatic calls.
    await page.evaluate(d=>{currentSessionRole='ReadOnly';renderPlayerCommands(d);renderPlayerPermissions(playerPermissionState);},snapshot);
    const roPosts=posts.length;await page.evaluate(async()=>{setAllPlayerCommands(false);resetTeleportRolePolicies();await savePlayerCommands();await togglePlayerCommand('home',false);await changePlayerPermission('alice-uid','grant','root');});
    assert.equal(posts.length,roPosts);assert.equal(await page.locator('#playerCommandsSave').isDisabled(),true);
    await page.evaluate(()=>{currentSessionRole='Owner';renderPlayerCommands({available:false});});
    assert.equal(await page.locator('#playerCmdSpawnEnabled').isDisabled(),true);await page.evaluate(()=>savePlayerCommands());assert.equal(posts.length,roPosts);
    assert.deepEqual(errors,[],'Dashboard must not throw browser errors');
    console.log('PASS: compiled desktop/mobile dashboard; command search/provider filters/XSS escaping; lazy reads; drafts survive navigation, failed saves and permission actions; busy/read-only/unavailable guards; strict validation; inheritance/wildcard/saved-role/reset behavior; keyboard tabs; isolated warp payloads.');
    // Test dedicated Warp Defaults request independently from Player Commands.
    await page.evaluate(()=>{currentSessionRole='Owner';setSection('warps');});await page.waitForFunction(()=>warpState?.available===true);
    await page.fill('#warpDefaultCooldown','42');await page.click('#warpDefaultsSave');await page.waitForFunction(()=>warpState?.defaultCooldownSeconds===42);
    const warpPost=posts.at(-1);assert.equal(warpPost.endpoint,'/api/warps/settings');assert.deepEqual(warpPost.body,{enabled:true,defaultCooldownSeconds:42,defaultWarmupSeconds:17,defaultCancelOnMove:false});
    console.log('PASS: Warp Management saves only warp defaults through its dedicated endpoint.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
