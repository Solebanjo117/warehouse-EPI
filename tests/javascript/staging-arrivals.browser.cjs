// Real Razor output exported by StagingArrivalRouteTests; no operational server or database writes.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const { createRequire } = require('node:module');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const fixtures = path.join(root, 'artifacts/staging-ui/fixtures');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) {
    const lookup = JSON.parse(fs.readFileSync(path.join(fixtures, `staging-lookup-${language}.json`), 'utf8'));
    for (const width of [768, 1024, 1440]) {
      for (const theme of ['light', 'dark']) {
        const page = await browser.newPage({ viewport: { width, height: 1024 } });
        const errors = [];
        let failLookup = false;
        page.on('pageerror', error => errors.push(error.message));
        await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
        await page.route('http://staging.test/**', async route => {
          const url = new URL(route.request().url());
          if (url.pathname === '/list' || url.pathname === '/putaway')
            return route.fulfill({ contentType: 'text/html', path: path.join(fixtures, `staging-${url.pathname === '/list' ? 'list' : 'putaway'}-${language}.html`) });
          if (url.pathname === '/Operations/Lookup') {
            if (failLookup) return route.fulfill({ status: 503, body: '' });
            const handler = url.searchParams.get('handler');
            const requestedLocation = (url.searchParams.get('q') || url.searchParams.get('code') || '').startsWith('MANUAL')
              ? { ...lookup.location, id: '01234567-1111-2222-3333-012345678999', code: 'MANUAL-DEST' } : lookup.location;
            const response = handler === 'Locations' ? [requestedLocation] : handler === 'ResolveCode' ? { product: null, location: requestedLocation } :
              handler === 'Balance' ? { quantity: 150, version: 1 } : [];
            return route.fulfill({ json: response });
          }
          if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
          const relative = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, '');
          const file = path.resolve(assets, relative);
          if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
          return route.fulfill({ status: 404, body: '' });
        });
        await page.goto('http://staging.test/list');
        assert.equal(await page.getByRole('link', { name: language === 'en' ? 'Put away' : 'Acomodar', exact: true }).count(), 2);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.screenshot({ path: path.join(root, `artifacts/staging-ui/list-${language}-${width}-${theme}.png`), fullPage: true });
        await page.goto('http://staging.test/putaway');
        const destination = page.locator('#destination-code');
        assert.equal(await destination.inputValue(), '');
        const suggestion = page.locator('[data-destination-proposal]');
        assert.equal(await suggestion.count(), 1);
        await suggestion.focus(); await suggestion.press('Enter');
        await page.waitForFunction(() => document.querySelector('#destination-code').value === 'DEST-STAGING');
        await page.waitForFunction(() => !document.querySelector('[data-review-button]').disabled);
        await page.locator('[data-edit-step="destination"]').click();
        await page.screenshot({ path: path.join(root, `artifacts/staging-ui/proposal-${language}-${width}-${theme}.png`) });
        assert.equal(await page.locator('#product-code').getAttribute('readonly'), 'readonly');
        assert.equal(await page.locator('#source-code').getAttribute('readonly'), 'readonly');
        failLookup = true;
        await destination.fill('RETRY');
        await page.locator('[data-operation-feedback]:not(.d-none)').waitFor();
        assert.equal(await destination.inputValue(), 'RETRY');
        failLookup = false;
        await destination.fill('DEST');
        await page.getByRole('button', { name: /DEST-STAGING/ }).click();
        const review = page.locator('[data-review-button]');
        assert.equal(await review.isEnabled(), true);
        await review.click();
        await page.locator('[data-pin-input]').waitFor({ state: 'visible' });
        assert.match(await page.locator('[data-confirmation-summary]').innerText(), /100/);
        await page.locator('#confirm-operation .modal-footer [data-bs-dismiss]').click();
        await page.locator('#confirm-operation').waitFor({ state: 'hidden' });
        await page.locator('[data-edit-step="destination"]').click();
        await destination.fill('MANUAL-DEST');
        await destination.press('Enter');
        await page.waitForFunction(() => !document.querySelector('[data-review-button]').disabled);
        assert.equal(await destination.inputValue(), 'MANUAL-DEST');
        assert.equal(await review.isEnabled(), true);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.screenshot({ path: path.join(root, `artifacts/staging-ui/putaway-${language}-${width}-${theme}.png`), fullPage: true });
        assert.deepEqual(errors, []);
        await page.close();
      }
    }
    }
    console.log('Passed: 12 viewport/theme/language combinations, destination suggestions and confirmation; no movements submitted.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
