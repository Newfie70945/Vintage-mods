const fs=require('fs');
const assert=require('assert');
const {chromium}=require('playwright');
(async()=>{
 const html=fs.readFileSync(process.argv[2]||'/tmp/nss-markdown-panel.html','utf8');
 const browser=await chromium.launch({executablePath:'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 const errors=[];
 const snapshot={available:true,markdownEnabled:true,markdownAllowColors:false,markdownAllowLinks:false,markdownAllowedRoleCodes:['player'],boldPlayerName:true,formatGeneralChat:true,additionalFormattedChatGroupIds:[],preview:'ExamplePlayer: Hello world'};
 try {
 for(const width of [1440,390]){
 const page=await browser.newPage({viewport:{width,height:950}});let submitted;
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',async route=>{
 const path=new URL(route.request().url()).pathname;let data={};
 if(path==='/')return route.fulfill({contentType:'text/html',body:html});
 if(path==='/api/session')data={authenticated:true,username:"Owner",role:"Owner",owner:true,safeDiagnosticsMode:true,csrfToken:"fixture"};
 if(path==='/api/chat-display')data=snapshot;
 if(path==='/api/chat-display/settings'){submitted=route.request().postDataJSON();data={chatDisplay:{...snapshot,...submitted},message:'Saved'};}
 await route.fulfill({contentType:'application/json',body:JSON.stringify(data)});
 });
 await page.goto('http://nss-test.invalid/');
 await page.waitForFunction(()=>document.querySelector('#app').style.display==='block'&&currentSessionOwner);
 await page.evaluate(()=>setSection('chatDisplay',false));
 await page.evaluate(()=>loadChatDisplay());
 assert(await page.locator('#chatMarkdownEnabled').isChecked());
 assert.equal(await page.locator('#chatMarkdownRoles').inputValue(),'player');
 await page.locator('#chatMarkdownColors').locator('..').click();await page.locator('#chatMarkdownLinks').locator('..').click();
 await page.locator('#chatMarkdownRoles').fill('admin, moderator');
 await page.locator('#chatDisplaySave').click();
 await page.waitForFunction(()=>document.querySelector('#chatMarkdownRoles').value==='admin, moderator');
 // Wait for the save response to render rather than inspecting an in-flight input.
 await page.waitForTimeout(150);
 assert(submitted&&submitted.markdownAllowColors&&submitted.markdownAllowLinks);
 assert.deepEqual(submitted.markdownAllowedRoleCodes,['admin','moderator']);
 assert(await page.locator('#chatMarkdownColors').isChecked());
 const box=await page.locator('#chatMarkdownRoles').boundingBox();assert(box.width>100&&box.x+box.width<=width+1,'Mobile role field extends beyond viewport');
 await page.evaluate(()=>{currentSessionRole='ReadOnly';renderChatDisplay();});
 for(const id of ['chatMarkdownEnabled','chatMarkdownColors','chatMarkdownLinks','chatMarkdownRoles','chatDisplaySave'])assert(await page.locator('#'+id).isDisabled());
 await page.close();
 }
 assert.deepEqual(errors,[]);
 console.log('PASS: desktop/mobile Markdown controls, settings save payload/roundtrip, read-only controls and no browser errors.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
