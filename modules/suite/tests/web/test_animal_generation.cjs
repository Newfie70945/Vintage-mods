const assert=require('node:assert/strict'),{chromium}=require('playwright'),html=require('./embedded_page.cjs')();
(async()=>{const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox']});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],writes=[];
 const rules={enabled:false,defaultLimit:20,defaultRadiusChunks:3,exclusions:['game:player'],rules:[{pattern:'test:hare',limit:8,radiusChunks:0,generationMode:'Off',minimumProtectedGeneration:1,registeredMatches:1}]};
 page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
 await page.route('**/*',route=>{const req=route.request(),url=new URL(req.url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});let body={};
 if(url.pathname==='/api/animals/rules')body=rules;
 if(url.pathname==='/api/animals/status')body={lines:['Disabled fixture; no removals']};
 if(url.pathname==='/api/animals/rules/set'){const v=req.postDataJSON();writes.push(v);rules.rules.push({pattern:v.pattern,limit:v.limit,radiusChunks:v.radiusChunks,generationMode:v.generationMode||'Inherit',minimumProtectedGeneration:v.minimumProtectedGeneration||1});body={success:true,message:'Rule saved'};}
 return route.fulfill({contentType:'application/json',body:JSON.stringify(body)});});
 await page.goto('http://animal.invalid/');await page.evaluate(async()=>{showApp({username:'Fixture',role:'Owner',owner:true,safeDiagnosticsMode:false});setSection('animals',false);await loadAnimalRules(false);renderAnimalScanResult({lines:['MODE\tLoaded fixture',...Array.from({length:120},(_,i)=>`TOTAL\ttest:${i===0?'raccoon':i===1?'hare':'species-'+i}\t100\t90\t10\t3\t1`),'ROW\ttest:raccoon\t0,0\t<default>\t100\t20\t3\t90\t10']});});
 assert.equal(await page.locator('[data-animal-pop-rule]').count(),120);assert.equal(await page.locator('#animalPopulation details').getAttribute('open'),null);
 const box=await page.locator('#animalPopulation .tableWrap').first().evaluate(e=>({height:e.clientHeight,scroll:e.scrollHeight}));assert.ok(box.height<=320&&box.scroll>box.height,'totals are height-limited and scrollable');
 await page.fill('#animalPopulationFilter','raccoon');assert.equal(await page.locator('[data-animal-pop-code]:visible').count(),1);
 await page.locator('[data-animal-pop-rule="test:raccoon"]').click();assert.equal(writes.length,0,'scan action only fills editor');assert.equal(await page.inputValue('#animalRulePattern'),'test:raccoon');
 await page.selectOption('#animalRuleGeneration','Off');await page.click('#animalRuleSave');await page.waitForFunction(()=>document.querySelector('#animalRuleRows').textContent.includes('test:raccoon'));assert.equal(writes.at(-1).generationMode,'Off');assert.equal(writes.at(-1).limit,20);
 await page.fill('#animalPopulationFilter','hare');await page.locator('[data-animal-pop-rule="test:hare"]').click();assert.equal(await page.inputValue('#animalRuleRadius'),'0');assert.equal(await page.inputValue('#animalRuleGeneration'),'Off','scan edit restores policy');
 await page.selectOption('#animalRuleGeneration','ProtectFromGeneration');assert.equal(await page.locator('#animalRuleGenerationMin').isDisabled(),false);await page.fill('#animalRuleGenerationMin','5');await page.click('#animalRuleSave');await page.waitForTimeout(100);assert.equal(writes.at(-1).minimumProtectedGeneration,5);
 await page.click('#animalRuleClear');assert.equal(await page.inputValue('#animalRuleGeneration'),'Inherit');assert.equal(await page.locator('#animalRuleGenerationMin').isDisabled(),true);
 await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,'mobile page does not overflow');
 assert.deepEqual(errors,[]);console.log('PASS population create/edit buttons, scroll bounds/filter, generation save/edit/reset and mobile layout.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
