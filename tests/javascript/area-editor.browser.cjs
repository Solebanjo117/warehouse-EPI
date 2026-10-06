// Rendered Razor fixtures and local assets only; no operational application or database.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = path.join(root, 'artifacts/ui/playwright/area-fixtures');
const output = path.join(root, 'artifacts/ui/playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ reducedMotion: 'reduce' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('http://area-editor.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('.html')) return route.fulfill({
        contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, path.basename(url.pathname)), 'utf8')
      });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    for (const language of ['es', 'en']) {
      for (const view of ['editor', 'list']) {
        await page.goto(`http://area-editor.test/area-${view}-${language}.html`);
        for (const width of [800, 1024, 1440]) {
          await page.setViewportSize({ width, height: 1000 });
          for (const theme of ['light', 'dark', 'system']) {
            await page.emulateMedia({ colorScheme: 'dark' });
            await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(button => button.click());
            assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme, JSON.stringify(errors));
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
            const primary = page.locator(view === 'editor' ? 'form:not([data-area-delete-form]) button.btn-primary' : 'article a.btn-primary').first();
            assert.ok((await primary.boundingBox()).height >= 44);
            if (theme !== 'system') await page.screenshot({ path: path.join(output, `area-${view}-${language}-${width}-${theme}.png`), fullPage: true });
          }
        }
        if (view === 'editor') {
          await page.locator('#Input_Code').focus();
          await page.keyboard.press('Tab');
          assert.equal(await page.locator('#Input_Description').evaluate(input => input === document.activeElement), true);
          await page.locator('label[for="Input_IsBlocked"]').click();
          assert.equal(await page.locator('#Input_IsBlocked').isChecked(), true);
          await page.locator('#Input_BlockReason').fill('Revisión de prueba');
          assert.equal(await page.locator('#Input_BlockReason').inputValue(), 'Revisión de prueba');
          assert.equal(await page.locator('input[name="DeleteInput.Pin"]').inputValue(), '');
        } else {
          assert.equal(await page.locator('article').count(), 1);
          assert.match(await page.locator('article').innerText(), /Descripción corregida/);
        }
      }
    }
    assert.deepEqual(errors, []);
    console.log('Area UI: 2 languages, 3 widths, light/dark/system, keyboard and state controls passed.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
