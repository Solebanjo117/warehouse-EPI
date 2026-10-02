// Current Razor fixtures, isolated Edge and intercepted requests; no operational service or database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = path.join(root, 'artifacts/schedule-clear-all/copy-fixtures');
const tablet = path.join(root, 'artifacts/schedule-clear-all/tablet-fixtures');
const output = path.join(root, 'artifacts/schedule-clear-all/browser');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = [], confirmations = [], writes = [];
    let accept = true, language = 'es', screen = 'copy', loseSave = false, holdCopy = null;
    const source = JSON.parse(fs.readFileSync(path.join(fixtures, 'source.json')));
    const originalRows = structuredClone(source.rows);
    const openings = JSON.parse(fs.readFileSync(path.join(fixtures, 'openings.json')));
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', async dialog => {
      confirmations.push([dialog.type(), dialog.message()]);
      await (accept ? dialog.accept() : dialog.dismiss());
    });
    await page.route('http://clear.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), handler = url.searchParams.get('handler');
      if (request.method() === 'POST') {
        writes.push(handler);
        if (handler === 'WorkspaceReview') return route.fulfill({ json: { canConfirm: true, fingerprint: 'clear-reviewed', errors: [] } });
        assert.equal(handler, 'WorkspaceSave');
        assert.equal(loseSave, true);
        return route.abort('failed');
      }
      if (handler === 'WorkspaceOpenings') return route.fulfill({ json: screen === 'copy' ? openings : [] });
      if (handler === 'WorkspaceCopy') {
        if (holdCopy) await holdCopy;
        return route.fulfill({ json: source });
      }
      if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
      if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(screen === 'copy'
        ? path.join(fixtures, language === 'en' ? 'before-en.html' : 'before.html')
        : path.join(tablet, `program-${screen}-${language}.html`), 'utf8') });
      const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
      const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
      return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
    });
    const clear = page.locator('[data-workspace-clear-all]');
    const main = day => page.locator(`[data-workspace-rows] > tr > [data-workspace-day-cell="${day}"] > input.production-week-workspace__quantity`);
    const stored = () => page.evaluate(() => {
      const root = document.querySelector('[data-week-workspace]');
      const key = `warehouse-epi:schedule:${root.dataset.userId}:${root.dataset.weekId}`;
      return { key, value: localStorage.getItem(key), legacy: localStorage.getItem(`${key}:legacy-paste`) };
    });
    const reset = async () => {
      await page.evaluate(() => localStorage.clear());
      await page.goto('http://clear.test/Admin/Production/Schedule');
      await page.waitForFunction(() => document.querySelector('[data-week-workspace]')?.dataset.readonly === 'true' || !!window.ProductionWeekWorkspace);
    };
    const prepare = async (products = true, carry = false, quantities = false) => {
      if (!await page.locator('[data-workspace-copy-panel]').isVisible()) await page.locator('[data-workspace-copy-toggle]').click();
      await page.locator('[data-workspace-copy-products]').setChecked(products);
      await page.locator('[data-workspace-copy-openings]').setChecked(carry);
      await page.locator('[data-workspace-copy-quantities]').setChecked(quantities);
      await page.locator('[data-workspace-copy]').click();
      await page.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    };
    const discard = async (keyboard = false) => {
      const previous = confirmations.length;
      if (keyboard) { await clear.focus(); assert.equal(await clear.evaluate(button => button === document.activeElement), true); }
      await Promise.all([page.waitForEvent('load'), keyboard ? page.keyboard.press('Enter') : clear.click()]);
      await page.waitForFunction(() => !!window.ProductionWeekWorkspace && !window.ProductionWeekWorkspace.state().pending);
      assert.equal(confirmations.length - previous, 1); // No second beforeunload dialog after confirming.
      assert.equal(confirmations.at(-1)[0], 'confirm');
      assert.equal((await stored()).value, null);
      assert.equal(await clear.isVisible(), false);
      assert.equal(await page.locator('[data-workspace-recovery]').isVisible(), false);
      const url = new URL(page.url());
      assert.equal(url.searchParams.get('View'), 'program');
      assert.equal(url.searchParams.has('DeleteLineId'), false);
      assert.equal(writes.length, 0);
    };

    await page.goto('http://clear.test/Admin/Production/Schedule?ActionPanel=copy&DeleteLineId=stale-link');
    await prepare();
    assert.equal(await page.locator('[data-workspace-discard-preparation]').count(), 0);
    assert.equal(await clear.isVisible(), true);
    assert.match(await page.locator('[data-workspace-count]').textContent(), /^0 /);
    const pending = (await stored()).value;
    accept = false; await clear.click(); accept = true;
    assert.equal((await stored()).value, pending);
    assert.equal(await main(0).inputValue(), '');
    const legacy = 'Texto sin procesar anterior';
    await page.evaluate(({ key, legacy }) => {
      localStorage.setItem(`${key}:legacy-paste`, legacy);
      localStorage.setItem('warehouse-epi:schedule:another-user:another-week', 'Otro borrador');
    }, { key: (await stored()).key, legacy });
    await discard(true);
    assert.equal((await stored()).legacy, legacy);
    assert.equal(await page.evaluate(() => localStorage.getItem('warehouse-epi:schedule:another-user:another-week')), 'Otro borrador');

    await prepare(false, true);
    await main(6).fill('12.3456');
    await page.locator('[data-workspace-opening-input]').first().fill('10');
    await discard();
    assert.equal(await page.locator('[data-workspace-rows] > tr').count(), 0);

    source.rows = [100, 200].map((quantity, index) => ({ ...originalRows[0], id: `copy-${index}`, day: 0, quantity }));
    await prepare(true, true, true);
    await main(0).fill('400');
    assert.equal(JSON.parse((await stored()).value).skuDayTargets[source.rows[0].productId][0], '400');
    await page.reload();
    assert.equal(await clear.isDisabled(), true);
    await page.getByRole('button', { name: 'Recuperar', exact: true }).click();
    await page.waitForFunction(() => !document.querySelector('[data-workspace-clear-all]').disabled);
    assert.equal(await main(0).inputValue(), '400');
    await discard();

    // Saved quantities and notes are restored from the server fixture, rather than removed by a POST.
    screen = 'editable'; await reset();
    const baseline = await page.locator('[data-workspace-rows] input.production-week-workspace__quantity').evaluateAll(inputs => inputs.map(input => input.value));
    await main(0).first().fill('1');
    const metadata = page.locator('input[id$="-notes"]').first();
    await metadata.evaluate(input => { for (let parent = input.parentElement; parent; parent = parent.parentElement) if (parent.tagName === 'DETAILS') parent.open = true; });
    const originalNotes = await metadata.inputValue();
    await metadata.fill('Una nota todavía sin guardar');
    await discard();
    assert.deepEqual(await page.locator('[data-workspace-rows] input.production-week-workspace__quantity').evaluateAll(inputs => inputs.map(input => input.value)), baseline);
    assert.equal(await page.locator('input[id$="-notes"]').first().inputValue(), originalNotes);

    screen = 'copy'; source.rows = originalRows; await reset(); await prepare();
    let resumeCopy;
    holdCopy = new Promise(resolve => { resumeCopy = resolve; });
    await page.locator('[data-workspace-copy]').click();
    assert.equal(await clear.isDisabled(), true);
    resumeCopy(); holdCopy = null;
    await page.waitForFunction(() => !document.querySelector('[data-workspace-copy]').disabled);
    assert.equal(await clear.isEnabled(), true);
    await discard();

    // The global action remains outside horizontal table scrolling, including narrow widths and zoom.
    let layouts = 0;
    for (language of ['es', 'en']) {
      await reset(); await prepare();
      await page.locator('[data-workspace-copy-toggle]').click();
      for (const theme of ['light', 'dark', 'system']) {
        await page.emulateMedia({ colorScheme: theme === 'light' ? 'light' : 'dark' });
        await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme === 'system' ? 'dark' : theme), theme);
        for (const width of [360, 575, 576, 768, 899, 900, 1199, 1200, 1440]) {
          await page.setViewportSize({ width, height: 1000 });
          await page.mouse.move(width - 5, 5);
          await page.evaluate(() => { document.activeElement?.blur(); return document.fonts.ready; });
          await page.waitForFunction(width => {
            const content = document.querySelector('.app-content'), sidebar = document.querySelector('.app-sidebar');
            const left = parseFloat(getComputedStyle(content).marginLeft);
            return Math.abs(left - (width >= 1200 ? 250 : width >= 900 ? 72 : 0)) < .1 &&
              (width >= 900 || sidebar.getBoundingClientRect().right <= .1);
          }, width);
          const box = await clear.boundingBox();
          assert.ok(box.height >= 44 && box.x >= 0 && box.x + box.width <= width + 1, JSON.stringify({ theme, width, box }));
          assert.equal(await clear.textContent(), language === 'en' ? 'Clear all' : 'Limpiar todo');
          const view = page.locator('[data-workspace-count]').locator('..');
          const boxes = await view.locator(':scope > *:visible').evaluateAll(nodes => nodes.map(node => { const r = node.getBoundingClientRect(); return { left: r.left, right: r.right, top: r.top, bottom: r.bottom }; }));
          for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) {
            const a = boxes[i], b = boxes[j];
            assert.ok(Math.min(a.right, b.right) <= Math.max(a.left, b.left) + 1 || Math.min(a.bottom, b.bottom) <= Math.max(a.top, b.top) + 1, JSON.stringify({ width, a, b }));
          }
          layouts++;
          if (language === 'es' && theme === 'light' && [360, 1440].includes(width)) await page.screenshot({ path: path.join(output, `clear-${width}.png`), fullPage: true, animations: 'disabled' });
        }
      }
      await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
      for (const width of [768, 899, 900, 1199, 1200, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        const box = await clear.boundingBox();
        assert.ok(box.height >= 88 && box.x + box.width <= width + 1);
      }
      await page.evaluate(() => { document.documentElement.style.zoom = ''; });
      await discard();
    }
    for (const state of ['draft', 'open', 'closed']) {
      screen = state; await reset();
      if (state === 'closed') assert.equal(await clear.count(), 0);
      else assert.equal(await clear.isVisible(), false);
    }

    screen = 'copy'; language = 'es'; loseSave = true; await reset(); await prepare(false, true);
    await page.locator('[data-workspace-review]').click();
    await page.locator('[data-workspace-review-panel]').waitFor();
    if (await page.locator('[data-workspace-reason-panel]').isVisible()) {
      await page.locator('[data-workspace-reason]').fill('Continuar pendientes');
      await page.locator('[data-workspace-recheck]').click();
    }
    await page.waitForFunction(() => !!document.querySelector('[data-workspace-payload]').value);
    await page.locator('[data-workspace-pin]').fill('0123');
    await page.locator('[data-workspace-save]').click();
    await page.waitForFunction(() => window.ProductionWeekWorkspace.state().status === 'uncertain');
    assert.equal(await clear.isDisabled(), true);
    const uncertain = (await stored()).value, count = confirmations.length;
    await clear.dispatchEvent('click');
    assert.equal((await stored()).value, uncertain);
    assert.equal(confirmations.length, count);
    assert.equal(writes.filter(handler => handler === 'WorkspaceSave').length, 1);
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ layouts, zoom: 12, errors, savedSchedulePreserved: true, uncertainSaveProtected: true }, null, 2));
    console.log(`Passed clear-all flows, ${layouts} layouts, 12 zoom cases, saved baseline and uncertain-save protections.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
