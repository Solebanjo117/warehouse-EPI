// Browser verification of HTML rendered by ProductionDailyReportRouteTests; no live database or app service.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const directory = path.join(root, 'artifacts/production-reports');
const fixtures = path.join(directory, 'fixtures');
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
    try {
        const page = await browser.newPage();
        const errors = []; page.on('pageerror', error => errors.push(error.message));
        await page.route('http://reports.test/**', async route => {
            const url = new URL(route.request().url());
            if (url.pathname.startsWith('/fixtures/')) {
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, path.basename(url.pathname)), 'utf8') });
            }
            if (url.pathname === '/Operations/Production') {
                const date = url.searchParams.get('Through');
                const offset = { '2026-09-21': 0, '2026-09-22': 1, '2026-09-27': 6 }[date];
                const name = url.searchParams.get('ReportKind') === 'daily' ? 'navigation-summary' : `navigation-${offset}`;
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(fixtures, name + '.html'), 'utf8') });
            }
            const relative = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, '');
            const file = path.resolve(root, 'src/WarehouseEPI.Web/wwwroot', relative);
            if (!file.startsWith(path.join(root, 'src/WarehouseEPI.Web/wwwroot') + path.sep)) return route.fulfill({ status: 404 });
            if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
            return route.fulfill({ status: 204 });
        });
        for (const name of ['daily', 'products']) {
            for (const width of [360, 768, 899, 900, 1199, 1200, 1440]) {
                await page.setViewportSize({ width, height: 1000 });
                await page.goto(`http://reports.test/fixtures/${name}.html`);
                assert.equal(await page.locator('.report-table').count(), 1);
                const days = page.locator('[data-report-days]');
                assert.equal(await days.count(), name === 'products' ? 1 : 0);
                if (name === 'products') {
                    assert.equal(await days.locator('[data-report-day]').count(), 7);
                    assert.equal(await days.locator('[aria-current="date"]').count(), 1);
                    for (const link of await days.locator('a').all()) {
                        assert.ok((await link.boundingBox()).height >= 44);
                    }
                    await days.locator('[data-report-back]').focus();
                    await page.keyboard.press('Tab');
                    assert.equal(await days.locator('[data-report-day]').first().evaluate(el => el === document.activeElement), true);
                    if (width === 360) {
                        const lines = await days.locator('[data-report-day]').evaluateAll(links => new Set(links.map(link => Math.round(link.getBoundingClientRect().top))).size);
                        assert.ok(lines > 1, 'Day links must wrap on mobile');
                    }
                }
                if (name === 'daily') {
                    assert.equal(await page.locator('.report-table tbody tr').count(), 7);
                }
                assert.equal(await page.locator('.report-table thead').getByText('Unidad', { exact: true }).count(), 0);
                assert.equal(await page.locator('.report-table thead').getByText('Por conciliar', { exact: true }).count(), 0);
                assert.equal(await page.locator('.report-table thead').getByText('T1', { exact: true }).count(), 3);
                assert.equal(await page.locator('.report-table thead').getByText('T2', { exact: true }).count(), 3);
                if (width === 768) {
                    await page.locator('[data-report-kind]').selectOption('products');
                    await page.locator('[data-report-period]').selectOption('day');
                    assert.equal(await page.locator('[data-report-date]').isVisible(), true);
                    await page.locator('[data-report-period]').selectOption('week');
                    assert.equal(await page.locator('[data-report-date]').isVisible(), false);
                    await page.locator('[data-report-period]').selectOption('day');
                    await page.locator('[data-report-kind]').selectOption(name === 'daily' ? 'daily' : 'products');
                }
                const dimensions = await page.evaluate(() => ({ viewport: innerWidth, width: document.documentElement.scrollWidth }));
                assert.ok(dimensions.width <= dimensions.viewport + 1, `Page overflow: ${name} ${width}`);
                const scroll = page.locator('.report-scroll');
                const topScroll = page.locator('[data-report-scroll-top]');
                const overflowing = await scroll.evaluate(el => el.scrollWidth > el.clientWidth + 1);
                if (overflowing) {
                    await topScroll.waitFor({ state: 'visible' });
                    const forward = await topScroll.evaluate(el => {
                        el.scrollLeft = Math.min(200, el.scrollWidth - el.clientWidth); return el.scrollLeft;
                    });
                    await page.waitForFunction(value => Math.abs(document.querySelector('[data-report-scroll]').scrollLeft - value) <= 1, forward);
                    const back = await scroll.evaluate(el => { el.scrollLeft = el.scrollLeft / 2; return el.scrollLeft; });
                    await page.waitForFunction(value => Math.abs(document.querySelector('[data-report-scroll-top]').scrollLeft - value) <= 1, back);
                    // A delayed mirror event must not rewind a newer movement on the active bar.
                    for (const selector of ['[data-report-scroll-top]', '[data-report-scroll]']) {
                        const positions = await page.evaluate(selector => {
                            const source = document.querySelector(selector);
                            const target = document.querySelector(selector === '[data-report-scroll]' ? '[data-report-scroll-top]' : '[data-report-scroll]');
                            source.scrollLeft = 10;
                            source.dispatchEvent(new Event('scroll'));
                            source.scrollLeft = 40;
                            target.dispatchEvent(new Event('scroll'));
                            const active = source.scrollLeft;
                            source.dispatchEvent(new Event('scroll'));
                            return [active, target.scrollLeft];
                        }, selector);
                        assert.deepEqual(positions, [40, 40], `Scroll bounced: ${name} ${width} ${selector}`);
                        await page.waitForTimeout(50);
                    }
                } else assert.equal(await topScroll.isVisible(), false);
                if (name === 'products') assert.equal(await page.locator('.report-table thead').getByText('Descripción', { exact: true }).count(), 0);
                await scroll.focus();
                await page.keyboard.press('ArrowRight');
                assert.equal(await scroll.evaluate(el => el === document.activeElement), true);
                await page.waitForTimeout(250); // Finish the native keyboard scroll animation before taking evidence.
                for (const theme of ['light', 'dark']) {
                    await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme), theme);
                    await page.evaluate(() => {
                        window.scrollTo(0, 0);
                        document.querySelector('.report-scroll').scrollLeft = 0;
                        document.querySelector('[data-report-scroll-top]').scrollLeft = 0;
                    });
                    if ([360, 768, 1440].includes(width)) await page.screenshot({ path: path.join(directory, `${name}-${width}-${theme}.png`), animations: 'disabled' });
                    if (name === 'products' && [360, 768, 1440].includes(width)) {
                        await days.screenshot({ path: path.join(directory, `days-${width}-${theme}.png`), animations: 'disabled' });
                    }
                }
            }
        }
        await page.goto('http://reports.test/fixtures/navigation-summary.html');
        await page.locator('.report-table tbody a').first().click();
        for (const date of ['2026-09-21', '2026-09-22', '2026-09-27']) {
            if (date !== '2026-09-21') {
                await page.locator(`[data-report-day="${date}"]`).focus();
                await page.keyboard.press('Enter');
            }
            await page.waitForURL(url => url.searchParams.get('Through') === date);
            const query = new URL(page.url()).searchParams;
            assert.equal(query.get('Sku'), 'RECOVER');
            assert.equal(query.get('Reference'), 'REF & 1');
            assert.equal(query.get('Area'), 'Cutting');
            assert.equal(query.get('ReportPage'), '1');
            assert.equal(await page.locator(`[data-report-day="${date}"][aria-current="date"]`).count(), 1);
        }
        await page.locator('[data-report-back]').click();
        await page.waitForURL(url => url.searchParams.get('ReportKind') === 'daily');
        assert.equal(new URL(page.url()).searchParams.get('Through'), '2026-09-27');
        assert.equal(await page.locator('[data-report-days]').count(), 0);
        await page.goto('http://reports.test/fixtures/empty.html');
        assert.equal(await page.locator('[data-report-day]').count(), 7);
        assert.equal(await page.locator('[data-report-back]').isVisible(), true);
        await page.goto('http://reports.test/fixtures/week.html');
        assert.equal(await page.locator('[data-report-days]').count(), 0);
        await page.goto('http://reports.test/fixtures/print.html');
        assert.equal(await page.locator('.report-table tbody tr').count(), 28);
        assert.equal(await page.locator('nav').count(), 0);
        await page.evaluate(() => { window.print = () => { window.reportPrinted = true; }; });
        await page.locator('[data-report-print]').click();
        assert.equal(await page.evaluate(() => window.reportPrinted), true);
        await page.emulateMedia({ media: 'print' });
        assert.equal(await page.locator('[data-report-print]').isVisible(), false);
        assert.equal(await page.locator('.report-scroll').evaluate(el => getComputedStyle(el).overflowX), 'visible');
        await page.pdf({ path: path.join(directory, 'products-print.pdf'), preferCSSPageSize: true, printBackground: true });
        await page.screenshot({ path: path.join(directory, 'print.png'), fullPage: true });
        assert.deepEqual(errors, []);
        console.log('Reports: daily navigation, filters, empty/week views, 7 widths, 2 themes, keyboard and print passed.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
