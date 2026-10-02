// Optional browser regression suite. Generate fixtures with WAREHOUSE_DISPLAY_FIXTURES and LocationDisplayTests first.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = process.env.WAREHOUSE_DISPLAY_FIXTURES || path.join(root, 'artifacts/ui/playwright/display-fixtures');
const output = path.join(root, 'artifacts/ui/playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, reducedMotion: 'reduce' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    let changed = false, fail = false, refreshes = 0;
    await page.route('http://display.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname === '/Locations/Display') {
        const racks = url.searchParams.get('racks') || '1';
        const orientation = url.searchParams.get('orientation') || 'auto';
        const suffix = orientation === 'auto' ? '' : `-${orientation}`;
        const refresh = url.searchParams.has('refresh');
        if (refresh) { refreshes++; if (fail) return route.fulfill({ status: 503 }); }
        const file = !url.searchParams.has('play') ? `config-${racks}-${orientation}.html`
          : refresh ? `refresh-${racks}-${url.searchParams.get('rows')}${suffix}.html` : `display-${racks}${suffix}.html`;
        let html = fs.readFileSync(path.join(fixtures, file), 'utf8');
        if (url.searchParams.has('withoutMap')) html = html.replace(/<fieldset[^>]*data-display-here-picker[^>]*>[\s\S]*?<\/fieldset>/, '');
        if (changed && refresh) html = html.replaceAll('1,250', '9,999');
        return route.fulfill({ contentType: 'text/html', body: html });
      }
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const visible = () => page.locator('[data-display-slide]:visible');
    const resize = async (width, height, compact) => {
      await page.setViewportSize({ width, height });
      if (compact !== undefined) await page.waitForFunction(expected => document.body.dataset.displayCompact === String(expected), compact);
    };
    const results = [];
    for (const racks of [1,2,3]) {
      changed = false; fail = false;
      await resize(1920,1080);
      await page.goto(`http://display.test/Locations/Display?play=true&rows=A&rows=B&racks=${racks}`);
      await page.locator('[data-display-pause]').click();
      for (const theme of ['light','dark']) {
        await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
        for (const width of [1920,1280,1024]) {
          await resize(width, width === 1920 ? 1080 : 768, racks > 1 ? false : undefined);
          assert.equal(await visible().locator('[data-display-rack]').count(), racks);
          const layout = await page.evaluate(() => {
            const levels = [...document.querySelectorAll('[data-display-slide]:not([hidden]) .display-rack')].map(r => [...r.querySelectorAll('.display-rack-level')].map(l => Math.round(l.getBoundingClientRect().top)));
            return { width: innerWidth, height: innerHeight, scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight, levels };
          });
          assert.ok(layout.scrollWidth <= width, `Horizontal overflow: ${JSON.stringify(layout)}`);
          if (racks > 1 && width === 1920) assert.ok(layout.scrollHeight <= layout.height, 'The grouped exhibition must fit a Full HD monitor');
          layout.levels.forEach(levels => assert.deepEqual(levels, layout.levels[0], 'Rack levels must align'));
          results.push({ racks, theme, ...layout });
          if (racks > 1 && width === 1920) await page.screenshot({ path: path.join(output, `grouped-${racks}-${theme}.png`), fullPage: true });
        }
      }
      if (racks === 1) continue;
      await resize(768,1024,true);
      assert.equal(await visible().locator('[data-display-rack]').count(),1);
      assert.equal(await page.locator('[data-display-slide]').count(),4);
      assert.equal(await visible().getAttribute('data-display-racks'),'A-3');
      await page.locator('[data-display-next]').click();
      assert.equal(await visible().getAttribute('data-display-racks'),'A-2');
      assert.match(await page.locator('[data-display-map-caption]').innerText(), /Rack A-2/);
      assert.equal(await page.locator('[data-map-rack].is-current').count(),1);
      await page.screenshot({ path: path.join(output, `grouped-${racks}-portrait.png`), fullPage: true });
      changed = true;
      await page.waitForFunction(() => document.querySelector('[data-display-slide]:not([hidden])').textContent.includes('9,999'));
      assert.equal(await visible().getAttribute('data-display-racks'),'A-2');
      assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'),'true');
      await resize(1920,1080,false);
      assert.equal(await visible().locator('[data-display-rack]').count(),racks);
      assert.ok((await visible().getAttribute('data-display-racks')).split(' ').includes('A-2'));
      assert.equal(await page.locator('[data-map-rack].is-current').count(),racks);
      assert.ok(await visible().innerText().then(text => text.includes('9,999')));
      assert.equal(new URL(page.url()).searchParams.get('racks'),String(racks));
      fail = true;
      await page.waitForFunction(() => document.querySelector('[data-display-status]').textContent.includes('No se pudo actualizar'));
      await resize(768,1024,true);
      assert.equal(await visible().locator('[data-display-rack]').count(),1);
      assert.equal(await visible().getAttribute('data-display-racks'),'A-2', 'Rotation must retain the focused rack');
      fail = false;
      await page.waitForFunction(() => document.querySelector('[data-display-status]').textContent === 'Pausado');
      await page.locator('[data-display-row-jump="B"]').click();
      assert.equal(await visible().getAttribute('data-display-racks'),'B-1');
      await resize(1280,768,false);
      assert.equal(await visible().getAttribute('data-display-racks'),'B-1');
    }

    // The selected composition applies even when its shape differs from the physical window.
    for (const orientation of ['portrait', 'auto', 'landscape']) {
      for (const racks of [1, 2, 3]) {
        fail = false; changed = false;
        for (const [width, height] of [[1920, 1080], [1080, 1920], [768, 1024]]) {
          const vertical = orientation === 'portrait' || width < 900 || orientation === 'auto' && height > width;
          await page.setViewportSize({ width, height });
          await page.goto(`http://display.test/Locations/Display?play=true&rows=A&rows=B&racks=${racks}&orientation=${orientation}`);
          await page.locator('[data-display-pause]').click();
          assert.equal(await page.locator('body').getAttribute('data-display-effective-orientation'), vertical ? 'portrait' : 'landscape');
          assert.equal(await visible().locator('[data-display-rack]').count(), vertical ? 1 : racks);
          for (const theme of ['light', 'dark']) {
            await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
            const layout = await page.evaluate(() => {
              const svg = document.querySelector('[data-display-map-svg]').getBoundingClientRect();
              const rack = document.querySelector('[data-display-slide]:not([hidden]) [data-display-rack]').getBoundingClientRect();
              const cells = [...document.querySelectorAll('[data-display-slide]:not([hidden]) .display-cell')];
              return { width: innerWidth, height: innerHeight, scrollWidth: document.documentElement.scrollWidth,
                scrollHeight: document.documentElement.scrollHeight, rackBottom: rack.bottom, mapTop: svg.top,
                mapHeight: svg.height, clipped: cells.some(cell => cell.scrollHeight > cell.clientHeight + 1 || cell.scrollWidth > cell.clientWidth + 1) };
            });
            if (layout.scrollWidth > width || layout.clipped || (width === 1920 || width === 1080 && vertical) && layout.scrollHeight > height)
              await page.screenshot({ path: path.join(output, 'orientation-overflow.png'), fullPage: true });
            assert.ok(layout.scrollWidth <= width, `Orientation width overflow: ${JSON.stringify({ orientation, racks, theme, ...layout })}`);
            assert.equal(layout.clipped, false, `Position content must remain visible: ${JSON.stringify({ orientation, racks, theme, ...layout })}`);
            assert.ok(layout.mapHeight > 0, 'The context map must remain visible');
            if (vertical) assert.ok(layout.mapTop >= layout.rackBottom, 'Vertical map must sit below the rack');
            // A forced landscape layout on a portrait monitor may need extra height; target displays must fit.
            if (width === 1920 || width === 1080 && vertical)
              assert.ok(layout.scrollHeight <= height, `Orientation height overflow: ${JSON.stringify({ orientation, racks, theme, ...layout })}`);
            results.push({ orientation, racks, theme, ...layout });
            if (racks === 3 && (width === 1920 || width === 1080 && vertical))
              await page.screenshot({ path: path.join(output, `orientation-${orientation}-${width}-${theme}.png`), fullPage: true });
          }
          await page.locator('[data-display-next]').click();
          if (await visible().getAttribute('data-display-row') !== 'A')
            await page.locator('[data-display-row-jump="A"]').click();
          const focused = await visible().getAttribute('data-display-racks');
          changed = true;
          await page.waitForFunction(() => document.querySelector('[data-display-slide]:not([hidden])').textContent.includes('9,999'));
          assert.equal(await visible().getAttribute('data-display-racks'), focused);
          assert.equal(await page.locator('[data-display-pause]').getAttribute('aria-pressed'), 'true');
          assert.equal(new URL(await page.locator('.display-actions a').getAttribute('href'), page.url()).searchParams.get('racks'), String(racks));
          changed = false;
        }
      }
    }

    // Configuration preserves the user's group size, including without the optional map picker.
    for (const withoutMap of [false, true]) {
      await page.setViewportSize({ width: 1920, height: 1080 });
      await page.goto(`http://display.test/Locations/Display?rows=A&racks=3&orientation=auto${withoutMap ? '&withoutMap=true' : ''}`);
      assert.equal(await page.locator('[data-display-here-map]').count(), withoutMap ? 0 : 1);
      await page.locator('input[name="orientation"][value="portrait"]').focus();
      await page.keyboard.press('Space');
      assert.equal(await page.locator('[data-display-preview]').getAttribute('data-preview-orientation'), 'portrait');
      assert.equal(await page.locator('input[type="radio"][name="racks"]:checked').inputValue(), '1');
      assert.equal(await page.locator('input[type="radio"][name="racks"]:disabled').count(), 3);
      let data = await page.locator('[data-display-config]').evaluate(form => Object.fromEntries(new FormData(form)));
      assert.equal(data.racks, '3');
      assert.equal(data.orientation, 'portrait');
      await page.locator('input[name="orientation"][value="landscape"]').check();
      assert.equal(await page.locator('input[type="radio"][name="racks"]:checked').inputValue(), '3');
      assert.equal(await page.locator('[data-display-preview]').getAttribute('data-preview-racks'), '3');
      data = await page.locator('[data-display-config]').evaluate(form => Object.fromEntries(new FormData(form)));
      assert.equal(data.racks, '3');
      await page.locator('input[type="radio"][name="racks"][value="2"]').check();
      await page.locator('input[name="orientation"][value="portrait"]').check();
      await page.locator('input[name="orientation"][value="auto"]').check();
      assert.equal(await page.locator('input[type="radio"][name="racks"]:checked').inputValue(), '2');
      await resize(768, 1024);
      await page.waitForFunction(() => document.querySelector('[data-display-preview]').dataset.previewRacks === '1');
      assert.equal(await page.locator('[data-display-preview]').getAttribute('data-preview-orientation'), 'portrait');
      assert.equal(await page.locator('[data-display-preview]').getAttribute('data-preview-racks'), '1');
      await resize(1920, 1080);
      await page.waitForFunction(() => document.querySelector('[data-display-preview]').dataset.previewRacks === '2');
      assert.equal(await page.locator('[data-display-preview]').getAttribute('data-preview-racks'), '2');
    }

    await page.goto('http://display.test/Locations/Display?rows=A&racks=3&orientation=portrait');
    await page.locator('input[name="orientation"][value="landscape"]').check();
    assert.equal(await page.locator('input[type="radio"][name="racks"]:checked').inputValue(), '3', 'Reopening configuration must retain the requested group size');
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({ passed:true, refreshes, results },null,2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
