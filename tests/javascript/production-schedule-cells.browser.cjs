// Current Razor HTTP fixtures and intercepted requests; no operational app or database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const evidence = process.env.WAREHOUSE_SCHEDULE_CELLS_OUTPUT || path.join(root, 'artifacts/schedule-cells');
const fixtures = process.env.WAREHOUSE_TABLET_UI_FIXTURES || path.join(evidence, 'tablet-fixtures');
const before = process.argv.includes('--before');
const quick = process.argv.includes('--quick');
const output = path.join(evidence, before ? 'before/browser' : 'browser');
fs.mkdirSync(output, { recursive: true });
const decode = value => value.replaceAll('&quot;', '"').replaceAll('&amp;', '&');
const encode = value => JSON.stringify(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const errors = [], geometry = []; let status = 'draft', language = 'es', variant = 'saved', cases = 0;
    page.on('pageerror', error => errors.push(error.message));
    await page.route('http://cells.test/**', async route => {
      const request = route.request(), url = new URL(request.url());
      assert.equal(request.method(), 'GET', 'Cell verification must not save');
      if (url.searchParams.get('handler') === 'WorkspaceOpenings') return route.fulfill({ json: [] });
      if (url.searchParams.get('handler') === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
      if (url.searchParams.get('handler') === 'WorkspaceProducts') return route.fulfill({ json: [{ id: '00000000-0000-0000-0000-000000000099', sku: 'NEW-SKU', unit: 'EA', allowsDecimals: true }] });
      if (url.pathname === '/Admin/Production/Schedule') {
        let body = fs.readFileSync(path.join(fixtures, `program-${status}-${language}.html`), 'utf8');
        // Presentation variants exercise a single editable row and unequal status text lengths.
        if (variant === 'single') { let found = false; body = body.replace(/<span data-line="[^"]+"><\/span>/g, span => { if (found) return ''; found = true; return span; }); }
        if (variant === 'mixed') body = body.replace(/data-progress="([^"]+)"/g, (_, value) => {
          const product = JSON.parse(decode(value));
          product.days[0].areas[1].applies = false;
          product.days[1].areas[0].coverage.ratio = null;
          product.days[2].planned = 1234567890.1234;
          product.days[2].areas[2].produced = 1234567890.1234;
          return `data-progress="${encode(product)}"`;
        });
        return route.fulfill({ contentType: 'text/html', body });
      }
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const relative = path.join('src/WarehouseEPI.Web/wwwroot', asset);
      const baseline = path.join(evidence, 'before', relative), current = path.join(root, relative);
      const file = before && fs.existsSync(baseline) ? baseline : current;
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const open = async (width = 1440, height = 1000) => {
      await page.setViewportSize({ width, height });
      await page.goto('http://cells.test/Admin/Production/Schedule');
      await page.locator('[data-schedule-indicators]').first().waitFor({ state: 'attached' });
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); });
    };
    const check = async view => {
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Page overflow');
      if (!before) {
        assert.equal(await page.locator('#week-workspace-quantity-label').count(), 1);
        assert.equal(await page.locator('[data-workspace-rows] .production-week-workspace__quantity-label').count(), 0);
        assert.ok(await page.locator('[data-workspace-rows] input.production-week-workspace__quantity').evaluateAll(nodes =>
          nodes.every(node => node.getAttribute('aria-label') && node.getAttribute('aria-describedby')?.includes('week-workspace-quantity-label'))));
      }
      const data = await page.locator('[data-workspace-rows] > tr').evaluateAll(rows => rows.filter(row => !row.hidden).map(row => {
        const top = row.getBoundingClientRect().top;
        const box = node => {
          if (!node) return null;
          const rect = node.getBoundingClientRect();
          return { y: rect.top - top, x: rect.left, right: rect.right, height: rect.height, width: rect.width, font: getComputedStyle(node).fontSize };
        };
        return { cells: [...row.querySelectorAll(':scope > [data-workspace-day-cell]')].filter(cell => !cell.hidden).map(cell => ({
          contentOffset: cell.firstElementChild.getBoundingClientRect().top - cell.getBoundingClientRect().top,
          topInset: parseFloat(getComputedStyle(cell).paddingTop) + parseFloat(getComputedStyle(cell).borderTopWidth),
          quantity: box(cell.querySelector(':scope > .production-week-workspace__quantity, :scope > .production-week-workspace__planned')),
          heading: box(cell.querySelector('.production-schedule-progress__heading')),
          areas: [...cell.querySelectorAll('.production-schedule-progress__area')].map(area => ({
            box: box(area), metric: box(area.firstElementChild), value: box(area.firstElementChild.lastElementChild),
            produced: box(area.querySelector('.production-schedule-progress__produced')),
            producedValue: box(area.querySelector('.production-schedule-progress__produced > :last-child')),
            text: area.firstElementChild.textContent
          }))
        })) };
      }));
      for (const row of data) {
        for (const cell of row.cells) {
          if (!before) assert.ok(cell.quantity.height >= 44, `Quantity block ${cell.quantity.height}px`);
          if (!before && view === 'week') {
            assert.ok(Math.abs(cell.contentOffset - cell.topInset) <= 1, 'Long SKU or notes vertically center the daily content');
            assert.ok(Math.abs(cell.quantity.y - row.cells[0].quantity.y) <= 1, 'Daily quantity positions differ');
            if (cell.heading) assert.ok(Math.abs(cell.heading.y - row.cells[0].heading.y) <= 1, 'Progress headings differ');
            cell.areas.forEach((area, index) => assert.ok(Math.abs(area.metric.y - row.cells[0].areas[index].metric.y) <= 1, `Area ${index} positions differ`));
          }
          if (!before) cell.areas.forEach(area => {
            assert.ok(area.box.height > area.metric.height, 'Secondary line must be reserved');
            if (area.producedValue) assert.ok(Math.abs(area.producedValue.right - area.value.right) <= 1, 'Daily production is not aligned with the percentage');
          });
        }
      }
      if (!before) assert.ok(await page.locator('.production-schedule-progress__metric > *, .production-schedule-progress__produced > :last-child').evaluateAll(nodes => nodes.filter(node => node.checkVisibility()).every(node => node.scrollWidth <= node.clientWidth + 1)), 'Indicator text is clipped');
      assert.ok(await page.locator('[data-week-workspace] input:not([type="hidden"]), [data-week-workspace] button, [data-week-workspace] summary').evaluateAll(nodes => nodes.filter(node => node.checkVisibility()).every(node => node.getBoundingClientRect().height >= 44)), 'Touch target below 44px');
      return data;
    };
    const widths = quick ? [360, 1440] : [360, 575, 576, 768, 899, 900, 1199, 1200, 1440];
    for (status of ['draft', 'open', 'closed']) for (language of ['es', 'en']) for (const width of widths) {
      await open(width);
      for (const view of ['week']) {
        
        
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click());
          assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
          geometry.push({ status, language, width, view, theme, rows: await check(view) }); cases++;
          if (language === 'es' && [360, 768, 1440].includes(width) && theme !== 'system') {
            await page.locator('[data-week-workspace]').scrollIntoViewIfNeeded();
            await page.screenshot({ path: path.join(output, `${status}-${width}-${view}-${theme}.png`) });
          }
        }
      }
    }
    if (!before) {
      for (variant of ['single', 'mixed']) for (status of ['draft', 'closed']) for (language of ['es', 'en']) {
        await open();  await check('week');
        if (variant === 'mixed') {
          assert.equal(await page.locator('[data-workspace-day-cell="0"] .production-schedule-progress__area').nth(1).locator('.production-schedule-progress__produced').count(), 0);
          assert.match(await page.locator('[data-workspace-day-cell="1"] .production-schedule-progress__metric').first().textContent(), language === 'es' ? /Sin meta nueva/ : /No new target/);
          const projected = JSON.parse(decode((await page.locator('[data-progress]').first().getAttribute('data-progress'))));
          const formatted = projected.days[2].areas[2].produced.toLocaleString(language, { maximumFractionDigits: 4 });
          assert.ok((await page.locator('[data-workspace-day-cell="2"] .production-schedule-progress__produced').last().textContent()).includes(formatted));
          await page.locator('[data-workspace-scroll]').screenshot({ path: path.join(output, `mixed-${status}-${language}.png`) });
          for (const width of [360, 575, 576, 640]) {
            await page.setViewportSize({ width, height: 1000 }); 
             await check('week');
          }
        }
      }
      variant = 'single'; status = 'draft'; language = 'es'; await open();
      
      const amounts = page.locator('input.production-week-workspace__quantity:visible');
      const first = amounts.first(); const initial = await first.inputValue();
      await first.fill('invalid'); assert.equal(await first.getAttribute('aria-invalid'), 'true');
      assert.ok((await first.locator('..').innerText()).includes('Cantidad no válida.'));
      await first.fill('12345678901234.1234'); await first.press('Enter');
      assert.equal(await first.inputValue(), '12345678901234.1234');
      assert.equal(await first.getAttribute('aria-invalid'), 'false');
      assert.equal(await amounts.nth(1).evaluate(node => node === document.activeElement), true);
      const details = page.locator('[data-schedule-disclosure^="row:"]').first();
      await details.locator('summary').click(); await details.locator('input').last().fill('Notes '.repeat(70));
      await first.fill(initial); await check('week');
      const breakdown = page.locator('.production-schedule-progress__breakdown').first();
      await breakdown.locator('summary').click();
      await page.locator('[data-workspace-search]').fill('NEW'); await page.locator('[data-workspace-results] button').first().click();
      assert.equal(await page.locator('[data-schedule-disclosure^="row:"]').first().evaluate(node => node.open), true);
      assert.equal(await page.locator('.production-schedule-progress__breakdown').first().evaluate(node => node.open), true);
      assert.equal(await page.locator('input.production-week-workspace__quantity').first().inputValue(), initial);
      await page.evaluate(() => localStorage.clear());
      status = 'closed'; variant = 'saved'; await open();
      const search = page.locator('[data-workspace-filter]');
      await search.fill('tablet-long'); await search.press('Enter');
      assert.equal(await page.locator('[data-workspace-rows] > tr:visible').count(), 1);
       assert.equal(await search.inputValue(), 'tablet-long');
      await search.fill('missing'); assert.equal(await page.locator('[data-workspace-rows] > tr:visible').count(), 0);
      await search.fill(''); assert.equal(await page.locator('[data-workspace-rows] > tr:visible').count(), 1);
      for (status of ['draft', 'open', 'closed']) for (language of ['es', 'en']) {
        // 640 CSS px emulates a 1280px desktop viewport at 200% zoom.
        await open(640, 450);
        for (const view of ['week']) for (const theme of ['light', 'dark', 'system']) {
          
          await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click()); await check(view);
        }
      }
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ cases, zoom200Emulated: !before, errors, geometry }, null, 2));
    console.log(`PASS: ${cases} cell-layout cases${before ? ' (baseline)' : ', mixed statuses, long values, keyboard/focus, disclosure recovery, closed search and 200% zoom emulated'}.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
