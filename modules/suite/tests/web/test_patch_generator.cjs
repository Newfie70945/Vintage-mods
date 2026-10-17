const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const html=require('./embedded_page.cjs')();
(async()=>{
 const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],writes=[];
  let failSave=false,slowPreview=false;
  const file='fixture:recipes/grid/array.json';
  const inspection={file,kind:'recipe',roots:[{path:'/0',label:'First recipe',ingredients:[{key:'A',code:'game:stick'}]},{path:'/1',label:'Second recipe',ingredients:[{key:'B',code:'game:stone'}]}],fields:[{path:'/0/output/quantity',valueJson:'1',summary:'1'},{path:'/1/output/quantity',type:'Integer',valueJson:'2',summary:'2'},{path:'/1/ingredients/B/code',valueJson:'"game:stone"',summary:'"game:stone"'}]};
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',async route=>{
   const r=route.request(),u=new URL(r.url());if(u.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   let result={};
   if(u.pathname==='/api/session')result={authenticated:false};
   if(u.pathname==='/api/content-patches/assets')result={assets:[{file,kind:'recipe'}],totalMatches:1};
   if(u.pathname==='/api/content-patches/asset')result=inspection;
   if(u.pathname==='/api/content-patches')result={available:true,patches:[]};
   if(r.method()==='POST'){
    const body=r.postDataJSON();writes.push({path:u.pathname,body});
    if(u.pathname==='/api/content-patches/generate'){
     if(slowPreview)await new Promise(resolve=>setTimeout(resolve,250));
     result={success:true,generated:{name:body.name||'Balance recipe',file,path:body.rootPath+'/output/quantity',operation:'add'},preview:{beforeJson:'2',afterJson:String(body.quantity),patchJson:'[{"file":"'+file+'","op":"add","path":"/1/output/quantity","value":'+body.quantity+'}]',note:'Loaded asset preview.'}};
    }
    if(u.pathname==='/api/content-patches/build'){
     if(failSave)return route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({success:false,message:'Injected save failure'})});
     result={success:true,snapshot:{restartRequired:true,patches:[{id:'saved-fixture',name:body.name,file,path:'/1/output/quantity',operation:'add',enabled:body.activate}]}};
    }
   }
   await route.fulfill({contentType:'application/json',body:JSON.stringify(result)});
  });
  await page.goto('http://generator-fixture.invalid/');
 await page.evaluate(()=>checkSession());
  await page.evaluate(()=>{showApp({role:'Owner',owner:true,safeDiagnosticsMode:true});setSection('customPatches',false);loadCustomPatches(false);});
  await page.waitForFunction(()=>document.querySelector('#pgAsset').options.length===2);
  await page.locator('#pgAsset').selectOption(file);
  await page.waitForFunction(()=>document.querySelector('#pgRoot').options.length===2);
  await page.locator('#pgRoot').selectOption('/1');
  assert.equal(await page.locator('#pgQuantity').inputValue(),'2');
  assert.equal(await page.locator('#pgValueWrap').isVisible(),false);
  assert.equal(await page.locator('#patchAdvancedRuntime').getAttribute('open'),null);
  await page.locator('#pgName').fill('Cheaper second recipe');await page.locator('#pgQuantity').fill('5');await page.locator('#pgPreview').click();
  await page.waitForFunction(()=>!document.querySelector('#pgPreviewPanel').hidden);
  assert.equal(await page.locator('#pgBefore').textContent(),'2');assert.equal(await page.locator('#pgAfter').textContent(),'5');
  let generated=writes.find(x=>x.path.endsWith('/generate'));assert.equal(generated.body.rootPath,'/1');assert.equal(generated.body.guided,true);
  failSave=true;await page.locator('#pgApply').click();await page.waitForFunction(()=>!document.querySelector('#pgApply').disabled);
  assert.equal(await page.locator('#pgQuantity').inputValue(),'5');assert.equal(await page.locator('#pgPreviewPanel').isVisible(),true);
  failSave=false;await page.locator('#pgSaveDraft').click();await page.waitForFunction(()=>document.querySelector('#contentPatchRows').textContent.includes('Disabled'));
  assert.equal(writes.filter(x=>x.path.endsWith('/build')).at(-1).body.activate,false);
  await page.locator('#pgApply').click();await page.waitForFunction(()=>document.querySelector('#contentPatchRows').textContent.includes('Enabled'));
  assert.equal(writes.filter(x=>x.path.endsWith('/build')).at(-1).body.id,'saved-fixture');
  if(process.env.NSS_SCREENSHOT_DIR){await page.evaluate(()=>document.querySelector('#toastHost').replaceChildren());}if(process.env.NSS_SCREENSHOT_DIR)await page.locator('#patchGeneratorCard').screenshot({path:process.env.NSS_SCREENSHOT_DIR+'/patch-generator-desktop.png'});
  await page.locator('#pgQuantity').fill('7');assert.equal(await page.locator('#pgPreviewPanel').isVisible(),false);
  slowPreview=true;await page.locator('#pgPreview').click();await page.locator('#pgQuantity').fill('8');await page.waitForTimeout(350);
  assert.equal(await page.locator('#pgPreviewPanel').isVisible(),false,'outdated preview must not enable staging');
  await page.locator('#pgPreset').selectOption('property');assert.equal(await page.locator('#pgScalarNumber').isVisible(),true);await page.locator('#pgScalarNumber').fill('9');assert.equal(await page.evaluate(()=>pgRequest().valueJson),'9');
  await page.setViewportSize({width:430,height:900});
  assert.equal(await page.locator('#patchGeneratorCard').evaluate(el=>el.scrollWidth<=el.clientWidth+2),true,'mobile generator overflows');
  if(process.env.NSS_SCREENSHOT_DIR){await page.evaluate(()=>document.querySelector('#toastHost').replaceChildren());}if(process.env.NSS_SCREENSHOT_DIR)await page.locator('#patchGeneratorCard').screenshot({path:process.env.NSS_SCREENSHOT_DIR+'/patch-generator-mobile.png'});
  await page.evaluate(()=>{currentSessionOwner=false;currentSessionRole='ReadOnly';pgAccess();});
  assert.equal(await page.locator('#pgPreview').isDisabled(),true);assert.equal(await page.locator('#pgApply').isDisabled(),true);
  const chart=await page.evaluate(()=>{
   setSection('serverStatus',false);playerAnalyticsData={daily:[{date:'2026-10-06',high:10,low:0,average:4}]};
   const strokes=[],values=[],original=chartLine,oldGrid=drawPlayerChartGrid;drawPlayerChartGrid=(...args)=>{const g=oldGrid(...args),originalY=g.yFor;g.yFor=v=>{values.push(v);return originalY(v);};return g;};chartLine=(ctx,points,color,width)=>strokes.push({points,color});drawDailyPlayerChart();chartLine=original;drawPlayerChartGrid=oldGrid;
   return{strokes,values,title:document.querySelector('#playerDailyChart').closest('.card').textContent};
  });
  assert.match(chart.title,/Daily Peak \/ Average/);assert.equal(chart.strokes.length,2);assert.deepEqual(chart.values,[10,4]);assert(chart.strokes[1].points[0][1]>chart.strokes[0].points[0][1]);
  assert.deepEqual(errors,[]);
  console.log('PASS: loaded asset search, array recipe selection, generated preview, failed-save draft retention, draft/stage updates, stale preview rejection, mobile/read-only controls and daily average chart.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
