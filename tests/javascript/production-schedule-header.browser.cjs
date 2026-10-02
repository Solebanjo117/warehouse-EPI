// Renewed HTTP fixtures, isolated Edge, and current assets. No application writes.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const before = process.argv.includes('--before');
const flowsOnly = process.argv.includes('--flows-only');
const initialOnly = process.argv.includes('--initial-only');
const evidence = path.join(root, 'artifacts/tablet-tanda2');
const fixtures = process.env.WAREHOUSE_TABLET_UI_FIXTURES || path.join(evidence, before ? 'before/fixtures' : 'fixtures');
const output = process.env.WAREHOUSE_TABLET_UI_OUTPUT || path.join(evidence, before ? 'before/browser' : 'browser');
const widths = [360, 575, 576, 768, 899, 900, 991, 992, 1199, 1200, 1280];
const screens = ['program-draft', 'program-open', 'program-closed', 'products', 'products-open', 'products-closed',
  'summary-draft', 'summary-open', 'summary-closed', 'review', 'review-blocked', 'empty'];
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, hasTouch: true });
  const page = await context.newPage();
  const errors = [], results = [], initialGeometry = [];
  let screen = 'program-draft', language = 'es', writes = 0;
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.accept());
  const routeRequests = async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.hostname !== 'header.test') return route.abort();
    if (request.method() !== 'GET') { writes++; return route.fulfill({ status: 405 }); }
    const handler = url.searchParams.get('handler');
    if (handler === 'WorkspaceOpenings') return route.fulfill({ json: [] });
    if (handler === 'WorkspaceOperation') return route.fulfill({ json: { saved: false } });
    if (handler === 'WorkspaceCopy') return route.fulfill({ path:path.join(fixtures, `copy-source-${language}.json`), contentType:'application/json' });
    if (handler === 'WorkspaceResolveProducts') {
      const source = JSON.parse(fs.readFileSync(path.join(fixtures, `copy-source-${language}.json`),'utf8'));
      return route.fulfill({ json:source.rows.map(row => ({ id:row.productId,sku:row.sku,unit:row.unit,allowsDecimals:row.allowsDecimals })) });
    }
    if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ path: path.join(fixtures, `${screen}-${language}.html`), contentType: 'text/html' });
    const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
    const relative = path.join('src/WarehouseEPI.Web/wwwroot', `.${asset}`);
    const previous = path.join(evidence, 'before', relative);
    const current = path.join(root, relative);
    const file = before && fs.existsSync(previous) ? previous : current;
    if (file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
    return route.fulfill({ status: 204 });
  };
  await context.route('**/*', routeRequests);
  const frames = () => page.evaluate(async () => {
    await new Promise(resolve => requestAnimationFrame(resolve));
    await new Promise(resolve => requestAnimationFrame(resolve));
  });
  const open = async (name, lang, width, theme = 'light') => {
    screen = name; language = lang;
    await page.setViewportSize({ width, height: width === 360 ? 800 : width === 1280 ? 900 : 1024 });
    await page.goto('http://header.test/Admin/Production/Schedule');
    await page.locator('.production-daily').waitFor();
    await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
    await frames();
  };
  const overflow = async label => assert.ok(await page.evaluate(() => Math.max(document.body.scrollWidth,
    document.documentElement.scrollWidth) <= document.documentElement.clientWidth + 1), `${label}: horizontal page overflow`);
  const touchSize = async () => {
    const controls = await page.locator('.production-schedule__title .btn, .production-schedule__context .btn, .production-schedule__context select, .production-schedule-sections a, [data-workspace-copy-toggle], [data-workspace-view], [data-workspace-day], .production-week-workspace__explanation > summary').evaluateAll(nodes => nodes.filter(node => node.getClientRects().length).map(node => ({height:node.getBoundingClientRect().height,text:node.textContent})));
    for (const control of controls) assert.ok(control.height >= 44, `Touch target below 44px: ${control.text}`);
  };
  const checkHeader = async width => {
    const row = page.locator('.production-schedule__context');
    assert.equal(await row.count(), 1);
    assert.equal(await page.locator('[data-schedule-week-select]').count(), 1);
    assert.equal(await page.locator('.production-week-workspace__context').count(), 0);
    if (screen !== 'empty') {
      assert.equal(await page.locator('.production-schedule-sections a').count(), 3);
      assert.equal(await page.locator('.production-schedule-sections [aria-current="page"]').count(), 1);
      const nav = await page.locator('.production-schedule-sections').boundingBox();
      assert.ok(nav.x >= 0 && nav.x + nav.width <= width + 1);
      assert.equal(await page.locator('[data-schedule-week-status]').count(), 1);
    }
    await row.evaluate(el => {
      document.activeElement?.blur();
      window.scrollTo({ top: el.getBoundingClientRect().top + window.scrollY + 200, behavior: 'instant' });
    });
    await frames();
    const geometry = await row.evaluate(el => ({ position: getComputedStyle(el).position,
      offset: getComputedStyle(el).top, top: el.getBoundingClientRect().top,
      bar: document.querySelector('.app-topbar').getBoundingClientRect().bottom,
      reached: scrollY >= 160 }));
    assert.equal(geometry.position, 'sticky');
    assert.equal(geometry.offset, width < 900 ? '64px' : '0px');
    if (geometry.reached) assert.ok(geometry.top >= (width < 900 ? geometry.bar : 0) - 1, JSON.stringify(geometry));
    const selector = page.locator('[data-schedule-week-select]');
    if (await selector.isEnabled()) {
      await selector.focus();
      assert.equal(await row.evaluate(el => getComputedStyle(el).position), width <= 900 ? 'static' : 'sticky');
      await selector.evaluate(el => el.blur());
    }
    if (screen.startsWith('program')) {
      const field = page.locator('[data-workspace-search], [data-workspace-filter]').first();
      await field.focus();
      assert.equal(await row.evaluate(el => getComputedStyle(el).position), width <= 900 ? 'static' : 'sticky');
      await field.evaluate(el => el.blur());
    }
    assert.equal(await row.evaluate(el => getComputedStyle(el).position), 'sticky');
  };
  const firstScreen = async width => {
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
    await frames();
    const search = page.locator('[data-workspace-search]');
    assert.equal(await page.locator('[data-workspace-search]').isVisible(), true);
    assert.equal(await page.locator('[data-workspace-copy-panel]').isVisible(), false);
    assert.equal(await page.locator('[data-workspace-paste]').count(), 0);
    const box = await search.boundingBox();
    const copyBox = await page.locator('[data-workspace-copy-toggle]').boundingBox();
    if (width >= 576) assert.ok(Math.abs(box.y + box.height - copyBox.y - copyBox.height) < 1, 'Search and copy share a row');
    else assert.ok(copyBox.y >= box.y + box.height, 'Copy stacks below search on mobile');
    if (width === 360) assert.ok(box.y + box.height <= 800, `Search below initial mobile screen: ${JSON.stringify(box)}`);
    if ([768,1280].includes(width)) {
      const tableHead = await page.locator('.production-week-workspace__table thead:visible').first().boundingBox();
      const firstSku = await page.locator('[data-workspace-rows] tr > th').first().boundingBox();
      const height = width === 1280 ? 900 : 1024;
      assert.ok(tableHead.y + tableHead.height < height, `Table heading below initial ${width}: ${JSON.stringify(tableHead)}`);
      assert.ok(firstSku.y < height - 20, `First SKU below initial ${width}: ${JSON.stringify(firstSku)}`);
      initialGeometry.push({ width, height, language, search: box, tableHead, firstSku });
    }
  };
  const contrast = async () => {
    // Bootstrap animates button colors for 150ms when the theme changes.
    await page.waitForTimeout(170);
    const labels = await page.locator('.production-schedule__title .btn, .production-schedule-sections a, .production-schedule__preparation, [data-workspace-copy-toggle]').evaluateAll(nodes => {
      const rgb = color => color.match(/[\d.]+/g)?.map(Number) || [];
      const luminance = color => color.slice(0,3).map(value => { const n = value/255; return n <= .04045 ? n/12.92 : ((n+.055)/1.055)**2.4; }).reduce((sum,value,index) => sum+value*[.2126,.7152,.0722][index],0);
      return nodes.filter(node => node.getClientRects().length).map(node => {
        let parent = node, background;
        while(parent) { background = rgb(getComputedStyle(parent).backgroundColor); if(background.length === 3 || background[3] > 0) break; parent = parent.parentElement; }
        const text = luminance(rgb(getComputedStyle(node).color)), bg = luminance(background);
        return {text:node.textContent,color:getComputedStyle(node).color,background,theme:document.documentElement.dataset.bsTheme,parent:parent?.className,ratio:(Math.max(text,bg)+.05)/(Math.min(text,bg)+.05)};
      });
    });
    for (const label of labels) assert.ok(label.ratio >= 4.5, `Text contrast: ${JSON.stringify(label)}`);
  };
  try {
    if (before) {
      for (const name of ['program-draft','program-open','products','summary-draft','review','empty'])
        for (const width of [360,768,1280]) for (const theme of ['light','dark']) {
          await open(name, 'es', width, theme);
          await page.screenshot({ path: path.join(output, `${name}-es-${theme}-${width}.png`) });
        }
      console.log('Captured 36 before screenshots with the original markup and assets.');
      return;
    }
    if (!flowsOnly) for (const theme of ['light','dark','system']) {
      await page.emulateMedia({ colorScheme: theme === 'system' ? 'dark' : theme });
      for (const lang of ['es','en']) for (const width of widths) for (const name of initialOnly ? ['program-draft'] : screens) {
        const label = `${name}/${lang}/${theme}/${width}`;
        await open(name, lang, width, theme);
        assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
        await overflow(label); await touchSize();
        if (name === 'program-draft') {
          await firstScreen(width);
          assert.equal(await page.locator('[data-workspace-count]').textContent(), lang === 'en' ? '0 changes' : '0 cambios');
        }
        if (name === 'review' || name === 'review-blocked') {
          assert.equal(await page.locator('[name="Publish.Pin"]').inputValue(), '');
          assert.equal(await page.locator('[data-schedule-publish-confirm]').isEnabled(), name === 'review');
        }
        if (lang === 'es' && [360,768,1280].includes(width) && ['light','dark'].includes(theme)
            && ['program-draft','program-open','products','summary-draft','review','empty'].includes(name))
          await page.screenshot({ path: path.join(output, `${name}-es-${theme}-${width}.png`) });
        await checkHeader(width); await overflow(label);
        results.push({ screen:name, language:lang, theme, width, passed:true });
      }
      console.log(`Passed ${theme}: ${widths.length*2*(initialOnly ? 1 : screens.length)} screen, viewport and language checks.`);
    }
    // Actual editing and recovery, using the existing state store and method controls.
    await open('program-draft','es',768);
    await page.locator('[data-schedule-orders] > summary').evaluateAll(nodes => nodes.forEach(el => { if (el.getAttribute('aria-expanded') !== 'true') el.click(); }));
    const quantity = page.locator('[data-workspace-rows] input.production-week-workspace__quantity:visible').first();
    await quantity.fill('41');
    assert.equal(await quantity.getAttribute('aria-invalid'), 'true');
    await page.locator('[data-schedule-orders] input.production-week-workspace__quantity:visible').first().fill('31');
    assert.equal(await page.locator('[data-workspace-count]').textContent(),'1 cambio');
    assert.ok((await page.locator('[data-workspace-count]').getAttribute('class')).includes('text-bg-warning'));
    for (let toggle = 0; toggle < 2; toggle++) {
      await page.locator('[data-workspace-copy-toggle]').tap();
      assert.equal(await quantity.inputValue(),'41');
      assert.equal(await page.locator('[data-workspace-search]').isVisible(),true);
    }
    await page.evaluate(() => {
      const root = document.querySelector('[data-week-workspace]');
      const key = `warehouse-epi:schedule:${root.dataset.userId}:${root.dataset.weekId}`;
      const prior = JSON.parse(localStorage.getItem(key));
      prior.pasteText = 'SKU\tLunes\tNotas\nLONG-SKU\t12\tPENDING-NOTES';
      prior.inputMode = 'paste'; localStorage.setItem(key, JSON.stringify(prior));
    });
    await page.reload();
    assert.equal(await page.locator('[data-workspace-legacy-paste]').isVisible(),true);
    assert.ok((await page.locator('[data-workspace-legacy-text]').textContent()).includes('PENDING-NOTES'));
    assert.equal(await page.locator('[data-workspace-recovery]').isVisible(),true);
    assert.equal(await page.locator('[data-schedule-preparation-status]').getAttribute('data-preparation-state'),'recovery');
    await overflow('recovery pending');
    await page.screenshot({ path:path.join(output,'recovery-pending-768.png') });
    await page.evaluate(() => localStorage.clear());
    await open('program-copy','es',768);
    if (!await page.locator('[data-workspace-copy-panel]').isVisible()) await page.locator('[data-workspace-copy-toggle]').tap();
    await page.locator('[data-workspace-copy-quantities]').check();
    await page.locator('[data-workspace-copy]').tap();
    await page.waitForFunction(() => window.ProductionWeekWorkspace.state().pending);
    await page.locator('[data-schedule-orders] > summary').evaluateAll(nodes => nodes.forEach(el => { if (el.getAttribute('aria-expanded') !== 'true') el.click(); }));
    await page.locator('[data-workspace-rows] input.production-week-workspace__quantity:visible').first().waitFor();
    assert.equal(await page.locator('[data-schedule-preparation-status]').getAttribute('data-preparation-state'),'changed');
    const copyPreview = await page.locator('[data-workspace-rows]').innerText();
    await page.locator('[data-workspace-copy-toggle]').tap();
    assert.equal(await page.locator('[data-workspace-rows]').innerText(),copyPreview);
    for (const width of [360,1280,768]) {
      await page.setViewportSize({width,height:width===360?800:width===1280?900:1024});
      await frames(); await overflow(`copy prepared ${width}`); await touchSize();
    }
    await page.screenshot({path:path.join(output,'copy-prepared-768.png')});
    await page.evaluate(() => localStorage.clear());
    await open('program-draft','en',360);
    for (const state of ['saved','changed','prepared','saving','uncertain','recovery']) {
      await page.evaluate(status => { window.ProductionWeekWorkspace.state = () => ({ pending:status !== 'saved',status }); window.dispatchEvent(new Event('schedulepreparationchange')); },state);
      assert.equal(await page.locator('[data-schedule-preparation-status]').getAttribute('data-preparation-state'),state);
      const translated = await page.locator('[data-schedule-context]').getAttribute(`data-state-${state}`);
      assert.equal(await page.locator('[data-schedule-preparation-status]').textContent(),translated);
    }
    for (const theme of ['light','dark','system']) {
      await page.emulateMedia({colorScheme:'dark'});
      await open('program-draft','en',768,theme);
      await contrast();
      for (const status of ['saved','changed','prepared','saving','uncertain','recovery']) {
        await page.evaluate(status => { window.ProductionWeekWorkspace.state = () => ({pending:status !== 'saved',status}); window.dispatchEvent(new Event('schedulepreparationchange')); },status);
        await contrast();
      }
    }
    // Keyboard reachability of the methods, plus native disclosure with Enter/Escape.
    await open('program-draft','es',360);
    await page.locator('[data-workspace-search]').focus();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('[data-workspace-copy-toggle]').evaluate(el => el === document.activeElement),true);
    await page.keyboard.press('Enter');
    assert.equal(await page.locator('[data-workspace-copy-panel]').isVisible(),true);
    await page.keyboard.press('Tab');
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('[data-workspace-copy-panel]').isVisible(),false);
    assert.equal(await page.locator('[data-workspace-copy-toggle]').evaluate(el => el === document.activeElement),true);
    const zoom = await browser.newContext({ viewport:{width:640,height:450},deviceScaleFactor:2,hasTouch:true });
    await zoom.route('**/*',routeRequests);
    const zoomPage = await zoom.newPage();
    for (const lang of ['es','en']) for (const name of screens) {
      screen = name; language = lang;
      await zoomPage.goto('http://header.test/Admin/Production/Schedule');
      await zoomPage.locator('.production-schedule__context').waitFor();
      await zoomPage.locator('details').evaluateAll(nodes => nodes.forEach(el => { if(!el.classList.contains('dropdown')) el.open = true; }));
      assert.ok(await zoomPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Zoom ${name}/${lang}`);
      assert.equal(await zoomPage.locator('[data-schedule-week-select]').isVisible(),true);
    }
    await zoom.close();
    assert.deepEqual(errors,[]); assert.equal(writes,0);
    fs.writeFileSync(path.join(output,initialOnly ? 'counts-results.json' : flowsOnly ? 'flows-results.json' : 'results.json'),JSON.stringify({ passed:results.length, zoom200Emulated:24,
      methodsAndRecovery:true, preparationStates:6, contrast:'WCAG AA 4.5:1 across three themes', writes, errors, initialGeometry, results },null,2));
    console.log(`Passed ${results.length} matrix checks, 24 emulated zoom checks, methods and recovery; zero writes/errors.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
