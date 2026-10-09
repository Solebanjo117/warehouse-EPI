// Razor fixtures from SupplierSheetRouteTests; no operational server or data.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cli = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cli('playwright');
const output = path.join(root, 'artifacts/ui/playwright/supplier-sheet');
const products = [0, 1].map(i => ({ id: `00000000-0000-0000-0000-00000000000${i}`, sku: `SKU-${i}`, description: 'Product description' }));
const longProducts = Array.from({ length: 12 }, (_, i) => ({
    id: `10000000-0000-0000-0000-${String(i).padStart(12, '0')}`,
    sku: `50-M160-T64-W${i + 16}`,
    description: 'White, 160 Mesh, 64 Thread, 16 Width. Descripción extensa para comprobar que cada sugerencia conserva su altura completa sin superponerse.'
}));
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        let fail = false;
        let language = 'es';
        let restored;
        await page.route('http://supplier.test/**', async route => {
            const url = new URL(route.request().url());
            if (url.pathname === '/Preferences/Language') {
                if (fail) return route.fulfill({ status: 503 });
                const body = route.request().postData();
                language = /name="language"\r?\n\r?\nen/.test(body) ? 'en' : 'es';
                return route.fulfill({ status: 200, body: 'ok' });
            }
            if (url.pathname === '/Operations/Labels/SupplierSheet' && url.searchParams.get('handler') === 'Restore') {
                restored = new URLSearchParams(route.request().postData());
                const fixture = restored.get('preview') === 'true' ? restored.get('AllProducts') === 'true' ? 'all' : 'preview' : 'initial';
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(output, `${fixture}-${language}.html`), 'utf8') });
            }
            if (url.pathname === '/Operations/Lookup') {
                if (fail) return route.fulfill({ status: 503 });
                if (url.searchParams.get('q') === 'slow') await new Promise(resolve => setTimeout(resolve, 700));
                const exact = url.searchParams.get('handler') === 'ResolveProduct';
                const items = url.searchParams.get('q') === 'none' ? [] : url.searchParams.get('q') === 't6' ? longProducts : products;
                return route.fulfill({ json: exact ? products[1] : items });
            }
            if (/^\/(initial|preview|multipage|all)-(es|en)$/.test(url.pathname))
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(output, url.pathname.slice(1) + '.html'), 'utf8') });
            const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
            const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
            return fs.existsSync(file) && fs.statSync(file).isFile() ? route.fulfill({ path: file }) : route.fulfill({ status: 204 });
        });
        for (const culture of ['es', 'en']) {
            const targetLanguage = culture === 'es' ? 'en' : 'es';
            await page.goto(`http://supplier.test/initial-${culture}`);
            assert.equal(await page.locator('html').getAttribute('lang'), culture);
            const input = page.locator('#supplier-product');
            const rows = page.locator('[data-supplier-selection] > li');
            const suggestions = page.locator('#supplier-results button');
            await page.locator('[data-all-products]').check();
            assert.equal(await page.locator('[data-generate-sheet]').isEnabled(), true);
            assert.equal(await input.isVisible(), false);
            await page.locator('[data-all-products]').uncheck();
            assert.equal(await page.locator('[data-generate-sheet]').isEnabled(), false);
            for (const width of [390, 800, 1440]) {
                await page.setViewportSize({ width, height: 1000 });
                for (const theme of ['light', 'dark']) {
                    await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(el => el.click());
                    await input.fill(''); await input.fill('t6');
                    await suggestions.nth(11).waitFor();
                    const metrics = await suggestions.evaluateAll(items => items.map(item => ({
                        height: item.getBoundingClientRect().height,
                        bottom: item.getBoundingClientRect().bottom,
                        textBottom: item.querySelector('small').getBoundingClientRect().bottom
                    })));
                    assert.ok(metrics.every(m => m.height >= 44 && m.textBottom <= m.bottom), 'Suggestion text must fit inside its own row');
                    for (let i = 0; i < 12; i++) await input.press('ArrowDown');
                    assert.ok(await page.locator('#supplier-results').evaluate(el => el.scrollTop > 0));
                    await page.screenshot({ path: path.join(output, `suggestions-${culture}-${width}-${theme}.png`), fullPage: true });
                    await input.press('Escape');
                }
            }
            await input.fill('SKU');
            await suggestions.first().waitFor();
            await input.press('ArrowDown'); await input.press('Enter');
            assert.equal(await rows.count(), 1);
            assert.equal(await input.inputValue(), '');
            await input.fill('SKU-1'); await input.press('Enter');
            await rows.nth(1).waitFor();
            await input.fill('SKU-1'); await input.press('Enter');
            await page.waitForTimeout(350);
            assert.equal(await rows.count(), 2);
            await input.fill('slow'); await page.waitForTimeout(300);
            await input.fill('none'); await page.waitForTimeout(900);
            assert.equal(await suggestions.count(), 0);
            fail = true;
            await input.fill('network'); await page.waitForTimeout(400);
            assert.equal(await rows.count(), 2);
            assert.equal(await input.inputValue(), 'network');
            fail = false;
            await input.fill('SKU'); await suggestions.first().waitFor();
            await input.press('Escape'); assert.equal(await suggestions.count(), 0);
            await page.locator('[data-remove-product]').first().click();
            assert.equal(await rows.count(), 1);
            await page.goto(`http://supplier.test/preview-${culture}`);
            assert.equal(await page.locator('[data-supplier-name]').inputValue(), 'Proveedor de prueba & Asociados');
            assert.ok((await page.locator('[data-printed-supplier]').textContent()).includes('Proveedor de prueba & Asociados'));
            await page.locator('[data-supplier-name]').fill('Otro proveedor');
            assert.equal(await page.locator('[data-sheet-preview]').count(), 0);
            await page.goto(`http://supplier.test/all-${culture}`);
            assert.equal(await page.locator('[data-all-products]').isChecked(), true);
            assert.equal(await page.locator('[data-sheet-row]').count(), 102);
            assert.equal(await page.locator('.supplier-sheet-table thead th').count(), 2);
            await page.emulateMedia({ media: 'print' });
            assert.equal(await page.locator('[data-sheet-row]:visible').count(), 102);
            assert.equal(await page.locator('html').evaluate(el => getComputedStyle(el).colorScheme), 'light');
            await page.pdf({ path: path.join(output, `all-products-${culture}.pdf`), preferCSSPageSize: true, printBackground: true });
            await page.emulateMedia({ media: 'screen' });
            await Promise.all([
                page.waitForURL('**/Operations/Labels/SupplierSheet?handler=Restore'),
                page.locator(`.app-language-control button[value="${targetLanguage}"]`).first().evaluate(el => el.click())
            ]);
            assert.equal(restored.get('AllProducts'), 'true');
            assert.equal(await page.locator('[data-sheet-row]').count(), 102);
            assert.equal(await page.locator('html').getAttribute('lang'), targetLanguage);
            await page.goto(`http://supplier.test/preview-${culture}`);
            for (const width of [800, 1024, 1440]) {
                await page.setViewportSize({ width, height: 1000 });
                for (const theme of ['light', 'dark', 'system']) {
                    await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(el => el.click());
                    await page.waitForTimeout(200);
                    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
                    await page.screenshot({ path: path.join(output, `${culture}-${width}-${theme}.png`), fullPage: true });
                }
            }
            await page.locator('[data-remove-product]').first().click();
            assert.equal(await page.locator('[data-sheet-preview]').count(), 0);
            await page.goto(`http://supplier.test/multipage-${culture}`);
            await page.emulateMedia({ media: 'print' });
            assert.equal(await page.locator('body').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 255)');
            assert.equal(await page.locator('[data-sheet-row]:visible').count(), 40);
            assert.equal(await page.locator('[data-supplier-sheet]').isVisible(), false);
            assert.equal(await page.locator('[data-printed-supplier]').isVisible(), true);
            const sizes = await page.locator('.supplier-barcode').evaluateAll(els => els.map(el => ({ barcode: el.getBoundingClientRect().width, cell: el.parentElement.clientWidth })));
            assert.ok(sizes.every(s => s.barcode <= s.cell));
            await page.pdf({ path: path.join(output, `supplier-sheet-${culture}.pdf`), preferCSSPageSize: true, printBackground: true });
            await page.emulateMedia({ media: 'screen' });
            await page.goto(`http://supplier.test/preview-${culture}`);
            const languageButton = page.locator(`.app-language-control button[value="${targetLanguage}"]`).first();
            fail = true;
            await languageButton.evaluate(el => el.click());
            await page.waitForTimeout(200);
            assert.equal(await page.locator('[data-sheet-preview]').count(), 1);
            assert.equal(await page.locator('[data-supplier-name]').inputValue(), 'Proveedor de prueba & Asociados');
            fail = false;
            await Promise.all([
                page.waitForURL('**/Operations/Labels/SupplierSheet?handler=Restore'),
                languageButton.evaluate(el => el.click())
            ]);
            assert.equal(restored.get('preview'), 'true');
            assert.equal(restored.getAll('ProductIds').length, 2);
            assert.equal(restored.get('SupplierName'), 'Proveedor de prueba & Asociados');
            assert.equal(await page.locator('html').getAttribute('lang'), targetLanguage);
            assert.equal(await page.locator('[data-sheet-preview]').count(), 1);
            await page.goto(`http://supplier.test/preview-${culture}`);
            await page.locator('[data-supplier-name]').fill('Borrador proveedor');
            await Promise.all([
                page.waitForURL('**/Operations/Labels/SupplierSheet?handler=Restore'),
                page.locator(`.app-language-control button[value="${targetLanguage}"]`).first().evaluate(el => el.click())
            ]);
            assert.equal(restored.get('preview'), 'false');
            assert.equal(restored.get('SupplierName'), 'Borrador proveedor');
            assert.equal(await page.locator('[data-sheet-preview]').count(), 0);
        }
        assert.deepEqual(errors, []);
        console.log('PASS: ES/EN, suggestions, keyboard/HID, duplicates, stale responses, network, removal, preview invalidation, responsive themes and multipage PDF.');
    } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
