const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');

const script = fs.readFileSync(path.join(__dirname,
  '../../src/WarehouseEPI.Web/wwwroot/js/production-week-workspace.js'), 'utf8');
const scheduleScript = fs.readFileSync(path.join(__dirname,
  '../../src/WarehouseEPI.Web/wwwroot/js/production-schedule.js'), 'utf8');
const visit = node => [node, ...node.children.flatMap(visit)];
const product = (id, sku, unit = 'EA', allowsDecimals = false) => ({ id, sku, unit, allowsDecimals });
const firstId = '00000000-0000-0000-0000-000000000002';
const secondId = '00000000-0000-0000-0000-000000000003';

class Element {
  constructor(tag = 'div') {
    this.tag = tag; this.children = []; this.dataset = {}; this.events = {};
    this.style = {}; this.value = ''; this.hidden = false; this.className = ''; this.scrollLeft = 0;
    this.attributes = {}; this.classList = { toggle() {} };
  }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = [...nodes]; }
  remove() { this.removed = true; }
  addEventListener(name, handler) { this.events[name] = handler; }
  setAttribute(key, value) { this.attributes[key] = value; }
  removeAttribute(key) { delete this.attributes[key]; }
  checkValidity() { return true; }
  reportValidity() {}
  prepend(...nodes) { this.children.unshift(...nodes); }
  focus() { this.focused = true; }
  scrollIntoView() {}
  getBoundingClientRect() { return { left: 24, bottom: 100, width: 200 }; }
  get lastElementChild() { return this.children.at(-1); }
  querySelectorAll(selector) {
    const nodes = this.children.flatMap(child => [child, ...child.querySelectorAll(selector)]);
    if (selector === 'input.production-week-workspace__quantity')
      return nodes.filter(node => node.tag === 'input' && node.className.includes('production-week-workspace__quantity'));
    if (selector === 'input.production-week-workspace__quantity, input.production-opening-quantity')
      return nodes.filter(node => node.tag === 'input' && (node.className.includes('production-week-workspace__quantity') || node.className.includes('production-opening-quantity')));
    if (selector === '[data-workspace-row]') return nodes.filter(node => node.dataset.workspaceRow);
    const copyDay = /^\[data-copy-day="(\d+)"\]$/.exec(selector);
    if (copyDay) return nodes.filter(node => node.dataset.copyDay === copyDay[1]);
    return [];
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  closest(selector) { return selector === 'td' ? { hidden: false } : null; }
}

