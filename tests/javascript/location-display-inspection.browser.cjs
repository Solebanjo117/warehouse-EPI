const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtureRoot = process.env.WAREHOUSE_DISPLAY_FIXTURES || path.join(root, 'artifacts/ui/playwright/display-fixtures');
const output = path.join(root, 'artifacts/ui/playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const language of ['es', 'en']) {
      const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, reducedMotion: 'reduce' });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      const fixtures = path.join(fixtureRoot, language, 'interactive');
      const locations = JSON.parse(fs.readFileSync(path.join(fixtures, 'locations.json'), 'utf8'));
      const rack = locations.locations.find(item => item.kind === 'Rack' && item.quantity < 0);
      const area = locations.locations.find(item => item.kind === 'Area');
      const assigned = locations.locations.find(item => !item.hasNonZeroBalance);
      let fail = false, failProducts = false, holdRack = false, releaseRack, releaseProducts, inspectRequests = 0;
      await page.route('http://display.test/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/Locations/Display') {
          const handler = url.searchParams.get('handler');
          if (handler === 'Products') {
            const q = url.searchParams.get('q');
            if (failProducts) return route.fulfill({ status: 503 });
            if (q === 'slow') await new Promise(resolve => { releaseProducts = resolve; });
            const file = q === 'EXTRA' ? 'partial-products.json' : 'products.json';
            return route.fulfill({ contentType: 'application/json', body: q === 'absent' ? '[]' : fs.readFileSync(path.join(fixtures, file), 'utf8') });
          }
          if (handler === 'ProductLocations') return route.fulfill({ contentType: 'application/json', body: JSON.stringify(locations) });
          if (handler === 'Inspect') {
            inspectRequests++;
            if (fail) return route.fulfill({ status: 503 });
            if (holdRack && url.searchParams.get('rowCode') === 'B') {
              holdRack = false;
              await new Promise(resolve => { releaseRack = resolve; });
            }
            const id = url.searchParams.get('locationId');
            const file = id === area.locationId ? 'area.html' : id === assigned.locationId || url.searchParams.get('rowCode') === 'A' ? 'assigned.html' : 'rack.html';
            return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, file), 'utf8') });
          }
          const file = url.searchParams.get('racks') === '2' ? 'display-grouped.html' : 'display.html';
          let html = fs.readFileSync(path.join(fixtures, file), 'utf8');
          if (url.searchParams.has('withoutMap')) html = html.replace(/<figure class="display-map"[\s\S]*?<\/figure>/, '');
          return route.fulfill({ contentType: 'text/html', body: html });
        }
        const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
        const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
        return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
      });
      await page.goto('http://display.test/Locations/Display?play=true&rows=A');
      const visibleSlide = page.locator('[data-display-slide]:visible');
      const startingRack = await visibleSlide.getAttribute('data-display-racks');
      await page.locator('[data-display-next]').click();
      const savedRack = await visibleSlide.getAttribute('data-display-racks');
      assert.notEqual(savedRack, startingRack);
      const geometry = async container => container.evaluate(element => {
        const cell = element.querySelector('.display-cell');
        const map = document.querySelector('[data-display-map]');
        const css = getComputedStyle(cell);
        return { width: element.getBoundingClientRect().width, x: element.getBoundingClientRect().x,
          frameY: element.querySelector('.display-rack-frame').getBoundingClientRect().y,
          minHeight: css.minHeight, codeSize: getComputedStyle(cell.querySelector('.display-cell-code')).fontSize,
          mapWidth: map.getBoundingClientRect().width, mapX: map.getBoundingClientRect().x,
          mapHeight: map.getBoundingClientRect().height };
      });
      const originalGeometry = await geometry(visibleSlide);
      await page.locator(`[data-display-map-select][data-map-rack="${savedRack}"]`).click();
      const inspect = page.locator('[data-display-inspection]');
      await inspect.waitFor({ state: 'visible' });
      assert.deepEqual(await geometry(inspect), originalGeometry, 'Inspection keeps the exhibition rack and map geometry');
      assert.equal(await inspect.locator('.display-sign').innerText(), 'A');
      await page.screenshot({ path: path.join(output, `display-inspection-same-layout-${language}.png`), fullPage: true });
      await page.locator('[data-display-pause]').click();
      const mapRack = page.locator('[data-display-map-select][data-map-rack="B-2"]');
      await mapRack.focus();
      await page.keyboard.press('Enter');
      await inspect.waitFor({ state: 'visible' });
      assert.match(await inspect.innerText(), /EXTRA-5/);
      assert.equal(await inspect.locator('.display-cell').count(), 9);
      assert.equal(await visibleSlide.count(), 0);
      assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('[data-display-next]').isVisible(), false);
      for (const theme of ['light', 'dark']) {
        await page.evaluate(theme => { document.documentElement.dataset.bsTheme = theme; }, theme);
        for (const width of [1280, 1190, 910, 890, 768]) {
          await page.setViewportSize({ width, height: width < 900 ? 1024 : 800 });
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Overflow ${language}/${theme}/${width}`);
          assert.ok(await page.evaluate(() => document.querySelector('[data-display-map]').getBoundingClientRect().bottom <= document.querySelector('.display-footer').getBoundingClientRect().top), 'Map stays above footer');
          if (width === 1280 || width === 768) await page.screenshot({ path: path.join(output, `display-inspection-${language}-${theme}-${width}.png`), fullPage: true });
        }
      }
      await page.locator('[data-display-search-open]').click();
      const input = page.locator('[data-display-search-input]');
      await input.fill('EXTRA');
      await page.locator('[data-display-product-options] [role="option"]').first().waitFor();
      assert.equal(await page.locator('[data-display-product-options] [role="option"]').count(), 5);
      assert.equal(await input.getAttribute('aria-expanded'), 'true');
      assert.match(await inspect.innerText(), /B-2/);
      assert.equal(await visibleSlide.count(), 0, 'Typing keeps the inspected rack visible');
      assert.equal(await page.locator('[data-display-selected-product]').isVisible(), false, 'Typing does not select automatically');
      await page.screenshot({ path: path.join(output, `display-product-suggestions-${language}.png`), fullPage: true });
      await input.press('Escape');
      assert.equal(await input.getAttribute('aria-expanded'), 'false');
      assert.equal(await page.locator('[data-display-search-panel]').isVisible(), true, 'First Escape dismisses suggestions');
      // An older autocomplete response cannot replace suggestions for the latest text.
      await input.fill('slow');
      while (!releaseProducts) await new Promise(resolve => setTimeout(resolve, 10));
      await input.fill('EXTRA');
      await page.locator('[data-display-product-options] [role="option"]').first().waitFor();
      releaseProducts();
      await new Promise(resolve => setTimeout(resolve, 150));
      assert.equal(await page.locator('[data-display-product-options] [role="option"]').count(), 5);
      failProducts = true;
      await input.fill('network-error');
      await page.waitForFunction(() => /No se pudo consultar|Could not load/.test(document.querySelector('[data-display-search-status]').textContent));
      assert.match(await inspect.innerText(), /B-2/);
      failProducts = false;
      await input.fill('LOOK');
      await page.locator('[data-display-product-options] [role="option"]').first().waitFor();
      await input.press('ArrowDown');
      assert.equal(await input.getAttribute('aria-activedescendant'), 'display-product-option-0');
      await input.press('Enter');
      await page.waitForFunction(() => document.querySelectorAll('[data-display-location-result]').length === 3);
      assert.equal(await input.inputValue(), 'LOOKUP');
      assert.equal(await input.getAttribute('aria-expanded'), 'false');
      assert.match(await inspect.innerText(), /B-2/, 'Choosing a product retains the rack until a location is chosen');
      assert.equal(await page.locator('[data-display-selected-product]').isVisible(), true);
      // Manual/HID Enter still resolves an exact code immediately.
      await input.fill('123456789');
      await input.press('Enter');
      await page.waitForFunction(() => document.querySelector('[data-display-search-input]').value === 'LOOKUP'
        && document.querySelectorAll('[data-display-location-result]').length === 3
        && document.querySelectorAll('.is-product-match').length === 3);
      assert.equal(await page.locator('.is-product-match').count(), 3);
      assert.equal(await page.locator('.is-assigned-match').count(), 1);
      const locationButtons = page.locator('[data-display-location-result]');
      assert.equal(await locationButtons.count(), 3);
      await page.locator(`[data-display-location-result="${rack.locationId}"]`).click();
      await inspect.waitFor({ state: 'visible' });
      await page.waitForFunction(id => document.querySelector('.is-selected-location')?.dataset.displayLocation === id, rack.locationId);
      assert.equal(await inspect.locator('.is-selected-product').count(), 1);
      assert.equal(await page.locator('[data-map-rack="B-2"]').getAttribute('aria-pressed'), 'true');
      await page.screenshot({ path: path.join(output, `display-search-${language}.png`), fullPage: true });
      const beforePoll = inspectRequests;
      await page.waitForFunction(() => document.querySelector('[data-display-inspection] [data-inspection-updated]'));
      await new Promise(resolve => setTimeout(resolve, 2200));
      assert.ok(inspectRequests > beforePoll, 'Visible inspection refreshes every two seconds');
      assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'), 'true');
      fail = true;
      await page.waitForFunction(() => /No se pudo actualizar|Could not refresh/.test(document.querySelector('[data-display-status]').textContent));
      assert.match(await inspect.innerText(), /EXTRA-5/);
      fail = false;
      await page.locator(`[data-display-location-result="${area.locationId}"]`).click();
      await page.waitForFunction(() => document.querySelector('[data-display-inspection]').textContent.includes('SHIPPING'));
      assert.equal(await page.locator(`[data-map-location="${area.locationId}"]`).getAttribute('aria-pressed'), 'true');
      await page.locator(`[data-display-location-result="${assigned.locationId}"]`).click();
      await page.waitForFunction(() => /Asignado sin saldo|Assigned without stock/.test(document.querySelector('[data-display-inspection]').textContent));
      await page.locator('[data-display-pause]').click();
      assert.equal(await visibleSlide.getAttribute('data-display-racks'), savedRack);
      assert.equal(await page.locator('.is-product-match').count(), 0);
      assert.equal(await page.locator('[data-display-search-panel]').isVisible(), false);
      assert.equal(await inspect.isVisible(), false);
      assert.equal(await page.locator('[data-display-next]').isVisible(), true);
      // A late rack response must not override a newer area selection.
      holdRack = true;
      await mapRack.click();
      await page.waitForFunction(() => document.body.dataset.displayConsulting === 'true');
      while (!releaseRack) await new Promise(resolve => setTimeout(resolve, 10));
      await page.locator(`[data-map-location="${area.locationId}"]`).click();
      await page.waitForFunction(() => document.querySelector('[data-display-inspection]').textContent.includes('SHIPPING'));
      releaseRack();
      await new Promise(resolve => setTimeout(resolve, 150));
      assert.match(await inspect.innerText(), /SHIPPING/);
      await page.locator('[data-display-pause]').click();
      await page.locator('[data-display-search-open]').click();
      await input.fill('absent');
      await input.press('Enter');
      await page.waitForFunction(() => /No se encontraron productos|No products found/.test(document.querySelector('[data-display-search-status]').textContent));
      await input.press('Escape');
      assert.equal(await page.locator('[data-display-search-panel]').isVisible(), false);
      assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('[data-display-search-open]').evaluate(element => element === document.activeElement), true);
      await page.goto('http://display.test/Locations/Display?play=true&rows=A&withoutMap=1');
      await page.locator('[data-display-search-open]').click();
      await input.fill('LOOKUP');
      await input.press('Enter');
      await page.waitForFunction(() => document.querySelectorAll('[data-display-location-result]').length === 3);
      const missingMap = language === 'es' ? 'Ubicación no visible en el croquis' : 'Location not visible on the map';
      assert.equal(await page.getByText(missingMap, { exact: true }).count(), 3);
      await page.locator(`[data-display-location-result="${area.locationId}"]`).click();
      await page.waitForFunction(() => document.querySelector('[data-display-inspection]').textContent.includes('SHIPPING'));
      // Consultation presents one rack using the single-rack design, then restores grouping.
      await page.setViewportSize({ width: 1280, height: 800 });
      await page.goto('http://display.test/Locations/Display?play=true&rows=A&racks=2');
      assert.equal(await visibleSlide.locator('[data-display-rack]').count(), 2);
      await page.locator('[data-display-search-open]').click();
      assert.equal(await visibleSlide.locator('.display-rack').first().evaluate(element => getComputedStyle(element).display), 'grid');
      await mapRack.click();
      await inspect.waitFor({ state: 'visible' });
      assert.equal(await inspect.locator('[data-display-rack]').count(), 1);
      assert.equal(await inspect.locator('.display-rack').evaluate(element => getComputedStyle(element).display), 'block');
      assert.equal(await page.locator('.display-stage').evaluate(element => getComputedStyle(element).display), 'grid');
      assert.equal(await geometry(inspect).then(value => value.mapWidth), originalGeometry.mapWidth);
      await page.locator('[data-display-pause]').click();
      assert.equal(await visibleSlide.locator('[data-display-rack]').count(), 2);
      assert.equal(await visibleSlide.locator('.display-rack').first().evaluate(element => getComputedStyle(element).display), 'grid');
      assert.deepEqual(errors, []);
      await page.close();
      console.log(`Interactive display: ${language}, racks/areas/search/poll/errors/late replies/themes/keyboard/widths passed`);
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
