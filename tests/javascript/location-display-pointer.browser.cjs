// Optional browser suite. Uses existing LocationDisplayTests fixtures and current static assets.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = process.env.WAREHOUSE_DISPLAY_FIXTURES || path.join(root, 'artifacts/ui/playwright/display-fixtures');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const results = [];
    for (const viewport of [{ width: 1280, height: 800 }, { width: 768, height: 1024 }]) {
      const context = await browser.newContext({ viewport, hasTouch: true });
      await context.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.hostname !== 'display.test') return route.abort();
        if (url.pathname === '/Locations/Display') {
          const name = url.searchParams.has('refresh') ? `refresh-1-${url.searchParams.get('rows')}.html` : 'display-1.html';
          return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, name), 'utf8') });
        }
        const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
        const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
        return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
      });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      const cdp = await context.newCDPSession(page);
      const holder = page.locator('[data-display-slides]');
      const currentRack = () => page.locator('[data-display-slide]:visible').getAttribute('data-display-racks');
      const settled = () => page.waitForFunction(() => !document.querySelector('[data-display-slides]').classList.contains('display-is-settling'));
      const origin = async () => {
        await holder.scrollIntoViewIfNeeded();
        const box = await holder.boundingBox();
        return { box, x: box.x + box.width * .6, y: Math.min(box.y + 100, viewport.height - 180) };
      };
      const mouseDrag = async (dx, dy = 0, outside = false) => {
        const point = await origin();
        await page.mouse.move(point.x, point.y);
        await page.mouse.down();
        assert.equal(await holder.evaluate(element => getComputedStyle(element).userSelect), 'none');
        await page.mouse.move(outside ? point.box.x - 20 : point.x + dx, point.y + dy, { steps: 8 });
        await page.mouse.up();
        await settled();
        assert.equal(await holder.evaluate(element => element.classList.contains('display-pointer-active')), false);
      };
      const touchDrag = async (dx, dy = 0, cancel = false) => {
        const point = await origin();
        const touch = { x: point.x, y: point.y, radiusX: 1, radiusY: 1, force: 1, id: 1 };
        await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [touch] });
        for (let step = 1; step <= 8; step++) {
          await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ ...touch, x: touch.x + dx * step / 8, y: touch.y + dy * step / 8 }] });
        }
        await cdp.send('Input.dispatchTouchEvent', { type: cancel ? 'touchCancel' : 'touchEnd', touchPoints: [] });
        await settled();
      };
      for (const theme of ['light', 'dark']) {
        await page.goto('http://display.test/Locations/Display?play=true&rows=A&rows=B&racks=1');
        await page.locator('[data-display-pause]').click();
        await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
        const first = await currentRack();
        const second = await page.locator('[data-display-slide]').nth(1).getAttribute('data-display-racks');
        await mouseDrag(-220);
        assert.equal(await currentRack(), second, 'Mouse drag advances exactly one rack');
        await mouseDrag(220);
        assert.equal(await currentRack(), first, 'Mouse drag goes back');
        await mouseDrag(-30);
        assert.equal(await currentRack(), first, 'Short mouse drag stays on the same rack');
        await mouseDrag(0, 100);
        assert.equal(await currentRack(), first, 'Vertical mouse drag does not navigate');
        await mouseDrag(0, 0, true);
        assert.equal(await currentRack(), second, 'Captured mouse release outside the rack finishes navigation');
        await page.locator('[data-display-prev]').click();
        assert.equal(await currentRack(), first);
        const point = await origin();
        await page.mouse.move(point.x, point.y);
        await page.mouse.down({ button: 'right' });
        await page.mouse.move(point.x - 220, point.y, { steps: 8 });
        await page.mouse.up({ button: 'right' });
        assert.equal(await currentRack(), first, 'Right mouse button does not navigate');
        await touchDrag(-220);
        assert.equal(await currentRack(), second, 'Native touch advances exactly one rack without duplicate mouse handling');
        await touchDrag(220);
        assert.equal(await currentRack(), first, 'Native touch goes back');
        await touchDrag(-220, 0, true);
        assert.equal(await currentRack(), first, 'Cancelled touch restores the rack');
        await touchDrag(0, 100);
        assert.equal(await currentRack(), first, 'Vertical touch does not navigate');
        assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'), 'true');
        assert.deepEqual(errors, []);
        results.push({ ...viewport, theme, mouse: 'pass', releaseOutside: 'pass', touch: 'pass', cancellation: 'pass' });
      }
      await context.close();
    }
    console.log(JSON.stringify({ browser: browser.version(), fixture: 'Local HTML with current assets; emulated touch', results }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error.stack); process.exitCode = 1; });