function setup(copyRows = [], options = {}) {
  const selectors = new Map();
  const keys = ['rows', 'message', 'search', 'results', 'form', 'review-panel', 'day', 'day-picker',
    'summary', 'count', 'empty', 'day-summary', 'review', 'back', 'payload', 'save',
    'review-list', 'copy', 'source', 'copy-toggle', 'copy-panel', 'legacy-paste', 'legacy-text', 'discard-legacy',
    'copy-quantities', 'copy-products', 'copy-openings', 'recovery', 'clear-all'];
  keys.push('controls', 'reason', 'reason-panel', 'recheck', 'pin', 'pin-panel', 'copy-status', 'progress-warning');
  for (const key of keys) selectors.set(`[data-workspace-${key}]`, new Element());
  selectors.get('[data-workspace-copy-products]').checked = true;
  const root = new Element();
  const openingRoot = new Element();
  root.dataset = { newWeek: String(!!options.newWeek), targetReady: 'true', weekId: '00000000-0000-0000-0000-000000000001', weekVersion: '0',
    weekStart: '2026-09-21', weekStatus: options.weekStatus || 'Draft', userId: 'admin', searchUrl: '/search', workspaceReviewUrl: '/workspace-review',
    resolveUrl: '/resolve', resolveProductsUrl: '/products', copyUrl: '/copy', operationUrl: '/operation', reviewUrl: '/review', requestMode: options.requestMode || 'add',
    programUrl: 'https://localhost/schedule?WeekId=current&View=program',
    textSave: 'Guardar cambios', textContinue: 'Guardar y continuar a apertura', textPrepared: 'Incorpora o descarta',
    textRecovery: 'Recupera o descarta', textInvalid: 'Completa las cantidades',
    textCountOne: options.language === 'en' ? '{0} change' : '{0} cambio',
    textCountMany: options.language === 'en' ? '{0} changes' : '{0} cambios' };
  root.querySelector = selector => selector === '[data-opening-editor]' ? openingRoot : selectors.get(selector) || null;
  const views = ['week', 'day'].map(name => {
    const button = new Element('button'); button.dataset.workspaceView = name; return button;
  });
  root.querySelectorAll = selector => {
    if (selector === '[data-workspace-view]') return views;
    if (selector === '[data-line]') return (options.savedLines || []).map(line => {
      const node = new Element(); node.dataset.line = JSON.stringify(line); return node;
    });
    if (selector === '[data-workspace-total]' || selector.includes('[data-workspace-day-head]')) return [];
    return [];
  };
  const body = new Element('body');
  const document = { body, querySelector: selector => selector === '[data-week-workspace]' ? root : null, querySelectorAll: () => [],
    createElement: tag => new Element(tag) };
  const storage = new Map();
  if (options.prior) storage.set(`warehouse-epi:schedule:admin:${options.newWeek ? 'new' : root.dataset.weekId}`, JSON.stringify(options.prior));
  if (options.legacyText) storage.set(`warehouse-epi:schedule:admin:${root.dataset.weekId}:legacy-paste`, options.legacyText);
  const store = { getItem: key => storage.get(key) || null,
    setItem: (key, value) => storage.set(key, value), removeItem: key => {
      if (options.failClear) throw Error('storage unavailable');
      storage.delete(key);
    } };
  const timers = [];
  const confirmations = [];
  let openingOptions = options.openingOptions || [];
  const prepared = new Map(), excluded = new Set();
  const openingEditor = {
    ready: Promise.resolve(),
    options: () => openingOptions.map(row => ({ ...row, prepared: prepared.get(row.key) || row.prepared || 0,
      protected: row.protected || prepared.has(row.key) })),
    changes: () => openingOptions.filter(row => String(excluded.has(row.key) ? row.selected : prepared.get(row.key) ?? row.prepared ?? row.selected) !== String(row.selected || 0))
      .map(row => ({ ...row, quantity: String(prepared.get(row.key) ?? row.prepared ?? row.selected), before: row.selected || 0 })),
    groups: () => {
      const groups = new Map();
      openingOptions.filter(row => prepared.has(row.key) || row.prepared > 0 || row.selected > 0).forEach(row => {
        if (!groups.has(row.productId)) groups.set(row.productId, { productId: row.productId, sku: row.sku, unit: row.unit, entries: [] });
        groups.get(row.productId).entries.push({ ...row, quantity: String(excluded.has(row.key) ? row.selected : prepared.get(row.key) ?? row.prepared ?? row.selected),
          cachedQuantity: String(prepared.get(row.key) ?? row.prepared ?? row.selected), excluded: excluded.has(row.key) });
      }); return [...groups.values()];
    },
    setArea: (id, area, value) => {
      let remaining = Number(value);
      openingOptions.filter(row => row.productId === id && row.area === area && !excluded.has(row.key)).forEach(row => {
        const amount = Math.min(remaining, row.available); prepared.set(row.key, String(amount)); remaining -= amount;
      });
      root.events.openingchange?.();
    },
    setQuantity: (key, value) => prepared.set(key, value),
    setEnabled: (keys, enabled) => keys.forEach(key => { if (enabled) excluded.delete(key); else excluded.add(key); }),
    errors: () => [], reveal() {}, revalidate: async () => false,
    snapshot: () => ({ values: Object.fromEntries(prepared), excluded: [...excluded] }),
    restore: state => { for (const [key, value] of Object.entries(state?.values || state || {})) if (value) prepared.set(key, value);
      excluded.clear(); (state?.excluded || []).forEach(key => excluded.add(key)); },
    prepare: async entries => {
      if (options.staleOpening) throw Error('El pendiente de origen cambió. Prepara de nuevo el arrastre.');
      entries.forEach(entry => prepared.set(entry.key, entry.quantity)); return entries.length;
    },
    describe: change => `Arrastre ${change.key}: ${change.quantity}`
  };
  const requests = [], navigations = [], windowEvents = {};
  let saveRequests = 0;
  const fetch = async url => {
    requests.push(String(url));
    const target = new URL(url, 'https://localhost');
    if (target.pathname === '/save') {
      saveRequests++;
      if (options.lostSave) throw Error('offline');
      return { ok: true, json: async () => ({ saved: true, count: 1, url: '/schedule?WeekId=created-week' }) };
    }
    if (target.pathname === '/operation') return { ok: true, json: async () => ({ saved: options.operationSaved === true }) };
    if (target.pathname === '/workspace-review') return { ok: true, json: async () => ({ canConfirm: true, fingerprint: 'reviewed-copy' }) };
    if (target.pathname === '/search') return { ok: true, json: async () =>
      (options.products || [{ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1',
        description: 'Descripción muy larga que antes se solapaba', unit: 'EA', allowsDecimals: false }]) };
    if (target.pathname === '/copy') return { ok: true, json: async () =>
      ({ id: 'source-week', version: options.copyVersion ?? 3, rows: copyRows.map((row, index) => ({ id: `source-${index}`,
        productId: '00000000-0000-0000-0000-000000000002', unit: 'EA', allowsDecimals: false, ...row })) }) };
    if (target.pathname === '/resolve') return { ok: true, json: async () =>
      ({ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1' }) };
    if (target.pathname === '/products') return { ok: true, json: async () =>
      (options.activeProducts || options.products || [{ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1', unit: 'EA', allowsDecimals: false }]) };
    throw Error(`Unexpected ${target}`);
  };
  selectors.get('[data-workspace-form]').action = 'https://localhost/save';
  const location = { origin: 'https://localhost', href: 'https://localhost/schedule', reload() { navigations.push('reload'); }, assign(url) { navigations.push(url); }, replace(url) { navigations.push(url); } };
  const runtime = { document, URL, crypto: webcrypto,
    matchMedia: () => ({ matches: false }),
    window: { location,
      confirm: question => { confirmations.push(question); return options.confirmDelete !== false; },
      addEventListener(name, handler) { windowEvents[name] = handler; }, dispatchEvent() {},
      ProductionWeekPicker: { selectDate: async () => {} },
      ProductionWeekOpenings: { get: () => options.openingOptions ? openingEditor : null } },
    location, history: { replaceState(_state, _title, url) { location.href = String(url); } },
    localStorage: store, sessionStorage: store, structuredClone,
    setTimeout: handler => { timers.push(handler); return timers.length; },
    clearTimeout() {}, fetch, FormData: class { set() {} }, CustomEvent: class { constructor(type) { this.type = type; } } };
  const sandbox = vm.createContext(runtime);
  vm.runInContext(scheduleScript, sandbox);
  vm.runInContext(script, sandbox);
  return { get: key => selectors.get(`[data-workspace-${key}]`), storage, views, body, openingEditor, prepared, confirmations,
    workspace: runtime.window.ProductionWeekWorkspace, preparation: runtime.window.ProductionSchedulePreparation,
    navigations, requests, windowEvents, saveRequests: () => saveRequests,
    async flushSearch() { for (const handler of timers.splice(0)) await handler(); } };
}

test('change counts stay localized after edits, including singular and plural', async () => {
  for (const language of ['es', 'en']) {
    const ui = setup([], { language });
    assert.equal(ui.get('count').textContent, language === 'en' ? '0 changes' : '0 cambios');
    ui.get('search').value = 'SKU-1';
    await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
    const quantities = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
    quantities[0].value = '20'; quantities[0].events.input();
    assert.equal(ui.get('count').textContent, language === 'en' ? '1 change' : '1 cambio');
    quantities[1].value = '30'; quantities[1].events.input();
    assert.equal(ui.get('count').textContent, language === 'en' ? '2 changes' : '2 cambios');
  }
});

test('copy panel preserves search and edits, closes with Escape and recovers its open state', async () => {
  const ui = setup();
  ui.get('search').value = 'SKU-1';
  await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = '20'; quantity.events.input();
  ui.get('copy-toggle').events.click();
  assert.equal(ui.get('copy-panel').hidden, false);
  assert.equal(ui.get('search').hidden, false);
  let prevented = false;
  ui.get('copy-panel').events.keydown({ key: 'Escape', preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(ui.get('copy-panel').hidden, true);
  assert.equal(ui.get('copy-toggle').focused, true);
  ui.get('copy-toggle').events.click();
  assert.equal(quantity.value, '20');
  const prior = JSON.parse([...ui.storage.values()][0]);
  const recovered = setup([], { prior });
  assert.equal(recovered.get('copy-panel').hidden, false);
  await recovered.workspace.ready;
  assert.equal(recovered.get('recovery').hidden, true);
  assert.equal(recovered.get('clear-all').disabled, false);
  assert.equal(recovered.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0].value, '20');
  await recovered.workspace.openReview();
  assert.deepEqual(recovered.navigations, []);
});

test('copy toggle starts collapsed and does not prepare or save when opened', () => {
  const ui = setup();
  assert.equal(ui.get('copy-toggle').attributes['aria-expanded'], 'false');
  assert.equal(ui.get('copy-panel').hidden, true);
  ui.get('copy-toggle').events.click();
  assert.equal(ui.get('copy-toggle').attributes['aria-expanded'], 'true');
  assert.equal(ui.get('copy-panel').hidden, false);
  ui.get('copy-toggle').events.click();
  assert.equal(ui.get('copy-panel').hidden, true);
  assert.equal(ui.get('search').hidden, false);
  assert.deepEqual(ui.requests, []);
  assert.equal(ui.workspace.state().status, 'saved');
});

test('opening saves the reviewed operation before navigating and never publishes automatically', async () => {
  const ui = setup();
  ui.get('search').value = 'SKU-1';
  await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = '20'; quantity.events.input();
  await ui.workspace.openReview();
  assert.equal(ui.get('save').textContent, 'Guardar y continuar a apertura');
  assert.deepEqual(ui.navigations, []);
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.deepEqual(ui.navigations, ['/review']);
  assert.equal(ui.saveRequests(), 1);
  assert.equal(ui.requests.some(url => url.includes('Publish')), false);
});

test('invalid new quantities block opening and warn before leaving', async () => {
  for (const value of ['invalid', '-4']) {
    const ui = setup();
    ui.get('search').value = 'SKU-1';
    await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
    const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
    quantity.value = value; quantity.events.input();
    await ui.workspace.openReview();
    assert.deepEqual(ui.navigations, []);
    assert.equal(ui.workspace.state().pending, true);
    let warned = false;
    ui.windowEvents.beforeunload({ preventDefault() { warned = true; } });
    assert.equal(warned, true);
  }
});

test('legacy unprocessed text is archived without blocking opening and can be explicitly discarded', async () => {
  const raw = 'SKU\tLunes\nSKU-1\t20';
  const ui = setup([], { prior: { pasteText: raw, inputMode: 'paste' } });
  assert.equal(ui.get('legacy-text').textContent, raw);
  assert.equal(ui.get('legacy-paste').hidden, false);
  assert.equal(ui.workspace.state().pending, false);
  await ui.workspace.openReview();
  assert.deepEqual(ui.navigations, ['/review']);
  assert.equal([...ui.storage].find(([key]) => key.endsWith(':legacy-paste'))[1], raw);
  ui.get('discard-legacy').events.click();
  assert.equal(ui.get('legacy-paste').hidden, true);
  assert.equal([...ui.storage.keys()].some(key => key.endsWith(':legacy-paste')), false);
});

test('uncertain save keeps the opening intent and retries the same operation', async () => {
  const options = { lostSave: true, operationSaved: false };
  const ui = setup([], options);
  ui.get('search').value = 'SKU-1';
  await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = '20'; quantity.events.input();
  await ui.workspace.openReview();
  const payload = ui.get('payload').value;
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.equal(ui.workspace.state().status, 'uncertain');
  assert.deepEqual(ui.navigations, []);
  const prior = JSON.parse([...ui.storage.values()][0]);
  assert.equal(prior.continueToOpen.operationId, JSON.parse(payload).operationId);
  assert.equal(prior.pendingSent, true);
  assert.equal(JSON.stringify(prior).includes('Pin'), false);
  options.operationSaved = true;
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.equal(ui.get('payload').value, payload);
  assert.deepEqual(ui.navigations, ['/review']);
});

test('stored detection covers notes, independent carryover and preparation previews', () => {
  const ui = setup();
  const inspect = ui.preparation.inspect;
  assert.equal(inspect({ rows: [{ originalDetails: { notes: '' }, details: { notes: 'changed' }, cells: [] }], openingChangeCount: 0 }).pending, true);
  assert.equal(inspect({ rows: [], openingChangeCount: 1 }).status, 'changed');
  assert.equal(inspect({ openingPreview: [{}], openingChangeCount: 0 }).status, 'prepared');
  assert.equal(inspect({ pendingSent: true }).status, 'uncertain');
  assert.equal(inspect({ rows: [{ originalDetails: {}, details: {}, cells: [{ lineId: 'line', value: '20.00', original: '20' }] }], openingChangeCount: 0 }).pending, false);
  for (const value of ['2e1', '0x14', '20.00000']) {
    assert.equal(inspect({ rows: [{ originalDetails: {}, details: {}, cells: [{ lineId: 'line', value, original: '20' }] }], openingChangeCount: 0 }).pending, true);
  }
  assert.equal(inspect({ rows: [{ originalDetails: {}, details: {}, cells: [{ lineId: 'line', value: '99999999999999.0002', original: '99999999999999.0001' }] }], openingChangeCount: 0 }).pending, true);
});

test('recovered confirmed save continues only for the matching opening intent', async () => {
  const ui = setup([], { lostSave: true });
  ui.get('search').value = 'SKU-1';
  await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = '20'; quantity.events.input();
  await ui.workspace.openReview();
  await ui.get('form').events.submit({ preventDefault() {} });
  const prior = JSON.parse([...ui.storage.values()][0]);
  const matching = setup([], { prior, operationSaved: true });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(matching.navigations, ['/review']);
  assert.equal(matching.saveRequests(), 0);
  prior.continueToOpen.operationId = 'another-operation';
  const unrelated = setup([], { prior, operationSaved: true });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(unrelated.navigations, ['reload']);
});

test('SKU suggestions show one SKU only, even when the search result has a long description', async () => {
  const ui = setup();
  ui.get('search').value = 'SKU';
  ui.get('search').events.input();
  await ui.flushSearch();
  const option = ui.get('results').children[0];
  assert.equal(option.children.length, 1);
  assert.equal(option.children[0].textContent, 'SKU-1');
  assert.equal(option.attributes['aria-label'], 'SKU-1');
});

test('scanner focuses the existing SKU without inserting a second row and review counts one daily change', async () => {
  const ui = setup();
  const search = ui.get('search');
  search.value = 'SKU-1';
  await search.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(ui.get('rows').children.length, 1);
  search.value = 'SKU-1';
  await search.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(ui.get('rows').children.length, 1);
  const quantity = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  assert.equal(quantity.focused, true);
  quantity.value = '20'; quantity.events.input();
  await ui.get('review').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.skuTotals.length, 1);
  assert.equal(payload.skuTotals[0].quantity, '20');
  assert.equal(ui.get('count').textContent, '1 cambio');
});

test('order references share one disclosure with notes and preserve their save payload', async () => {
  const ui = setup();
  ui.get('search').value = 'SKU-1';
  await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const row = ui.get('rows').children[0];
  for (let index = 1; index <= 3; index++) {
    const field = visit(row).find(n => n.tag === 'input' && n.id?.endsWith(`-orderReference${index}`));
    assert.ok(field);
    assert.ok(visit(row).filter(n => n.tag === 'details').some(n => visit(n).includes(field)));
    assert.ok(visit(row).some(n => n.tag === 'label' && n.htmlFor === field.id));
    field.value = `000${index}`; field.events.input();
  }
  const quantity = row.querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = '20'; quantity.events.input();
  await ui.get('review').events.click();
  const line = JSON.parse(ui.get('payload').value).skuTotals[0];
  assert.deepEqual([line.orderReference1, line.orderReference2, line.orderReference3], ['0001', '0002', '0003']);
});

test('searching a SKU already present focuses its only row without another-order action', async () => {
  const ui = setup();
  ui.get('search').value = 'SKU-1'; await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  const row = ui.get('rows').children[0];
  const order = visit(row).find(n => n.id?.endsWith('-orderReference1'));
  order.value = '0001'; order.events.input();
  ui.get('search').value = 'SKU-1'; await ui.get('search').events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(ui.get('rows').children.length, 1);
  assert.equal(ui.get('results').children.length, 0);
  assert.equal(order.value, '0001');
});

for (const includeQuantities of [false, true]) {
  test(`copy enters the planning table immediately, quantities=${includeQuantities}`, async () => {
    const ui = setup([{ id: 'mon', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'thu', sku: 'SKU-1', day: 3, quantity: 25 }]);
    ui.get('source').value = 'source-week'; ui.get('copy-quantities').checked = includeQuantities;
    await ui.get('copy').events.click();
    assert.equal(ui.get('rows').children.length, 1);
    const inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
    assert.equal(inputs[0].value, includeQuantities ? '10' : ''); assert.equal(inputs[3].value, includeQuantities ? '25' : '');
    if (!includeQuantities) { assert.equal(inputs[0].placeholder, 'Programar'); assert.equal(inputs[3].placeholder, 'Programar'); }
    inputs[0].value = '9'; inputs[0].events.input();
    await ui.get('copy').events.click();
    assert.equal(ui.get('rows').children.length, 1);
    assert.equal(ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0].value, '9');
  });
}
const opening = (key, sku = 'SKU-1', area = 0, extra = {}) => ({ key, sourceWeekId: 'source-week', sourceStart: '2026-09-14', sourceLineId: key,
  productId: sku === 'SKU-1' ? firstId : secondId, sku, unit: 'EA', allowsDecimals: false, sequence: 1, area, available: 12, selected: 0, prepared: 0,
  fingerprint: 'fresh', ...extra });
const carryAmount = (ui, sku, area = 'Corte') => visit(ui.get('rows')).find(node => node.tag === 'input' && node.attributes['aria-label'] === `${sku} · arrastre de ${area}`);
const setCarry = (ui, sku, area, value) => { const input = carryAmount(ui, sku, area); input.value = value; input.events.input(); };
async function copy(ui, carry = false, products = true) {
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = carry; ui.get('copy-products').checked = products;
  await ui.get('copy').events.click();
}
test('preparing again refreshes unedited copied quantities when the option changes in either direction', async () => {
  const source = [{ id: 'mon', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'thu', sku: 'SKU-1', day: 3, quantity: 25 }];
  const ui = setup(source, { openingOptions: [opening('sew', 'SKU-1', 1)] });
  await copy(ui, true);
  const amounts = () => ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity').map(input => input.value);
  assert.deepEqual(amounts(), ['', '', '', '', '', '', '']);
  setCarry(ui, 'SKU-1', 'Costura', '8');
  for (const includeQuantities of [true, false, true]) {
    ui.get('copy-quantities').checked = includeQuantities;
    await copy(ui, true);
    assert.deepEqual(amounts(), includeQuantities ? ['10', '', '', '25', '', '', ''] : ['', '', '', '', '', '', '']);
    assert.equal(ui.get('rows').children.length, 1);
    assert.equal(carryAmount(ui, 'SKU-1', 'Costura').value, '8');
  }
  source[1].quantity = 30;
  await copy(ui, true);
  assert.equal(amounts()[3], '30');
  await ui.get('review').events.click();
  assert.deepEqual(JSON.parse(ui.get('payload').value).skuTotals.map(change => change.quantity), ['10', '30']);
  assert.equal(ui.saveRequests(), 0);
});
test('changing the quantity option preserves manual edits, including zero and cleared copied cells', async () => {
  for (const edited of ['9', '0', '']) {
    const ui = setup([{ id: 'mon', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'tue', sku: 'SKU-1', day: 1, quantity: 20 }]);
    ui.get('copy-quantities').checked = true; await copy(ui);
    const inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
    inputs[0].value = edited; inputs[0].events.input();
    for (const includeQuantities of [false, true]) {
      ui.get('copy-quantities').checked = includeQuantities; await copy(ui);
      const refreshed = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
      assert.equal(refreshed[0].value, edited);
      assert.equal(refreshed[1].value, includeQuantities ? '20' : '');
    }
  }
});
test('repeated copies refresh distinct same-day source lines without duplicating them or changing saved quantities', async () => {
  const savedLines = [{ Id: 'saved', Version: 1, ProductId: firstId, Sku: 'SKU-1', Unit: 'EA', Day: 0, Quantity: 7 }];
  const ui = setup([{ id: 'one', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'two', sku: 'SKU-1', day: 0, quantity: 20 }], { savedLines });
  await copy(ui);
  ui.get('copy-quantities').checked = true;
  await copy(ui); await copy(ui);
  assert.equal(ui.get('rows').children.length, 1);
  const amounts = ui.get('rows').children.map(row => row.querySelectorAll('input.production-week-workspace__quantity')[0].value);
  assert.deepEqual(amounts, ['37']);
  await ui.get('review').events.click();
  const changes = JSON.parse(ui.get('payload').value).skuTotals;
  assert.equal(changes.length, 1);
  assert.equal(changes[0].quantity, '37');
});
test('recovered copies refresh unedited amounts while preserving edits, including drafts from before refresh tracking', async () => {
  const source = [{ id: 'mon', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'tue', sku: 'SKU-1', day: 1, quantity: 20 }];
  for (const legacy of [false, true]) {
    const ui = setup(source); await copy(ui);
    const input = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
    input.value = '9'; input.events.input();
    const prior = JSON.parse([...ui.storage.values()][0]);
    if (legacy) prior.rows.forEach(row => row.cells.forEach(cell => { delete cell.copyPreparedValue; }));
    const recovered = setup(source, { prior });
    await recovered.workspace.ready;
    recovered.get('copy-quantities').checked = true; await copy(recovered);
    const refreshed = recovered.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
    assert.equal(refreshed[0].value, '9'); assert.equal(refreshed[1].value, '20');
    assert.equal(recovered.get('rows').children.length, 1);
  }
});
test('same-day copied rows sum and retain packing references without quantity partitions', async () => {
  const ui = setup([{ id: 'one', sku: 'SKU-1', day: 0, quantity: 10, orderReference1: 'OLD-ORDER' },
    { id: 'two', sku: 'SKU-1', day: 0, quantity: 20 }, { id: 'three', sku: 'SKU-1', day: 1, quantity: 30 }]);
  ui.get('copy-quantities').checked = true; await copy(ui);
  assert.equal(ui.get('rows').children.length, 1); await ui.get('review').events.click();
  const changes = JSON.parse(ui.get('payload').value).skuTotals;
  assert.equal(changes.length, 2); assert.ok(changes.every(change => change.orderReference1 === 'OLD-ORDER'));
  assert.deepEqual(changes.map(change => change.quantity), ['30', '30']);
});
test('empty sources explain the result without adding another table', async () => {
  const ui = setup(); await copy(ui);
  assert.equal(ui.get('rows').children.length, 0); assert.match(ui.get('message').children[0].textContent, /no tiene productos activos/);
});
test('carry-only products show the selected week and preserve partial quantities when prepared twice', async () => {
  const ui = setup([], { openingOptions: [opening('sew', 'SKU-1', 1, { available: 60 }), opening('rtp', 'SKU-1', 2, { available: 100 }),
    opening('other', 'SKU-2', 1, { sourceWeekId: 'older-week' })] });
  await copy(ui, true, false);
  assert.equal(carryAmount(ui, 'SKU-1', 'Costura').value, '60'); assert.equal(carryAmount(ui, 'SKU-1', 'Ready to Pack').value, '100');
  setCarry(ui, 'SKU-1', 'Costura', '25'); await ui.get('copy').events.click();
  assert.equal(carryAmount(ui, 'SKU-1', 'Costura').value, '25'); assert.equal(ui.prepared.has('other'), false);
  assert.equal(ui.get('rows').children[0].children[1].dataset.workspaceOpeningArea, 0);
});
test('carry-only daily editors stay empty without staging program and persist only edited days', async () => {
  const ui = setup([], { openingOptions: [opening('sew', 'SKU-1', 1)] });
  await copy(ui, true, false);
  let inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  assert.equal(inputs.length, 7);
  assert.ok(inputs.every(input => input.value === '' && input.disabled === false));
  let prior = JSON.parse([...ui.storage.values()][0]);
  assert.equal(prior.rows.length, 0);
  await ui.get('review').events.click();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals.length, 0);
  inputs[6].value = '9'; inputs[6].events.input();
  await copy(ui, true, false);
  inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  assert.equal(inputs[6].value, '9');
  prior = JSON.parse([...ui.storage.values()][0]);
  assert.equal(prior.rows.length, 1);
  assert.equal(prior.rows[0].cells[6].value, '9');
});
test('stored undistributed totals are pending even without any changed order quantity', () => {
  const ui = setup();
  assert.equal(ui.preparation.inspect({ rows: [], skuDayTargets: {} }).pending, false);
  assert.equal(ui.preparation.inspect({ rows: [], skuDayTargets: { sku: { 0: '400' } } }).pending, true);
  assert.equal(ui.preparation.inspect({ rows: [], skuDayTargets: { sku: { 0: '' } } }).pending, true);
});
test('origins are collapsed and show orders and original weeks without technical identifiers', async () => {
  const ui = setup([], { openingOptions: [opening('technical-root', 'SKU-1', 1, { orderReference1: 'PO-M6', notes: 'Continuar', originalStart: '2026-09-07', sequence: 4 })] });
  await copy(ui, true, false);
  const origins = visit(ui.get('rows')).find(node => node.tag === 'details');
  assert.notEqual(origins.open, true);
  const labels = visit(origins).map(node => node.textContent).join(' ');
  assert.match(labels, /Pedido 1: PO-M6/); assert.match(labels, /origen inicial 07\/09\/2026/); assert.match(labels, /Renglón 4/);
  assert.doesNotMatch(labels, /technical-root/);
});
test('a manually zeroed carry-only preparation survives recovery and another preparation', async () => {
  const options = [opening('sew', 'SKU-1', 1)];
  const ui = setup([], { openingOptions: options }); await copy(ui, true, false);
  setCarry(ui, 'SKU-1', 'Costura', '0');
  assert.equal(ui.workspace.state().prepared, true);
  const prior = JSON.parse([...ui.storage.values()][0]);
  const recovered = setup([], { openingOptions: options, prior });
  await recovered.workspace.ready;
  assert.equal(carryAmount(recovered, 'SKU-1', 'Costura').value, '0');
  assert.equal(recovered.get('copy-products').checked, false);
  assert.equal(recovered.get('source').value, 'source-week');
  await copy(recovered, true, false);
  assert.equal(carryAmount(recovered, 'SKU-1', 'Costura').value, '0');
});

test('saving program retains a zeroed carryover preparation for reactivation', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }], { openingOptions: [opening('sew', 'SKU-1', 1)] });
  ui.get('copy-quantities').checked = true; await copy(ui, true);
  setCarry(ui, 'SKU-1', 'Costura', '0');
  await ui.get('review').events.click(); await ui.get('form').events.submit({ preventDefault() {} });
  const retained = JSON.parse([...ui.storage.values()][0]);
  assert.equal(retained.openings.values.sew, '0'); assert.equal(retained.deferredPreparation, true);
  assert.equal(retained.pendingSent, false); assert.equal(retained.rows.length, 0);
});

test('excluding carryover preserves edits and restores them on reactivation and local recovery', async () => {
  const ui = setup([], { openingOptions: [opening('sew', 'SKU-1', 1)] }); await copy(ui, true, false);
  setCarry(ui, 'SKU-1', 'Costura', '8'); ui.get('copy-openings').checked = false; ui.get('copy-openings').events.change();
  assert.equal(ui.openingEditor.changes().length, 0); assert.equal(ui.prepared.get('sew'), '8');
  const prior = JSON.parse([...ui.storage.values()][0]);
  const recovered = setup([], { openingOptions: [opening('sew', 'SKU-1', 1)], prior });
  await recovered.workspace.ready;
  assert.equal(recovered.openingEditor.changes().length, 0);
  recovered.get('copy-openings').checked = true; recovered.get('copy-openings').events.change();
  assert.equal(carryAmount(recovered, 'SKU-1', 'Costura').value, '8');
});
test('row selection excludes copied program without losing daily adjustments', async () => {
  const ui = setup([{ id: 'one', sku: 'SKU-1', day: 0, quantity: 15 }]); ui.get('copy-quantities').checked = true; await copy(ui);
  const selector = visit(ui.get('rows')).find(node => node.attributes['aria-label'] === 'Incluir producto SKU-1');
  selector.checked = false; selector.events.change();
  assert.equal(ui.get('count').textContent, '0 cambios');
  const restoredSelector = visit(ui.get('rows')).find(node => node.attributes['aria-label'] === 'Incluir producto SKU-1');
  restoredSelector.checked = true; restoredSelector.events.change(); await ui.get('review').events.click();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals[0].quantity, '15');
});
test('combined copy uses a single guarded review and ADMIN PIN for an open destination', async () => {
  const ui = setup([{ id: 'one', sku: 'SKU-1', day: 0, quantity: 50 }], { openingOptions: [opening('sew', 'SKU-1', 1)], weekStatus: 'Open' });
  ui.get('copy-quantities').checked = true; await copy(ui, true);
  await ui.get('review').events.click(); assert.equal(ui.get('save').disabled, true);
  ui.get('reason').value = 'Continuar pendientes'; ui.get('reason').events.input(); await ui.get('recheck').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.skuTotals.length, 1); assert.equal(payload.openings.length, 1); assert.equal(payload.reviewedFingerprint, 'reviewed-copy');
  assert.equal(ui.get('pin-panel').hidden, false);
});
test('carryover and program allow review of all 101 changes', async () => {
  const ui = setup([{ id: 'program', sku: 'SKU-1', day: 0, quantity: 50 }], { openingOptions: Array.from({ length: 100 }, (_, index) => opening(`root-${index}`)) });
  ui.get('copy-quantities').checked = true; await copy(ui, true);
  assert.equal(ui.prepared.size, 100); assert.equal(ui.get('rows').children.length, 1);
  await ui.get('review').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.skuTotals.length + payload.openings.length, 101);
});
test('stale carryover stops combined preparation without staging program', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 50 }], { openingOptions: [opening('sew')], staleOpening: true });
  await copy(ui, true); assert.equal(ui.get('rows').children.length, 0); assert.equal(ui.prepared.size, 0);
  assert.match(ui.get('message').children[0].textContent, /cambió/);
});
test('a changed source requires another review and keeps the quantities', async () => {
  const options = {}; const ui = setup([{ id: 'one', sku: 'SKU-1', day: 0, quantity: 50 }], options);
  ui.get('copy-quantities').checked = true; await copy(ui); options.copyVersion = 4;
  await ui.get('review').events.click(); assert.equal(ui.get('payload').value, '');
  assert.match(ui.get('message').children[0].textContent, /origen cambió/);
  await ui.get('review').events.click(); assert.equal(JSON.parse(ui.get('payload').value).skuTotals[0].quantity, '50');
});
test('legacy prepared previews recover into planning and report invalid SKU inline', async () => {
  const prior = { version: 0, inputMode: 'paste', preview: [{ sku: 'UNKNOWN', selected: true,
    days: ['10', '', '', '', '', '', ''], details: { notes: 'Preserved' } }] };
  const ui = setup([], { prior });
  await ui.workspace.ready;
  assert.equal(ui.get('rows').children.length, 1); await ui.get('review').events.click();
  assert.equal(ui.get('payload').value, ''); assert.match(ui.get('message').children[0].textContent, /SKU activo/);
});
test('legacy copy previews recover directly into planning with their edited quantities', async () => {
  const prior = { version: 0, previewSource: { id: 'source-week', version: 3 }, preview: [{ sku: 'SKU-1', selected: true,
    days: ['17', '', '', '', '', '', ''], suggestedDays: [0], details: {} }] };
  const ui = setup([], { prior }); await ui.workspace.ready;
  assert.equal(ui.get('rows').children.length, 1); assert.equal(ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0].value, '17');
  assert.equal(ui.get('copy-panel').hidden, false);
});

