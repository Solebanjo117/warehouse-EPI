// Sanitized Razor fixtures and local assets; no production requests or credentials.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const output = path.join(root, 'artifacts/ui/playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ reducedMotion: 'reduce' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('http://inventory.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('.html')) return route.fulfill({ contentType: 'text/html',
        body: fs.readFileSync(path.join(output, 'internal-inventory-fixtures', path.basename(url.pathname)), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    for (const language of ['es', 'en']) {
      await page.goto(`http://inventory.test/import-${language}.html`);
      const toggle = page.locator('[aria-controls="internal-inventory-form"]');
      const password = page.locator('#inventory-password');
      await toggle.focus();
      await page.keyboard.press('Enter');
      await password.waitFor({ state: 'visible' });
      await page.waitForFunction(() => document.activeElement?.id === 'inventory-password');
      assert.equal(await password.inputValue(), '');
      assert.equal(await page.locator('[data-internal-inventory-form] input:not([type=hidden])').count(), 1);
      await page.keyboard.press('Tab');
      assert.equal(await page.locator('[data-internal-inventory-form] button').evaluate(button => button === document.activeElement), true);
      for (const width of [800, 1024, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(button => button.click());
          assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
          assert.ok((await page.locator('[data-internal-inventory-form] button').boundingBox()).height >= 44);
          if (theme !== 'system') await page.screenshot({ path: path.join(output, `internal-inventory-${language}-${width}-${theme}.png`), fullPage: true });
        }
      }
      await page.locator('[data-internal-inventory-form]').evaluate(form => form.addEventListener('submit', event => event.preventDefault()));
      await password.fill('synthetic-test-value');
      await page.locator('[data-internal-inventory-form] button').click();
      assert.equal(await page.locator('[data-internal-inventory-form] button').isDisabled(), true);
      assert.equal(await page.locator('[data-inventory-loading]').isVisible(), true);
      await page.evaluate(() => window.dispatchEvent(new Event('pageshow')));
      assert.equal(await password.inputValue(), '');
      assert.equal(await page.locator('[data-internal-inventory-form] button').isEnabled(), true);
    }
    assert.deepEqual(errors, []);
    console.log('Internal inventory: ES/EN, 800/1024/1440, light/dark/system, keyboard, loading and password reset passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
