// Only Razor HTTP fixtures and intercepted writes; never connects to the operational app.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_COPY_CARRYOVER_FIXTURES || path.join(root, 'artifacts/copy-carryover/copy-fixtures');
const output = process.env.WAREHOUSE_COPY_CARRYOVER_OUTPUT || path.join(root, 'artifacts/single-planning-table/browser');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; let saved = false, closed = false, writes = 0, reviews = 0;
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    const source = JSON.parse(fs.readFileSync(path.join(fixtures, 'source.json')));
    const openings = JSON.parse(fs.readFileSync(path.join(fixtures, 'openings.json')));
    await page.route('http://carry.test/**', async route => {
      const request = route.request(); const url = new URL(request.url()); const handler = url.searchParams.get('handler');
      if (request.method() === 'POST') {
        if (handler === 'WorkspaceReview') {
          const body = request.postDataJSON(); reviews++;
          assert.equal(body.openings.length, 2); assert.equal(body.changes.length, 1);
          assert.equal(body.reason, 'Pendientes de la semana elegida');
          assert.equal(JSON.stringify(body).includes('0123'), false);
          return route.fulfill({ json: { canConfirm: true, fingerprint: 'reviewed-copy', errors: [] } });
        }
        assert.equal(handler, 'WorkspaceSave'); writes++;
        const body = request.postData(); assert.ok(body.includes('reviewed-copy')); assert.ok(body.includes('Pendientes de la semana elegida'));
        saved = true; return route.fulfill({ json: { saved: true, count: 3 } });
      }
      if (handler === 'WorkspaceOpenings') return route.fulfill({ json: openings.map(row => ({ ...row, selected: saved ? 100 : 0 })) });
      if (handler === 'WorkspaceCopy') return route.fulfill({ json: source });
      if (handler === 'WorkspaceResolveProducts') return route.fulfill({ json: source.rows.map(row => ({ id: row.productId, sku: row.sku, unit: row.unit, allowsDecimals: row.allowsDecimals })) });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html',
        body: fs.readFileSync(path.join(fixtures, closed ? 'closed.html' : saved ? 'saved.html' : 'before.html'), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
      return route.fulfill({ status: 204 });
    });
    await page.goto('http://carry.test/Admin/Production/Schedule');
    await page.locator('[data-workspace-copy-toggle]').click();
    assert.equal(await page.locator('[data-opening-editor]').count(), 1);
    assert.equal(await page.locator('[data-workspace-copy-openings]').isEnabled(), true);
    assert.equal(await page.locator('[data-workspace-copy-openings]').isChecked(), false);
    // Preparing the same source again applies the current quantity option to untouched cells.
    const prepareCopy = async () => {
      await page.locator('[data-workspace-copy]').click();
      await page.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    };
    await page.locator('[data-workspace-copy-openings]').check();
    await prepareCopy();
    const copiedMonday = page.getByRole('textbox', { name: 'FG-100, Lunes, EA', exact: true });
    assert.equal(await copiedMonday.inputValue(), '');
    await page.locator('[data-workspace-copy-quantities]').check();
    await prepareCopy();
    assert.equal(await copiedMonday.inputValue(), '100');
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
    assert.equal((await page.locator('[data-workspace-count]').textContent()).trim(), '3 cambios');
    assert.ok((await page.locator('[data-workspace-day-summary]').textContent()).includes('Lunes: 100 EA'));
    await page.screenshot({ path: path.join(output, 'refreshed-copy.png'), fullPage: true, animations: 'disabled' });
    await page.locator('[data-workspace-copy-quantities]').uncheck();
    await prepareCopy();
    assert.equal(await copiedMonday.inputValue(), '');
    await copiedMonday.fill('45');
    await page.locator('[data-workspace-copy-quantities]').check();
    await prepareCopy();
    assert.equal(await copiedMonday.inputValue(), '45');
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
    assert.equal(writes, 0);
    await Promise.all([page.waitForEvent('load'), page.locator('[data-workspace-clear-all]').click()]);
    await page.locator('[data-workspace-copy-toggle]').click();
    await page.locator('[data-workspace-copy-products]').uncheck();
    await page.locator('[data-workspace-copy-openings]').check();
    await page.locator('[data-workspace-copy]').click();
    const table = page.locator('.production-week-workspace__table');
    const sew = page.getByLabel('FG-100 · arrastre de Costura', { exact: true });
    const rtp = page.getByLabel('FG-100 · arrastre de Ready to Pack', { exact: true });
    await sew.waitFor(); assert.equal(await sew.inputValue(), '100'); assert.equal(await rtp.inputValue(), '100');
    assert.equal(await page.locator('[data-week-workspace] table').count(), 1);
    assert.equal(await page.locator('[data-workspace-empty]').isVisible(), false);
    assert.equal(await page.locator('[data-workspace-paste-preview]').count(), 0);
    const heads = await table.locator('thead tr').last().locator('th').allTextContents();
    assert.deepEqual(heads.slice(0, 4), ['Corte', 'Costura', 'Ready to Pack', 'Lunes']);
    assert.equal(await page.getByRole('navigation', { name: 'Días de la semana', exact: true }).count(), 0);
    await sew.fill('60');
    const origins = table.locator('details').filter({ has: page.getByText('Ver origen', { exact: true }) });
    assert.equal(await origins.getAttribute('open'), null);
    await origins.locator('summary').focus(); await page.keyboard.press('Enter');
    assert.notEqual(await origins.getAttribute('open'), null); await page.keyboard.press('Enter');
    await sew.fill('0'); await rtp.fill('0'); await page.reload();
    await page.getByRole('button', { name: 'Recuperar', exact: true }).click();
    await sew.waitFor(); assert.equal(await sew.inputValue(), '0'); assert.equal(await rtp.inputValue(), '0');
    await page.locator('[data-workspace-copy]').click(); assert.equal(await sew.inputValue(), '0');
    await sew.fill('60'); await rtp.fill('100');
    for (const width of [768, 899, 900, 1199, 1200, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      for (const theme of ['light', 'dark', 'system']) {
        await page.emulateMedia({ colorScheme: theme === 'system' ? 'dark' : 'light' });
        await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
        await page.waitForFunction(width => parseFloat(getComputedStyle(document.querySelector('.app-content')).marginLeft) ===
          (width >= 1200 ? 250 : width >= 900 ? 72 : 0), width);
        const areaCells = table.locator('[data-workspace-opening-area]');
        assert.equal(await areaCells.count(), 3);
        const metrics = await areaCells.evaluateAll(cells => cells.map(cell => ({
          positive: cell.classList.contains('production-week-workspace__carry-positive'),
          background: getComputedStyle(cell).backgroundColor,
          input: cell.querySelector('input')?.getBoundingClientRect().height || 44
        })));
        assert.deepEqual(metrics.map(cell => cell.positive), [false, true, true]);
        assert.ok(metrics.every(cell => cell.input >= 44)); assert.notEqual(metrics[0].background, metrics[1].background);
        
        assert.equal(await table.locator('[data-workspace-opening-area]:visible').count(), 3);
        assert.equal(await table.locator('[data-workspace-day-head]:visible').count(), 1);
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.waitForFunction(() => scrollY === 0);
        await page.screenshot({ path: path.join(output, `copy-${width}-${theme}.png`), fullPage: true, animations: 'disabled' });
      }
    }
    // Repeated orders remain in one SKU row, inside its last-cell disclosure.
    await page.evaluate(() => {
      const root = document.querySelector('[data-week-workspace]');
      const key = `warehouse-epi:schedule:${root.dataset.userId}:${root.dataset.weekId}`;
      const prior = JSON.parse(localStorage.getItem(key));
      prior.preview = ['ONE', 'TWO'].map((order, index) => ({ sku: 'FG-100', selected: true,
        days: [String(index ? 7 : 5), '', '', '', '', '', ''], details: { orderReference1: order } }));
      localStorage.setItem(key, JSON.stringify(prior));
    });
    await page.reload();
    await page.getByRole('button', { name: 'Recuperar', exact: true }).click();
    await table.locator('[data-schedule-orders] > summary').click();
    assert.equal(await page.locator('[data-week-workspace] table').count(), 1);
    assert.equal(await table.locator('table').count(), 0);
    assert.equal(await table.locator('.production-week-workspace__order-row:visible').count(), 2);
    assert.equal(await table.locator('tbody > tr').count(), 1);
    assert.equal(await table.locator('.production-week-workspace__carry-positive').count(), 2);
    
    const firstOrder = table.locator('.production-week-workspace__order-row').first();
    const firstMonday = firstOrder.locator('[data-workspace-order-day="0"] input');
    await firstMonday.focus(); await page.keyboard.press('Enter');
    assert.equal(await firstOrder.locator('[data-workspace-order-day="1"] input').evaluate(input => input === document.activeElement), true);
    await table.locator('[data-schedule-orders] > summary').click();
    await sew.focus(); await page.keyboard.press('Tab');
    assert.equal(await table.locator('.production-week-workspace__order-row input').evaluateAll(inputs => inputs.some(input => input === document.activeElement)), false);
    await Promise.all([page.waitForEvent('load'), page.locator('[data-workspace-clear-all]').click()]);
    if (!await page.locator('[data-workspace-copy-panel]').isVisible()) await page.locator('[data-workspace-copy-toggle]').click();
    await page.locator('[data-workspace-copy-products]').check();
    await page.locator('[data-workspace-copy-openings]').check();
    await page.locator('[data-workspace-copy-quantities]').check();
    await page.locator('[data-workspace-copy]').click();
    const monday = page.getByRole('textbox', { name: 'FG-100, Lunes, EA', exact: true });
    await monday.fill('50'); await sew.fill('100');
    await page.getByRole('checkbox', { name: 'Incluir producto FG-100', exact: true }).uncheck();
    assert.equal((await page.locator('[data-workspace-total]').textContent()).trim(), '0 EA');
    await page.getByRole('checkbox', { name: 'Incluir producto FG-100', exact: true }).check();
    assert.equal(await monday.inputValue(), '50');
    await page.locator('[data-workspace-copy]').click(); assert.equal(await monday.inputValue(), '50');
    assert.equal(await sew.inputValue(), '100');
    await page.locator('[data-workspace-copy-openings]').uncheck();
    assert.equal(await sew.inputValue(), '0');
    await page.locator('[data-workspace-copy-openings]').check(); assert.equal(await sew.inputValue(), '100');
    for (const width of [768, 1200, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      await page.waitForFunction(width => parseFloat(getComputedStyle(document.querySelector('.app-content')).marginLeft) ===
        (width >= 1200 ? 250 : width >= 900 ? 72 : 0), width);
      
      await page.evaluate(() => window.scrollTo(0, 0)); await page.waitForFunction(() => scrollY === 0);
      await page.screenshot({ path: path.join(output, `combined-${width}.png`), fullPage: true, animations: 'disabled' });
    }
    await page.locator('[data-workspace-review]').click();
    await page.waitForFunction(() => !document.querySelector('[data-workspace-review-panel]').hidden || document.querySelector('[data-workspace-message]').textContent.includes('cambió'));
    assert.equal(await page.locator('[data-workspace-save]').isDisabled(), true);
    await page.locator('[data-workspace-reason]').fill('Pendientes de la semana elegida');
    await page.locator('[data-workspace-recheck]').click();
    await page.locator('[data-workspace-pin]').waitFor({ state: 'visible' });
    await page.locator('[data-workspace-pin]').fill('0123');
    assert.equal(await page.evaluate(() => Object.values(localStorage).join('').includes('0123')), false);
    await page.locator('[data-workspace-save]').click();
    await page.waitForFunction(() => document.querySelector('[data-week-workspace]')?.dataset.weekVersion === '4');
    await page.waitForFunction(() => document.querySelector('[data-workspace-day-cell] input')?.value === '50');
    assert.equal(await sew.inputValue(), '100'); assert.equal(await rtp.inputValue(), '100');
    assert.equal(await page.locator('.production-week-workspace__opening-summary').count(), 0);
    assert.equal(await page.locator('[data-week-workspace] table').count(), 1);
    assert.equal(writes, 1); assert.equal(reviews, 1); assert.deepEqual(errors, []);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.locator('[data-theme-choice="light"]').evaluate(el => el.click());
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.waitForFunction(() => scrollY === 0);
    await page.screenshot({ path: path.join(output, 'saved-desktop.png'), fullPage: true, animations: 'disabled' });
    closed = true; await page.reload();
    const closedOrigin = page.locator('[data-workspace-rows] details').filter({ has: page.getByText('Ver origen', { exact: true }) });
    await closedOrigin.waitFor(); assert.equal(await closedOrigin.getAttribute('open'), null);
    assert.equal(await page.locator('[data-week-workspace] table').count(), 1);
    assert.equal(await page.locator('[data-workspace-rows] input').count(), 0);
    assert.equal(await page.locator('.production-week-workspace__carry-positive').count(), 2);
    assert.deepEqual(errors, []);
    console.log('PASS: one SKU row, three carryover columns, copy and legacy recovery, last-cell orders, 18 viewport/theme checks, repeated copy, exclusion, one atomic reviewed save.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