test('recovered pasted rows retain notes and selection, and archived text survives a confirmed save', async () => {
  const raw = 'SKU\tLunes\nSKU-1\t17';
  const prior = { version: 0, inputMode: 'paste', pasteText: raw, preview: [{ sku: 'SKU-1', selected: true,
    days: ['17', '', '', '', '', '', ''], details: { notes: 'Old note', orderReference1: 'OLD-PO' } }] };
  const ui = setup([], { prior });
  await ui.workspace.ready;
  assert.equal(ui.get('copy-panel').hidden, true);
  await ui.get('review').events.click();
  const line = JSON.parse(ui.get('payload').value).skuTotals[0];
  assert.equal(line.quantity, '17'); assert.equal(line.notes, 'Old note'); assert.equal(line.orderReference1, 'OLD-PO');
  assert.equal(ui.get('payload').value.includes(raw), false);
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.equal([...ui.storage].find(([key]) => key.endsWith(':legacy-paste'))[1], raw);
  const reloaded = setup([], { legacyText: raw });
  assert.equal(reloaded.get('legacy-text').textContent, raw);
  assert.equal(reloaded.workspace.state().pending, false);
});

test('legacy excluded rows remain excluded on recovery and old copy links open the panel', async () => {
  const ui = setup([], { prior: { version: 0, preview: [{ sku: 'SKU-1', selected: false,
    days: ['17', '', '', '', '', '', ''], details: {} }] } });
  await ui.workspace.ready;
  const included = visit(ui.get('rows')).find(node => node.tag === 'input' && node.type === 'checkbox');
  assert.equal(included.checked, false);
  await ui.get('review').events.click();
  assert.equal(ui.get('payload').value, '');
  included.checked = true; included.events.change();
  await ui.get('review').events.click();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals[0].quantity, '17');
  const linked = setup([], { requestMode: 'copy' });
  assert.equal(linked.get('copy-panel').hidden, false);
  assert.equal(linked.get('search').hidden, false);
});

