// Optional browser suite. Requires playwright and WAREHOUSE_BALANCE_FIXTURES from the HTTP test.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const fixtures = process.env.WAREHOUSE_BALANCE_FIXTURES || path.join(root, 'artifacts/balance-priority1/fixtures');
const baseHtml = fs.readFileSync(path.join(fixtures, 'balance.html'), 'utf8');
const rowHtml = fs.readFileSync(path.join(fixtures, 'row.html'), 'utf8');
const addedId = rowHtml.match(/data-balance-product="([^"]+)"/)[1];
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
    try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    let restores = 0, current = '0', registered = false;
    let forceReason = false;
    await page.route('http://balance.test/**', async route => {
        const url = new URL(route.request().url());
        const handler = url.searchParams.get('handler');
        if (handler === 'BalanceEditRestore') {
            restores++;
            const body = route.request().postDataJSON();
            return route.fulfill({ json: registered ? { registered: true, operationId: body.edit.operationId, recordId: 'receipt' } : {
                registered: false, conflict: false, html: rowHtml, blocked: false, unavailable: [],
                operationId: body.edit.planChanges?.length || body.edit.newPlans?.length ? require('node:crypto').randomUUID() : body.edit.operationId,
                cells: body.edit.cells.map(cell => ({ cell, current, blocked: false, conflict: cell.observed !== current }))
            } });
        }
        if (handler === 'BalanceProducts') {
            assert.fail('The balance must not request catalogue lookups');
        }
        if (handler === 'BalanceRows') return route.fulfill({ json: { html: rowHtml, editable: true } });
        if (handler === 'BalanceEditPreview') {
            const submitted = route.request().postDataJSON();
            return route.fulfill({ json: { canConfirm: true, fingerprint: 'review', errors: [], requiresReason: forceReason,
                cells: submitted.cells.map(cell => ({ cell, current: Number(cell.observed), sku: 'M5-S-50UP-7M-NOHDL-US-LONG-REVIEW-SKU' })),
                plans: submitted.planChanges.map(change => ({ change, current: Number(change.observed), sku: 'REVIEW-PLAN' })),
                newPlans: submitted.newPlans.map(change => ({ change, sku: 'REVIEW-NEW' })), balance: { products: [] } } });
        }
        if (url.pathname.startsWith('/Operations/Production')) {
            const date = url.searchParams.get('Through');
            const html = date ? baseHtml.replace(/data-date="[^"]+"/, `data-date="${date}"`) : baseHtml;
            return route.fulfill({ contentType: 'text/html', body: html });
        }
        const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
        const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
        if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
        return route.fulfill({ status: 204 });
    });
    await page.goto('http://balance.test/Operations/Production');
    await page.waitForFunction(() => window.ProductionBalanceWorkspace);
    assert.equal(await page.locator('.production-weekly-plan input,[data-plan-line],[data-new-plan]').count(), 0);
    assert.equal(await page.locator('[data-balance-plan]').count(), await page.locator('[data-balance-product]').count());
    assert.equal(await page.locator('[data-balance-metric]').count(), 0);
    assert.equal(await page.locator('#balance-product-search,#balance-product-results').count(), 0);
    const first = page.locator('[data-edit-area="0"][data-edit-shift="1"]').first();
    const editorOrder = await page.evaluate(() => {
        const table = document.querySelector('[data-balance-scroll]');
        const actions = document.querySelector('[data-balance-actions]');
        const review = document.querySelector('[data-edit-review-button]');
        return {
            sticky: getComputedStyle(actions).position === 'sticky',
            followsTable: !!(table.compareDocumentPosition(actions) & Node.DOCUMENT_POSITION_FOLLOWING),
            sameForm: review.closest('form') === document.querySelector('#balance-editor') &&
                document.querySelector('#balance-edit-pin').form === document.querySelector('#balance-editor'),
            singleReview: document.querySelectorAll('[data-edit-review-button]').length === 1
        };
    });
    assert.deepEqual(editorOrder, { sticky: true, followsTable: true, sameForm: true, singleReview: true });
    const instant = await first.evaluate(input => {
        const row = input.closest('[data-balance-product]');
        const t2 = row.querySelector('[data-edit-area="0"][data-edit-shift="2"]');
        const intermediate = row.querySelector('[data-area="0"][data-balance-field="pendingAfterShift1"]');
        const final = row.querySelector('[data-area="0"][data-balance-field="netPending"]');
        const original = [Number(intermediate.textContent), Number(final.textContent)];
        const closing = document.querySelector('#week-close');
        input.value = String(Number(input.value) + 50); input.dispatchEvent(new Event('input', { bubbles: true }));
        t2.value = String(Number(t2.value) + 30); t2.dispatchEvent(new Event('input', { bubbles: true }));
        const result = { original, projected: [Number(intermediate.textContent), Number(final.textContent)],
            retainedClosing: closing === document.querySelector('#week-close'),
            confirmationHidden: document.querySelector('[data-edit-confirm]').hidden };
        input.value = input.dataset.original; input.dispatchEvent(new Event('input', { bubbles: true }));
        t2.value = t2.dataset.original; t2.dispatchEvent(new Event('input', { bubbles: true }));
        result.restored = [Number(intermediate.textContent), Number(final.textContent)];
        return result;
    });
    assert.deepEqual(instant.projected, [instant.original[0] - 50, instant.original[1] - 80]);
    assert.deepEqual(instant.restored, instant.original);
    assert.ok(instant.retainedClosing, 'Editing must not rebuild the weekly section');
    assert.ok(instant.confirmationHidden, 'Immediate pending never bypasses server review');
    await first.fill('5');
    const firstRow = first.locator('xpath=ancestor::tr');
    const otherTurn = firstRow.locator('[data-edit-area="0"][data-edit-shift="2"]');
    const otherArea = firstRow.locator('[data-edit-area="1"][data-edit-shift="1"]');
    const originals = [await otherTurn.inputValue(), await otherArea.inputValue()];
    await otherTurn.fill('20'); await otherArea.fill('20');
    await page.locator('[data-edit-review-button]').click();
    const compactReview = page.locator('[data-edit-review]');
    await page.waitForFunction(() => !document.querySelector('[data-edit-review]').hidden);
    assert.equal(await compactReview.locator('[data-review-summary]').innerText(), '1 producto · 3 cambios');
    assert.equal(await compactReview.locator('section').count(), 1);
    assert.equal(await compactReview.locator('tbody tr').count(), 2);
    assert.equal(await compactReview.locator('tbody tr').nth(1).locator('td').nth(1).innerText(), '—');
    assert.equal(await page.locator('[data-edit-reason-field]').isVisible(), false);
    assert.equal(await page.locator('#balance-edit-pin').evaluate(el => el === document.activeElement), true);
    for (const width of [360, 768, 900, 1280]) {
        await page.setViewportSize({ width, height: 900 });
        for (const theme of ['light', 'dark']) {
            await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme), theme);
            await compactReview.scrollIntoViewIfNeeded();
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `review overflow at ${width}`);
            await compactReview.screenshot({ path: path.join(fixtures, `review-${width}-${theme}.png`) });
        }
    }
    forceReason = true;
    await page.locator('[data-edit-review-button]').click();
    await page.waitForFunction(() => document.querySelector('#balance-edit-reason') === document.activeElement);
    assert.equal(await page.locator('[data-edit-reason-field]').isVisible(), true);
    await page.locator('#balance-edit-reason').fill('Correction');
    forceReason = false;
    await otherTurn.fill(originals[0]); await otherArea.fill(originals[1]);
    assert.equal(await compactReview.isVisible(), false);
    assert.equal(await page.locator('[data-edit-confirm]').isVisible(), false);
    assert.equal(await page.locator('#balance-edit-reason').inputValue(), 'Correction');
    await page.setViewportSize({ width: 1280, height: 900 });
    // Preserve recovery of drafts created before the catalogue input was removed.
    await page.evaluate(productId => {
        window.addEventListener('beforeunload', () => {
            const form = document.querySelector('#balance-editor');
            const key = `epi.balance.v1:${form.dataset.week}:${form.dataset.date}`;
            const draft = JSON.parse(sessionStorage.getItem(key));
            draft.products.push(productId); draft.added.push(productId);
            draft.edit.cells.push({ productId, area: 1, shift: 2, observed: '0', requested: '1,2' });
            sessionStorage.setItem(key, JSON.stringify(draft));
        }, { once: true });
    }, addedId);
    const added = page.locator(`[data-balance-product="${addedId}"] [data-edit-area="1"][data-edit-shift="2"]`);
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-workspace-status]').textContent.includes('recuperados'));
    assert.equal(await page.locator(`[data-balance-product="${addedId}"]`).count(), 1);
    assert.equal(await first.inputValue(), '5');
    assert.equal(await added.inputValue(), '1,2');
    assert.ok(restores > 0);
    assert.equal(await page.locator('#balance-edit-pin').inputValue(), '');
    await page.locator('[data-balance-day="2026-09-22"]').click();
    await page.waitForURL('**Through=2026-09-22**');
    assert.equal(await first.inputValue(), '0');
    await page.locator('[data-balance-day="2026-09-21"]').click();
    await page.waitForFunction(() => document.querySelector('[data-workspace-status]').textContent.includes('recuperados'));
    assert.equal(await first.inputValue(), '5');
    assert.equal(await added.inputValue(), '1,2');
    current = '2';
    await page.reload();
    await page.waitForSelector('[data-workspace-conflicts] button');
    assert.ok(await page.locator('[data-edit-review-button]').isDisabled());
    await page.locator('[data-workspace-conflicts] button').filter({ hasText: 'Mantener mi total' }).first().click();
    assert.equal(await first.inputValue(), '5');
    assert.equal(await first.getAttribute('data-original'), '2');
    // A second context gets a separate storage key; returning recovers the first context.
    await added.fill('4');
    const values = await page.evaluate(() => Object.values(sessionStorage).map(x => JSON.parse(x)));
    assert.ok(values.every(x => !('pin' in x.edit) && !('fingerprint' in x.edit)));
    for (const width of [360, 768, 900, 1280]) {
        await page.setViewportSize({ width, height: 900 });
        for (const theme of ['light', 'dark']) {
            await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme), theme);
            const viewport = page.locator('[data-balance-scroll]');
            const top = page.locator('[data-balance-scroll-top]');
            const overflow = await viewport.evaluate(el => el.scrollWidth > el.clientWidth + 1);
            await page.waitForFunction(overflow => document.querySelector('[data-balance-scroll-top]').hidden !== overflow, overflow);
            if (overflow) {
                await page.waitForFunction(() => {
                    const top = document.querySelector('[data-balance-scroll-top]');
                    const table = document.querySelector('[data-balance-scroll]');
                    return top.clientWidth === table.clientWidth && Math.abs(top.scrollWidth - table.scrollWidth) <= 1;
                });
                await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
                const limits = await page.evaluate(() => ['[data-balance-scroll-top]', '[data-balance-scroll]']
                    .map(selector => { const el = document.querySelector(selector); return el.scrollWidth - el.clientWidth; }));
                assert.ok(Math.abs(limits[0] - limits[1]) <= 1, 'Both bars must reach the same last column');
                for (const selector of ['[data-balance-scroll-top]', '[data-balance-scroll]']) {
                    const positions = await page.evaluate(selector => {
                        const source = document.querySelector(selector);
                        const target = document.querySelector(selector === '[data-balance-scroll]' ? '[data-balance-scroll-top]' : '[data-balance-scroll]');
                        source.scrollLeft = 10;
                        source.dispatchEvent(new Event('scroll'));
                        source.scrollLeft = 40;
                        target.dispatchEvent(new Event('scroll'));
                        const current = source.scrollLeft;
                        source.dispatchEvent(new Event('scroll'));
                        return [current, target.scrollLeft];
                    }, selector);
                    assert.deepEqual(positions, [40, 40], 'A delayed mirrored event must not rewind either bar');
                    await page.waitForTimeout(50);
                }
                await top.evaluate(el => { el.scrollLeft = el.scrollWidth; });
                await page.waitForTimeout(100);
                const edge = await page.evaluate(() => ['[data-balance-scroll-top]', '[data-balance-scroll]']
                    .map(s => { const e = document.querySelector(s); return [e.scrollLeft, e.scrollWidth, e.clientWidth]; }));
                assert.ok(Math.abs(edge[1][0] - (edge[1][1] - edge[1][2])) <= 1, `right edge ${width} ${theme}: ${JSON.stringify(edge)}`);
                await viewport.evaluate(el => { el.scrollLeft = 0; });
                await page.waitForFunction(() => document.querySelector('[data-balance-scroll-top]').scrollLeft === 0);
                if (width === 1280 && theme === 'light') {
                    for (const bar of [top, viewport]) {
                        await top.evaluate(el => { el.scrollLeft = 0; });
                        await page.waitForFunction(() => document.querySelector('[data-balance-scroll]').scrollLeft === 0);
                        await bar.evaluate(async el => {
                            el.scrollIntoView({ block: 'end', behavior: 'instant' });
                            await new Promise(resolve => requestAnimationFrame(resolve));
                            await new Promise(resolve => requestAnimationFrame(resolve));
                        });
                        const rect = await bar.boundingBox();
                        // Drag each native thumb, then leave both bars untouched to catch bouncing.
                        await page.mouse.move(rect.x + 35, rect.y + rect.height - 7);
                        await page.mouse.down();
                        await page.mouse.move(rect.x + 210, rect.y + rect.height - 7, { steps: 20 });
                        await page.mouse.up();
                        await page.waitForTimeout(100);
                        const position = await bar.evaluate(el => el.scrollLeft);
                        assert.ok(position > 0, 'Native scrollbar dragging should move the table');
                        await page.waitForTimeout(250);
                        for (const synced of [top, viewport]) assert.ok(Math.abs(await synced.evaluate(el => el.scrollLeft) - position) <= 1, 'Native drag must settle without bouncing');
                    }
                }
            }
            await page.locator('#production-balance-table').scrollIntoViewIfNeeded();
            if (width < 900) assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `overflow at ${width}`);
            await page.locator('#production-balance-table').evaluate(el => el.scrollIntoView({ block: 'start' }));
            await page.screenshot({ path: path.join(fixtures, `balance-${width}-${theme}.png`) });
        }
    }
    const closing = page.locator('#week-close');
    await closing.locator('details').evaluateAll(elements => elements.forEach(el => { el.open = true; }));
    assert.equal(await closing.locator('th').getByText('Unidad', { exact: true }).count(), 0);
    assert.equal(await closing.locator('[data-label="Unidad"]').count(), 0);
    for (const section of ['next-week-heading', 'completion-heading', 'part-summary-heading'])
        assert.equal(await page.locator(`#${section} tfoot tr`).count(), 1, 'Closing totals must not split by unit');
    await closing.screenshot({ path: path.join(fixtures, 'weekly-close-no-units.png') });
    registered = true;
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-workspace-status]').textContent.includes('receipt'));
    assert.equal(await page.evaluate(() => Object.keys(sessionStorage).filter(x => x.startsWith('epi.balance.v1:')).length), 0);
    registered = false;
    current = '0';
    await first.fill('5');
    const retiredOperation = await page.evaluate(() => {
        const form = document.querySelector('#balance-editor');
        const key = `epi.balance.v1:${form.dataset.week}:${form.dataset.date}`;
        const draft = JSON.parse(sessionStorage.getItem(key));
        const operationId = draft.edit.operationId;
        window.addEventListener('beforeunload', () => {
            const draft = JSON.parse(sessionStorage.getItem(key));
            draft.edit.planChanges = [{ lineId: 'retired-line', observed: '30', requested: '40', expectedLineVersion: 1, expectedWeekVersion: 1 }];
            draft.edit.newPlans = [{ operationId: 'retired-plan', productId: draft.products[0], requested: '15', expectedWeekVersion: 1 }];
            sessionStorage.setItem(key, JSON.stringify(draft));
        }, { once: true });
        return operationId;
    });
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-workspace-status]').textContent.includes('Programa semanal'));
    assert.equal(await first.inputValue(), '5');
    assert.ok(await first.isEnabled());
    assert.equal(await page.locator('.production-weekly-plan input').count(), 0);
    const recovery = await page.evaluate(operationId => {
        const form = document.querySelector('#balance-editor');
        const key = `epi.balance.v1:${form.dataset.week}:${form.dataset.date}`;
        return { draft: JSON.parse(sessionStorage.getItem(key)), planning: JSON.parse(sessionStorage.getItem(`${key}:planning:${operationId}`)) };
    }, retiredOperation);
    assert.notEqual(recovery.draft.edit.operationId, retiredOperation);
    assert.deepEqual(recovery.draft.edit.planChanges, []);
    assert.deepEqual(recovery.draft.edit.newPlans, []);
    assert.equal(recovery.planning.planChanges[0].requested, '40');
    assert.equal(recovery.planning.newPlans[0].requested, '15');
    await page.locator('[data-edit-review-button]').click();
    await page.waitForFunction(() => !document.querySelector('[data-edit-review]').hidden);
    assert.equal(await page.locator('[data-edit-review] [data-review-summary]').innerText(), '1 producto · 1 cambio');
    await page.locator('[data-edit-discard]').click();
    await page.evaluate(() => {
        window.addEventListener('beforeunload', () => {
            const form = document.querySelector('#balance-editor');
            sessionStorage.setItem(`epi.balance.v1:${form.dataset.week}:${form.dataset.date}`, '{corrupt');
        }, { once: true });
    });
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-workspace-status]').textContent.includes('Conservamos el borrador'));
    assert.ok(await first.isDisabled());
    await page.locator('[data-edit-discard]').click();
    assert.ok(await first.isEnabled());
    await page.evaluate(() => { Storage.prototype.setItem = () => { throw new Error('blocked'); }; });
    await first.fill('8');
    let dialogs = 0; page.on('dialog', async dialog => { dialogs++; await dialog.dismiss(); });
    await page.locator('[data-balance-day="2026-09-22"]').click();
    assert.equal(dialogs, 1);
    assert.equal(await first.inputValue(), '8');
    assert.deepEqual(errors, []);
    console.log('PASS: read-only planning, legacy planning retained separately, production draft recovery, instant T1/T2 pending and undo, stable weekly section, invalid raw values, reload/day recovery, per-cell conflict, secret exclusion, receipt recovery, corrupt/blocked storage, 8 visual viewports/themes.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
