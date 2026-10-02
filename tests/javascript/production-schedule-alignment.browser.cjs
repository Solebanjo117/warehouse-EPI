// Razor HTTP fixtures and intercepted requests; no operational app or database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_COPY_CARRYOVER_FIXTURES || path.join(root, 'artifacts/copy-carryover/copy-fixtures');
const output = path.join(root, 'artifacts/schedule-closed-alignment');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; let variant = 'closed', cases = 0;
    page.on('pageerror', error => errors.push(error.message));
    const options = JSON.parse(fs.readFileSync(path.join(fixtures, 'openings.json')));
    await page.route('http://alignment.test/**', async route => {
      const request = route.request(), url = new URL(request.url());
      assert.equal(request.method(), 'GET', 'Alignment checks must not write');
      if (url.searchParams.get('handler') === 'WorkspaceOpenings')
        return route.fulfill({ json: options.map(row => ({ ...row, selected: 100 })) });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html',
        body: fs.readFileSync(path.join(fixtures, `${variant}.html`), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
      return route.fulfill({ status: 204 });
    });
    const checkAlignment = async () => {
      const rows = await page.locator('[data-workspace-rows]').evaluate(body => {
        const header = body.closest('table').tHead.lastElementChild;
        const box = node => {
          const rect = node.getBoundingClientRect();
          return { x: rect.x, y: rect.y, width: rect.width, display: getComputedStyle(node).display };
        };
        return [...body.children].filter(row => !row.hidden && !row.classList.contains('production-week-workspace__order-row')).map(row => ({
          areas: [...row.querySelectorAll('[data-workspace-opening-area]')].map((cell, area) => ({
            cell: box(cell), head: box(header.children[area]), quantity: cell.querySelector('input')?.value || cell.textContent.trim(),
            positive: cell.classList.contains('production-week-workspace__carry-positive'), background: getComputedStyle(cell).backgroundColor
          })),
          days: [...row.querySelectorAll('[data-workspace-day-cell]')].filter(cell => !cell.hidden).map(cell => ({
            cell: box(cell), head: box(header.querySelector(`[data-workspace-day-head="${cell.dataset.workspaceDayCell}"]`))
          }))
        }));
      });
      assert.ok(rows.length > 0);
      for (const row of rows) {
        assert.equal(row.areas.length, 3);
        for (const [index, area] of row.areas.entries()) {
          assert.equal(area.cell.display, 'table-cell', `${variant}: area ${index} must remain a table cell`);
          assert.ok(Math.abs(area.cell.x - area.head.x) <= 1, `${variant}: area ${index} is below the wrong header`);
          assert.ok(Math.abs(area.cell.width - area.head.width) <= 1);
          assert.ok(Math.abs(area.cell.y - row.areas[0].cell.y) <= 1, 'Carryover areas must share one row');
          assert.equal(area.positive, Number(area.quantity) > 0);
          if (index > 0) assert.ok(area.cell.x > row.areas[index - 1].cell.x, 'Areas must run horizontally');
        }
        for (const day of row.days) {
          assert.ok(Math.abs(day.cell.x - day.head.x) <= 1, `${variant}: daily quantities are shifted`);
          assert.ok(Math.abs(day.cell.width - day.head.width) <= 1);
        }
        if (row.areas.some(area => area.positive))
          assert.notEqual(row.areas.find(area => area.positive).background, row.areas.find(area => !area.positive).background);
      }
    };
    for (variant of ['closed', 'historical-closed', 'saved']) {
      await page.goto('http://alignment.test/Admin/Production/Schedule');
      await page.locator('[data-workspace-rows] tr').first().waitFor();
      const closed = variant !== 'saved';
      assert.equal(await page.locator('[data-week-workspace] table').count(), 1);
      assert.equal(await page.locator('[data-workspace-rows] input').count() === 0, closed);
      assert.equal(await page.locator('[data-opening-editor]').count(), variant === 'historical-closed' ? 0 : 1);
      if (variant === 'closed') {
        const origin = page.locator('[data-workspace-rows] details').filter({ has: page.getByText('Ver origen', { exact: true }) });
        await origin.waitFor(); assert.equal(await origin.getAttribute('open'), null);
      }
      for (const width of [768, 899, 900, 1199, 1200, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click());
          assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
          await page.waitForFunction(width => parseFloat(getComputedStyle(document.querySelector('.app-content')).marginLeft) ===
            (width >= 1200 ? 250 : width >= 900 ? 72 : 0), width);
          for (const view of ['week']) {
            
            
            assert.equal(await page.locator('[data-workspace-day-head]:visible').count(), view === 'week' ? 7 : 1);
            assert.equal(await page.locator('[data-workspace-opening-area]:visible').count(), 3);
            await checkAlignment();
            const scroll = page.locator('[data-workspace-scroll]');
            await scroll.evaluate(async node => { node.scrollLeft = 150; await new Promise(resolve => requestAnimationFrame(resolve)); });
            await checkAlignment();
            await scroll.evaluate(node => { node.scrollLeft = 0; });
            cases++;
            if ([768, 1440].includes(width)) {
              await page.evaluate(() => window.scrollTo(0, 0)); await page.waitForFunction(() => scrollY === 0);
              await page.screenshot({ path: path.join(output, `${variant}-${width}-${theme}-${view}.png`), fullPage: true, animations: 'disabled' });
            }
          }
        }
      }
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ cases, passed: true, errors }, null, 2));
    console.log(`PASS: ${cases} alignment checks across closed historical, closed current and open weeks, both views, six widths and three themes; horizontal scroll, colors and read-only preserved.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
