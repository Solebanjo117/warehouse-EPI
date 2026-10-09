// Razor fixtures from CoverageMetricLinkTests; no application or database required.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const output = path.join(root, 'artifacts/coverage-metrics');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');

(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        for (const language of ['es', 'en']) {
            for (const theme of ['light', 'dark']) {
                for (const width of [390, 900, 1280]) {
                    const context = await browser.newContext({ javaScriptEnabled: false, reducedMotion: 'reduce', viewport: { width, height: 1000 } });
                    const page = await context.newPage();
                    await page.route('http://coverage.test/**', async route => {
                        const url = new URL(route.request().url());
                        if (url.pathname === '/Reports/Inventory') {
                            const key = url.searchParams.get('coverageClass') || 'all';
                            return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `coverage-${language}-${key}.html`) });
                        }
                        const relative = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, '');
                        const file = path.resolve(assets, relative);
                        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile())
                            return route.fulfill({ path: file });
                        return route.fulfill({ status: 204 });
                    });
                    await page.goto('http://coverage.test/Reports/Inventory?coverageClass=critical');
                    await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme), theme);
                    const cards = page.locator('[data-coverage-class]');
                    assert.equal(await cards.count(), 6);
                    assert.equal(await page.locator('.coverage-selected').innerText(), language === 'es' ? 'Seleccionado' : 'Selected');
                    for (const card of await cards.all()) {
                        const box = await card.boundingBox();
                        assert.ok(box.height >= 44 && box.width >= 44);
                        const parent = await card.locator('..').boundingBox();
                        assert.ok(Math.abs(box.width - parent.width) <= 3, 'Whole card must be clickable');
                    }
                    const critical = page.locator('[data-coverage-class="critical"]');
                    await critical.focus();
                    assert.equal(await critical.evaluate(el => getComputedStyle(el).outlineStyle), 'solid');
                    await page.screenshot({ path: path.join(output, `${language}-${theme}-${width}.png`), fullPage: true });
                    await page.keyboard.press('Tab');
                    assert.equal(await page.evaluate(() => document.activeElement.dataset.coverageClass), 'low');
                    await page.keyboard.press('Enter');
                    await page.waitForURL('**coverageClass=low**');
                    assert.equal(await page.locator('#coverage-class').inputValue(), 'low');
                    await page.locator('[data-coverage-class="low"]').click();
                    await page.waitForURL('**coverageClass=low**');
                    await page.locator('[data-coverage-class="exhausted"]').click();
                    await page.waitForURL('**coverageClass=exhausted**');
                    await page.locator('[data-coverage-all]').click();
                    await page.waitForURL(url => !url.searchParams.get('coverageClass'));
                    const url = new URL(page.url());
                    for (const [key, value] of Object.entries({ view: 'coverage', period: '60', status: 'all', search: 'A&B', unitId: '1', pageNumber: '1' }))
                        assert.equal(url.searchParams.get(key), value);
                    assert.equal(await page.locator('#coverage-class').inputValue(), '');
                    assert.equal(await page.locator('.coverage-metric[aria-current]').count(), 0);
                    await context.close();
                }
            }
        }
        console.log('Coverage metrics: 12 ES/EN, light/dark and responsive scenarios passed without JavaScript.');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
