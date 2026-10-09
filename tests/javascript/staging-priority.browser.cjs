// Razor fixtures produced by StagingArrivalRouteTests. No operational server.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');
const output = path.join(root, 'artifacts/staging-ui');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) for (const width of [768, 1024, 1440]) for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 1024 } });
      const errors = []; page.on('pageerror', e => errors.push(e.message));
      await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
      await page.route('http://priority.test/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/Operations/Staging') {
          const state = url.searchParams.get('fixture');
          return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', state === 'pending' ? `staging-list-${language}.html` : `staging-priority${state ? '-' + state : ''}-${language}.html`) });
        }
        if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
        const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, ''));
        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 404, body: '' });
      });
      await page.goto('http://priority.test/Operations/Staging'); await page.waitForLoadState('networkidle');
      assert.equal(await page.locator('[data-pending-total]').innerText(), '28');
      assert.equal(await page.locator('[data-pending-urgent]').innerText(), '28');
      assert.equal(await page.locator('[data-arrival-priority="urgent"]').count(), 3);
      assert.equal(await page.locator('#Priority').inputValue(), 'urgent');
      const unidentified = page.locator('article').first();
      const identify = unidentified.getByRole('button', { name: language === 'en' ? 'Identify material' : 'Identificar material', exact: true });
      await identify.focus(); await identify.press('Enter');
      await unidentified.locator('[name="PhysicalQuantity"]').waitFor({ state: 'visible' });
      assert.equal(await identify.getAttribute('aria-expanded'), 'true');
      const historicalMenu = unidentified.locator('[data-bs-toggle="dropdown"]');
      await historicalMenu.click();
      assert.equal(await unidentified.locator('.dropdown-menu a').count(), 2);
      await historicalMenu.press('Escape');
      await page.locator('#Priority').focus();
      assert.ok(await page.locator('#Priority').evaluate(e => e === document.activeElement));
      await page.locator('#Priority').selectOption('soon');
      const search = page.getByRole('button', { name: language === 'en' ? 'Search' : 'Buscar', exact: true });
      await search.focus(); await search.press('Enter'); await page.waitForLoadState('networkidle');
      const searched = new URL(page.url());
      assert.equal(searched.searchParams.get('Priority'), 'soon');
      assert.equal(searched.searchParams.get('PageNumber'), null);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `priority-${language}-${width}-${theme}.png`) });
      await page.goto('http://priority.test/Operations/Staging?fixture=error');
      assert.equal(await page.locator('#Priority').inputValue(), 'urgent');
      assert.equal(await page.locator('[data-arrival-age]').count(), 3);
      await page.goto('http://priority.test/Operations/Staging?fixture=empty');
      assert.equal(await page.locator('[data-pending-total]').innerText(), '0');
      assert.equal(await page.locator('[data-arrival-priority]').count(), 0);
      await page.goto('http://priority.test/Operations/Staging?fixture=pending');
      const card = page.locator('article').first();
      assert.equal(await card.locator('[data-open-incidents]').count(), 0);
      assert.equal(await card.locator('[data-staging-actions] .btn:visible').count(), 2);
      const more = card.getByRole('button', { name: language === 'en' ? 'More actions' : 'Más acciones', exact: true });
      await more.focus(); await more.press('ArrowDown');
      await card.locator('.dropdown-menu.show').waitFor();
      assert.equal(await card.locator('.dropdown-menu a').count(), 3);
      assert.ok(await card.locator('.dropdown-menu a').first().evaluate(e => e === document.activeElement));
      const links = await card.locator('.dropdown-menu a').evaluateAll(items => items.map(e => ({ height: e.getBoundingClientRect().height, href: e.getAttribute('href') })));
      assert.ok(links.every(item => item.height >= 44));
      assert.ok(links.some(item => item.href.includes('/Operations/Incidents/Create?arrivalLineId=')));
      assert.ok(links.some(item => item.href.includes('/Operations/Staging/Split?arrivalLineId=')));
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `actions-${language}-${width}-${theme}.png`) });
      await page.keyboard.press('Escape');
      assert.equal(await more.getAttribute('aria-expanded'), 'false');
      assert.ok(await more.evaluate(e => e === document.activeElement));
      assert.deepEqual(errors, []); await page.close();
    }
    console.log('Passed 12 priority scenarios: ES/EN, light/dark, 768/1024/1440, keyboard filters, errors and empty state.');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
