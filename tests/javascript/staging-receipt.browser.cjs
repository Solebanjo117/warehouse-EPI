// Rendered test fixtures; all requests stay in an isolated, mocked browser context.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const output = path.join(root, 'artifacts/staging-receipt');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) for (const width of [768, 1024, 1440]) for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 1024 } });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
      await page.route('http://receipt.test/**', async route => {
        const url = new URL(route.request().url());
        const fixture = url.pathname === '/receipt' ? 'receipt' : url.pathname === '/Operations/Staging' ? 'arrival' :
          url.pathname === '/Operations/PalletLabels' ? 'plates' : null;
        if (fixture) return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `${fixture}-${language}.html`) });
        if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
        const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, ''));
        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 404, body: '' });
      });
      await page.goto('http://receipt.test/receipt');
      const groups = page.locator('[data-staging-arrival]');
      assert.equal(await groups.count(), 2);
      assert.equal(await page.locator('a[href="/Operations/Entry?mode=staging"]').count(), 1);
      const first = groups.first();
      const id = await first.getAttribute('data-staging-arrival');
      const arrival = first.locator('a[href*="/Operations/Staging?"]');
      assert.ok((await arrival.getAttribute('href')).includes(id));
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `receipt-${language}-${width}-${theme}.png`), fullPage: true });
      await arrival.focus(); await arrival.press('Enter');
      await page.waitForLoadState('networkidle');
      assert.ok(page.url().includes(id));
      assert.equal(await page.getByRole('link', { name: language === 'es' ? 'Ver todas las llegadas' : 'View all arrivals', exact: true }).count(), 1);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `arrival-${language}-${width}-${theme}.png`), fullPage: true });
      await page.goto('http://receipt.test/receipt');
      const print = page.locator('[data-staging-arrival]').first().locator('a[href*="handler=StagingArrival"]');
      await print.focus(); await print.press('Enter');
      await page.waitForLoadState('networkidle');
      assert.equal(await page.locator('.label-dynamic').count(), 1);
      assert.ok(page.url().includes(id));
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: path.join(output, `plates-${language}-${width}-${theme}.png`), fullPage: true });
      assert.deepEqual(errors, []);
      await page.close();
    }
    console.log('Passed 12 receipt scenarios: ES/EN, light/dark, three widths; exact arrival and print links via keyboard.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