test('saving a partial copied program keeps its blank days and restores already saved rows alongside them', async () => {
  const source = [{ id: 'mon', sku: 'SKU-1', day: 0, quantity: 10 }, { id: 'tue', sku: 'SKU-1', day: 1, quantity: 20 }];
  const ui = setup(source); ui.get('copy-quantities').checked = true; await copy(ui);
  const fields = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  fields[1].value = ''; fields[1].events.input(); await ui.get('review').events.click();
  await ui.get('form').events.submit({ preventDefault() {} });
  const prior = JSON.parse([...ui.storage].find(([key]) => key.startsWith('warehouse-epi:schedule:') && !key.endsWith(':saved'))[1]);
  assert.deepEqual(prior.rows[0].suggestedDays, [1]); assert.equal(prior.rows[0].cells[0].value, '');
  const recovered = setup(source, { prior, savedLines: [{ Id: 'saved-mon', Version: 1, ProductId: firstId, Sku: 'SKU-1', Unit: 'EA', Day: 0, Quantity: 10 }] });
  await recovered.workspace.ready;
  assert.equal(recovered.get('rows').children.length, 1);
  assert.ok(visit(recovered.get('rows')).some(node => node.tag === 'input' && node.value === '10'));
});
test('confirmed row deletion hides saved days, stages them, and can be undone', async () => {
  const savedLines = [
    { Id: 'monday', Version: 2, ProductId: firstId, Sku: 'SKU-1', Unit: 'EA', Day: 0, Quantity: 1 },
    { Id: 'thursday', Version: 3, ProductId: firstId, Sku: 'SKU-1', Unit: 'EA', Day: 3, Quantity: 4 }
  ];
  const ui = setup([], { savedLines });
  const action = label => visit(ui.get('rows')).find(node => node.tag === 'button' && node.textContent === label);
  const undo = () => visit(ui.get('message')).find(node => node.tag === 'button' && node.textContent === 'Deshacer');
  assert.equal(action('Duplicar fila'), undefined);
  action('Eliminar fila').events.click();
  assert.match(ui.confirmations[0], /SKU-1/);
  assert.equal(ui.get('rows').children.length, 0);
  assert.equal(ui.get('empty').hidden, false);
  assert.ok(undo());
  await ui.get('review').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.skuTotals.length, 2);
  assert.ok(payload.skuTotals.every(change => change.quantity === '0'));
  undo().events.click();
  assert.equal(ui.get('count').textContent, '0 cambios');
  assert.equal(ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0].value, '1');
  const tuesday = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[1];
  tuesday.value = '2'; tuesday.events.input();
  action('Eliminar fila').events.click();
  await ui.get('review').events.click();
  assert.ok(JSON.parse(ui.get('payload').value).skuTotals.every(change => change.quantity === '0'));
  undo().events.click();
  assert.equal(ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[1].value, '2');

  const cancelled = setup([], { savedLines, confirmDelete: false });
  visit(cancelled.get('rows')).find(node => node.tag === 'button' && node.textContent === 'Eliminar fila').events.click();
  assert.equal(cancelled.confirmations.length, 1);
  assert.equal(cancelled.get('rows').children.length, 1);
  assert.equal(cancelled.get('count').textContent, '0 cambios');
});

