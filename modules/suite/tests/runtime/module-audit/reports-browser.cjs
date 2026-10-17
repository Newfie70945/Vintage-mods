const fs = require('fs');
const assert = require('assert');
const {chromium} = require('playwright');
(async () => {
  const html = fs.readFileSync(process.argv[2] || '/tmp/nss-auditfix-webpanel.html', 'utf8');
  const browser = await chromium.launch({executablePath:'/usr/bin/chromium', headless:true, args:['--no-sandbox']});
  const errors = [];
  const base = {id:1, status:'Open', reporterName:'Fixture player', reason:'Local report', noteCount:1000, eventCount:1001};
  try {
    for (const width of [1440, 390]) {
      const page = await browser.newPage({viewport:{width,height:1000}});
      const details = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        let data = {};
        if (url.pathname === '/') return route.fulfill({contentType:'text/html',body:html});
        if (url.pathname === '/api/session') data = {authenticated:true,username:"Owner",role:"Owner",owner:true,safeDiagnosticsMode:true,csrfToken:"fixture"};
        if (url.pathname === '/api/reports') data = {storageReady:true,openCount:1,totalCount:1,reports:[base]};
        if (url.pathname === '/api/reports/detail') {
          const offset = Number(url.searchParams.get('offset'));
          details.push({offset,limit:Number(url.searchParams.get('limit'))});
          if (offset === 100) await new Promise(resolve => setTimeout(resolve, 150));
          data = {...base,historyOffset:offset,historyLimit:100,
            notes:Array.from({length:100}, (_,i) => ({author:'Staff',text:`Note ${1000-offset-i} <script>unsafe</script>`})),
            events:Array.from({length:100}, (_,i) => ({actor:'Staff',action:'Note',detail:`Event ${1001-offset-i}`}))};
        }
        await route.fulfill({contentType:'application/json',body:JSON.stringify(data)});
      });
      await page.goto('http://nss-audit.invalid/');
      await page.waitForFunction(()=>document.querySelector('#app').style.display==='block'&&currentSessionOwner);
      await page.evaluate(() => setSection('reports',false));
      await page.evaluate(() => loadReports());
      await page.locator('[data-report-id="1"]').click();
      await page.waitForFunction(() => document.querySelector('#reportDetail').textContent.includes('Note 1000'));
      assert.equal(await page.locator('#reportDetail .reportTimelineItem').count(), 200);
      assert.equal(await page.locator('#reportDetail script').count(), 0, 'Staff note inserted executable HTML');
      assert(details.every(request => request.limit === 100));
      await page.getByRole('button',{name:'Older history',exact:true}).click();
      await page.waitForFunction(() => document.querySelector('#reportDetail').textContent.includes('Note 900'));
      assert((await page.locator('#reportDetail').textContent()).includes('History 101–200 of 1001'));
      // A slow older-page response must not replace a later request for the newest page.
      await page.evaluate(async () => {const older=showReport(1,100);const newest=showReport(1,0);await Promise.all([older,newest]);});
      assert((await page.locator('#reportDetail').textContent()).includes('History 1–100 of 1001'));
      await page.evaluate(async () => {currentSessionRole='ReadOnly';await showReport(1);});
      for (const button of await page.locator('#reportDetail [data-report-write]').all()) assert(await button.isDisabled());
      assert(await page.getByRole('button',{name:'Older history',exact:true}).isEnabled());
      await page.evaluate(async () => {currentSessionRole='Owner';reportsData.storageReady=false;await showReport(1);});
      for (const button of await page.locator('#reportDetail [data-report-write]').all()) assert(await button.isDisabled());
      await page.close();
    }
    assert.deepEqual(errors, []);
    console.log('PASS: desktop/mobile report paging, bounded detail rendering, HTML escaping, stale response protection and read-only/protected-storage controls.');
  } finally {await browser.close();}
})().catch(error => {console.error(error);process.exit(1);});
