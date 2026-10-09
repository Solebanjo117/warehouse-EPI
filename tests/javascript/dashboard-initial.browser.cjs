// Uses Razor fixtures exported by DashboardRouteTests, without a running application.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const output = path.join(root, 'artifacts/dashboard-initial');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');

(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        for (const language of ['es', 'en']) {
            for (const entries of [3, 0]) {
                for (const theme of ['light', 'dark']) {
                    const page = await browser.newPage({ viewport: { width: theme === 'dark' ? 768 : 1440, height: 1000 } });
                    const errors = [];
                    const requests = [];
                    let fail = false;
                    let slowCalendar = false;
                    page.on('pageerror', error => errors.push(error.message));
                    await page.route('http://dashboard.test/**', async route => {
                        const url = new URL(route.request().url());
                        if (url.pathname === '/') {
                            return route.fulfill({ contentType: 'text/html', path: path.join(output, 'fixtures', `dashboard-${language}-${entries}.html`) });
                        }
                        if (url.pathname === '/Reports/Dashboard') {
                            requests.push(url.href);
                            if (fail) return route.fulfill({ status: 503 });
                            const handler = url.searchParams.get('handler');
                            const file = handler === 'Metrics' ? `metrics-${entries}.json` : handler === 'Activity' ? `activity-${entries}.json` :
                                `products-${entries}-${url.searchParams.get('days')}-${url.searchParams.get('pageNumber')}.json`;
                            if (handler === 'Activity' && slowCalendar) await new Promise(resolve => setTimeout(resolve, 400));
                            return route.fulfill({ contentType: 'application/json', path: path.join(output, 'fixtures', file) });
                        }
                        const relative = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, '');
                        const file = path.resolve(assets, relative);
                        if (file.startsWith(assets + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) {
                            return route.fulfill({ path: file });
                        }
                        return route.fulfill({ status: 204 });
                    });
                    await page.goto('http://dashboard.test/');
                    assert.equal(await page.locator('html').getAttribute('lang'), language);
                    // Assert initial rendering before any theme mutation or range/refresh click.
                    const state = await page.evaluate(() => {
                        const canvas = document.querySelector('[data-dashboard-chart]');
                        const chart = Chart.getChart(canvas);
                        return { labels: chart.data.labels, series: chart.data.datasets.map(item => item.data),
                            detail: document.querySelector('[data-dashboard-detail-total]').textContent,
                            fallbackHidden: document.querySelector('[data-dashboard-fallback]').hidden };
                    });
                    assert.equal(state.labels.length, 14);
                    assert.ok(state.labels.every(Boolean));
                    state.series.forEach((values, index) => assert.deepEqual(values, Array(14).fill(entries * (index + 1))));
                    assert.equal(state.detail.trim(), String(entries * 10));
                    assert.equal(state.fallbackHidden, true);
                    assert.equal(requests.length, 0, 'Initial chart must not need a metrics request');
                    await page.evaluate(theme => document.documentElement.setAttribute('data-bs-theme', theme), theme);
                    const ready = () => page.waitForFunction(() => document.querySelector('[data-dashboard-chart-shell]').getAttribute('aria-busy') !== 'true');
                    const total = async () => Number((await page.locator('[data-dashboard-total]').textContent()).replace(/[^0-9]/g, ''));
                    await page.locator('[data-dashboard-view="line"]').click();
                    assert.deepEqual(await page.evaluate(() => { const chart = Chart.getChart(document.querySelector('[data-dashboard-chart]')); return [chart.config.type, chart.options.scales.y.stacked, chart.data.datasets[0].tension]; }), ['line', false, 0]);
                    await page.locator('[data-dashboard-range="7"]').click();
                    await ready();
                    assert.equal(await page.evaluate(() => Chart.getChart(document.querySelector('canvas[data-dashboard-chart]')).data.labels.length), 7);
                    assert.equal(await total(), entries * 70);
                    await page.locator('[data-dashboard-range="14"]').click();
                    await ready();
                    await page.locator('[data-dashboard-chart]').focus();
                    await page.keyboard.press('ArrowLeft');
                    assert.ok((await page.locator('[data-dashboard-detail-link]').getAttribute('href')).includes('2026-10-06'));
                    if (entries) assert.deepEqual(await page.evaluate(() => {
                        const chart = Chart.getChart(document.querySelector('[data-dashboard-chart]'));
                        const point = chart.getDatasetMeta(0).data[2];
                        return [...chart.ctx.getImageData(point.x * chart.currentDevicePixelRatio, point.y * chart.currentDevicePixelRatio, 1, 1).data];
                    }), [22, 132, 91, 255], 'The receipt line must actually paint, not only contain data');
                    await page.locator('[data-dashboard-chart]').screenshot({ path: path.join(output, `${language}-${entries}-${theme}-lines.png`) });
                    slowCalendar = true;
                    await page.locator('[data-dashboard-view="calendar"]').click();
                    await page.locator('[data-dashboard-view="line"]').click();
                    await ready();
                    assert.equal(await page.locator('[data-dashboard-view="line"]').getAttribute('aria-pressed'), 'true');
                    slowCalendar = false;
                    await page.locator('[data-dashboard-view="calendar"]').click();
                    await ready();
                    assert.equal(await page.locator('[data-calendar-date]').count(), 90);
                    assert.equal(await total(), entries * 900);
                    assert.equal(await page.locator('[data-dashboard-day-rows] tr').count(), 90);
                    await page.locator('[data-calendar-date]').first().click();
                    assert.equal(await page.locator('[data-calendar-date]').first().getAttribute('aria-pressed'), 'true');
                    await page.keyboard.press('ArrowRight');
                    assert.equal(await page.locator('[data-calendar-date]').nth(7).getAttribute('aria-pressed'), 'true');
                    if (entries) {
                        assert.equal(await page.locator('.dashboard-product').count(), 10);
                        await page.locator('.dashboard-product summary').first().click();
                        assert.equal(await page.locator('.dashboard-product[open]').count(), 1);
                        assert.ok((await page.locator('.dashboard-product[open] a').getAttribute('href')).includes('sku=DASH-00'));
                        await page.locator('[data-dashboard-product-page="2"]').click();
                        await ready();
                        assert.equal(await page.locator('.dashboard-product').count(), 2);
                        await page.locator('[data-dashboard-product-page="1"]').click();
                        await ready();
                    }
                    await page.evaluate(() => window.scrollTo({ top: window.scrollY + document.querySelector('[data-dashboard-calendar]').getBoundingClientRect().top - 100, behavior: 'instant' }));
                    await page.locator('[data-dashboard-calendar]').screenshot({ path: path.join(output, `${language}-${entries}-${theme}-calendar.png`) });
                    await page.locator('[data-dashboard-view="bar"]').click();
                    await ready();
                    assert.ok((await page.locator('[data-dashboard-detail-link]').getAttribute('href')).includes('2026-10-07'));
                    fail = true;
                    await page.locator('[data-dashboard-refresh]').click();
                    await page.waitForFunction(() => document.querySelector('[data-dashboard-status]').classList.contains('is-stale'));
                    assert.equal(await total(), entries * 140);
                    assert.equal(await page.locator('[data-dashboard-retry]').isVisible(), true);
                    fail = false;
                    await page.locator('[data-dashboard-retry]').click();
                    await ready();
                    assert.equal(await page.locator('[data-dashboard-retry]').isVisible(), false);
                    const requestCount = requests.length;
                    await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
                    await page.locator('[data-dashboard-refresh]').click();
                    assert.equal(requests.length, requestCount);
                    await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')); });
                    await ready();
                    assert.ok(requests.length > requestCount);
                    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
                    if (language === 'es' && entries === 3 && theme === 'light') {
                        for (const width of [360, 899, 900, 1199, 1200]) {
                            await page.setViewportSize({ width, height: 1000 });
                            await page.locator('[data-dashboard-view="calendar"]').click(); await ready();
                            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, `No page overflow at ${width}px`);
                            assert.ok((await page.locator('[data-calendar-date]').first().boundingBox()).height >= 44);
                            await page.locator('[data-dashboard-view="bar"]').click(); await ready();
                        }
                    }
                    assert.deepEqual(errors, []);
                    await page.close();
                }
            }
        }
        console.log('PASS: 8 cases: initial load, bars/lines/calendar, 7/14/90 days, pagination, keyboard, stale requests and network recovery.');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
