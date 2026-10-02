// Rendered Razor fixtures; no live app, credentials, or production database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const toolRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = toolRequire('playwright');
const directory = path.join(root, 'artifacts/validation/manual-backups');
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        async function selectTheme(theme, width) {
            if (!await page.locator(`[data-theme-choice="${theme}"]`).first().isVisible()) {
                if (await page.locator('[data-nav-toggle]').isVisible()) await page.locator('[data-nav-toggle]').click();
                if (!await page.locator('.app-account-menu').evaluate(element => element.open)) await page.locator('.app-account-menu summary').click();
            }
            await page.locator(`[data-theme-choice="${theme}"]`).first().click();
            assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme);
            await page.locator('.app-account-menu summary').click();
            await page.keyboard.press('Escape');
            if (width < 900) await page.waitForFunction(() => document.querySelector('.app-sidebar').getBoundingClientRect().right <= 1);
        }
        let completed = false;
        let progressFailures = 0;
        await page.route('http://backups.test/**', async route => {
            const url = new URL(route.request().url());
            if (url.searchParams.get('handler') === 'Status') {
                if (url.pathname === '/BackupRestoreProgress' && progressFailures++ === 0)
                    return route.fulfill({ status: 503, body: 'restarting fixture' });
                completed = true;
                return route.fulfill({ contentType: 'application/json', body: '{"running":false}' });
            }
            if (url.pathname.startsWith('/fixtures/')) {
                let name = path.basename(url.pathname);
                if (completed && name === 'running.html') name = 'ready.html';
                if (completed && name === 'restore-progress.html') name = 'restore-completed.html';
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(directory, 'fixtures', name), 'utf8') });
            }
            if (url.pathname === '/BackupRestoreProgress') return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(directory, 'fixtures', 'restore-completed.html'), 'utf8') });
            const relative = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}\.(css|js)$/, '.$1').replace(/^\//, '');
            const file = path.resolve(root, 'src/WarehouseEPI.Web/wwwroot', relative);
            if (!file.startsWith(path.join(root, 'src/WarehouseEPI.Web/wwwroot') + path.sep)) return route.fulfill({ status: 404 });
            if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
            return route.fulfill({ status: 204 });
        });
        for (const width of [360, 768, 899, 900, 1199, 1200, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            await page.goto('http://backups.test/fixtures/ready.html');
            assert.equal(await page.locator('.backup-board h1').innerText(), 'Respaldos');
            assert.equal(await page.locator('#backup-password').inputValue(), '');
            assert.equal(await page.locator('#backup-confirmation').inputValue(), '');
            assert.equal(await page.locator('#backup-password').isEnabled(), true);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, `Overflow at ${width}px`);
            for (const button of await page.locator('.backup-board .btn').all()) assert.ok((await button.boundingBox()).height >= 44);
            await page.locator('#backup-password').focus();
            await page.keyboard.press('Tab');
            assert.equal(await page.locator('#backup-confirmation').evaluate(element => element === document.activeElement), true);
            for (const theme of ['light', 'dark']) {
                if (!await page.locator(`[data-theme-choice="${theme}"]`).first().isVisible()) {
                    if (await page.locator('[data-nav-toggle]').isVisible()) await page.locator('[data-nav-toggle]').click();
                    if (!await page.locator('.app-account-menu').evaluate(element => element.open)) await page.locator('.app-account-menu summary').click();
                }
                await page.locator(`[data-theme-choice="${theme}"]`).first().click();
                assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme);
                await page.locator('.app-account-menu summary').click();
                await page.keyboard.press('Escape');
                assert.equal(await page.locator('body').evaluate(element => element.classList.contains('nav-open')), false);
                if (width < 900) await page.waitForFunction(() => document.querySelector('.app-sidebar').getBoundingClientRect().right <= 1);
                if ([360, 1440].includes(width)) await page.screenshot({ path: path.join(directory, `backup-${width}-${theme}.png`), fullPage: true });
            }
        }
        await page.emulateMedia({ colorScheme: 'dark' });
        await page.locator('.app-account-menu summary').click();
        await page.locator('[data-theme-choice="system"]').click();
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), 'dark');
        await page.emulateMedia({ colorScheme: 'light' });
        await page.waitForFunction(() => document.documentElement.dataset.bsTheme === 'light');
        await page.goto('http://backups.test/fixtures/running.html');
        assert.equal(await page.locator('#backup-password').isDisabled(), true);
        await page.waitForSelector('.backup-board[data-backup-running="false"]', { timeout: 12000 });
        assert.equal(completed, true);
        assert.equal(await page.locator('.backup-list a').count(), 1);
        for (const width of [360, 768, 899, 900, 1199, 1200, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            for (const language of ['', '-en']) {
                await page.goto(`http://backups.test/fixtures/restore-review${language}.html`);
                assert.equal(await page.locator('#restore-pin').inputValue(), '');
                assert.equal(await page.locator('#restore-password').inputValue(), '');
                assert.equal(await page.locator('#restore-confirmation').inputValue(), '');
                assert.equal(await page.locator('#restore-file').getAttribute('accept'), '.webackup');
                assert.equal(await page.locator('#restore-accepted').isChecked(), false);
                assert.equal(await page.locator('button.btn-danger').isEnabled(), true);
                await page.locator('#restore-pin').focus(); await page.keyboard.press('Tab');
                assert.equal(await page.locator('#restore-confirmation').evaluate(element => element === document.activeElement), true);
                for (const theme of ['light', 'dark']) {
                    await selectTheme(theme, width);
                    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, `Restore overflow: ${width}, ${language}, ${theme}`);
                    for (const button of await page.locator('.backup-board .btn').all()) assert.ok((await button.boundingBox()).height >= 44);
                    if (!language && [360, 1440].includes(width)) await page.screenshot({ path: path.join(directory, `restore-${width}-${theme}.png`), fullPage: true });
                }
            }
        }
        completed = false;
        await page.goto('http://backups.test/fixtures/restore-progress.html');
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), 'dark');
        await page.waitForSelector('.backup-board[data-backup-running="false"]', { timeout: 16000 });
        assert.match(await page.locator('h2').innerText(), /Importación completada/);
        assert.equal(await page.locator('.btn-primary').getAttribute('href'), '/Admin/Login');
        assert.deepEqual(errors, []);
        console.log('Backup and import UI: 7 widths, ES/EN, light/dark/system, keyboard, empty secrets, progress and completion verified.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
