// Real Razor fixtures rendered by ProductionDayOverviewTests, served without operational data.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const output = path.join(root, 'artifacts/ui/playwright');
const fixtures = path.join(output, 'overview-fixtures');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ reducedMotion: 'reduce' });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.origin !== 'http://overview.test') return route.abort();
      if (url.pathname === '/Operations/Production') {
        const tab = url.searchParams.get('Tab') || 'summary';
        const lang = url.searchParams.get('culture') || 'es';
        const shift = url.searchParams.get('OverviewShift') || '0';
        const day = url.searchParams.get('Day');
        const name = tab === 'balance' ? 'balance' : tab === 'capture' ? 'capture' : day === '2035-01-01' ? 'summary-empty' : day === 'bad' ? 'summary-error' : `summary-${lang}-${shift}`;
        return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, name + '.html'), 'utf8') });
      }
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const start = 'http://overview.test/Operations/Production?Tab=summary&Day=2026-09-21';
    for (const language of ['es', 'en']) {
      await page.goto(start + '&culture=' + language);
      assert.equal(await page.locator('[data-overview-area]').count(), 3);
      for (const width of [390, 800, 899, 900, 1199, 1200, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(el => el.click());
          assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${language}/${width}/${theme}`);
          assert.ok((await page.locator('.day-overview__intro .btn').boundingBox()).height >= 44);
          if (language === 'es' && [800, 1440].includes(width) && theme !== 'system')
            await page.screenshot({ path: path.join(output, `production-overview-${width}-${theme}.png`), fullPage: true });
        }
      }
    }
    await page.setViewportSize({ width: 1024, height: 1000 });
    await page.goto(start);
    await page.locator('#overview-day').focus();
    await page.locator('#overview-shift').selectOption('2');
    await page.locator('#overview-shift').focus();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('[data-overview-filter] button').evaluate(el => el === document.activeElement), true);
    await Promise.all([page.waitForURL(/OverviewShift=2/), page.locator('[data-overview-filter] button').click()]);
    assert.equal(await page.locator('#overview-shift').inputValue(), '2');
    await page.goBack();
    await page.goForward();
    assert.equal(await page.locator('#overview-shift').inputValue(), '2');
    assert.equal(await page.locator('[data-overview-filter]').getAttribute('aria-busy'), null);
    await page.locator('.day-attention__product a').first().click();
    assert.equal(new URL(page.url()).searchParams.get('Sku'), 'RECOVER-001');
    assert.equal(new URL(page.url()).searchParams.get('Through'), '2026-09-21');
    await page.goBack();
    await page.locator('.day-overview__intro .btn').click();
    assert.equal(new URL(page.url()).searchParams.get('Tab'), 'capture');
    assert.equal(new URL(page.url()).searchParams.get('Day'), '2026-09-21');
    for (const day of ['2035-01-01', 'bad']) {
      await page.goto(start.replace('2026-09-21', day));
      assert.equal(await page.locator('[data-overview-area]').count(), 0);
      assert.ok(await page.locator('.day-overview__empty').isVisible());
    }
    assert.deepEqual(errors, []);
    console.log('Overview: Edge, 2 languages, 7 widths, light/dark/system, filters, links, back/forward, empty/error; passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
