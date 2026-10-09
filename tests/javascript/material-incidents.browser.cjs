// Razor fixtures produced by MaterialIncidentRouteTests; no operational server or real camera.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(path.join(root, 'tools/playwright/package.json'));
const { chromium } = createRequire(toolRequire.resolve('@playwright/cli/package.json'))('playwright');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');
const output = path.join(root, 'artifacts/incidents-ui');
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aTioAAAAASUVORK5CYII=', 'base64');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) for (const width of [768, 1024, 1440]) for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 1024 } });
      const errors = []; page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(value => localStorage.setItem('warehouseEpi.theme', value), theme);
      await page.route('http://incidents.test/**', async route => {
        const url = new URL(route.request().url());
        if (url.searchParams.get('handler') === 'Products') return route.fulfill({ json: [{ id: '01234567-1111-2222-3333-012345678901', sku: 'INCIDENT-TEST', code: 'INCIDENT-TEST', description: 'Material with a damaged package', isActive: false }] });
        if (url.searchParams.get('handler') === 'Locations') return route.fulfill({ json: [{ id: '01234567-1111-2222-3333-012345678902', code: 'G2-HIST', description: 'Historical warehouse location', isActive: false, isBlocked: true }] });
        if (url.searchParams.get('handler') === 'SingleStockProduct') return route.fulfill({ json: { id: '01234567-1111-2222-3333-012345678901', code: 'INCIDENT-TEST' } });
        if (url.searchParams.get('handler') === 'SingleStockLocation') return route.fulfill({ json: { id: '01234567-1111-2222-3333-012345678902', code: 'G2-HIST' } });
        if (url.pathname.startsWith('/Operations/Incidents')) {
          const state = url.searchParams.get('fixture') || 'create';
          return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `${state}-${language}.html`),
            headers: { 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:" } });
        }
        if (url.pathname.includes('Notifications')) return route.fulfill({ json: { items: [], count: 0 } });
        const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, ''));
        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 404, body: '' });
      });
      await page.goto('http://incidents.test/Operations/Incidents/Create?fixture=initial');
      const contextProduct = page.locator('#new-incident-product');
      const contextLocation = page.locator('#new-incident-location');
      const proceed = page.locator('[data-context-continue]');
      assert.equal(await contextProduct.inputValue(), '');
      assert.equal(await contextLocation.isDisabled(), true);
      assert.equal(await proceed.isDisabled(), true);
      await contextProduct.fill('blanco rollo');
      await page.locator('#new-incident-products button').waitFor();
      assert.equal(await proceed.isDisabled(), true);
      await contextProduct.press('ArrowDown'); await contextProduct.press('Enter');
      assert.equal(await contextLocation.inputValue(), '');
      assert.equal(await contextLocation.isEnabled(), true);
      await contextLocation.fill('g2'); await page.locator('#new-incident-locations button').waitFor();
      await contextLocation.press('ArrowDown'); await contextLocation.press('Enter');
      assert.equal(await proceed.isEnabled(), true);
      await contextProduct.fill('otro');
      assert.equal(await contextLocation.inputValue(), '');
      assert.equal(await proceed.isDisabled(), true);
      await page.locator('#new-incident-products button').waitFor();
      await contextProduct.press('ArrowDown'); await contextProduct.press('Enter');
      await contextLocation.fill('g2'); await page.locator('#new-incident-locations button').waitFor();
      await contextLocation.press('ArrowDown'); await contextLocation.press('Enter');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `initial-${language}-${width}-${theme}.png`) });
      await proceed.press('Enter');
      await page.waitForURL(url => url.searchParams.has('ProductId'));
      assert.equal(new URL(page.url()).searchParams.get('ReturnUrl'), '/Operations/Incidents?status=all&search=sample&pageNumber=2');
      assert.equal(await page.locator('[data-incident-form]').count(), 1);
      await page.goto('http://incidents.test/Operations/Incidents/Create');
      await page.locator('#Description').fill('Empaque mojado y roto');
      await page.locator('#Quantity').fill('40');
      assert.equal(await page.locator('[data-difference-field]').isVisible(), false);
      await page.locator('#Kind').selectOption('QuantityDifference');
      assert.equal(await page.locator('[data-difference-field]').isVisible(), true);
      await page.locator('[data-photos]').setInputFiles({ name: 'evidence.png', mimeType: 'image/png', buffer: png });
      await page.waitForFunction(() => document.querySelector('[data-photo-preview] img')?.naturalWidth === 1);
      const cameraChooser = page.waitForEvent('filechooser');
      await page.locator('[data-open-camera]').click();
      await (await cameraChooser).setFiles({ name: 'camera.png', mimeType: 'image/png', buffer: png });
      assert.equal(await page.locator('[data-photo-preview] img').count(), 2);
      await page.locator('[data-photo-preview] button').first().click();
      assert.equal(await page.locator('[data-photo-preview] img').count(), 1);
      const review = page.locator('[data-incident-review]'); await review.focus(); await review.press('Enter');
      await page.waitForFunction(() => document.activeElement?.matches('[data-incident-pin]'));
      assert.match(await page.locator('[data-incident-summary]').innerText(), /Empaque mojado/);
      assert.equal(await page.locator('[data-incident-summary] img').count(), 1);
      await page.waitForFunction(() => document.querySelector('[data-incident-summary] img')?.naturalWidth === 1);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `review-${language}-${width}-${theme}.png`) });
      await page.keyboard.press('Escape'); await page.waitForFunction(() => document.activeElement?.matches('[data-incident-review]'));
      assert.equal(await page.locator('[data-incident-pin]').inputValue(), '');
      await page.locator('[data-photos]').setInputFiles({ name: 'invalid.txt', mimeType: 'text/plain', buffer: Buffer.from('invalid') });
      await review.click(); assert.equal(await page.locator('.modal.show').count(), 0);
      for (const fixture of ['error', 'details', 'list']) {
        await page.goto(`http://incidents.test/Operations/Incidents?fixture=${fixture}`);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${fixture} overflow at ${width}`);
        if (fixture === 'error') { assert.equal(await page.locator('[data-incident-pin]').inputValue(), ''); assert.equal(await page.locator('#Description').inputValue(), 'Empaque mojado y roto'); }
        if (fixture === 'list') {
          const product = page.locator('#ProductCode'), location = page.locator('#LocationCode');
          await product.fill('t6');
          await page.locator('#incident-products button').waitFor();
          await product.press('ArrowDown'); await product.press('Enter');
          assert.equal(await product.inputValue(), 'INCIDENT-TEST');
          assert.equal(await page.locator('#ProductId').inputValue(), '01234567-1111-2222-3333-012345678901');
          assert.equal(await product.getAttribute('aria-expanded'), 'false');
          await page.waitForFunction(() => document.querySelector('#LocationCode').value === 'G2-HIST');
          assert.equal(await page.locator('#LocationId').inputValue(), '01234567-1111-2222-3333-012345678902');
          await product.fill('otro');
          assert.equal(await location.inputValue(), '');
          assert.equal(await page.locator('#LocationId').inputValue(), '');
          assert.equal(await page.locator('#ProductId').inputValue(), '');
          await page.locator('#incident-products button').waitFor();
          await product.press('Escape');
          assert.equal(await page.locator('#incident-products button').count(), 0);
          await location.fill('g2');
          await page.locator('#incident-locations button').waitFor();
          await page.screenshot({ path: path.join(output, `filters-${language}-${width}-${theme}.png`) });
          await page.locator('#incident-locations button').click();
          assert.equal(await location.inputValue(), 'G2-HIST');
          assert.equal(await page.locator('#LocationId').inputValue(), '01234567-1111-2222-3333-012345678902');
          await page.waitForFunction(() => document.querySelector('#ProductCode').value === 'INCIDENT-TEST');
          assert.equal(await page.locator('#ProductId').inputValue(), '01234567-1111-2222-3333-012345678901');
          assert.equal(await product.getAttribute('aria-expanded'), 'false');
          await location.fill('');
          assert.equal(await product.inputValue(), '');
          assert.equal(await page.locator('#ProductId').inputValue(), '');
          assert.equal(await page.locator('#LocationId').inputValue(), '');
          assert.equal(await page.locator('#incident-locations button').count(), 0);
          await page.route('**/*handler=SingleStockProduct*', async route => {
            await new Promise(resolve => setTimeout(resolve, 400));
            await route.fulfill({ json: { id: 'late-id', code: 'LATE-PRODUCT' } }).catch(() => {});
          });
          await location.fill('g2'); await page.locator('#incident-locations button').waitFor();
          await page.locator('#incident-locations button').click();
          await product.fill('manual');
          await page.waitForTimeout(500);
          assert.equal(await product.inputValue(), 'manual');
          assert.equal(await page.locator('#ProductId').inputValue(), '');
          await page.unroute('**/*handler=SingleStockProduct*');
          await page.route('**/*handler=SingleStockLocation*', async route => {
            await new Promise(resolve => setTimeout(resolve, 400));
            await route.fulfill({ json: { id: 'late-location', code: 'LATE-LOCATION' } }).catch(() => {});
          });
          await product.fill('t6'); await page.locator('#incident-products button').waitFor();
          await product.press('ArrowDown'); await product.press('Enter');
          await location.fill('manual-location');
          await page.waitForTimeout(500);
          assert.equal(await location.inputValue(), 'manual-location');
          assert.equal(await page.locator('#LocationId').inputValue(), '');
          await page.unroute('**/*handler=SingleStockLocation*');
          await product.fill('manual');
          await page.route('**/*handler=SingleStockProduct*', route => route.fulfill({ json: null }));
          await location.fill('g2'); await page.locator('#incident-locations button').waitFor();
          const noSingle = page.waitForResponse(response => response.url().includes('handler=SingleStockProduct'));
          await page.locator('#incident-locations button').click(); await noSingle;
          assert.equal(await product.inputValue(), 'manual');
          await page.unroute('**/*handler=SingleStockProduct*');
          await page.route('**/*handler=SingleStockProduct*', route => route.fulfill({ status: 503, body: '' }));
          await location.fill('g2'); await page.locator('#incident-locations button').waitFor();
          await page.locator('#incident-locations button').click();
          await page.waitForFunction(() => /consultar el stock|Stock could not/.test(document.querySelector('#incident-products-feedback').textContent));
          assert.equal(await product.inputValue(), 'manual');
          await page.unroute('**/*handler=SingleStockProduct*');
          await page.route('**/*handler=Products*', route => route.fulfill({ status: 503, body: '' }));
          await product.fill('conservar');
          await page.waitForFunction(() => /red local|local network/.test(document.querySelector('#incident-products-feedback').textContent));
          assert.equal(await product.inputValue(), 'conservar');
          await page.unroute('**/*handler=Products*');
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        }
      }
      for (const fixture of ['staging', 'tracking', 'inventory', 'location']) {
        await page.goto(`http://incidents.test/Operations/Incidents?fixture=${fixture}`);
        assert.ok(await page.locator('a[href*="/Operations/Incidents/Create"]').count() > 0);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${fixture} overflow at ${width}`);
        if (width === 768 && language === 'es' && theme === 'light') await page.screenshot({ path: path.join(output, `${fixture}-es-768-light.png`) });
      }
      await page.goto('http://incidents.test/Operations/Incidents/Create?fixture=picker');
      await page.locator('[data-product-search]').fill('INCIDENT');
      await page.waitForSelector('[data-product-results] a');
      await page.locator('[data-product-search]').press('ArrowDown');
      assert.ok(await page.locator('[data-product-results] a').evaluate(element => element === document.activeElement));
      await page.keyboard.press('Enter');
      await page.waitForURL(url => url.searchParams.has('productId'));
      assert.deepEqual(errors, []); await page.close();
    }
    console.log('Passed 12 incident UI scenarios: ES/EN, light/dark, tablet/desktop, keyboard review, photo and camera chooser, error recovery and product suggestions. Physical camera not tested.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
