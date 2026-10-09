// Real Razor fixtures from StagingSplitRouteTests; no operational server or writes.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const output = path.join(root, 'artifacts/staging-split');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');
const other = { id: '11111111-1111-1111-1111-111111111111', code: 'OTHER', description: 'Other destination', isWip: false };
const integerProduct = { id: '22222222-2222-2222-2222-222222222222', sku: 'INTEGER', description: 'Integer unit', unitCode: 'PZA', allowsDecimals: false, isActive: true };
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) for (const width of [768, 1024, 1440]) for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 1024 } });
      const errors = []; page.on('pageerror', e => errors.push(e.message));
      await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
      await page.route('http://split.test/**', async route => {
        const url = new URL(route.request().url());
        if (['/entry', '/split', '/success'].includes(url.pathname))
          return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `${url.pathname.slice(1)}-${language}.html`) });
        if (url.pathname === '/Operations/Lookup') {
          const handler = url.searchParams.get('handler');
          return route.fulfill({ json: handler === 'Balance' ? { quantity: 0, version: 1 } : handler === 'Products' ? [integerProduct]
            : handler === 'Locations' || handler === 'ProductLocations' ? [other] : [] });
        }
        if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
        const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, ''));
        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 404, body: '' });
      });
      await page.goto('http://split.test/entry'); await page.waitForLoadState('networkidle');
      await page.locator('[data-distribution-toggle]').check();
      const parts = page.locator('[data-part-quantity]');
      assert.equal(await page.locator('[data-manual-quantity]').isVisible(), false);
      assert.ok(await parts.nth(0).evaluate(e => e === document.activeElement));
      assert.equal(await parts.nth(0).inputValue(), '');
      await parts.nth(0).fill('10');
      assert.ok(await page.locator('[data-distribution-error]').textContent());
      assert.ok(await page.locator('[data-review-button]').isDisabled());
      await parts.nth(1).fill('20');
      assert.equal(await page.locator('[data-quantity]').inputValue(), '30');
      assert.match(await page.locator('[data-balance-text]').textContent(), /→ 30/);
      assert.equal(await page.locator('[data-distribution-error]').textContent(), '');
      assert.equal(await page.locator('[data-distribution-difference]').count(), 0);
      assert.equal(await page.locator('[data-distribution-sum]').count(), 0);
      await page.locator('[data-distribution-toggle]').uncheck();
      assert.ok(await page.locator('[data-quantity]').evaluate(e => e === document.activeElement));
      assert.equal(await page.locator('[data-quantity]').inputValue(), '30');
      await page.locator('[data-quantity]').fill('999');
      await page.locator('[data-distribution-toggle]').check();
      assert.equal(await page.locator('[data-quantity]').inputValue(), '30');
      for (const invalid of ['0', '-1', '0.00001', '99999999999999.9999']) {
        await parts.nth(1).fill(invalid);
        assert.ok(await page.locator('[data-review-button]').isDisabled());
      }
      await parts.nth(0).fill('0.1'); await parts.nth(1).fill('0.2');
      assert.equal(await page.locator('[data-quantity]').inputValue(), '0.3');
      await parts.nth(0).fill('40'); await parts.nth(1).fill('60');
      await page.locator('[data-add-part]').focus(); await page.keyboard.press('Enter');
      assert.equal(await parts.count(), 3); assert.ok(await parts.nth(2).evaluate(e => e === document.activeElement));
      assert.ok(await page.locator('[data-review-button]').isDisabled());
      await parts.nth(1).fill('35'); await parts.nth(2).fill('25');
      assert.equal(await page.locator('[data-distribution-total]').textContent(), '100 EA');
      await page.locator('[data-remove-part]').nth(2).click();
      assert.equal(await page.locator('[data-quantity]').inputValue(), '75');
      await page.locator('[data-add-part]').click(); await parts.nth(2).fill('25');
      await page.locator('[data-review-button]').click();
      await page.locator('#confirm-operation.show').waitFor();
      await page.waitForFunction(() => document.querySelector('[data-pin-input]') === document.activeElement);
      assert.match(await page.locator('[data-confirmation-summary]').textContent(), /40.*35.*25/);
      await page.keyboard.press('Escape'); await page.locator('#confirm-operation').waitFor({ state: 'hidden' });
      await page.locator('[data-edit-step="quantity"]').click();
      assert.ok(await parts.nth(0).evaluate(e => e === document.activeElement));
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `entry-${language}-${width}-${theme}.png`), fullPage: true });
      await page.locator('[data-edit-step="product"]').click();
      await page.locator('#product-code').fill('INTEGER');
      await page.locator('[data-lookup-field="product"] [data-lookup-results]').getByRole('button', { name: /INTEGER/ }).click();
      await page.locator('[data-edit-step="quantity"]').click();
      await parts.nth(0).fill('1.5');
      assert.ok(await page.locator('[data-review-button]').isDisabled());
      await parts.nth(0).fill('40');
      assert.ok(await parts.nth(0).evaluate(e => e.checkValidity()));
      assert.ok(await page.locator('[data-review-button]').isEnabled());
      await page.locator('[data-edit-step="destination"]').click();
      await page.locator('#destination-code').fill('OTHER');
      await page.locator('[data-lookup-field="destination"] [data-lookup-results]').getByRole('button', { name: /OTHER/ }).click();
      assert.equal(await page.locator('[data-distribution-toggle]').isChecked(), false);
      assert.ok(await page.locator('[data-distribution-toggle]').isDisabled());
      assert.equal(await page.locator('[data-quantity]').inputValue(), '100');
      assert.equal(await page.locator('[data-quantity]').getAttribute('readonly'), null);
      await page.goto('http://split.test/split'); await page.waitForLoadState('networkidle');
      await parts.nth(0).fill('15'); await parts.nth(1).fill('20');
      assert.equal(await page.locator('[data-distribution-difference]').textContent(), '5 EA');
      assert.ok(await page.locator('[data-distribution-error]').textContent());
      await parts.nth(1).fill('25');
      await page.locator('[data-split-review]').focus(); await page.keyboard.press('Enter');
      await page.locator('#split-confirm.show').waitFor();
      await page.waitForFunction(() => document.querySelector('[data-pin-input]') === document.activeElement);
      assert.match(await page.locator('[data-split-summary]').textContent(), /15.*25/);
      await page.locator('[data-pin-input]').fill('0000');
      await page.keyboard.press('Escape'); await page.locator('#split-confirm.show').waitFor({ state: 'hidden' });
      await page.waitForFunction(() => document.querySelector('[data-pin-input]').value === '');
      assert.equal(await page.locator('[data-pin-input]').inputValue(), '');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `split-${language}-${width}-${theme}.png`), fullPage: true });
      await page.goto('http://split.test/success'); await page.waitForLoadState('networkidle');
      assert.equal(await page.locator('a[href*="handler=StagingArrival"]').count(), 1);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      assert.deepEqual(errors, []); await page.close();
    }
    console.log('Passed 12 browser scenarios: ES/EN, light/dark, 768/1024/1440; distribution totals, keyboard review, PIN cleanup and success links.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
