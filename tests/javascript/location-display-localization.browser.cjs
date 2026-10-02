const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = path.join(root, 'artifacts/ui/playwright/display-localization');
const output = path.join(root, 'artifacts/ui/playwright/localization');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let checks = 0;
  try {
    for (const language of ['es', 'en']) {
      const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, reducedMotion: 'reduce' });
      const errors = []; let fail = false;
      page.on('pageerror', error => errors.push(error.message));
      await page.route('http://display.test/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/Locations/Display') {
          const refresh = url.searchParams.has('refresh');
          if (refresh && fail) return route.fulfill({ status: 503 });
          const racks = url.searchParams.get('racks') || '2';
          const orientation = url.searchParams.get('orientation') || 'auto';
          const suffix = orientation === 'auto' ? '' : '-' + orientation;
          const file = !url.searchParams.has('play') ? `config-${racks}-${orientation}.html`
            : refresh ? `refresh-${racks}-${url.searchParams.get('rows')}${suffix}.html` : `display-${racks}${suffix}.html`;
          return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, language, file), 'utf8') });
        }
        const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
        const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
        if (fs.existsSync(file)) return route.fulfill({ path: file });
        return route.fulfill({ status: 404 });
      });
      await page.goto('http://display.test/Locations/Display?racks=2');
      assert.equal(await page.locator('html').getAttribute('lang'), language);
      assert.equal(await page.locator('h1').textContent(), language === 'en' ? 'Row display' : 'Exhibición de filas');
      await page.locator('input[name="orientation"][value="portrait"]').check();
      assert.match(await page.locator('[data-display-preview-caption]').textContent(), language === 'en' ? /Preview: portrait, one rack/ : /Vista previa: vertical, un rack/);
      await page.locator('[data-display-here-map]').click();
      assert.equal(await page.locator('[data-display-here-status]').textContent(), language === 'en' ? 'Monitor location marked.' : 'Ubicación del monitor marcada.');
      await page.locator('[data-display-here-clear]').click();
      assert.equal(await page.locator('[data-display-here-status]').textContent(), language === 'en' ? 'No monitor location.' : 'Sin ubicación del monitor.');
      for (const checkbox of await page.locator('input[name="rows"]').all()) await checkbox.uncheck();
      assert.equal(await page.locator('input[name="rows"]').first().evaluate(input => input.validationMessage), language === 'en' ? 'Select at least one row.' : 'Selecciona al menos una fila.');
      checks += 6;
      await page.goto('http://display.test/Locations/Display?play=true&rows=A&rows=B&racks=2');
      await page.locator('[data-display-pause]').click();
      assert.equal(await page.locator('[data-display-pause]').textContent(), language === 'en' ? 'Resume' : 'Reanudar');
      for (const [width, height] of [[1920, 1080], [768, 1024]]) {
        await page.setViewportSize({ width, height });
        await page.waitForFunction(() => document.body.dataset.displayEffectiveOrientation === (innerWidth < 900 ? 'portrait' : 'landscape'));
        assert.match(await page.locator('[data-display-slide]:visible').getAttribute('aria-label'), language === 'en' ? /^Row A/ : /^Fila A/);
        assert.match(await page.locator('[data-display-progress]').textContent(), language === 'en' ? / of / : / de /);
        for (const theme of ['light', 'dark']) {
          await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
          await page.screenshot({ path: path.join(output, `display-${language}-${width}-${theme}.png`), fullPage: true });
        }
        checks += 2;
      }
      fail = true;
      await page.waitForFunction(expected => document.querySelector('[data-display-status]').textContent === expected,
        language === 'en' ? 'Could not refresh · showing the last query' : 'No se pudo actualizar · se muestra la última consulta');
      fail = false;
      await page.waitForFunction(expected => document.querySelector('[data-display-status]').textContent === expected, language === 'en' ? 'Paused' : 'Pausado');
      await page.locator('[data-display-row-jump="B"]').click();
      assert.match(await page.locator('[data-display-map-caption]').textContent(), language === 'en' ? /^Row B/ : /^Fila B/);
      assert.deepEqual(errors, []);
      checks += 4;
      await page.close();
    }
    console.log(JSON.stringify({ passed: true, languages: ['es', 'en'], checks, screenshots: output }));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