test('saved day cells omit the Quitar control and capture warning', async () => {
  const ui = setup([], { savedLines: [{ Id: 'monday', Version: 2, ProductId: firstId,
    Sku: 'SKU-1', Unit: 'EA', Day: 0, Quantity: 10, CanRemove: false,
    RemovalReason: 'Este renglón tiene capturas registradas, incluso si fueron revertidas.' }] });
  const row = ui.get('rows').children[0];
  const nodes = visit(row);
  assert.equal(nodes.some(node => node.tag === 'button' && node.textContent === 'Quitar'), false);
  assert.equal(nodes.some(node => node.textContent?.includes('Este renglón tiene capturas')), false);
  assert.equal(nodes.find(node => node.tag === 'button' && node.textContent === 'Eliminar fila').disabled, true);
  const quantity = row.querySelectorAll('input.production-week-workspace__quantity')[0];
  quantity.value = ''; quantity.events.input();
  await ui.get('review').events.click();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals[0].quantity, '0'); // Server review enforces the historic minimum.
});

test('delete row removes an unsaved product from preparation', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }]);
  ui.get('source').value = 'source-week';
  await ui.get('copy').events.click();
  assert.equal(ui.get('rows').children.length, 1);
  visit(ui.get('rows')).find(node => node.tag === 'button' && node.textContent === 'Eliminar fila').events.click();
  assert.match(ui.confirmations[0], /preparación/);
  assert.equal(ui.get('rows').children.length, 0);
  visit(ui.get('message')).find(node => node.tag === 'button' && node.textContent === 'Deshacer').events.click();
  assert.equal(ui.get('rows').children.length, 1);
});

