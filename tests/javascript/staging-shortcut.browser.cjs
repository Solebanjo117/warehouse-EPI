// Razor fixtures exported by StagingEntryShortcutTests; uses no operational server.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const output = path.join(root, 'artifacts/staging-shortcut');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');
const other = { id: '11111111-1111-1111-1111-111111111111', code: 'OTHER', description: 'Other destination', isWip: false };
const products = ['FIRST', 'SECOND'].map((sku, index) => ({ id: `22222222-2222-2222-2222-22222222222${index}`, sku,
  description: sku, unitCode: 'PZA', allowsDecimals: false, isActive: true,
  defaultEntryLocationId: other.id, defaultEntryLocationCode: other.code, isDefaultEntryLocationAvailable: true }));

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) for (const width of [768, 1024, 1440]) for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 1024 } });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
      await page.route('http://shortcut.test/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/Modules/operations' || url.pathname === '/Operations/Entry')
          return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `${url.pathname.includes('Modules') ? 'hub' : 'entry'}-${language}.html`) });
        if (url.pathname === '/Operations/Lookup') {
          const handler = url.searchParams.get('handler');
          const code = url.searchParams.get('code');
          const response = handler === 'Products' ? products : handler === 'ProductLocations' || handler === 'Locations' ? [other] :
            handler === 'ResolveCode' ? { product: products.find(p => p.sku === code) ?? null, location: code === other.code ? other : null } :
            handler === 'Balance' ? { quantity: 0, version: 1 } : [];
          return route.fulfill({ json: response });
        }
        if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
        const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, ''));
        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 404, body: '' });
      });
      await page.goto('http://shortcut.test/Modules/operations');
      const shortcut = page.locator('a[href="/Operations/Entry?mode=staging"]');
      assert.equal(await shortcut.count(), 1);
      assert.equal(await page.locator('[data-staging-count]').getAttribute('data-staging-count'), '0');
      assert.ok((await shortcut.boundingBox()).height >= 44);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `hub-${language}-${width}-${theme}.png`), fullPage: true });
      await shortcut.focus();
      await shortcut.press('Enter');
      await page.locator('#product-code').waitFor();
      await page.waitForLoadState('networkidle');
      assert.deepEqual(errors, []);
      const stagingList = page.locator('a[data-staging-count]');
      assert.equal(await stagingList.getAttribute('href'), '/Operations/Staging');
      assert.equal(await stagingList.getAttribute('data-staging-count'), '0');
      await stagingList.focus();
      assert.equal(await stagingList.evaluate(el => el === document.activeElement), true);
      assert.equal(await page.evaluate(() => new FormData(document.querySelector('[data-operation-form]')).get('Mode')), 'staging');
      assert.equal(await page.locator('#destination-code').inputValue(), 'STAGING');
      assert.equal(await page.locator('[data-selected-id="product"]').inputValue(), '');
      for (const sku of ['FIRST', 'SECOND']) {
        if (sku === 'SECOND') await page.locator('[data-edit-step="product"]').click();
        await page.locator('#product-code').fill(sku);
        await page.locator('[data-lookup-field="product"] [data-lookup-results]').getByRole('button', { name: new RegExp(sku) }).click();
        assert.equal(await page.locator('#destination-code').inputValue(), 'STAGING');
      }
      await page.locator('[data-edit-step="destination"]').click();
      await page.locator('#destination-code').fill('OTHER');
      await page.locator('[data-lookup-field="destination"] [data-lookup-results]').getByRole('button', { name: /OTHER/ }).click();
      assert.equal(await page.locator('[data-selected-id="destination"]').inputValue(), other.id);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `entry-${language}-${width}-${theme}.png`), fullPage: true });
      assert.deepEqual(errors, []);
      await page.close();
    }
    console.log('Passed 12 browser scenarios: ES/EN, light/dark, 768/1024/1440; keyboard shortcut, product changes and editable destination.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
