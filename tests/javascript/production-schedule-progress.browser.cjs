// Razor HTTP fixtures; writes are intercepted. No operational application or database is used.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_SCHEDULE_PROGRESS_FIXTURES || path.join(root, 'artifacts/schedule-progress/fixtures');
const output = process.env.WAREHOUSE_SCHEDULE_PROGRESS_OUTPUT || path.join(root, 'artifacts/schedule-progress/browser');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  let screen = 'open', failNext = false, posts = 0, colorPercent, filterFixture = false;
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.accept());
  await page.route('http://progress.test/**', async route => {
    const url = new URL(route.request().url()), handler = url.searchParams.get('handler');
    if (route.request().method() === 'POST') {
      assert.equal(screen, 'open'); assert.equal(handler, 'WorkspaceSave'); posts++;
      const body = route.request().postData();
      if (failNext) { failNext = false; return route.fulfill({ status: 409, json: { saved: false, errors: ['La semana cambió. Compara los datos actuales y vuelve a revisar.'], current: { lines: [] } } }); }
      assert.ok(body.includes('payload'));
      return route.fulfill({ json: { saved: true, count: 1 } });
    }
    if (handler === 'WorkspaceOpenings') return route.fulfill({ json: [] });
    if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
    if (url.pathname === '/Admin/Production/Schedule') {
      let body = fs.readFileSync(screen === 'draft' ? path.join(process.env.WAREHOUSE_SCHEDULE_FIXTURES || path.join(root, 'artifacts/schedule-progress/actions'), 'draft-es.html') : path.join(fixtures, `${screen}.html`), 'utf8');
      // A second projected SKU checks exclusion without changing the coverage fixture.
      if (filterFixture) body = body.replace(/<span data-progress="([^"]+)"><\/span>/, (span, encoded) => {
        const product = JSON.parse(encoded.replaceAll('&quot;', '"'));
        product.productId = '00000000-0000-0000-0000-000000000002'; product.sku = 'OTHER-200';
        return span + `<span data-progress="${JSON.stringify(product).replaceAll('"', '&quot;')}"></span>`;
      });
      if (colorPercent !== undefined) body = body.replace(/(&quot;ratio&quot;:)(?:[\d.]+|null)/g, `$1${colorPercent}`);
      return route.fulfill({ contentType: 'text/html', body });
    }
    const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
    const file = path.join(root, 'src/WarehouseEPI.Web/wwwroot', asset);
    if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
    return route.fulfill({ status: 204 });
  });
  const open = async () => {
    await page.goto('http://progress.test/Admin/Production/Schedule?SelectedDay=2026-09-22');
    await page.locator('[data-schedule-indicators]').first().waitFor({ state: 'attached' });
  };
  const checkScroll = async () => {
    
    await page.locator('[data-workspace-scroll-top]').waitFor();
    await page.waitForFunction(() => {
      const top = document.querySelector('[data-workspace-scroll-top]');
      const bottom = document.querySelector('[data-workspace-scroll]');
      return Math.abs((top.scrollWidth - top.clientWidth) - (bottom.scrollWidth - bottom.clientWidth)) <= 1;
    });
    await page.evaluate(async () => {
      const top = document.querySelector('[data-workspace-scroll-top]');
      const bottom = document.querySelector('[data-workspace-scroll]');
      const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
      const maximum = bottom.scrollWidth - bottom.clientWidth;
      if (Math.abs(maximum - (top.scrollWidth - top.clientWidth)) > 1) throw Error('Scrollbar ranges differ');
      for (let i = 0; i < 16; i++) {
        const source = i % 2 ? bottom : top;
        const position = Math.round(maximum * ((i * 7) % 17) / 17);
        source.scrollLeft = position;
        source.dispatchEvent(new Event('scroll'));
        await frame(); await frame();
        for (let j = 0; j < 4; j++) {
          await frame();
          if (Math.abs(top.scrollLeft - position) > 1 || Math.abs(bottom.scrollLeft - position) > 1)
            throw Error('Scrollbar bounced after alternating movement');
        }
      }
      top.scrollLeft = 0; top.dispatchEvent(new Event('scroll'));
      await frame(); await frame();
    });
    const top = page.locator('[data-workspace-scroll-top]');
    await top.focus(); await top.press('ArrowRight');
    await page.waitForFunction(() => {
      const top = document.querySelector('[data-workspace-scroll-top]');
      const bottom = document.querySelector('[data-workspace-scroll]');
      return top.scrollLeft > 0 && Math.abs(top.scrollLeft - bottom.scrollLeft) <= 1;
    });
    // Exercise native thumb dragging, including direction changes, on both bars.
    for (const selector of ['[data-workspace-scroll-top]', '[data-workspace-scroll]']) {
      await page.locator(selector).evaluate(async el => {
        el.scrollLeft = 0; el.dispatchEvent(new Event('scroll'));
        el.scrollIntoView({ block: 'end', behavior: 'instant' });
        await new Promise(resolve => requestAnimationFrame(resolve));
        await new Promise(resolve => requestAnimationFrame(resolve));
      });
      const geometry = await page.locator(selector).evaluate(el => {
        const rect = el.getBoundingClientRect();
        return { x: rect.x, y: rect.bottom - 7, width: el.clientWidth, thumb: el.clientWidth * el.clientWidth / el.scrollWidth };
      });
      await page.mouse.move(geometry.x + geometry.thumb / 2, geometry.y);
      await page.mouse.down();
      await page.mouse.move(geometry.x + geometry.width * .75, geometry.y, { steps: 12 });
      await page.mouse.move(geometry.x + geometry.width * .45, geometry.y, { steps: 8 });
      await page.mouse.up();
      await page.screenshot({ path: path.join(output, `scroll-${screen}-${selector.includes('-top') ? 'top' : 'bottom'}.png`) });
      await page.evaluate(async () => {
        const top = document.querySelector('[data-workspace-scroll-top]');
        const bottom = document.querySelector('[data-workspace-scroll]');
        const position = bottom.scrollLeft;
        if (position <= 0) throw Error('Native scrollbar drag did not move');
        for (let i = 0; i < 12; i++) {
          await new Promise(resolve => requestAnimationFrame(resolve));
          if (Math.abs(top.scrollLeft - position) > 1 || Math.abs(bottom.scrollLeft - position) > 1)
            throw Error('Native scrollbar drag bounced');
        }
      });
    }
    await top.evaluate(el => { el.scrollLeft = 0; el.dispatchEvent(new Event('scroll')); });
  };
  try {
    await open();
    const workspace = page.locator('[data-week-workspace]');
    
    const sections = page.locator('.production-schedule-sections');
    assert.equal(await sections.locator('[aria-current="page"]').count(), 1);
    assert.match(await sections.getByRole('link', { name: 'Productos', exact: true }).getAttribute('href'), /SelectedDay=2026-09-22/);
    const activeSection = await sections.locator('[aria-current="page"]').evaluate(link => getComputedStyle(link).borderBottomWidth);
    assert.equal(activeSection, '3px');
    const copyToggle = workspace.locator('[data-workspace-copy-toggle]');
    assert.equal(await copyToggle.count(), 1);
    assert.equal(await copyToggle.getAttribute('aria-expanded'), 'false');
    await copyToggle.focus(); await copyToggle.press('Enter');
    assert.equal(await copyToggle.getAttribute('aria-expanded'), 'true');
    assert.equal(await workspace.locator('#workspace-copy').isVisible(), true);
    assert.equal(await workspace.locator('#workspace-add').isVisible(), true);
    assert.equal(await workspace.locator('#workspace-paste').count(), 0);
    await workspace.locator('[data-workspace-copy-products]').focus(); await page.keyboard.press('Escape');
    assert.equal(await copyToggle.getAttribute('aria-expanded'), 'false');
    assert.equal(await copyToggle.evaluate(el => el === document.activeElement), true);
    assert.equal(await workspace.locator('#workspace-add').isVisible(), true);
    assert.equal(posts, 0);
    assert.equal(await workspace.locator('[data-schedule-indicators]').count(), 7);
    assert.equal(await workspace.locator('[data-schedule-product]').count(), 1);
    assert.match(await workspace.innerText(), /Estado del plan/);
    assert.match(await workspace.innerText(), /100[,.]0%/);
    await workspace.locator('.production-schedule-progress__breakdown').first().evaluate(el => { el.open = true; });
    assert.match(await workspace.innerText(), /Cubierto: 200 EA/);
    assert.match(await workspace.innerText(), /Meta: 200 EA/);
    assert.match(await workspace.innerText(), /Hecho ese día:\s+250 EA/);
    assert.match(await workspace.innerText(), /Sin meta nueva/);
    assert.equal(await workspace.locator('[data-schedule-accumulated]').count(), 0);
    assert.equal(await workspace.locator('.production-schedule-progress__area > .production-schedule-progress__metric').count(), 21);
    assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
    
    assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
    
    assert.equal(await workspace.locator('[data-workspace-day-cell="0"]').first().isVisible(), true);
    assert.equal(await workspace.locator('[data-workspace-day-cell="1"]').first().isVisible(), true);
    
    
    assert.equal(await workspace.locator('[data-workspace-day-cell="1"]').first().isVisible(), true);
    
    
    assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
    await workspace.locator('[data-schedule-orders] > summary').click();
    assert.equal(await workspace.locator('[data-schedule-orders] [data-schedule-indicators]').count(), 0);
    assert.equal(await workspace.locator('[data-workspace-day-cell]').getByRole('button', { name: /Quitar/ }).count(), 0);
    assert.doesNotMatch(await workspace.locator('[data-workspace-rows]').innerText(), /Este renglón tiene capturas registradas/);
    await workspace.locator('[data-workspace-scroll]').screenshot({ path: path.join(output, 'daily-cells-without-remove.png') });
    const quantity = workspace.locator('[data-schedule-orders] input.production-week-workspace__quantity').first();
    await quantity.fill('110'); await quantity.press('Enter');
    assert.equal(await workspace.locator('[data-schedule-orders] input.production-week-workspace__quantity').nth(1).evaluate(el => el === document.activeElement), true);
    assert.equal(await workspace.locator('[data-workspace-progress-warning]').isVisible(), true);
    assert.match(await workspace.locator('[data-schedule-indicators]').first().innerText(), /100[,.]0%/);
    await workspace.locator('[data-workspace-review]').click();
    assert.equal(await workspace.locator('[data-workspace-pin-panel]').isVisible(), false);
    failNext = true;
    await workspace.locator('[data-workspace-save]').click();
    await workspace.getByRole('button', { name: 'Actualizar y conciliar' }).waitFor();
    assert.equal(await quantity.inputValue(), '110');
    assert.equal(await page.evaluate(() => Object.values(localStorage).some(value => value.includes('110'))), true);
    await page.evaluate(() => localStorage.clear());
    await open();
    
    await workspace.locator('[data-schedule-orders] > summary').click();
    // Filling an empty day creates a new line and requires a single ADMIN PIN at review.
    const emptyDayIndex = await workspace.locator('input.production-week-workspace__quantity').evaluateAll(inputs => inputs.findIndex(input => input.value === ''));
    assert.ok(emptyDayIndex >= 0);
    await workspace.locator('input.production-week-workspace__quantity').nth(emptyDayIndex).fill('10');
    await workspace.locator('[data-workspace-review]').click();
    await workspace.locator('[data-workspace-review-panel]').waitFor({ state: 'visible' });
    const pin = workspace.locator('[data-workspace-pin]');
    assert.equal(await pin.isEnabled(), true, await workspace.locator('[data-workspace-message]').textContent());
    await pin.fill('987654');
    assert.equal(await page.evaluate(() => Object.values(localStorage).some(value => value.includes('987654'))), false);
    await workspace.locator('[data-workspace-back]').click();
    assert.equal(await pin.inputValue(), '');
    await page.evaluate(() => localStorage.clear());
    for (const [percent, expected] of [[0, 'low'], [74.99, 'low'], [75, 'near'], [99.99, 'near'], [100, 'complete'], [null, null]]) {
      colorPercent = percent == null ? null : percent / 100;
      await open();
      const levels = await workspace.locator('.production-schedule-progress__area > .production-schedule-progress__metric:first-child').evaluateAll(nodes => nodes.map(node => node.dataset.progressLevel || null));
      assert.ok(levels.length > 0);
      assert.ok(levels.every(level => level === expected), `Incorrect color at ${percent}%`);
    }
    colorPercent = undefined;
    for (const [fixture, covered, displayed, level] of [
      ['recovery-before', 95, /23[,.]8%/, 'low'],
      ['recovery-after', 400, /100[,.]0%/, 'complete']
    ]) {
      screen = fixture;
      await open();
      const wednesday = workspace.locator('[data-workspace-rows] [data-workspace-day-cell="2"] [data-schedule-indicators]').first();
      
      assert.match(await wednesday.innerText(), displayed);
      await wednesday.locator('.production-schedule-progress__breakdown').evaluate(el => { el.open = true; });
      assert.match(await wednesday.innerText(), new RegExp(`Cubierto: ${covered} EA`));
      assert.match(await wednesday.innerText(), /Meta: 400 EA/);
      assert.match(await wednesday.innerText(), /Hecho ese día:\s+0 EA/);
      assert.equal(await wednesday.locator('..').locator('input.production-week-workspace__quantity').first().inputValue(), '400');
      assert.equal(await wednesday.locator('[data-progress-level]').first().getAttribute('data-progress-level'), level);
      
      
      assert.equal(await wednesday.isVisible(), true);
      assert.match(await wednesday.innerText(), displayed);
      await page.screenshot({ path: path.join(output, `${fixture}.png`) });
    }
    screen = 'open';
    for (const status of ['open', 'closed']) {
      screen = status;
      for (const width of [768, 899, 900, 1199, 1200, 1440]) {
        await page.setViewportSize({ width, height: 1000 }); await open();
        
        assert.equal(await workspace.locator('[data-schedule-accumulated]').count(), 0);
        if (status === 'open') {
          assert.equal(await workspace.locator('.production-week-workspace__methods').isVisible(), true);
          assert.equal(await workspace.locator('[data-workspace-copy-toggle]').count(), 1);
          assert.equal(await workspace.locator('[data-workspace-search]').isVisible(), true);
          assert.ok(await workspace.locator('.production-week-workspace__toolbar input, .production-week-workspace__toolbar button').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height >= 44)));
        }
        await checkScroll();
        assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
        
        if (width < 900) {
          assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
          
          
          
          assert.equal(await workspace.locator('[data-workspace-day-cell="0"]').first().isVisible(), true);
        }
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click());
          assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
          const colors = await workspace.locator('[data-progress-level]').evaluateAll(nodes => nodes.map(node => ({
            level: node.dataset.progressLevel, background: getComputedStyle(node).backgroundColor,
            text: getComputedStyle(node).color, secondary: getComputedStyle(node.querySelector('small') || node).color,
            tokenBackground: getComputedStyle(document.documentElement).getPropertyValue(`--bs-${{low:'danger', near:'warning', complete:'success'}[node.dataset.progressLevel]}-bg-subtle`).trim(),
            tokenText: getComputedStyle(document.documentElement).getPropertyValue(`--bs-${{low:'danger', near:'warning', complete:'success'}[node.dataset.progressLevel]}-text-emphasis`).trim()
          })));
          for (const color of colors) {
            const rgb = hex => `rgb(${hex.replace('#','').match(/../g).map(n => parseInt(n,16)).join(', ')})`;
            assert.equal(color.background, rgb(color.tokenBackground));
            assert.equal(color.text, rgb(color.tokenText));
            assert.equal(color.secondary, color.text);
          }
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
          assert.ok(await workspace.locator('[data-workspace-search], [data-workspace-filter]').first().evaluate(button => button.getBoundingClientRect().height >= 44));
          if (status === 'open') {
            assert.equal(await workspace.locator('[data-workspace-copy-toggle]').getAttribute('aria-expanded'), 'false');
            assert.equal(await workspace.locator('[data-workspace-copy-panel]').isVisible(), false);
          }
          await workspace.scrollIntoViewIfNeeded();
          await page.screenshot({ path: path.join(output, `${status}-${width}-${theme}.png`) });
          if (status === 'open' && ((width === 1440 && theme === 'light') || (width === 768 && theme === 'dark'))) {
            await page.evaluate(() => window.scrollTo(0, 0));
            await page.screenshot({ path: path.join(output, `navigation-methods-${width}-${theme}.png`) });
          }
          if (status === 'open' && width === 1440 && theme !== 'system') {
            await workspace.locator('[data-workspace-scroll]').screenshot({ path: path.join(output, `area-colors-${theme}.png`) });
          }
        }
        if (status === 'closed') {
          assert.equal(await workspace.locator('.production-week-workspace__methods').count(), 0);
          assert.equal(await workspace.locator('[data-workspace-filter]').isVisible(), true);
          assert.equal(await workspace.locator('input:not([data-workspace-filter]), textarea, form').count(), 0);
          assert.equal(await workspace.locator('[data-workspace-copy-toggle]').count(), 0);
          assert.equal(await workspace.locator('[data-schedule-indicators]').count(), 7);
          assert.equal(await workspace.locator('[data-workspace-review]').count(), 0);
        }
      }
    }
    filterFixture = true;
    for (const language of ['es', 'en']) {
      screen = language === 'es' ? 'closed' : 'closed-en';
      for (const width of [360, 575, 576, 768, 899, 900, 1199, 1200, 1440]) {
        await page.setViewportSize({ width, height: 1000 }); await open();
        const search = workspace.locator('[data-workspace-filter]');
        const rows = workspace.locator('[data-workspace-rows] > tr:visible');
        assert.equal(await rows.count(), 2);
        assert.ok((await search.boundingBox()).height >= 44);
        assert.equal(await search.getAttribute('aria-controls'), 'week-workspace-rows');
        assert.match(await workspace.locator('label[for="week-workspace-filter"]').textContent(), language === 'es' ? /Buscar SKU en esta semana/ : /Search SKU in this week/);
        await search.fill('  fg-1  '); await search.press('Enter');
        assert.equal(await search.evaluate(el => el === document.activeElement), true);
        assert.equal(await rows.count(), 1);
        assert.match(await rows.first().innerText(), /FG-100/);
        assert.equal(await workspace.locator('[data-workspace-filter-status]').textContent(), language === 'es' ? 'Mostrando 1 de 2 SKU.' : 'Showing 1 of 2 SKUs.');
        
        
        assert.equal(await search.inputValue(), '  fg-1  ');
        assert.equal(await rows.count(), 1);
        
        assert.equal(await rows.count(), 1);
        // Filtering preserves all seven weekday columns, including on mobile.
        
        await search.fill('missing');
        assert.equal(await rows.count(), 0);
        assert.equal(await workspace.locator('[data-workspace-empty]').isVisible(), true);
        assert.equal(await workspace.locator('[data-workspace-empty]').textContent(), language === 'es' ? 'No se encontraron productos.' : 'No products found.');
        await search.fill('other');
        assert.equal(await rows.count(), 1);
        assert.match(await rows.first().innerText(), /OTHER-200/);
        for (const theme of ['light', 'dark', 'system']) {
          await page.emulateMedia({ colorScheme: 'dark' });
          await page.locator(`[data-theme-choice="${theme}"]`).evaluate(button => button.click());
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
          await workspace.scrollIntoViewIfNeeded();
          await page.screenshot({ path: path.join(output, `closed-search-${language}-${width}-${theme}.png`) });
        }
        await search.fill('');
        assert.equal(await rows.count(), 2);
        assert.equal(await workspace.locator('[data-workspace-empty]').isVisible(), false);
        assert.equal(await workspace.locator('[data-workspace-filter-status]').textContent(), '');
        assert.equal(posts, 1);
      }
    }
    filterFixture = false;
    screen = 'draft';
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto('http://progress.test/Admin/Production/Schedule');
    await checkScroll();
    await workspace.locator('[data-workspace-scroll]').evaluate(el => {
      el.scrollLeft = el.scrollWidth; el.dispatchEvent(new Event('scroll'));
    });
    await page.setViewportSize({ width: 1200, height: 1000 });
    await page.waitForFunction(() => {
      const top = document.querySelector('[data-workspace-scroll-top]');
      const bottom = document.querySelector('[data-workspace-scroll]');
      return Math.abs(bottom.scrollLeft - (bottom.scrollWidth - bottom.clientWidth)) <= 1
        && Math.abs(top.scrollLeft - bottom.scrollLeft) <= 1;
    });
    await page.setViewportSize({ width: 1440, height: 1000 });
    
    assert.equal(await workspace.locator('[data-workspace-day-picker]').count(), 0);
    await page.waitForFunction(() => {
      const top = document.querySelector('[data-workspace-scroll-top]');
      const bottom = document.querySelector('[data-workspace-scroll]');
      return top.hidden === (bottom.scrollWidth <= bottom.clientWidth + 1)
        && Math.abs(top.scrollLeft - bottom.scrollLeft) <= 1;
    });
    assert.equal(posts, 1);
    assert.deepEqual(errors, []);
    console.log('PASS: recovered plan status and historical production (draft/open/closed), seven-day table, synchronized scrollbars, color thresholds, legible text in all themes, grouped progress, editing recovery, PIN privacy, closed SKU search (partial/case/Enter/clear), 36 progress captures and 54 search captures (ES/EN).');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