test('Enter advances through the weekly quantities and preserves notes', async () => {
  const ui = setup();
  const search = ui.get('search'); search.value = 'SKU-1';
  await search.events.keydown({ key: 'Enter', preventDefault() {} });
  const inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  inputs[0].value = '10'; inputs[0].events.input();
  let prevented = false;
  inputs[0].events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(inputs[1].focused, true);
  inputs[1].value = '5'; inputs[1].events.input();
  const visit = node => [node, ...node.children.flatMap(visit)];
  const notes = visit(ui.get('rows').children[0]).find(node => node.id?.endsWith('-notes'));
  notes.value = 'Preparar primero'; notes.events.input();
  assert.equal(inputs[0].value, '10');
  assert.equal(inputs[1].value, '5');
  assert.match(ui.get('day-summary').textContent, /Martes: 5 EA/);
  await ui.get('review').events.click();
  const changes = JSON.parse(ui.get('payload').value).skuTotals;
  assert.equal(changes.length, 2);
  assert.equal(changes[0].notes, 'Preparar primero');
});

test('clear all is offered for a prepared copy without quantities and cancellation keeps it intact', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], { confirmDelete: false });
  assert.equal(ui.get('clear-all').hidden, true);
  ui.get('copy-toggle').events.click();
  assert.equal(ui.get('clear-all').hidden, true);
  await copy(ui);
  assert.equal(ui.get('count').textContent, '0 cambios');
  assert.equal(ui.get('clear-all').hidden, false);
  assert.equal(ui.get('clear-all').disabled, false);
  const before = [...ui.storage];
  ui.get('clear-all').events.click();
  assert.match(ui.confirmations[0], /El programa guardado se conservará/);
  assert.deepEqual([...ui.storage], before);
  assert.deepEqual(ui.navigations, []);
  assert.equal(ui.get('rows').children.length, 1);
});

