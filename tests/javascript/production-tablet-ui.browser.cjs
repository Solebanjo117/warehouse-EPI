// Fresh Razor HTTP fixtures and current assets; this suite never writes to the application.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_TABLET_UI_FIXTURES || path.join(root, 'artifacts/tablet-tanda1/fixtures');
const output = process.env.WAREHOUSE_TABLET_UI_OUTPUT || path.join(root, 'artifacts/tablet-tanda1/browser');
const widths = [360, 768, 899, 900, 991, 992, 1199, 1200, 1280];
const screens = ['program-draft', 'program-open', 'program-closed', 'products', 'history'];
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 1024 }, colorScheme: 'light', hasTouch: true });
  const page = await context.newPage();
  const errors = [], results = [];
  let screen = 'program-draft', language = 'es', posts = 0;
  page.on('pageerror', error => errors.push(error.message));
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (request.method() !== 'GET') { posts++; return route.fulfill({ status: 405 }); }
    if (url.hostname !== 'tablet.test') return route.abort();
    const handler = url.searchParams.get('handler');
    if (handler === 'WorkspaceOpenings') return route.fulfill({ json: [] });
    if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
    if (url.pathname === '/Admin/Production/Schedule' || url.pathname === '/Operations/Production')
      return route.fulfill({ path: path.join(fixtures, `${screen}-${language}.html`), contentType: 'text/html' });
    const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
    const file = path.resolve(root, 'src/WarehouseEPI.Web/wwwroot', `.${asset}`);
    if (file.startsWith(path.join(root, 'src/WarehouseEPI.Web/wwwroot') + path.sep)
        && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
    return route.fulfill({ status: 204 });
  });
  const frames = () => page.evaluate(async () => {
    await new Promise(resolve => requestAnimationFrame(resolve));
    await new Promise(resolve => requestAnimationFrame(resolve));
  });
  const open = async (name, lang, width) => {
    screen = name; language = lang;
    await page.setViewportSize({ width, height: 1024 });
    await page.goto(`http://tablet.test/${name === 'history' ? 'Operations/Production' : 'Admin/Production/Schedule'}`);
    await page.locator('.production-daily').waitFor();
    await frames();
  };
  const checkOverflow = async label => {
    const dimensions = await page.evaluate(() => ({ width: document.documentElement.clientWidth,
      scroll: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) }));
    assert.ok(dimensions.scroll <= dimensions.width + 1, `${label}: page overflow ${JSON.stringify(dimensions)}`);
  };
  const checkTable = async width => {
    const table = page.locator('.production-schedule-table');
    await table.evaluate(el => {
      for (let parent = el.parentElement; parent; parent = parent.parentElement)
        if (parent.tagName === 'DETAILS') parent.open = true;
      el.querySelectorAll('details').forEach(details => { details.open = true; });
    });
    const labels = screen === 'products'
      ? (language === 'es' ? ['SKU', 'Día', 'Cantidad', 'Destino', 'Detalles']
        : ['SKU', 'Day', 'Quantity', 'Destination', 'Details'])
      : (language === 'es' ? ['#', 'SKU', 'Cantidad', 'Destino', 'Tipo original', 'Pedidos y anotaciones']
        : ['#', 'SKU', 'Quantity', 'Destination', 'Original type', 'Orders and annotations']);
    const rows = await table.locator('tbody tr').evaluateAll(nodes => nodes.map(row => ({
      display: getComputedStyle(row).display,
      cells: [...row.cells].map(cell => ({ label: cell.dataset.label,
        content: getComputedStyle(cell, '::before').content, span: getComputedStyle(cell).gridColumn,
        wide: cell.matches('.production-schedule-cell--sku, .production-schedule-cell--details, .production-schedule-cell--actions') }))
    })));
    assert.equal(rows.length, 2);
    for (const row of rows) {
      assert.deepEqual(row.cells.slice(0, -1).map(cell => cell.label), labels);
      assert.equal(row.display, width < 992 ? 'grid' : 'table-row');
      if (width < 992) for (const cell of row.cells) {
        if (cell.label !== undefined) assert.equal(cell.content, JSON.stringify(cell.label));
        assert.equal(cell.span, cell.wide ? '1 / -1' : 'auto');
      }
    }
  };
  const checkHeader = async width => {
    const header = page.locator('.production-schedule__context');
    await page.evaluate(() => {
      document.activeElement?.blur();
      const header = document.querySelector('.production-schedule__context');
      window.scrollTo({ top: header.getBoundingClientRect().top + window.scrollY + 60, behavior: 'instant' });
    });
    await frames();
    const geometry = await header.evaluate(el => ({ top: el.getBoundingClientRect().top,
      offset: getComputedStyle(el).top, position: getComputedStyle(el).position,
      barBottom: document.querySelector('.app-topbar').getBoundingClientRect().bottom }));
    assert.equal(geometry.position, 'sticky');
    assert.equal(geometry.offset, width < 900 ? '64px' : '0px');
    assert.ok(geometry.top >= (width < 900 ? geometry.barBottom : 0) - 1,
      `Header hidden at ${width}: ${JSON.stringify(geometry)}`);
    assert.ok(Math.abs(geometry.top - (width < 900 ? geometry.barBottom : 0)) <= 1,
      `Header did not reach its sticky position at ${width}: ${JSON.stringify(geometry)}`);
    const field = page.locator(screen === 'program-closed' ? '[data-week-workspace] [data-workspace-filter]'
      : '[data-week-workspace] input:not([type="hidden"]):not([disabled])').first();
    await field.focus();
    assert.equal(await header.evaluate(el => getComputedStyle(el).position), width <= 900 ? 'static' : 'sticky');
    await field.evaluate(el => el.blur());
    assert.equal(await header.evaluate(el => getComputedStyle(el).position), 'sticky');
  };
  const checkHistory = async () => {
    const forms = page.locator('form:has(input[name="Reverse.Pin"])');
    assert.equal(await forms.count(), 2);
    await forms.evaluateAll(nodes => nodes.forEach(form => { form.closest('details').open = true; }));
    for (const form of await forms.all()) {
      const reason = form.locator('[name="Reverse.Reason"]'), pin = form.locator('[name="Reverse.Pin"]');
      const reasonId = await reason.getAttribute('id'), pinId = await pin.getAttribute('id');
      await form.locator(`label[for="${reasonId}"]`).tap();
      assert.equal(await reason.evaluate(el => el === document.activeElement), true);
      await reason.fill(language === 'es' ? 'Motivo de prueba' : 'Test reason');
      assert.equal(await form.locator(`label[for="${reasonId}"]`).isVisible(), true);
      await reason.press('Tab');
      assert.equal(await pin.evaluate(el => el === document.activeElement), true);
      await form.locator(`label[for="${pinId}"]`).tap();
      assert.equal(await pin.evaluate(el => el === document.activeElement), true);
      assert.equal(await pin.inputValue(), '');
      await pin.press('Tab');
      assert.equal(await form.locator('button').evaluate(el => el === document.activeElement), true);
      for (const control of await form.locator('input:not([type="hidden"]), button').all())
        assert.ok((await control.boundingBox()).height >= 44, 'Reverse control below 44px');
      await reason.fill('');
    }
    await page.evaluate(() => document.activeElement?.blur());
  };
  try {
    for (const theme of ['light', 'dark', 'system']) {
      await open('program-draft', 'es', 1280);
      await page.locator('.app-account-menu > summary').click();
      await page.locator(`[data-theme-choice="${theme}"]`).click();
      await page.locator('.app-account-menu > summary').click();
      if (theme === 'system') {
        await page.emulateMedia({ colorScheme: 'dark' });
        await page.waitForFunction(() => document.documentElement.dataset.bsTheme === 'dark');
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), 'dark');
        await page.emulateMedia({ colorScheme: 'light' });
        await page.waitForFunction(() => document.documentElement.dataset.bsTheme === 'light');
      }
      for (const lang of ['es', 'en']) for (const width of widths) for (const name of screens) {
        const label = `${name}/${lang}/${theme}/${width}`;
        await open(name, lang, width);
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'dark' ? 'dark' : 'light', label);
        assert.equal(await page.locator(`[data-theme-choice="${theme}"]`).getAttribute('aria-pressed'), 'true');
        if (name.startsWith('program-')) await checkHeader(width);
        if (name === 'history') await checkHistory(); else await checkTable(width);
        await checkOverflow(label);
        if ((width === 768 || width === 1280) && (theme === 'light' || theme === 'dark') && lang === 'es') {
          if (name.startsWith('program-')) {
            await page.evaluate(() => { const header = document.querySelector('.production-schedule__context');
              window.scrollTo({ top: header.getBoundingClientRect().top + window.scrollY + 60, behavior: 'instant' }); });
          } else {
            await page.locator(name === 'history' ? '.production-daily table' : '.production-schedule-table').scrollIntoViewIfNeeded();
          }
          await page.screenshot({ path: path.join(output, `${name}-${lang}-${theme}-${width}.png`) });
        }
        results.push({ screen: name, language: lang, theme, width, passed: true });
      }
      console.log(`Passed ${theme}: 90 viewport checks`);
    }
    // Emulate 200% desktop browser zoom: half the CSS viewport and double pixel density.
    const zoomContext = await browser.newContext({ viewport: { width: 640, height: 512 }, deviceScaleFactor: 2 });
    const zoomPage = await zoomContext.newPage();
    // Reuse the isolated request routing, without cookies or saved authentication state.
    await zoomContext.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.hostname !== 'tablet.test') return route.abort();
      if (url.pathname === '/Admin/Production/Schedule' || url.pathname === '/Operations/Production')
        return route.fulfill({ path: path.join(fixtures, `${screen}-${language}.html`), contentType: 'text/html' });
      if (url.searchParams.get('handler') === 'WorkspaceOpenings') return route.fulfill({ json: [] });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.resolve(root, 'src/WarehouseEPI.Web/wwwroot', `.${asset}`);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    for (const lang of ['es', 'en']) for (const name of screens) {
      screen = name; language = lang;
      await zoomPage.goto(`http://tablet.test/${name === 'history' ? 'Operations/Production' : 'Admin/Production/Schedule'}`);
      await zoomPage.locator('.production-daily').waitFor();
      await zoomPage.locator('details').evaluateAll(nodes => nodes.forEach(el => { el.open = true; }));
      assert.ok(await zoomPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${name}/${lang}: zoom overflow`);
      assert.equal(await zoomPage.locator(name === 'history' ? 'label[for^="reverse-"]' : '.production-schedule-table td[data-label]').first().isVisible(), true);
      if (lang === 'es') await zoomPage.screenshot({ path: path.join(output, `${name}-zoom200-emulated.png`) });
    }
    await zoomContext.close();
    assert.equal(posts, 0, 'Unexpected application write');
    assert.deepEqual(errors, [], 'Browser JavaScript errors');
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: results.length,
      zoom200EmulatedChecks: 10, posts, javascriptErrors: errors, results }, null, 2));
    console.log(`Passed ${results.length} viewport checks and 10 emulated zoom checks; ${posts} writes; no JavaScript errors.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
