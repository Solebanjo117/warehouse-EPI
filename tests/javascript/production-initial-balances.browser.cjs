// Razor fixtures and intercepted requests only. Never connects to the operational application.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtureDir = path.join(root, 'artifacts/initial-carryover/fixtures');
const output = path.join(root, 'artifacts/initial-carryover/browser');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; let saved = false, closed = false, posts = 0, reviews = 0;
    page.on('pageerror', error => errors.push(error.message));
    const initials = JSON.parse(fs.readFileSync(path.join(fixtureDir, 'initials.json')));
    const source = JSON.parse(fs.readFileSync(path.join(fixtureDir, 'source.json')));
    const expected = [135, 0, 23], productId = initials[0].productId;
    const handleRoute = async route => {
      const request = route.request(), url = new URL(request.url()), handler = url.searchParams.get('handler');
      if (request.method() === 'POST') {
        if (handler === 'WorkspaceReview') {
          reviews++; const payload = request.postDataJSON();
          assert.equal(payload.changes.length, 0); assert.equal(payload.initialBalances.length, 3);
          assert.equal(payload.openings, undefined);
          assert.deepEqual(payload.initialBalances.map(row => Number(row.quantity)), expected);
          assert.equal(JSON.stringify(payload).includes('0123'), false);
          return route.fulfill({ json: { canConfirm: true, fingerprint: 'review-initial', errors: [] } });
        }
        assert.equal(handler, 'WorkspaceSave'); posts++; saved = true;
        return route.fulfill({ json: { saved: true, count: 3 } });
      }
      if (handler === 'WorkspaceInitialBalances') return route.fulfill({ json: initials.map(row => ({ ...row,
        quantity: saved ? String(expected[row.area]) : row.quantity, version: saved ? 1 : 0 })) });
      if (handler === 'WorkspaceCopy') return route.fulfill({ json: source });
      if (handler === 'WorkspaceProducts') return route.fulfill({ json: [{ id: productId, sku: 'FG-100', unit: 'EA', allowsDecimals: true }] });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html',
        body: fs.readFileSync(path.join(fixtureDir, closed ? 'closed.html' : saved ? 'saved.html' : 'before.html'), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return route.fulfill(fs.existsSync(file) && fs.statSync(file).isFile() ? { path: file } : { status: 204 });
    };
    await page.route('http://initial.test/**', handleRoute);
    const productsPage = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    productsPage.on('pageerror', error => errors.push(error.message));
    await productsPage.route('http://initial.test/**', handleRoute);
    await productsPage.goto('http://initial.test/Admin/Production/Schedule');
    await productsPage.locator('[data-workspace-copy-toggle]').click();
    await productsPage.locator('[data-workspace-copy-products]').check();
    await productsPage.locator('[data-workspace-copy-openings]').uncheck();
    await productsPage.locator('[data-workspace-copy-quantities]').uncheck();
    await productsPage.locator('[data-workspace-copy]').click();
    await productsPage.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    for (let area = 0; area < 3; area++) {
      const input = productsPage.locator('[data-workspace-opening-input="' + area + '"]');
      assert.equal(await input.isEnabled(), true);
      await input.fill(String(expected[area]));
      assert.equal(await input.inputValue(), String(expected[area]));
    }
    const productToggle = productsPage.locator('[data-workspace-rows] th').getByRole('checkbox', { name: 'Incluir producto FG-100', exact: true });
    assert.equal(await productToggle.count(), 1);
    assert.equal(await productsPage.locator('[data-workspace-rows] td').getByRole('checkbox', { name: 'Incluir producto FG-100', exact: true }).count(), 0);
    for (let day = 0; day < 4; day++) await productsPage.locator('[data-workspace-sku-day="' + day + '"]').fill(String([200, 100, 300, 20][day]));
    for (let area = 0; area < 3; area++) await productsPage.locator('[data-workspace-opening-input="' + area + '"]').fill(String([200, 300, 100][area]));
    const totalCell = productsPage.locator('[data-schedule-carry-total]').first();
    assert.equal(await productsPage.locator('[data-schedule-orders]').count(), 0);
    assert.deepEqual(await totalCell.locator('strong').allTextContents(), ['820 EA', '920 EA', '720 EA']);
    assert.equal(await totalCell.evaluate(el => el.previousElementSibling.textContent), '620 EA');
    assert.equal(await totalCell.evaluate(el => el.nextElementSibling === el.parentElement.lastElementChild), true);
    assert.equal(await productsPage.getByRole('columnheader', { name: 'Total con arrastre', exact: true }).count(), 1);
    assert.equal(await totalCell.evaluate(el => el.parentElement.children.length), 14);
    await totalCell.scrollIntoViewIfNeeded();
    await productsPage.screenshot({ path: path.join(output, 'total-with-carryover.png'), animations: 'disabled' });
    await productsPage.locator('[data-workspace-opening-input="1"]').fill('bad');
    assert.deepEqual(await totalCell.locator('strong').allTextContents(), ['820 EA', 'Por revisar', '720 EA']);
    await productsPage.locator('[data-workspace-opening-input="1"]').fill('300');
    await productsPage.locator('[data-workspace-sku-day="0"]').fill('bad');
    assert.deepEqual(await totalCell.locator('strong').allTextContents(), ['Por revisar', 'Por revisar', 'Por revisar']);
    await productsPage.locator('[data-workspace-sku-day="0"]').fill('200');
    await productsPage.reload(); await productsPage.evaluate(() => window.ProductionWeekWorkspace.ready);
    assert.deepEqual(await productsPage.locator('[data-schedule-carry-total] strong').allTextContents(), ['820 EA', '920 EA', '720 EA']);
    await productsPage.close();
    await page.goto('http://initial.test/Admin/Production/Schedule');
    const row = page.locator(`[data-schedule-product="${productId}"]`);
    const opening = area => row.locator(`[data-workspace-opening-input="${area}"]`);
    await opening(0).waitFor();
    assert.equal(await row.locator('[data-workspace-opening-input]').count(), 3);
    await page.locator('[data-workspace-copy-toggle]').click();
    await page.locator('[data-workspace-copy-products]').uncheck();
    await page.locator('[data-workspace-copy-openings]').check();
    const prepare = async () => {
      await page.locator('[data-workspace-copy]').click();
      await page.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    };
    await prepare();
    assert.equal(await opening(0).inputValue(), '0');
    await page.locator('[data-workspace-copy-openings]').uncheck();
    for (let area = 0; area < 3; area++) assert.equal(await opening(area).isEnabled(), true);
    await opening(0).fill('135'); await opening(0).press('Enter');
    assert.equal(await opening(1).evaluate(el => el === document.activeElement), true);
    await opening(1).fill('0'); await opening(2).fill('23');
    await page.locator('[data-workspace-copy-openings]').check(); await prepare();
    await page.locator('[data-workspace-copy-openings]').uncheck();
    assert.equal(await row.getByRole('checkbox', { name: 'Traer arrastre de FG-100', exact: true }).count(), 0);
    for (let area = 0; area < 3; area++) assert.equal(await opening(area).isEnabled(), true);
    for (let area = 0; area < 3; area++) assert.equal(await opening(area).inputValue(), String(expected[area]));
    await page.reload();
    await page.evaluate(() => window.ProductionWeekWorkspace.ready);
    assert.equal(await page.locator('[data-workspace-recovery]').isVisible(), false);
    for (let area = 0; area < 3; area++) assert.equal(await opening(area).inputValue(), String(expected[area]));
    assert.equal(await page.locator('[data-workspace-clear-all]').isEnabled(), true);
    for (const width of [768, 899, 900, 1199, 1200, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      for (const theme of ['light', 'dark', 'system']) {
        await page.emulateMedia({ colorScheme: 'dark' });
        if (width < 900) await page.locator('[data-nav-toggle]').click();
        const account = page.locator('.app-account-menu');
        if (!(await account.evaluate(el => el.open))) await account.locator('summary').click();
        await page.locator(`[data-theme-choice="${theme}"]`).click();
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
        if (theme === 'system') {
          await page.emulateMedia({ colorScheme: 'light' });
          await page.waitForFunction(() => document.documentElement.dataset.bsTheme === 'light');
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.waitForFunction(() => document.documentElement.dataset.bsTheme === 'dark');
        }
        await account.locator('summary').click();
        if (width < 900) await page.keyboard.press('Escape');
        assert.ok(await opening(0).evaluate(el => el.getBoundingClientRect().height >= 44));
        assert.equal(await opening(2).inputValue(), '23');
        const sticky = await page.evaluate(() => {
          const scroll = document.querySelector('[data-workspace-scroll]');
          const table = scroll.querySelector('table'), head = table.tHead;
          const body = table.tBodies[0];
          const copies = Array.from({ length: 15 }, () => body.rows[0].cloneNode(true));
          copies.forEach(row => body.append(row));
          scroll.scrollTop = 400; scroll.scrollLeft = scroll.scrollWidth;
          const result = { scrolled: scroll.scrollTop, top: head.getBoundingClientRect().top,
            containerTop: scroll.getBoundingClientRect().top,
            headerCount: head.querySelectorAll('th').length };
          copies.forEach(row => row.remove()); scroll.scrollTop = 0; scroll.scrollLeft = 0;
          return result;
        });
        assert.ok(sticky.scrolled > 0);
        assert.ok(Math.abs(sticky.top - sticky.containerTop) < 2);
        assert.ok(sticky.headerCount >= 12);
        const palette = await page.locator('.production-week-workspace__table > thead').evaluate(head => {
          const luminance = color => {
            const [r, g, b] = color.match(/[\d.]+/g).slice(0, 3).map(Number).map(value => {
              value /= 255; return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
            });
            return .2126 * r + .7152 * g + .0722 * b;
          };
          const background = cell => getComputedStyle(cell).backgroundColor;
          const contrast = (foreground, background) => {
            const a = luminance(foreground), b = luminance(background);
            return (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
          };
          return {
            sections: [...head.rows[0].cells].map(background),
            columns: [...head.rows[1].cells].map(background),
            contrasts: [...head.querySelectorAll('th')].flatMap(cell => [cell, ...cell.querySelectorAll('span')]
              .map(node => contrast(getComputedStyle(node).color, background(cell))))
          };
        });
        assert.equal(new Set(palette.sections).size, 6, `${width}/${theme}: distinct section colors`);
        assert.ok(palette.columns.slice(0, 3).every(color => color === palette.sections[1]));
        assert.ok(palette.columns.slice(3).every(color => color === palette.sections[2]));
        assert.ok(palette.contrasts.every(value => value >= 4.5), `${width}/${theme}: header contrast ${palette.contrasts}`);
        await page.screenshot({ path: path.join(output, `${width}-${theme}.png`), fullPage: true, animations: 'disabled' });
      }
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.locator('[data-workspace-review]').click();
    await page.locator('[data-workspace-reason]').fill('Ajustar arrastre');
    await page.locator('[data-workspace-recheck]').click();
    await page.waitForFunction(() => document.querySelector('[data-workspace-pin]') === document.activeElement);
    assert.equal(await page.locator('[data-workspace-pin]').evaluate(el => el === document.activeElement), true);
    assert.ok(await page.locator('[data-workspace-pin]').evaluate(el => {
      const rect = el.getBoundingClientRect(); return rect.top >= 0 && rect.bottom <= innerHeight;
    }));
    await page.locator('[data-workspace-pin]').fill('0123');
    await Promise.all([page.waitForEvent('load'), page.locator('[data-workspace-save]').click()]);
    assert.equal(posts, 1); assert.equal(reviews, 1);
    for (let area = 0; area < 3; area++) assert.equal(await opening(area).inputValue(), String(expected[area]));
    assert.equal(await page.locator('[data-workspace-clear-all]').isVisible(), false);
    assert.equal(await page.evaluate(() => Object.values(localStorage).some(value => value.includes('0123'))), false);
    // Regression: the actual copy UI produces 80 lines and 144 initial balances (including zero).
    const large = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    large.on('pageerror', error => errors.push(error.message));
    const products = Array.from({ length: 48 }, (_, i) => ({ productId: '00000000-0000-0000-0000-' + String(i + 1).padStart(12, '0'),
      sku: 'COPY-' + i, unit: 'EA', allowsDecimals: true }));
    const largeInitials = products.flatMap(product => [0, 1, 2].map(area => ({ ...product, area, quantity: '0', version: 0 })));
    const largeSource = { ...source, initialBalances: largeInitials.map(row => ({ ...row, sourceWeekId: source.id, sourceStart: '2026-09-28' })),
      rows: Array.from({ length: 81 }, (_, i) => ({ ...products[i % 48], id: 'line-' + i, day: i < 48 ? 0 : 1, quantity: 20 })) };
    let largeSaves = 0;
    await large.route('http://initial.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), handler = url.searchParams.get('handler');
      if (request.method() === 'POST') {
        if (handler === 'WorkspaceReview') return route.fulfill({ json: { canConfirm: true, fingerprint: 'review-large', errors: [] } });
        assert.equal(handler, 'WorkspaceSave'); largeSaves++;
        return route.fulfill({ json: { saved: true, count: 219 } });
      }
      if (handler === 'WorkspaceInitialBalances') return route.fulfill({ json: largeSaves ? largeInitials.filter(row => row.productId !== products[0].productId) : largeInitials });
      if (handler === 'WorkspaceCopy') return route.fulfill({ json: largeSource });
      if (handler === 'WorkspaceProducts') return route.fulfill({ json: products.map(product => ({ ...product, id: product.productId })) });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html',
        body: fs.readFileSync(path.join(fixtureDir, 'before.html'), 'utf8').replaceAll('data-week-status="Open"', 'data-week-status="Draft"') });
      return handleRoute(route);
    });
    await large.goto('http://initial.test/Admin/Production/Schedule');
    await large.locator('[data-workspace-copy-toggle]').click();
    await large.locator('[data-workspace-copy-products]').check();
    await large.locator('[data-workspace-copy-openings]').check();
    await large.locator('[data-workspace-copy-quantities]').check();
    await large.locator('[data-workspace-copy]').click();
    await large.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    await large.locator('[data-workspace-sku-input="' + products[32].productId + '"][data-workspace-sku-day="1"]').fill('');
    await large.getByRole('checkbox', { name: 'Incluir producto COPY-0', exact: true }).uncheck();
    await large.evaluate(() => window.ProductionWeekWorkspace.openReview());
    const largePayload = JSON.parse(await large.locator('[data-workspace-payload]').inputValue());
    assert.equal(largePayload.skuTotals.length, 78);
    assert.ok(largePayload.skuTotals.every(change => change.productId !== products[0].productId));
    assert.equal(largePayload.initialBalances.length, 141);
    assert.ok(largePayload.initialBalances.every(change => change.productId !== products[0].productId));
    assert.equal(largePayload.initialBalances.filter(row => row.quantity === '0').length, 141);
    assert.equal(largeSaves, 0);
    await Promise.all([large.waitForEvent('load'), large.locator('[data-workspace-save]').click()]);
    assert.equal(largeSaves, 1);
    const remaining = await large.evaluate(() => Object.entries(localStorage).filter(([key]) => key.startsWith('warehouse-epi:schedule:')).map(([, value]) => JSON.parse(value)));
    assert.ok(remaining.some(state => state.rows?.some(row => row.suggestedDays?.includes(1))));
    await large.evaluate(() => window.ProductionWeekWorkspace.ready);
    assert.equal(await large.locator('[data-workspace-progress-warning]').isVisible(), false);
    assert.equal(await large.locator('[data-schedule-preparation-status]').getAttribute('data-preparation-state'), 'saved');
    assert.equal(await large.locator('[data-workspace-clear-all]').isVisible(), true);
    assert.equal(await large.locator('[data-schedule-product="' + products[0].productId + '"]').count(), 0);
    await large.close();
    closed = true; await page.reload();
    assert.equal(await page.locator('[data-workspace-opening-input]').count(), 0);
    assert.equal(await page.locator('[data-schedule-carry-total]').count(), 1);
    assert.equal(await page.locator('[data-schedule-carry-total] [data-total-area]').count(), 3);
    assert.deepEqual(await page.locator('[data-schedule-carry-total] strong').allTextContents(), ['185 EA', '50 EA', '73 EA']);
    assert.deepEqual(errors, []);
    console.log('Verified: 18 views, three editable areas per SKU, product-only copy, excluded copy with manual totals, zero, recovery, Enter, save, closed read-only, and a large copy with complete SKU exclusion from both program and carryover, absent after save/reload.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