test('clear all discards copy, carryover and manual edits only for this user and week without posting', async () => {
  const savedLines = [{ Id: 'saved', Version: 1, ProductId: secondId, Sku: 'SKU-2', Unit: 'EA', Day: 2, Quantity: 20, Notes: 'Guardado' }];
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], { savedLines, legacyText: 'Respaldo anterior',
    openingOptions: [{ key: 'carry', sourceWeekId: 'source-week', productId: firstId, sku: 'SKU-1', unit: 'EA', area: 0, available: 40, selected: 0 }] });
  const fields = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  fields[2].value = '30'; fields[2].events.input();
  const notes = visit(ui.get('rows')).find(node => node.id?.endsWith('-notes'));
  notes.value = 'Sin guardar'; notes.events.input();
  await copy(ui, true);
  await ui.get('review').events.click();
  assert.ok(ui.get('payload').value);
  const key = [...ui.storage.keys()].find(key => !key.endsWith(':legacy-paste'));
  ui.storage.set('warehouse-epi:schedule:other-user:other-week', 'Otra preparación');
  const beforeRequests = ui.requests.length;
  ui.get('clear-all').events.click();
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.equal(ui.storage.has(key), false);
  assert.equal(ui.storage.get(`${key}:legacy-paste`), 'Respaldo anterior');
  assert.equal(ui.storage.get('warehouse-epi:schedule:other-user:other-week'), 'Otra preparación');
  assert.deepEqual(ui.navigations, ['https://localhost/schedule?WeekId=current&View=program']);
  assert.equal(ui.requests.length, beforeRequests);
  assert.equal(ui.saveRequests(), 0);
  let blocked = false;
  ui.windowEvents.beforeunload({ preventDefault() { blocked = true; } });
  assert.equal(blocked, false);
  // A late edit handler cannot recreate the discarded key before navigation completes.
  fields[2].events.input();
  assert.equal(ui.storage.has(key), false);
  const reload = setup([], { savedLines });
  assert.equal(reload.workspace.state().status, 'saved');
  assert.equal(reload.get('clear-all').hidden, true);
  assert.equal(reload.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[2].value, '20');
});

test('clear all cannot discard recovery or an uncertain save', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], { lostSave: true });
  ui.get('copy-quantities').checked = true; await copy(ui);
  await ui.get('review').events.click();
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.equal(ui.workspace.state().status, 'uncertain');
  assert.equal(ui.get('clear-all').disabled, true);
  const before = [...ui.storage];
  ui.get('clear-all').events.click();
  assert.deepEqual([...ui.storage], before);
  assert.deepEqual(ui.navigations, []);
  assert.equal(ui.confirmations.length, 0);
  const recovering = setup([], { prior: JSON.parse(before[0][1]) });
  assert.equal(recovering.workspace.state().status, 'recovery');
  assert.equal(recovering.get('clear-all').disabled, true);
  recovering.get('clear-all').events.click();
  assert.equal(recovering.confirmations.length, 0);
  assert.equal(recovering.storage.size, 1);
});

test('storage failure while clearing keeps the preparation and navigation protection', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], { failClear: true });
  await copy(ui);
  const before = [...ui.storage];
  ui.get('clear-all').events.click();
  assert.deepEqual([...ui.storage], before);
  assert.deepEqual(ui.navigations, []);
  assert.match(ui.get('message').children[0].textContent, /No se pudieron limpiar/);
  let blocked = false;
  ui.windowEvents.beforeunload({ preventDefault() { blocked = true; } });
  assert.equal(blocked, true);
});


test('automatic recovery enables clear all and clearing removes only the pending preparation', async () => {
  const original = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }]);
  original.get('copy-quantities').checked = true; await copy(original);
  const prior = JSON.parse([...original.storage.values()][0]);
  const recovered = setup([], { prior });
  await recovered.workspace.ready;
  assert.equal(recovered.get('recovery').hidden, true);
  assert.equal(recovered.get('clear-all').disabled, false);
  assert.equal(recovered.get('clear-all').hidden, false);
  recovered.get('clear-all').events.click();
  assert.equal(recovered.storage.size, 0);
  assert.equal(recovered.saveRequests(), 0);
  assert.equal(recovered.confirmations.length, 1);
});


test('automatic recovery retains a required reconciliation across another reload', async () => {
  const original = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }]);
  original.get('copy-quantities').checked = true; await copy(original);
  const prior = JSON.parse([...original.storage.values()][0]); prior.version = -1;
  const recovered = setup([], { prior }); await recovered.workspace.ready;
  assert.equal(recovered.get('recovery').hidden, false);
  assert.equal(recovered.get('clear-all').disabled, false);
  const again = setup([], { prior: JSON.parse([...recovered.storage.values()][0]) });
  await again.workspace.ready;
  assert.equal(again.get('recovery').hidden, false);
  assert.ok(visit(again.get('recovery')).some(node => node.textContent === 'Usar la semana actual como base y revisar'));
});


test('opening reviews copied amounts despite blank suggestions and retains them after saving', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }, { sku: 'SKU-1', day: 1, quantity: 20 }]);
  ui.get('copy-quantities').checked = true; await copy(ui);
  const fields = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  fields[1].value = ''; fields[1].events.input();
  assert.equal(ui.workspace.state().prepared, true);
  await ui.workspace.openReview();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals.length, 1);
  assert.equal(ui.saveRequests(), 0);
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.deepEqual(ui.navigations, ['/review']);
  const prior = JSON.parse([...ui.storage.values()][0]);
  assert.equal(ui.preparation.inspect(prior).pending, true);
  assert.equal(ui.preparation.inspect(prior).blocksOpening, false);
  const recovered = setup([], { prior }); await recovered.workspace.ready;
  assert.equal(recovered.workspace.state().status, 'saved');
  assert.equal(recovered.get('progress-warning').hidden, true);
  assert.equal(recovered.get('clear-all').hidden, false);
  await recovered.workspace.openReview();
  assert.deepEqual(recovered.navigations, ['/review']);
  assert.equal(recovered.saveRequests(), 0);
});

