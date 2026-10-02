// Isolated Edge, current Razor HTTP fixtures and intercepted writes only.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_COPY_CARRYOVER_FIXTURES || path.join(root, 'artifacts/schedule-sku-row/copy-fixtures');
const output = process.env.WAREHOUSE_SKU_ROW_OUTPUT || path.join(root, 'artifacts/schedule-sku-row/browser');
const before = process.argv.includes('--before');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = [], reviews = [], saves = [];
    let selected = 0;
    let language = 'es';
    let screen = 'copy';
    const source = JSON.parse(fs.readFileSync(path.join(fixtures, 'source.json')));
    const originalRows = structuredClone(source.rows);
    const openings = JSON.parse(fs.readFileSync(path.join(fixtures, 'openings.json')));
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await page.route('http://sku.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), handler = url.searchParams.get('handler');
      if (request.method() === 'POST') {
        if (handler === 'WorkspaceReview') {
          reviews.push(request.postDataJSON());
          return route.fulfill({ json: { canConfirm: true, fingerprint: 'sku-reviewed', errors: [] } });
        }
        assert.equal(handler, 'WorkspaceSave');
        const payload = JSON.parse(/name="payload"\r?\n\r?\n([^\r\n]+)/.exec(request.postData())[1]);
        saves.push(payload); selected = 100;
        return route.fulfill({ json: { saved: true, count: payload.changes.length + payload.openings.length } });
      }
      if (handler === 'WorkspaceOpenings') return route.fulfill({ json: screen === 'copy' ? openings.map(row => ({ ...row, selected })) : [] });
      if (handler === 'WorkspaceCopy') return route.fulfill({ json: source });
      if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(screen === 'copy'
        ? path.join(fixtures, language === 'en' ? 'before-en.html' : 'before.html')
        : path.join(process.env.WAREHOUSE_TABLET_UI_FIXTURES || path.join(root, 'artifacts/schedule-sku-row/tablet-fixtures'), `program-${screen}-${language}.html`), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const baseline = path.join(root, 'artifacts/schedule-sku-row/before', path.basename(asset));
      const file = before && fs.existsSync(baseline) ? baseline : path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const main = day => page.locator(`[data-workspace-rows] > tr > [data-workspace-day-cell="${day}"] > input.production-week-workspace__quantity`);
    const order = (index, day) => page.locator('[data-schedule-orders] [data-workspace-row]').nth(index).locator(`[data-workspace-order-day="${day}"] input.production-week-workspace__quantity`);
    const stored = () => page.evaluate(() => {
      const root = document.querySelector('[data-week-workspace]');
      return JSON.parse(localStorage.getItem(`warehouse-epi:schedule:${root.dataset.userId}:${root.dataset.weekId}`));
    });
    const reset = async () => { await page.evaluate(() => localStorage.clear()); selected = 0; await page.reload(); };
    const prepare = async (products = false, carry = true, quantities = false) => {
      const panel = page.locator('[data-workspace-copy-panel]');
      if (!await panel.isVisible()) await page.locator('[data-workspace-copy-toggle]').click();
      await page.locator('[data-workspace-copy-products]').setChecked(products);
      await page.locator('[data-workspace-copy-openings]').setChecked(carry);
      await page.locator('[data-workspace-copy-quantities]').setChecked(quantities);
      await page.locator('[data-workspace-copy]').click();
      await page.locator('[data-workspace-rows] > tr').first().waitFor();
      await page.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    };
    const review = async () => {
      await page.locator('[data-workspace-review]').click();
      await page.locator('[data-workspace-review-panel]').waitFor({ state: 'visible' });
      if (await page.locator('[data-workspace-reason-panel]').isVisible()) {
        await page.locator('[data-workspace-reason]').fill('Continuar pendientes');
        await page.locator('[data-workspace-recheck]').click();
      }
      await page.waitForFunction(() => !!document.querySelector('[data-workspace-payload]').value);
      return JSON.parse(await page.locator('[data-workspace-payload]').inputValue());
    };
    await page.goto('http://sku.test/Admin/Production/Schedule');
    await prepare();
    await page.evaluate(() => document.fonts.ready);
    await page.mouse.move(1400, 5);
    await page.screenshot({ path: path.join(output, before ? 'carry-before.png' : 'carry-after.png'), fullPage: true });
    if (before) return;
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
    assert.equal(await page.locator('table table').count(), 0);
    assert.deepEqual(await page.locator('[data-workspace-day-cell] > input').evaluateAll(inputs => inputs.map(input => [input.value, input.disabled])), Array.from({ length: 7 }, () => ['', false]));
    assert.equal((await stored()).rows.length, 0);
    const carryPayload = await review();
    assert.equal(carryPayload.changes.length, 0); assert.equal(carryPayload.openings.length, 2);
    assert.equal(JSON.stringify(carryPayload).includes('skuDayTargets'), false);
    await page.locator('[data-workspace-pin]').fill('0123');
    await page.locator('[data-workspace-save]').click();
    await page.waitForFunction(() => window.ProductionWeekWorkspace.state().status === 'saved');
    assert.equal((await stored()), null);
    assert.equal(saves.length, 1);
    // Carryover fields without any new programming do not create pending state on reload.
    assert.equal(await main(0).inputValue(), '');
    await reset(); await prepare();
    await main(6).fill('12.3456');
    assert.equal((await stored()).rows[0].cells[6].value, '12.3456');
    await page.locator('[data-workspace-copy]').click();
    assert.equal(await main(6).inputValue(), '12.3456');
    await page.getByRole('checkbox', { name: 'Traer arrastre de FG-100', exact: true }).uncheck();
    assert.equal(await main(6).isEnabled(), true);
    assert.equal(await main(6).inputValue(), '12.3456');
    await page.reload(); await page.getByRole('button', { name: 'Recuperar', exact: true }).click();
    assert.equal(await main(6).inputValue(), '12.3456');
    // Two copied lines on Monday and one on Tuesday stay distinct under one SKU.
    await reset();
    source.rows = [100, 200, 70].map((quantity, index) => ({ ...originalRows[0], id: `copy-line-${index}`, day: index === 2 ? 1 : 0, quantity }));
    await prepare(true, true, true);
    assert.equal(await main(0).inputValue(), '300'); assert.equal(await main(1).inputValue(), '70');
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
    assert.equal(await page.locator('[data-schedule-orders] [data-workspace-row]').count(), 2);
    await main(1).fill('80'); assert.equal(await main(1).inputValue(), '80');
    await main(0).fill('400');
    assert.equal(await page.locator('[data-schedule-orders]').evaluate(el => el.open), true);
    assert.equal(await order(0, 0).inputValue(), '100'); assert.equal(await order(1, 0).inputValue(), '200');
    assert.match(await page.locator('[data-workspace-sku-error]').first().textContent(), /total 400, asignado 300/);
    const reviewCount = reviews.length;
    await page.locator('[data-workspace-review]').click();
    assert.equal(reviews.length, reviewCount); assert.equal(await page.locator('[data-workspace-review-panel]').isVisible(), false);
    await page.reload(); await page.getByRole('button', { name: 'Recuperar', exact: true }).click();
    assert.equal(await main(0).inputValue(), '400'); assert.equal(await order(1, 0).inputValue(), '200');
    assert.equal(Object.values((await stored()).skuDayTargets)[0]['0'], '400');
    await page.locator('[data-workspace-copy]').click(); assert.equal(await main(0).inputValue(), '400');
    await order(0, 0).fill('150'); await order(1, 0).fill('250');
    assert.deepEqual((await stored()).skuDayTargets, {});
    assert.equal(await main(0).inputValue(), '400');
    assert.equal(await main(1).inputValue(), '80');
    await page.locator('[data-workspace-copy-toggle]').click();
    await main(0).fill('410');
    
    
    await page.locator('[data-workspace-scroll]').evaluate(el => { el.scrollLeft = el.scrollWidth; });
    await page.locator('[data-workspace-scroll]').screenshot({ path: path.join(output, 'manual-allocation.png') });
    await order(1, 0).fill('260');
    
    await page.locator('[data-workspace-copy-toggle]').click();
    const combined = await review();
    assert.deepEqual(combined.changes.filter(change => change.line.plannedDate.endsWith('-21')).map(change => change.line.quantity).sort(), ['150', '260']);
    assert.equal(combined.changes.length, 3); assert.equal(combined.openings.length, 2);
    await page.locator('[data-workspace-back]').click();
    await page.locator('[data-schedule-orders] > summary').click();
    await main(0).focus(); await page.keyboard.press('Enter');
    assert.equal(await main(1).evaluate(el => el === document.activeElement), true);
    const ids = await page.locator('[id]').evaluateAll(nodes => nodes.map(node => node.id));
    assert.equal(new Set(ids).size, ids.length);
    // Exact arithmetic keeps the fourth decimal and totals above an individual line's maximum.
    await page.locator('[data-schedule-orders] > summary').click();
    await order(0, 0).fill('99999999999999.9999'); await order(1, 0).fill('99999999999999.9999');
    assert.equal(await main(0).inputValue(), '199999999999999.9998');
    await main(0).fill('199999999999999.9997');
    assert.equal(await main(0).getAttribute('aria-invalid'), 'true');
    await order(1, 0).fill('99999999999999.9998');
    assert.equal(await main(0).getAttribute('aria-invalid'), 'false');
    await page.locator('[data-schedule-orders] input.production-week-workspace__quantity').first().press('Escape');
    assert.equal(await page.locator('[data-schedule-orders]').evaluate(el => el.open), false);
    assert.equal(await page.locator('[data-schedule-orders] > summary').evaluate(el => el === document.activeElement), true);
    // Presentation remains usable at every requested breakpoint and theme.
    let cases = 0;
    for (const width of [360, 575, 576, 768, 899, 900, 1199, 1200, 1440]) for (const theme of ['light', 'dark', 'system']) for (const view of ['week']) {
      await page.setViewportSize({ width, height: 1000 });
      await page.emulateMedia({ colorScheme: theme === 'system' ? 'dark' : 'light' });
      await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
      
      assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
      const metrics = await page.locator('input.production-week-workspace__quantity:visible').evaluateAll(inputs => inputs.map(input => ({ height: input.getBoundingClientRect().height, width: input.getBoundingClientRect().width })));
      assert.ok(metrics.every(metric => metric.height >= 44 && metric.width > 0));
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, `${width}/${theme}/${view}`);
      cases++;
    }
    await page.setViewportSize({ width: 640, height: 450 });
    
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    await page.setViewportSize({ width: 1440, height: 1000 });
    
    await main(0).fill('400'); await order(0, 0).fill('150'); await order(1, 0).fill('250');
    await page.locator('[data-schedule-orders] > summary').click();
    if (await page.locator('[data-workspace-copy-panel]').isVisible()) await page.locator('[data-workspace-copy-toggle]').click();
    await page.locator('[data-workspace-scroll]').evaluate(el => { el.scrollLeft = 0; });
    await page.locator('[data-workspace-scroll]').screenshot({ path: path.join(output, 'orders-after.png') });
    // Copy without quantities remains editable, and excluding an order retains its values.
    await reset(); await prepare(true, true, false);
    assert.equal(await main(0).inputValue(), '');
    await main(0).fill('30'); await order(0, 0).fill('10'); await order(1, 0).fill('20');
    await order(0, 1).fill('8');
    await page.locator('[data-workspace-copy]').click(); assert.equal(await main(0).inputValue(), '30');
    const include = page.locator('[data-schedule-orders] [data-workspace-row]').nth(1).getByRole('checkbox');
    await include.uncheck();
    assert.equal(await main(0).inputValue(), '10'); assert.equal(await order(1, 0).inputValue(), '20');
    await include.check(); assert.equal(await main(0).inputValue(), '30');
    await Promise.all([page.waitForEvent('load'), page.locator('[data-workspace-clear-all]').click()]);
    assert.equal((await stored()), null);
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 0);
    // Real English Razor fixture provides both the grouped caption and distribution error.
    language = 'en'; await reset(); await prepare(true, true, true);
    await main(0).fill('400');
    assert.match(await page.locator('[data-schedule-orders] > summary').textContent(), /Edit orders \(2\)/);
    assert.match(await page.locator('[data-workspace-sku-error]').first().textContent(), /Adjust the orders: total 400, assigned 300/);
    assert.match(await order(0, 0).getAttribute('aria-label'), /Line 1/);
    await order(0, 0).fill('150'); await order(1, 0).fill('250');
    assert.equal(await main(0).getAttribute('aria-invalid'), 'false');
    // Saved line IDs, per-day removal and undo remain independent under one SKU.
    screen = 'editable'; language = 'es'; await reset();
    assert.equal(await main(0).inputValue(), '40');
    await main(0).fill('50'); await order(0, 0).fill('40');
    const edited = await review();
    assert.equal(edited.changes.length, 1); assert.equal(edited.changes[0].kind, 'edit');
    assert.ok(edited.changes[0].lineId); assert.equal(edited.changes[0].line.quantity, '40');
    await page.locator('[data-workspace-back]').click();
    await main(0).fill('0');
    for (const index of [0, 1]) {
      const section = page.locator('[data-schedule-orders] [data-workspace-row]').nth(index);
      await section.locator('[data-schedule-disclosure^="row:"] > summary').click();
      await section.getByRole('button', { name: /Quitar día/ }).click();
    }
    const removed = await review();
    assert.equal(removed.changes.length, 2); assert.ok(removed.changes.every(change => change.kind === 'remove' && change.lineId && change.line === null));
    await page.locator('[data-workspace-back]').click();
    await page.locator('[data-schedule-orders] [data-workspace-row]').first().getByRole('button', { name: /Deshacer:/ }).click();
    assert.equal(await main(0).inputValue(), '40');
    // Existing production keeps its per-line deletion restriction.
    screen = 'open'; await reset();
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 1);
    await page.locator('[data-schedule-orders] > summary').click();
    await page.locator('[data-schedule-disclosure^="row:"] > summary').evaluateAll(nodes => nodes.forEach(node => node.click()));
    assert.ok(await page.getByRole('button', { name: /Quitar día/ }).evaluateAll(buttons => buttons.some(button => button.disabled)));
    assert.deepEqual(errors, []);
    console.log(`SKU row: carry-only save, editable days, manual allocation, recovery, exact decimals, focus and ${cases} presentation cases passed.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
