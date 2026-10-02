// Real Razor markup rendered by ProductionScheduleActionsTests; all HTTP writes are mocked.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packageRoot = path.join(root, 'tools/playwright/node_modules/.pnpm');
const playwright = fs.readdirSync(packageRoot).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packageRoot, playwright, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_SCHEDULE_FIXTURES || path.join(root, 'artifacts/schedule-ux/fixtures');
const read = (name, language = 'es') => fs.readFileSync(path.join(fixtures, `${name}-${language}.html`), 'utf8');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  const errors = [], posts = [];
  let screen = 'draft', language = 'es';
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.accept());
  await page.route('http://schedule.test/**', async route => {
    const url = new URL(route.request().url());
    const handler = url.searchParams.get('handler');
    if (route.request().method() === 'POST') {
      posts.push(handler);
      if (handler === 'WorkspaceSave') return route.fulfill({ json: { saved: true, count: 1 } });
      screen = handler === 'Close' ? 'closed' : 'open';
      return route.fulfill({ contentType: 'text/html', body: read(screen, language) });
    }
    if (handler === 'WorkspaceOpenings') return route.fulfill({ json: [] });
    if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
    if (url.pathname === '/Admin/Production/Schedule') {
      const review = url.searchParams.get('View') === 'review';
      let html = read(review && screen === 'draft' ? 'review' : screen, language);
      if (['close', 'reopen'].includes(url.searchParams.get('ActionPanel')))
        html = html.replace('data-auto-show="false"', 'data-auto-show="true"');
      return route.fulfill({ contentType: 'text/html', body: html });
    }
    const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
    const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
    if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
    return route.fulfill({ status: 204 });
  });
  try {
    await page.goto('http://schedule.test/Admin/Production/Schedule');
    await page.waitForFunction(() => window.ProductionWeekWorkspace);
    const quantity = page.locator('input.production-week-workspace__quantity').first();
    await quantity.fill('25');
    await page.locator('[data-workspace-copy-toggle]').click();
    assert.equal(await quantity.inputValue(), '25');
    assert.equal(await page.locator('[data-workspace-search]').isVisible(), true);
    assert.equal(await page.locator('[data-workspace-copy-panel]').isVisible(), true);
    assert.equal(await page.locator('[data-workspace-copy]').isEnabled(), false);
    await page.locator('[data-workspace-copy-toggle]').click();
    assert.equal(await quantity.inputValue(), '25');
    await page.locator('[data-schedule-open-review]').click();
    await page.locator('[data-workspace-review-panel]').waitFor({ state: 'visible' });
    assert.equal(await page.locator('[data-workspace-save]').innerText(), 'Guardar y continuar a apertura');
    assert.equal(posts.length, 0);
    await Promise.all([page.waitForURL('**View=review*'), page.locator('[data-workspace-save]').click()]);
    await page.locator('[data-schedule-publish-confirm]').waitFor();
    assert.equal(await page.locator('[data-schedule-publish-confirm]').isEnabled(), true);
    assert.deepEqual(posts, ['WorkspaceSave']);
    await page.evaluate(() => {
      const context = document.querySelector('[data-schedule-context]');
      const key = `warehouse-epi:schedule:${context.dataset.userId}:${context.dataset.weekId}`;
      localStorage.setItem(key, JSON.stringify({ pasteText: 'SKU\tLunes\nFG-100\t20' }));
    });
    await page.reload();
    assert.equal(await page.locator('[data-schedule-publish-confirm]').isEnabled(), true);
    assert.equal(await page.locator('[name="Publish.Pin"]').isEnabled(), true);
    assert.equal(await page.locator('[data-schedule-pending-warning]').isVisible(), false);
    await page.evaluate(() => localStorage.clear());
    screen = 'open';
    await page.goto('http://schedule.test/Admin/Production/Schedule?View=summary');
    await page.locator('[data-schedule-status-trigger]').click();
    await page.locator('#schedule-status-confirmation').waitFor({ state: 'visible' });
    await page.keyboard.press('Escape');
    await page.locator('#schedule-status-confirmation').waitFor({ state: 'hidden' });
    assert.equal(await page.locator('[data-schedule-status-trigger]').evaluate(button => button === document.activeElement), true);
    assert.deepEqual(posts, ['WorkspaceSave']);
    await page.locator('[data-schedule-status-trigger]').click();
    await page.locator('.modal-footer').getByRole('button', { name: 'Cancelar', exact: true }).click();
    await page.locator('#schedule-status-confirmation').waitFor({ state: 'hidden' });
    await page.goto('http://schedule.test/Admin/Production/Schedule?View=summary&ActionPanel=close');
    await page.locator('#schedule-status-confirmation').waitFor({ state: 'visible' });
    await page.locator('[data-schedule-confirm]').evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
    await page.getByRole('button', { name: 'Reabrir semana…', exact: true }).waitFor();
    assert.equal(posts.filter(handler => handler === 'Close').length, 1);
    await page.goto('http://schedule.test/Admin/Production/Schedule?View=summary&ActionPanel=reopen');
    await page.locator('#schedule-status-confirmation').waitFor({ state: 'visible' });
    await page.getByRole('button', { name: 'Confirmar reapertura', exact: true }).click();
    await page.getByRole('button', { name: 'Cerrar semana…', exact: true }).waitFor();
    assert.equal(posts.filter(handler => handler === 'Reopen').length, 1);
    screen = 'draft';
    await page.goto('http://schedule.test/Admin/Production/Schedule');
    const output = path.join(root, 'artifacts/schedule-ux/browser');
    fs.mkdirSync(output, { recursive: true });
    for (const width of [768, 899, 900, 1199, 1200, 1280]) {
      await page.setViewportSize({ width, height: 1024 });
      await page.goto('http://schedule.test/Admin/Production/Schedule');
      await page.waitForFunction(() => window.ProductionWeekWorkspace);
      await page.waitForFunction(() => innerWidth < 900 ||
        document.querySelector('.app-content').getBoundingClientRect().left >=
        document.getElementById('app-sidebar').getBoundingClientRect().right - 1);
      for (const theme of ['light', 'dark', 'system']) {
        await page.emulateMedia({ colorScheme: 'dark' });
        await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click());
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
        const layout = await page.locator('[data-schedule-open-review]').evaluate(button => ({
          height: button.getBoundingClientRect().height, overflow: document.documentElement.scrollWidth - innerWidth
        }));
        assert.ok(layout.height >= 44, `Touch target at ${width}/${theme}`);
        assert.ok(layout.overflow <= 1, `Page overflow at ${width}/${theme}: ${layout.overflow}`);
      }
      if ([768, 1280].includes(width)) await page.screenshot({ path: path.join(output, `draft-${width}-dark.png`), fullPage: true, animations: 'disabled' });
    }
    language = 'en'; screen = 'blocked';
    await page.goto('http://schedule.test/Admin/Production/Schedule?View=review');
    assert.equal(await page.getByRole('button', { name: 'Confirm opening', exact: true }).isEnabled(), false);
    assert.equal(await page.getByRole('link', { name: 'Configure areas and shifts', exact: true }).count(), 1);
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ posts, errors, widths: [768,899,900,1199,1200,1280], themes: ['light','dark','system'] }, null, 2));
    console.log('Schedule browser checks passed: save/open, pending preparation, close/reopen, keyboard, 6 widths and 3 themes.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
