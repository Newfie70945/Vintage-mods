// Production scripts with deliberately slow endpoints: exercise request admission,
// stale preference results and role drafts through the actual browser controls.
const assert=require('node:assert/strict'),{chromium}=require('playwright');
const html=require('./embedded_page.cjs')();
(async()=>{
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM||'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],reads=[],writes=[];
  let active=0,peak=0;
  const delay=()=>new Promise(resolve=>setTimeout(resolve,90));
  const role={code:'default',name:'Survivor',description:'',privilegeLevel:0,colorHex:'#FFFFFF',nameColorHex:'',defaultGameMode:'Survival',homeLimit:null,landClaimAllowance:100,landClaimMaxAreas:3,claimMinX:5,claimMinY:5,claimMinZ:5,privileges:['chat'],policies:{},autoGrant:false,assignedPlayers:1,isDefault:true};
  const roles={available:true,policiesAvailable:true,colorsAvailable:true,useRoleNameColors:false,revision:'first',globalHomeLimit:2,knownPrivileges:['chat'],gameModes:['Survival'],defaultCooldowns:{},roles:[role]};
  page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
  await page.route('**/*',async route=>{
   const req=route.request(),url=new URL(req.url()),endpoint=url.pathname;
   if(endpoint==='/')return route.fulfill({status:200,contentType:'text/html',body:html});
   let data={};
   if(endpoint==='/api/session')data={authenticated:false};
   if(endpoint==='/api/roles')data=roles;
   if(endpoint==='/api/roles/players'){
    reads.push(endpoint+url.search);active++;peak=Math.max(peak,active);await delay();active--;
    const name=url.searchParams.get('search')||'Alice';data={total:1,players:[{playerUid:name,playerName:name,roleCode:'default',online:false}]};
   }
   if(endpoint==='/api/console'){
    reads.push(endpoint+url.search);active++;peak=Math.max(peak,active);await delay();active--;
    const included=url.searchParams.get('includeChat')==='true';data={lines:['Server ready'],playerChatIncluded:included,chatLines:included?['Public chat marker']:[]};
   }
   if(endpoint.startsWith('/api/admin/database-storage')){
    reads.push(endpoint);if(req.method()==='POST')writes.push(endpoint);
    active++;peak=Math.max(peak,active);await delay();active--;
    data={detailedAnalysisAvailable:req.method()==='POST',databases:[],detailedAnalysisUnixMilliseconds:req.method()==='POST'?Date.now():0};
   }
   return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
  });
  await page.goto('http://panel-lifetime.invalid/');
  await page.evaluate(()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:true});setSection('roles');});
  await page.waitForFunction(()=>rolesData?.roles.length===1&&!roleBusy);
  assert.equal(await page.locator('#roleSave').isDisabled(),true,'Saved definition needs no redundant save');
  await page.fill('#roleName','Changed');assert.match(await page.textContent('#roleListCount'),/1 unsaved draft/);
  assert.equal(await page.evaluate(()=>window.dispatchEvent(new Event('beforeunload',{cancelable:true}))),false,'Unsaved role drafts protect navigation');
  await page.fill('#roleName','Survivor');assert.equal(await page.evaluate(()=>roleDrafts.size),0,'Reverting a field removes its draft');
  assert.equal(await page.evaluate(()=>window.dispatchEvent(new Event('beforeunload',{cancelable:true}))),true);
  await page.click('#roleNew');await page.fill('#roleName','Builder Draft');await page.click('[data-role-select="default"]');
  await page.click('[data-role-select="__new"]');assert.equal(await page.inputValue('#roleName'),'Builder Draft','New draft stays reachable after switching roles');
  await page.click('#roleDiscard');
  await page.fill('#rolePlayerSearch','Old');
  await page.evaluate(()=>{findRolePlayers();for(let i=0;i<20;i++)findRolePlayers();$('rolePlayerSearch').value='Latest';});
  await page.waitForFunction(()=>!rolePlayersLoading&&!rolePlayersReload&&rolePlayersData[0]?.playerName==='Latest');
  assert.equal(reads.filter(r=>r.startsWith('/api/roles/players')).length,2,'Player searches coalesce to one current and one latest request');
  assert.equal(peak,1,'Player searches stay serial');
  await page.evaluate(()=>{setSection('console',false);$('consoleIncludeChat').checked=true;loadConsole();for(let i=0;i<20;i++)loadConsole();$('consoleIncludeChat').checked=false;loadConsole();});
  await page.waitForFunction(()=>!consoleRequestInFlight&&$('consoleOutput').textContent.includes('Server ready'));
  assert.equal(reads.filter(r=>r.startsWith('/api/console')).length,2,'Console refreshes coalesce');
  assert.doesNotMatch(await page.textContent('#consoleOutput'),/Public chat marker/,'Old opt-in response cannot restore disabled chat');
  assert.equal(peak,1,'Console reads stay serial');
  await page.evaluate(()=>{setSection('databaseStorage',false);loadAdminDatabaseStorage(false);for(let i=0;i<20;i++){loadAdminDatabaseStorage(false);loadAdminDatabaseStorage(true);}});
  await page.waitForFunction(()=>!adminDatabaseStorageBusy&&!adminDatabaseAnalysisPending&&adminDatabaseStorageData?.detailedAnalysisAvailable);
  assert.equal(reads.filter(r=>r==='/api/admin/database-storage').length,1);
  assert.equal(writes.length,1,'Many analysis clicks queue one scan');assert.equal(peak,1,'Storage reads and analysis stay serial');
  assert.deepEqual(errors,[]);
  console.log('PASS: role saved/reverted/new drafts and unload protection; 20-request bursts coalesce player searches, console preferences and SQLite analysis without overlap or stale UI.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