test('excluded copied products are discarded when continuing to opening', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }]);
  ui.get('copy-quantities').checked = true; await copy(ui);
  const selector = visit(ui.get('rows')).find(node => node.attributes['aria-label'] === 'Incluir producto SKU-1');
  selector.checked = false; selector.events.change();
  await ui.workspace.openReview();
  assert.deepEqual(ui.navigations, ['/review']);
  assert.equal(ui.storage.size, 0);
  assert.equal(ui.saveRequests(), 0);
});


test('new-week confirmation transfers empty suggestions to the confirmed week', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }, { sku: 'SKU-1', day: 1, quantity: 20 }], { newWeek: true });
  ui.get('copy-quantities').checked = true; await copy(ui);
  const fields = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  fields[1].value = ''; fields[1].events.input();
  await ui.workspace.openReview();
  assert.equal(ui.saveRequests(), 0);
  assert.equal(ui.get('pin').focused, true);
  await ui.get('form').events.submit({ preventDefault() {} });
  assert.deepEqual(ui.navigations, ['/schedule?WeekId=created-week']);
  assert.equal(ui.storage.has('warehouse-epi:schedule:admin:new'), false);
  const remaining = JSON.parse(ui.storage.get('warehouse-epi:schedule:admin:created-week'));
  assert.equal(remaining.rows[0].cells[1].value, '');
  assert.equal(ui.preparation.inspect(remaining).blocksOpening, false);
});

test('review waits for automatic recovery instead of asking to incorporate it', async () => {
  const original = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }]);
  original.get('copy-quantities').checked = true; await copy(original);
  const prior = JSON.parse([...original.storage.values()][0]);
  const ui = setup([], { prior });
  await ui.workspace.openReview();
  assert.equal(JSON.parse(ui.get('payload').value).skuTotals.length, 1);
  assert.equal(ui.saveRequests(), 0);
});


test('pending saves, reconciliation and explicit zero totals still require confirmation', () => {
  const { inspect } = setup().preparation;
  for (const state of [{ pendingSent: true }, { needsReconcile: true }, { recoveryPending: true },
    { openings: { format: 2, values: { 'product:0': '0' } } }]) assert.equal(inspect(state).blocksOpening, true);
  assert.equal(inspect({ deferredPreparation: true, openingChangeCount: 0 }).blocksOpening, false);
});

test('retained suggestions do not announce unsaved changes but remain clearable', () => {
  const { inspect } = setup().preparation;
  for (const state of [
    { deferredPreparation: true },
    { rows: [{ productId: firstId, cells: [{ value: '', copyOrigin: true }] }] },
    { rows: [{ productId: firstId, included: false, cells: [{ value: '20' }] }] },
    { excludedProducts: [firstId], skuDayTargets: { [firstId]: { 0: '50' } } },
    { deferredPreparation: true, openings: { values: { 'product:0': '0' } }, openingChangeCount: 0 }
  ]) {
    const result = inspect(state);
    assert.equal(result.pending, true);
    assert.equal(result.status, 'saved');
    assert.equal(result.changed, false);
    assert.equal(result.blocksOpening, false);
  }
  assert.equal(inspect({ deferredPreparation: true, openingChangeCount: 1 }).status, 'changed');
});


test('excluding a product omits its copied program and all carryover, including after recovery', async () => {
  const options = [opening('sew', 'SKU-1', 1)];
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 15 }], { openingOptions: options });
  ui.get('copy-quantities').checked = true; await copy(ui, true);
  const checkbox = visit(ui.get('rows')).find(node => node.attributes['aria-label'] === 'Incluir producto SKU-1');
  checkbox.checked = false; checkbox.events.change();
  assert.equal(ui.get('count').textContent, '0 cambios');
  const prior = JSON.parse([...ui.storage.values()][0]);
  const recovered = setup([], { prior, openingOptions: options }); await recovered.workspace.ready;
  assert.equal(recovered.get('count').textContent, '0 cambios');
  const restored = visit(recovered.get('rows')).find(node => node.attributes['aria-label'] === 'Incluir producto SKU-1');
  restored.checked = true; restored.events.change();
  await recovered.get('review').events.click();
  const payload = JSON.parse(recovered.get('payload').value);
  assert.equal(payload.skuTotals.length, 1); assert.equal(payload.openings.length, 1);
});


test('legacy saved rows and undistributed totals recover into one editable daily total', async () => {
  const savedLines = ['a','b'].map((Id,index) => ({ Id, Version: 1, ProductId: firstId, Sku:'SKU-1', Unit:'EA', Day:0, Quantity:20, OrderReference1:'000'+index }));
  const original=setup([], {savedLines});
  assert.equal(original.get('rows').children.length,1);
  const field=original.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0];
  assert.equal(field.value,'40');
  field.value='45'; field.events.input();
  const prior=JSON.parse([...original.storage.values()][0]); prior.skuDayTargets={ [firstId]: {0:'55'} };
  const restored=setup([], {savedLines,prior}); await restored.workspace.ready;
  assert.equal(restored.get('rows').children.length,1);
  assert.equal(restored.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity')[0].value,'55');
  await restored.get('review').events.click();
  const payload=JSON.parse(restored.get('payload').value);
  assert.equal(payload.changes.length,0); assert.equal(payload.skuTotals.length,1);
  assert.equal(payload.skuTotals[0].quantity,'55');
  assert.deepEqual([payload.skuTotals[0].orderReference1,payload.skuTotals[0].orderReference2],['0000','0001']);
});

test('copy sums the 20/30 example and keeps a manually entered zero through copy and recovery', async () => {
  const source=[0,3,0,3].map((day,i)=>({id:'source-'+i,sku:'SKU-1',day,quantity:day===0?20:30}));
  const ui=setup(source);ui.get('copy-quantities').checked=true;await copy(ui);
  let fields=ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  assert.equal(fields[0].value,'40');assert.equal(fields[3].value,'60');
  fields[0].value='0';fields[0].events.input();await copy(ui);
  const recovered=setup(source,{prior:JSON.parse([...ui.storage.values()][0])});await recovered.workspace.ready;await copy(recovered);
  fields=recovered.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
  assert.equal(fields[0].value,'0');assert.equal(fields[3].value,'60');
});

test('more than three packing references require an explicit selection and keep full notes', async () => {
  const source=Array.from({length:4},(_,i)=>({id:'copy-'+i,sku:'SKU-1',day:0,quantity:5,orderReference1:'000'+i,notes:'note-'+i}));
  const ui=setup(source);ui.get('copy-quantities').checked=true;await copy(ui);
  await ui.get('review').events.click();assert.equal(ui.get('payload').value,'');
  assert.match(ui.get('message').children[0].textContent,/tres pedidos/);
  visit(ui.get('rows')).find(node=>node.tag==='button'&&node.textContent==='Usar estos pedidos').events.click();
  await ui.get('review').events.click();
  const total=JSON.parse(ui.get('payload').value).skuTotals[0];
  assert.equal(total.quantity,'20');assert.deepEqual([total.orderReference1,total.orderReference2,total.orderReference3],['0000','0001','0002']);
  assert.equal(total.notes,'note-0\nnote-1\nnote-2\nnote-3');
});
