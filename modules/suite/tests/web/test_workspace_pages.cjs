// Requires Playwright and Chromium; all requests are served from local fixtures.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const source = fs.readFileSync(path.join(__dirname, '../../src/WebDashboard/WebDashboardPage.cs'), 'utf8');
const html = require('./embedded_page.cjs')();
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    const errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
      const request = route.request();
      if (request.method() !== 'GET') writes.push(request.method() + ' ' + request.url());
      const pathname = new URL(request.url()).pathname;
      await route.fulfill({ status: 200, contentType: pathname === '/' ? 'text/html' : 'application/json', body: pathname === '/' ? html : JSON.stringify(pathname.includes('session') ? { authenticated: false } : {}) });
    });
    await page.addInitScript(() => localStorage.setItem('nsu.uiScale', '1.00'));
    async function open(section) {
      await page.goto('http://nss-preview.invalid/');
      await page.evaluate(section => { showApp({ username: 'Layout fixture', role: 'Owner', owner: true, safeDiagnosticsMode: true }); setSection(section, false); }, section);
      await page.waitForTimeout(150);
    }
    await open('admin');
    assert.equal(await page.locator('#workspaceJump option').count() > 1, true, 'visible page cards populate picker');
    await page.locator('#workspaceNavSearch').fill('offline inventory');
    assert.equal(await page.locator('#nav button[data-section="admin"]').isVisible(), true);
    assert.equal(await page.locator('#nav button[data-section="discord"]').isVisible(), false);
    await page.locator('#workspaceNavSearch').fill('no_such_page_173');
    assert.equal(await page.locator('#workspaceNavEmpty').isVisible(), true);
    await page.locator('#workspaceNavSearch').press('Escape');
    assert.equal(await page.locator('#workspaceNavSearch').inputValue(), '');
    await page.locator('#workspaceDensity').click();
    await page.locator('#workspaceFocus').click();
    assert.equal(await page.locator('body').evaluate(e => e.classList.contains('workspaceCompact') && e.classList.contains('workspaceFocus')), true);
    assert.equal(await page.locator('#nav').isVisible(), false);
    await page.locator('#workspaceFocus').click();
    const cardId = await page.locator('#admin .workspaceCard').first().getAttribute('id');
    const card = page.locator('#' + cardId);
    await card.locator('.workspaceCardToggle').click();
    assert.equal(await card.locator('.workspaceCardToggle').getAttribute('aria-expanded'), 'false');
    await open('admin');
    assert.equal(await page.locator('#workspaceDensity').getAttribute('aria-pressed'), 'true', 'compact preference persists');
    assert.equal(await page.locator('#' + cardId + ' .workspaceCardToggle').getAttribute('aria-expanded'), 'false', 'collapsed panel persists');
    await page.locator('#workspaceJump').selectOption(cardId);
    assert.equal(await page.locator('#' + cardId + ' .workspaceCardToggle').getAttribute('aria-expanded'), 'true', 'picker expands selected panel');
    await page.evaluate(() => {
      document.querySelector('#acDrawerTabInventory').classList.add('active');
      setSection('performance', false); setSection('anticheat', false);
    });
    assert.equal(await page.locator('#acDrawerTabInventory').evaluate(e => e.classList.contains('active')), true, 'navigation preserves nested tabs');
    await page.evaluate(() => setSection('admin', false));
    for (const width of [390, 768]) {
      await page.setViewportSize({ width, height: 900 });
      await page.waitForTimeout(100);
      assert.equal(await page.locator('#workspaceFocus').isVisible(), false);
      const overflow = await page.locator('.workspaceToolbar').evaluate(e => e.scrollWidth > e.clientWidth + 2);
      assert.equal(overflow, false, 'workspace controls fit mobile width ' + width);
    }
    assert.deepEqual(writes, [], 'presentation controls send no mutations');
    assert.deepEqual(errors, [], 'no browser script errors');
    if (process.env.NSS_PREVIEW_DIR) {
      fs.mkdirSync(process.env.NSS_PREVIEW_DIR, { recursive: true });
      for (const [name, width, section] of [['webpanel-admin-desktop',1440,'admin'],['webpanel-home-desktop',1440,'dashboard'],['webpanel-admin-mobile',390,'admin']]) {
        await page.setViewportSize({ width, height: 1000 });
        await page.evaluate(section => setSection(section, false), section);
        await page.waitForTimeout(150);
        await page.screenshot({ path: path.join(process.env.NSS_PREVIEW_DIR, name + '.png') });
      }
    }
    console.log('PASS: production WebPanel page search, picker, collapse/persistence, compact/focus, nested tabs, mobile controls, no mutation requests or script errors.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
