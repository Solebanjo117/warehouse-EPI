// Rendered test-host Razor fixture; local assets only, no production access.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cli = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cli('playwright');
const output = path.join(root, 'artifacts/ui/playwright/label-series');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ timezoneId: 'America/Matamoros' });
    await page.clock.install({ time: new Date('2026-10-08T02:30:00Z') });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('http://series.test/**', route => {
      const url = new URL(route.request().url());
      if (url.pathname === '/' || url.pathname === '/initial') return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(output, url.pathname === '/initial' ? 'initial.html' : 'series.html'), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    await page.goto('http://series.test/initial');
    assert.equal(await page.locator('[data-label-series-field]').inputValue(), 'rollNumber');
    const date = page.locator('[name="Input.Values[receivingMfgDate]"]');
    assert.equal(await date.inputValue(), '2026-10-07');
    await date.fill('2025-01-02');
    await page.locator('[data-label-series-field]').selectOption('');
    assert.equal(await date.inputValue(), '2025-01-02');
    await page.goto('http://series.test/');
    assert.equal(await date.inputValue(), '2025-01-02');
    assert.equal(await page.locator('[data-label-copy]').count(), 6);
    const selector = page.locator('[data-label-series-field]');
    const end = page.locator('[data-label-series-end]');
    assert.equal(await end.inputValue(), '003');
    await selector.selectOption('');
    assert.equal(await end.isVisible(), false);
    assert.equal(await end.isDisabled(), true);
    await selector.selectOption('rollNumber');
    const start = page.locator('[name="Input.Values[rollNumber]"]');
    assert.equal(await end.evaluate(el => el.closest('.col-sm-6').previousElementSibling.querySelector('input').name), 'Input.Values[rollNumber]');
    await start.focus();
    await page.keyboard.press('Tab');
    assert.equal(await end.evaluate(el => el === document.activeElement), true);
    assert.equal(await end.getAttribute('required'), '');
    await selector.selectOption('yards');
    assert.equal(await end.evaluate(el => el.closest('.col-sm-6').previousElementSibling.querySelector('input').name), 'Input.Values[yards]');
    await selector.selectOption('rollNumber');
    for (const width of [800, 1024, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      for (const theme of ['light', 'dark']) {
        await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(button => button.click());
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        await page.screenshot({ path: path.join(output, `${width}-${theme}.png`), fullPage: true });
      }
    }
    await page.emulateMedia({ media: 'print' });
    assert.equal(await selector.isVisible(), false);
    assert.equal(await page.locator('[data-label-copy]:visible').count(), 6);
    assert.deepEqual(errors, []);
    console.log('PASS: series controls, keyboard, 6 ordered labels, print layout, 800/1024/1440, light/dark.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
