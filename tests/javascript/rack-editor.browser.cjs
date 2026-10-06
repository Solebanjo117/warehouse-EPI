// Uses rendered Razor fixtures and local assets; never connects to the operational application.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = process.env.WAREHOUSE_RACK_FORMAT_FIXTURES || path.join(root, 'artifacts/ui/playwright/rack-format-fixtures');
const output = path.join(root, 'artifacts/ui/playwright');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('http://rack-format.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('.html')) return route.fulfill({
        contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, path.basename(url.pathname)), 'utf8')
      });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const visibleOrder = () => page.locator('[data-rack-position]:visible').evaluateAll(nodes => nodes.map(node => Number(node.dataset.pallet)));
    for (const language of ['es', 'en']) {
      await page.goto(`http://rack-format.test/editor-${language}.html`);
      assert.deepEqual(await visibleOrder(), [7,8,9,4,5,6,1,2,3]);
      await page.locator('[data-rack-columns-input]').selectOption('2');
      await page.locator('[data-rack-levels-input]').selectOption('2');
      assert.deepEqual(await visibleOrder(), [3,4,1,2]);
      assert.equal(await page.locator('[data-rack-present]:checked').count(), 4);
      await page.locator('#rack-role-1').selectOption('Wip');
      assert.equal(await page.locator('[data-rack-wip]:checked').getAttribute('value'), '1');
      await page.locator('#rack-present-4').uncheck();
      assert.equal(await page.locator('#rack-role-4').isDisabled(), true);
      await page.locator('[data-rack-levels-input]').selectOption('3');
      assert.deepEqual(await visibleOrder(), [5,6,3,4,1,2]);
      assert.equal(await page.locator('#rack-present-4').isChecked(), true);
      assert.equal(await page.locator('#rack-role-1').inputValue(), 'Wip');
      await page.locator('[data-rack-columns-input]').selectOption('3');
      assert.deepEqual(await visibleOrder(), [7,8,9,4,5,6,1,2,3]);
      await page.locator('[data-rack-columns-input]').selectOption('1');
      await page.locator('[data-rack-levels-input]').selectOption('1');
      assert.deepEqual(await visibleOrder(), [1]);

      await page.goto(`http://rack-format.test/saved-${language}.html`);
      assert.deepEqual(await visibleOrder(), [3,4,1,2]);
      for (const [width, height] of [[800,1280],[1024,768],[1440,1000]]) {
        await page.setViewportSize({ width, height });
        for (const theme of ['light','dark']) {
          await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
          const bounds = await page.locator('[data-rack-position]:visible').evaluateAll(nodes => nodes.map(node => {
            const box = node.getBoundingClientRect(); return { pallet: Number(node.dataset.pallet), x: box.x, y: box.y, width: box.width };
          }));
          assert.deepEqual(bounds.map(box => box.pallet), [3,4,1,2]);
          assert.equal(bounds[0].y, bounds[1].y);
          assert.equal(bounds[2].y, bounds[3].y);
          assert.ok(bounds[2].y > bounds[0].y && bounds[1].x > bounds[0].x);
          assert.ok(bounds.every(box => box.width > 100));
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
          await page.screenshot({ path: path.join(output, `rack-format-${language}-${width}-${theme}.png`), fullPage: true });
        }
      }
      await page.locator('[data-rack-all-wip]').click();
      assert.equal(await page.locator('[data-rack-wip]:checked').count(), 4);
      await page.locator('[data-rack-all-storage]').click();
      assert.equal(await page.locator('[data-rack-wip]:checked').count(), 0);

      await page.goto(`http://rack-format.test/protected-${language}.html`);
      await page.locator('[data-rack-columns-input]').selectOption('2');
      await page.locator('[data-rack-levels-input]').selectOption('2');
      assert.equal(await page.locator('[data-rack-levels-input]').inputValue(), '3');
      assert.equal(await page.locator('[data-rack-format-error]').isVisible(), true);
      assert.match(await page.locator('[data-rack-format-error]').innerText(), language === 'en' ? /stock or active assignments/ : /saldo o asignaciones activas/);
      assert.equal(await page.locator('#rack-present-5').isChecked(), true);
      assert.equal(await page.locator('#rack-present-5').isDisabled(), true);
      assert.deepEqual(await visibleOrder(), [5,6,3,4,1,2]);
      await page.locator('[data-rack-columns-input]').selectOption('3');
      assert.equal(await page.locator('[data-rack-format-error]').isHidden(), true);

      await page.goto(`http://rack-format.test/review-${language}.html`);
      await page.evaluate(() => {
        const form = document.querySelector('[data-rack-editor]');
        form.addEventListener('submit', event => {
          event.preventDefault();
          form.dataset.submittedWith = event.submitter?.hasAttribute('data-rack-save') ? 'save' : 'other';
        });
      });
      await page.locator('[data-rack-pin]').fill('0000');
      await page.locator('[data-rack-pin]').press('Enter');
      assert.equal(await page.locator('[data-rack-editor]').getAttribute('data-submitted-with'), 'save');

      await page.setViewportSize({ width: 1440, height: 1000 });
      for (const [name, cells, numbers] of [
        ['racks', '.rack-bay-frame > .bay-slot', '.bay-pallet'],
        ['details', '.location-neighbor-grid > .location-neighbor-position', '.location-neighbor-header > strong'],
        ['admin-details', '.location-neighbor-grid > .location-neighbor-position', '.location-neighbor-header > strong'],
        ['print', '.rack-sheet-frame > .sheet-cell', '.sheet-pallet'],
        ['display', '.display-rack .display-cell', '.display-cell-code > span:first-child'],
        ['map', '.map-keypad:visible > .map-position', 'strong']
      ]) {
        await page.goto(`http://rack-format.test/view-${name}-${language}.html`);
        if (name === 'map') await page.locator('.map-element').first().click();
        if (name === 'print') await page.emulateMedia({ media: 'print' });
        const rendered = await page.locator(cells).evaluateAll((nodes, selector) => nodes.map(node => {
          const box = node.getBoundingClientRect();
          const text = node.querySelector(selector).textContent.trim();
          return { pallet: Number(text.split('-').at(-1)), x: box.x, y: box.y, width: box.width };
        }), numbers);
        assert.deepEqual(rendered.map(cell => cell.pallet), [3,4,1,2], name);
        assert.equal(rendered[0].y, rendered[1].y, name);
        assert.equal(rendered[2].y, rendered[3].y, name);
        assert.ok(rendered[2].y > rendered[0].y && rendered[1].x > rendered[0].x, name);
        await page.screenshot({ path: path.join(output, `rack-format-view-${name}-${language}.png`), fullPage: true });
        if (name === 'print') await page.emulateMedia({ media: 'screen' });
      }
    }
    assert.deepEqual(errors, []);
    process.stdout.write('Rack format browser checks passed: numbering, resize, themes, protected positions, roles and Enter (ES/EN).\n');
  } finally {
    await browser.close();
  }
})().catch(error => { process.stderr.write(error.stack + '\n'); process.exitCode = 1; });
