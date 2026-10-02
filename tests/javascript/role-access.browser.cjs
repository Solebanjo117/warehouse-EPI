// Uses rendered TestServer fixtures, without starting the installed application.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const fixtures = path.join(root, 'artifacts/role-access/fixtures');
const output = path.join(root, 'artifacts/role-access/browser');
fs.mkdirSync(output, { recursive: true });
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const context = await browser.newContext();
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.route('http://roles.test/**', async route => {
            const url = new URL(route.request().url());
            if (url.searchParams.get('handler') === 'Snapshot') return route.fulfill({ json: { items: [], totalVisible: 0, generatedAtLocal: "2026-10-02T12:00:00-05:00" } });
            if (url.searchParams.has('handler')) return route.fulfill({ json: [] });
            const fixture = path.join(fixtures, path.basename(url.pathname));
            if (url.pathname.endsWith('.html') && fs.existsSync(fixture)) return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(fixture, 'utf8') });
            const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
            const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
            if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
            return route.fulfill({ status: 204 });
        });
        let checks = 0;
        for (const width of [768, 1024, 1366]) {
            await page.setViewportSize({ width, height: 1024 });
            for (const name of ['public-home', 'notifications', 'schedule', 'products', 'warehouse-role-warning', 'production-role-warning']) {
                await page.goto(`http://roles.test/${name}.html`);
                await page.waitForLoadState('networkidle');
                if (width >= 900) await page.waitForFunction(expected => document.querySelector('.app-content').getBoundingClientRect().x >= expected - 1, width >= 1200 ? 250 : 72);
                await page.keyboard.press('Tab');
                assert(await page.evaluate(() => document.activeElement !== document.body), 'Keyboard focus must reach a control');
                assert.equal(await page.locator('main.app-main').count(), 1);
                assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${name} overflow at ${width}`);
                if (name === 'warehouse-role-warning') {
                    assert.equal(await page.locator('[name="Input.Pin"]').inputValue(), '');
                    assert.equal(await page.locator('[name="Input.Quantity"]').inputValue(), '7');
                    assert.match(await page.locator('[data-operation-errors]').innerText(), /Este NIP pertenece a Producción/);
                }
                if (name === 'production-role-warning') {
                    assert.equal(await page.locator('[name="Group.Pin"]').inputValue(), '');
                    assert.match(await page.locator('[data-production-errors]').innerText(), /Este NIP pertenece a Operador/);
                    assert.equal(await page.locator('[name="Group.Rows[0].Quantity"]').inputValue(), '7');
                }
                if (name === 'schedule') assert.equal(await page.locator('main form[method="post"]').count(), 0);
                if (name === 'products') assert.equal(await page.locator('main a[href*="/Products/Edit"], main a[href*="/Products/Create"]').count(), 0);
                await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true, animations: 'disabled' });
                checks++;
            }
        }
        await page.goto('http://roles.test/schedule.html');
        await page.waitForLoadState('networkidle');
        await page.locator('.app-account-menu summary').click();
        for (const theme of ['dark', 'light', 'system']) {
            await page.locator(`[data-theme-choice="${theme}"]`).click();
            const expected = theme === 'system' ? 'light' : theme;
            assert.equal(await page.locator('html').getAttribute('data-bs-theme'), expected);
            await page.screenshot({ path: path.join(output, `schedule-${theme}.png`), fullPage: true, animations: 'disabled' });
        }
        assert.deepEqual(errors, []);
        console.log(`${checks} rendered fixture/viewport checks passed; PIN fields empty, input values retained, no page overflow or script errors.`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
