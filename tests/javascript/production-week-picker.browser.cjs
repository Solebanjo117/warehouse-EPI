const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const { chromium } = require(path.join(packages, fs.readdirSync(packages).find(n => n.startsWith('playwright@')), 'node_modules/playwright'));
const fixtures = process.env.WAREHOUSE_WEEK_PICKER_FIXTURES || path.join(root, 'artifacts/week-preparation/fixtures');
const output = path.join(root, 'artifacts/ui/week-preparation/browser'); fs.mkdirSync(output, { recursive: true });
(async () => {
 const browser = await chromium.launch({ channel: 'msedge', headless: true });
 try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, hasTouch: true });
  await context.addInitScript(() => { const original = fetch; window.fetch = (url, options) => original(url, { ...options, signal: undefined }); });
  const page = await context.newPage();
  let language = 'es', screen = 'ready', mode = 'normal', saved = false, writes = 0, carryover = false;
  const held = [], errors = [], flows = [], payloads = [];
  page.on('pageerror', e => errors.push(e.message));
  const json = (route, value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
  const monthReply = route => { const url = new URL(route.request().url()); return route.fulfill({ path: path.join(fixtures, `${url.searchParams.get('month')}-${language}.json`), contentType: 'application/json' }); };
  const product = () => JSON.parse(fs.readFileSync(path.join(fixtures, `products-ready-${language}.json`)))[0];
  await context.route('**/*', async route => {
   const request = route.request(), url = new URL(request.url()), handler = url.searchParams.get('handler');
   if (url.hostname !== 'week-picker.test') return route.abort();
   if (request.method() === 'POST') {
    writes++;
    if (handler === 'NewWeekReview') return mode === 'review-fail' ? json(route, {}, 503) : json(route, { canConfirm: true, fingerprint: 'review-fixture', errors: [] });
    if (handler === 'CreateWeek') {
     const raw = request.postData() || ''; const match = /name="payload"\r\n\r\n([^\r]*)/.exec(raw);
     if (match) payloads.push(JSON.parse(match[1]));
     if (mode === 'pin-fail') return json(route, { saved: false, errors: ['NIP incorrecto'] }, 400);
     if (mode === 'conflict') return json(route, { saved: false, errors: ['Ya creada'], viewUrl: '/existing' }, 409);
     if (mode === 'save-hold') { held.push(route); return; }
     if (mode === 'lost') { saved = false; return route.abort(); }
     if (mode === 'lost-confirmed') { saved = true; return route.abort(); }
     saved = true; return json(route, { saved: true, count: 1, url: '/done' });
    }
    return json(route, {}, 405);
   }
   if (handler === 'WeekOptions') {
    if (mode === 'hold') { held.push(route); return; }
    if (mode === 'month-fail') return json(route, {}, 503);
    return monthReply(route);
   }
   if (handler === 'NewWeekContext') {
    if (mode === 'context-fail') return json(route, {}, 503);
    if (mode === 'context-hold') { held.push(route); return; }
    return route.fulfill({ path: path.join(fixtures, `context-${screen}-${url.searchParams.get('weekStart')}-${language}.json`), contentType: 'application/json' });
   }
   if (handler === 'WorkspaceOpenings') {
    const date = url.searchParams.get('weekStart');
    const sources = date ? JSON.parse(fs.readFileSync(path.join(fixtures, `context-ready-${date}-${language}.json`))).sources : [];
    return json(route, carryover && sources.length ? [0,1,2].map(area => ({ sourceWeekId: sources.at(-1).id, sourceLineId: '00000000-0000-0000-0000-000000000031',
      sourceStart: '2026-09-07', originalStart: '2026-09-07', productId: product().id, sku: product().sku, unit: product().unit,
      allowsDecimals: product().allowsDecimals, sequence: 1, area, available: 15, selected: 0, provisional: false, fingerprint: 'carryover-fixture' })) : []);
   }
   if (handler === 'WorkspaceProducts' || handler === 'WorkspaceResolveProducts') return json(route, [product()]);
   if (url.pathname === '/Operations/Lookup') return json(route, product());
   if (handler === 'WorkspaceCopy' && url.searchParams.get('weekStart') < '2026-09-07') return json(route, {}, 400);
   if (handler === 'WorkspaceCopy') return json(route, { id: url.searchParams.get('sourceWeekId'), version: 1, status: 'Closed', rows: [{ ...product(), productId: product().id, id: '00000000-0000-0000-0000-000000000021', day: 0, quantity: 7 }] });
   if (handler === 'NewWeekOperation') return json(route, { saved, url: '/done' });
   if (url.pathname === '/done' || url.pathname === '/existing') return route.fulfill({ contentType: 'text/html', body: '<p>Saved fixture</p>' });
   if (url.pathname === '/Admin/Production/Schedule') return route.fulfill({ path: path.join(fixtures, `${screen}-${language}.html`), contentType: 'text/html' });
   const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
   const file = path.resolve(root, 'src/WarehouseEPI.Web/wwwroot', `.${asset}`);
   if (file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
   return route.fulfill({ status: 204 });
  });
  const picker = () => page.locator('[data-week-picker]'), workspace = () => page.locator('[data-week-workspace]');
  const date = value => picker().locator(`[data-week-picker-option][value="${value}"]`);
  const pin = () => workspace().locator('[data-workspace-pin]');
  const review = () => workspace().locator('[data-workspace-review]');
  const save = () => workspace().locator('[data-workspace-save]');
  const ready = () => page.waitForFunction(() => document.querySelector('[data-week-workspace]')?.dataset.targetReady === 'true');
  const done = () => page.waitForFunction(() => document.querySelector('[data-week-picker]')?.getAttribute('aria-busy') === 'false');
  const open = async (lang = 'es', state = 'ready', width = 1280, theme = 'light') => {
   if (page.url().startsWith('https://week-picker.test')) await page.evaluate(() => localStorage.clear());
   language = lang; screen = state; mode = 'normal'; saved = false;
   await page.setViewportSize({ width, height: width === 360 ? 800 : 1024 });
   await page.emulateMedia({ colorScheme: 'dark' });
   await page.goto('https://week-picker.test/Admin/Production/Schedule?ActionPanel=new');
   await picker().waitFor();
   if (state === 'ready') await ready();
   await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
   await page.waitForTimeout(250);
  };
  const check = (condition, label) => { assert.ok(condition, label); flows.push(label); };
  const quantities = () => workspace().locator('input[data-workspace-sku-day]:visible');
  const add = async () => { const search = workspace().locator('[data-workspace-search]'); await search.fill(product().sku); await search.press('Enter'); await quantities().first().waitFor(); await quantities().nth(0).fill('10'); await quantities().nth(6).fill('20'); };
  let visualCases = 0;
  if (!process.argv.includes('--flows-only')) for (const lang of ['es', 'en']) for (const state of ['ready', 'blocked', 'closed'])
   for (const theme of ['light', 'dark', 'system']) for (const width of [360, 768, 899, 900, 1199, 1200, 1280]) {
    if (state === 'closed') { language = lang; screen = state; await page.emulateMedia({ colorScheme: 'dark' }); await page.setViewportSize({ width, height: 900 }); await page.goto('https://week-picker.test/Admin/Production/Schedule'); await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click()); await page.waitForTimeout(250); }
    else await open(lang, state, width, theme);
    assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme === 'system' ? 'dark' : theme);
    assert.equal(await page.locator('[data-workspace-view], [data-workspace-day-picker]').count(), 0);
    assert.equal(await workspace().locator('[data-workspace-day-head]').count(), 7);
    assert.ok(await workspace().locator('[data-workspace-day-head]').evaluateAll(nodes => nodes.every(node => !node.hidden)));
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${lang}/${state}/${theme}/${width}: page overflow`);
    if (state === 'ready') {
     await add();
     const geometry = await page.evaluate(() => ({ width: innerWidth, document: document.documentElement.scrollWidth, scroll: scrollX,
      contentLeft: document.querySelector('.app-content')?.getBoundingClientRect().left, bodyClass: document.body.className }));
     assert.ok(geometry.document <= geometry.width + 1 && geometry.scroll === 0, `Weekly editing page geometry: ${JSON.stringify(geometry)}`);
    }
    if (state !== 'closed') {
     assert.equal(await date('2026-09-28').isDisabled(), true);
     assert.equal(await workspace().locator('[data-workspace-controls]').evaluate(el => el.disabled), state === 'blocked');
     assert.equal(await pin().isVisible(), false);
     assert.ok(await picker().locator('.btn:visible, .production-week-picker__choice').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height >= 44)), 'Picker touch targets');
     assert.ok(await picker().locator('[data-week-picker-month]').evaluate(node => {
      const luminance = color => {
       const values = color.match(/[\d.]+/g).slice(0,3).map(Number).map(value => { value /= 255; return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4; });
       return .2126 * values[0] + .7152 * values[1] + .0722 * values[2];
      };
      let parent = node, background;
      while (parent) { background = getComputedStyle(parent).backgroundColor; if (!background.endsWith(', 0)') && background !== 'transparent') break; parent = parent.parentElement; }
      const first = luminance(getComputedStyle(node).color), second = luminance(background);
      return (Math.max(first, second) + .05) / (Math.min(first, second) + .05) >= 4.5;
     }), 'Month contrast');
    }
    if (state === 'ready') assert.ok(await workspace().locator('[data-workspace-search], [data-workspace-review], input[data-workspace-sku-day]').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height >= 44)), 'Editor touch targets');
    if (lang === 'es' && state === 'ready' && ['light','dark'].includes(theme) && [360,768,1280].includes(width)) {
     await workspace().locator('[data-workspace-scroll]').evaluate(node => { node.scrollLeft = 0; node.dispatchEvent(new Event('scroll')); });
     await page.evaluate(() => window.scrollTo({ left: 0, top: 0, behavior: 'instant' }));
     await page.waitForTimeout(250);
     await page.screenshot({ path: path.join(output, `${lang}-${theme}-${width}.png`), fullPage: true });
    }
    visualCases++;
   }
  await open(); await add();
  await review().click(); await pin().waitFor({ state: 'visible' }); await pin().fill('1234');
  await date('2026-09-14').check(); await ready();
  check(await pin().inputValue() === '' && !(await pin().isVisible()), 'Changing Monday clears review and PIN');
  check(await quantities().nth(0).inputValue() === '10' && await quantities().nth(6).inputValue() === '20', 'Date change moves Monday and Sunday quantities');
  await review().click(); await pin().waitFor({ state: 'visible' });
  const shifted = JSON.parse(await workspace().locator('[data-workspace-payload]').inputValue());
  check(shifted.changes[0].line.plannedDate === '2026-09-14' && shifted.changes[1].line.plannedDate === '2026-09-20', 'Review uses new dates');
  await picker().locator('[data-week-picker-next]').click(); await done();
  check(await workspace().locator('[data-workspace-controls]').evaluate(el => el.disabled), 'No selected date suspends editing without losing preparation');
  await date('2026-10-05').check(); await ready(); check(await quantities().first().inputValue() === '10', 'Month navigation preserves preparation');
  await page.reload(); await page.locator('[data-workspace-recovery] button').first().click(); await ready();
  await quantities().first().waitFor(); check(await quantities().first().inputValue() === '10' && await workspace().getAttribute('data-week-start') === '2026-10-05', 'Recovery restores quantities and selected date');
  check(!JSON.stringify(await page.evaluate(() => Object.values(localStorage))).includes('1234'), 'PIN is never stored');
  await open(); await picker().locator('[data-week-picker-upcoming]').click(); await done();
  check(await picker().locator('[data-week-picker-existing]').isVisible() && await workspace().locator('[data-workspace-controls]').evaluate(el => el.disabled), 'Existing shortcut offers View week and suspends preparation');
  await picker().locator('[data-week-picker-current]').click(); await ready();
  await date('2026-09-21').focus(); await page.keyboard.press('ArrowUp'); await ready();
  check(await date('2026-09-14').isChecked(), 'Native radio keyboard selects another Monday');
  mode = 'month-fail'; await picker().locator('[data-week-picker-next]').click(); await done();
  check(await picker().getAttribute('data-month') === '2026-09' && await picker().locator('[data-week-picker-retry]').isVisible(), 'Failed month retains previous month and offers retry');
  mode = 'normal'; await picker().locator('[data-week-picker-retry]').click(); await done();
  check(await picker().getAttribute('data-month') === '2026-10', 'Month retry succeeds');
  await open(); mode = 'hold'; await picker().locator('[data-week-picker-next]').click(); await picker().locator('[data-week-picker-current]').click(); await page.waitForTimeout(50);
  assert.equal(held.length,2); const old = held.shift(), latest = held.shift(); await monthReply(latest); await ready(); await monthReply(old); await page.waitForTimeout(100);
  check(await picker().getAttribute('data-month') === '2026-09', 'Late month response cannot replace newer selection');
  await open(); mode = 'context-hold'; await date('2026-09-14').check(); await date('2026-09-21').check(); await page.waitForTimeout(70);
  assert.equal(held.length, 2);
  const oldContext = held.shift(), latestContext = held.shift();
  const contextReply = route => { const date = new URL(route.request().url()).searchParams.get('weekStart'); return route.fulfill({ path: path.join(fixtures, `context-ready-${date}-${language}.json`), contentType: 'application/json' }); };
  await contextReply(latestContext); await ready(); await contextReply(oldContext); await page.waitForTimeout(70);
  check(await workspace().getAttribute('data-week-start') === '2026-09-21', 'Late destination context cannot overwrite the latest Monday');
  await open(); mode = 'context-fail'; await date('2026-09-14').check(); await workspace().locator('[data-workspace-target-status] button').waitFor();
  check(await workspace().locator('[data-workspace-controls]').evaluate(el => el.disabled), 'Failed date context prevents editing and creation');
  mode = 'normal'; await workspace().locator('[data-workspace-target-status] button').click(); await ready();
  await add(); mode = 'review-fail'; await review().click(); await page.waitForTimeout(100);
  check(await pin().isVisible() === false && await quantities().first().inputValue() === '10', 'Failed review preserves quantities');
  mode = 'normal'; await review().click(); await pin().waitFor({ state: 'visible' });
  mode = 'pin-fail'; await pin().fill('1234'); await save().click(); await page.waitForFunction(() => document.querySelector('[data-workspace-message]').textContent.includes('NIP incorrecto'));
  check(await pin().inputValue() === '' && await quantities().first().inputValue() === '10', 'Invalid PIN clears only PIN');
  mode = 'conflict'; await pin().fill('1234'); await save().click(); await workspace().locator('[data-workspace-message] a').waitFor();
  check(await quantities().first().inputValue() === '10', 'Concurrent creation offers link and preserves preparation');
  carryover = true; await open();
  await workspace().locator('[data-workspace-copy-toggle]').click();
  const sourceOptions = await workspace().locator('[data-workspace-source] option').evaluateAll(options => options.map(option => option.value));
  await workspace().locator('[data-workspace-source]').selectOption(sourceOptions.at(-1));
  await workspace().locator('[data-workspace-copy-quantities]').check(); await workspace().locator('[data-workspace-copy-openings]').check();
  await workspace().locator('[data-workspace-copy]').click(); await quantities().first().waitFor();
  await review().click(); await pin().waitFor({ state: 'visible' });
  const copied = JSON.parse(await workspace().locator('[data-workspace-payload]').inputValue());
  check(copied.changes.length === 1 && copied.openings.length === 3, 'Copy and all three carryover areas prepare before creation');
  await picker().locator('[data-week-picker-previous]').click(); await done(); await date('2026-08-31').check(); await ready();
  await review().click(); await workspace().locator('[data-workspace-message] button').first().waitFor();
  check(await quantities().first().inputValue() === '7' && !(await pin().isVisible()), 'Ineligible carryover blocks confirmation and keeps copied quantities');
  await workspace().locator('[data-workspace-message] button').first().click(); await review().click();
  const removeRoots = workspace().locator('[data-workspace-message] button'); await removeRoots.first().waitFor();
  while (await removeRoots.count()) { await removeRoots.first().click(); await review().click(); if (await pin().isVisible()) break; }
  check(await quantities().first().inputValue() === '7', 'Discarding unavailable roots preserves the weekly program');
  carryover = false;
  await open(); await review().click(); await pin().waitFor({ state: 'visible' });
  check((await workspace().locator('[data-workspace-review-list]').textContent()).includes('sin programación'), 'Empty week is explicitly reviewed');
  await open(); await add(); await review().click(); await pin().waitFor({ state: 'visible' });
  mode = 'lost'; await pin().fill('1234'); await save().click(); await page.waitForFunction(() => document.querySelector('[data-workspace-message]').textContent.includes('No se recibió'));
  const pending = JSON.parse(await workspace().locator('[data-workspace-payload]').inputValue());
  check(!(await pin().isDisabled()) && await date('2026-09-14').isDisabled(), 'Uncertain operation allows PIN retry and locks date');
  await page.reload(); await page.locator('[data-workspace-recovery] button').first().click(); await pin().waitFor({ state: 'visible' });
  check(await pin().isEnabled(), 'Uncertain recovery restores confirmation with a fresh PIN');
  mode = 'save-hold'; await pin().fill('1234'); const before = writes; await save().click(); await page.waitForTimeout(70);
  await workspace().locator('[data-workspace-form]').evaluate(form => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })));
  check(writes === before + 1, 'Double submission sends one confirmation');
  const final = held.shift(); assert.ok(final); await json(final, { saved: true, count: 2, url: '/done' }); await page.waitForURL('**/done');
  check(payloads.at(-1).operationId === pending.operationId, 'Retry preserves operation ID');
  check(await page.evaluate(() => !Object.keys(localStorage).some(key => key.endsWith(':new'))), 'Confirmed save leaves no pending preparation');
  await open(); await add(); await review().click(); await pin().waitFor({ state: 'visible' });
  mode = 'lost-confirmed'; await pin().fill('1234'); await save().click(); await page.waitForURL('**/done');
  check(await page.evaluate(() => !Object.keys(localStorage).some(key => key.endsWith(':new'))), 'Lost success response is recovered through operation status');
  await open('en'); await add(); await review().click(); await pin().waitFor({ state: 'visible' });
  const englishReview = await workspace().locator('[data-workspace-review-list]').textContent();
  check(englishReview.includes('Monday') && englishReview.includes('Sunday') && englishReview.includes('(new)'), 'Review translates weekdays and change descriptions in English');
  mode = 'lost'; await pin().fill('1234'); await save().click(); await page.waitForFunction(() => document.querySelector('[data-workspace-message]').textContent.includes('The confirmation was not received'));
  check(await quantities().first().inputValue() === '10', 'English lost confirmation keeps the program and offers a translated retry');
  assert.deepEqual(errors, []);
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ visualCases, flows, errors, fixturePosts: writes }, null, 2));
  console.log(JSON.stringify({ visualCases, flows: flows.length, errors: errors.length, fixturePosts: writes }));
 } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
