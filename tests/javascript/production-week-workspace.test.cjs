const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const parser = require('../../src/WarehouseEPI.Web/wwwroot/js/production-week-paste.js');

const script = fs.readFileSync(path.join(__dirname,
  '../../src/WarehouseEPI.Web/wwwroot/js/production-week-workspace.js'), 'utf8');
const visit = node => [node, ...node.children.flatMap(visit)];
const product = (id, sku, unit = 'EA', allowsDecimals = false) => ({ id, sku, unit, allowsDecimals });
const firstId = '00000000-0000-0000-0000-000000000002';
const secondId = '00000000-0000-0000-0000-000000000003';

class Element {
  constructor(tag = 'div') {
    this.tag = tag; this.children = []; this.dataset = {}; this.events = {};
    this.style = {}; this.value = ''; this.hidden = false; this.className = '';
    this.attributes = {}; this.classList = { toggle() {} };
  }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = [...nodes]; }
  remove() { this.removed = true; }
  addEventListener(name, handler) { this.events[name] = handler; }
  setAttribute(key, value) { this.attributes[key] = value; }
  removeAttribute(key) { delete this.attributes[key]; }
  focus() { this.focused = true; }
  scrollIntoView() {}
  getBoundingClientRect() { return { left: 24, bottom: 100, width: 200 }; }
  get lastElementChild() { return this.children.at(-1); }
  querySelectorAll(selector) {
    const nodes = this.children.flatMap(child => [child, ...child.querySelectorAll(selector)]);
    if (selector === 'input.production-week-workspace__quantity')
      return nodes.filter(node => node.tag === 'input' && node.className.includes('production-week-workspace__quantity'));
    return [];
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  closest() { return { hidden: false }; }
}

function setup(copyRows = [], options = {}) {
  const selectors = new Map();
  const keys = ['rows', 'message', 'search', 'results', 'form', 'review-panel', 'day',
    'summary', 'count', 'empty', 'day-summary', 'review', 'back', 'payload', 'save',
    'review-list', 'paste-preview', 'paste', 'preview-paste', 'copy', 'source',
    'copy-quantities', 'copy-products', 'copy-openings', 'recovery'];
  for (const key of keys) selectors.set(`[data-workspace-${key}]`, new Element());
  selectors.get('[data-workspace-copy-products]').checked = true;
  const root = new Element();
  const openingRoot = new Element();
  root.dataset = { weekId: '00000000-0000-0000-0000-000000000001', weekVersion: '0',
    weekStart: '2026-09-21', userId: 'admin', searchUrl: '/search',
    resolveUrl: '/resolve', pasteUrl: '/paste', copyUrl: '/copy', operationUrl: '/operation' };
  root.querySelector = selector => selector === '[data-opening-editor]' ? openingRoot : selectors.get(selector) || null;
  const views = ['week', 'day'].map(name => {
    const button = new Element('button'); button.dataset.workspaceView = name; return button;
  });
  root.querySelectorAll = selector => {
    if (selector === '[data-workspace-view]') return views;
    if (selector === '[data-line]' ||
        selector === '[data-workspace-total]' || selector.includes('[data-workspace-day-head]')) return [];
    return [];
  };
  const body = new Element('body');
  const document = { body, querySelector: selector => selector === '[data-week-workspace]' ? root : null,
    createElement: tag => new Element(tag) };
  const storage = new Map();
  if (options.prior) storage.set(`warehouse-epi:schedule:admin:${root.dataset.weekId}`, JSON.stringify(options.prior));
  const store = { getItem: key => storage.get(key) || null,
    setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) };
  const timers = [];
  let openingOptions = options.openingOptions || [];
  const prepared = new Map();
  const openingEditor = {
    ready: Promise.resolve(),
    options: () => openingOptions.map(row => ({ ...row, prepared: prepared.get(row.key) || row.prepared || 0,
      protected: row.protected || prepared.has(row.key) })),
    changes: () => [...prepared].map(([key, quantity]) => ({ key, quantity })),
    errors: () => [], reveal() {},
    snapshot: () => ({ values: Object.fromEntries(prepared) }),
    restore: state => { for (const [key, value] of Object.entries(state?.values || state || {})) if (value) prepared.set(key, value); },
    prepare: async entries => {
      if (options.staleOpening) throw Error('El pendiente de origen cambió. Prepara de nuevo el arrastre.');
      entries.forEach(entry => prepared.set(entry.key, entry.quantity));
      return entries.length;
    },
    describe: change => `Arrastre ${change.key}: ${change.quantity}`
  };
  const fetch = async url => {
    const target = new URL(url, 'https://localhost');
    if (target.pathname === '/search') return { ok: true, json: async () =>
      (options.products || [{ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1',
        description: 'Descripción muy larga que antes se solapaba', unit: 'EA', allowsDecimals: false }]) };
    if (target.pathname === '/copy') return { ok: true, json: async () =>
      ({ id: 'source-week', version: options.copyVersion ?? 3, rows: copyRows.map(row => ({
        productId: '00000000-0000-0000-0000-000000000002', unit: 'EA', allowsDecimals: false, ...row })) }) };
    if (target.pathname === '/resolve') return { ok: true, json: async () =>
      ({ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1' }) };
    if (target.pathname === '/paste') return { ok: true, json: async () =>
      (options.activeProducts || options.products || [{ id: '00000000-0000-0000-0000-000000000002', sku: 'SKU-1', unit: 'EA', allowsDecimals: false }]) };
    throw Error(`Unexpected ${target}`);
  };
  vm.runInNewContext(script, { document, URL, crypto: webcrypto,
    matchMedia: () => ({ matches: false }),
    window: { location: { origin: 'https://localhost', reload() {} },
      addEventListener() {}, ProductionWeekPaste: parser,
      ProductionWeekOpenings: { get: () => options.openingOptions ? openingEditor : null } },
    location: { origin: 'https://localhost', reload() {} },
    localStorage: store, sessionStorage: store, structuredClone,
    setTimeout: handler => { timers.push(handler); return timers.length; },
    clearTimeout() {}, fetch, FormData: class {} });
  return { get: key => selectors.get(`[data-workspace-${key}]`), storage, views, body, openingEditor, prepared,
    async flushSearch() { for (const handler of timers.splice(0)) await handler(); } };
}

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
  ui.get('review').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.changes.length, 1);
  assert.equal(payload.changes[0].line.quantity, '20');
  assert.equal(ui.get('count').textContent, '1 cambio');
});

for (const includeQuantities of [false, true]) {
  test(`copy is visible and preserves Monday and Thursday with quantities=${includeQuantities}`, async () => {
    const ui = setup([
      { sku: 'SKU-1', day: 0, quantity: 10 },
      { sku: 'SKU-1', day: 3, quantity: 25 }
    ]);
    ui.get('source').value = 'source-week';
    ui.get('copy-quantities').checked = includeQuantities;
    await ui.get('copy').events.click();
    const preview = ui.get('paste-preview');
    assert.equal(preview.hidden, false);
    assert.equal(preview.focused, true);
    const visit = node => [node, ...node.children.flatMap(visit)];
    const labels = visit(preview).map(node => node.textContent);
    assert.ok(labels.includes('Revisar copia de semana'));
    assert.equal(visit(preview).filter(node => node.tag === 'input' && node.value === 'SKU-1').length, 1);
    assert.ok(!labels.includes('Días de origen'));
    if (!includeQuantities) {
      const emptyDays = visit(preview).filter(node => node.tag === 'input' && node.placeholder === 'Programar');
      assert.equal(emptyDays.length, 2);
    }
    await preview.lastElementChild.events.click();
    assert.equal(ui.get('rows').children.length, 1);
    const inputs = ui.get('rows').children[0].querySelectorAll('input.production-week-workspace__quantity');
    assert.equal(inputs[0].value, includeQuantities ? '10' : '');
    assert.equal(inputs[3].value, includeQuantities ? '25' : '');
    if (!includeQuantities) {
      assert.equal(inputs[0].placeholder, 'Programar');
      assert.equal(inputs[3].placeholder, 'Programar');
      assert.equal(inputs[1].placeholder, undefined);
    }
    assert.equal(preview.hidden, true);
  });
}

test('an empty source explains why there are no products to copy', async () => {
  const ui = setup();
  ui.get('source').value = 'source-week';
  await ui.get('copy').events.click();
  assert.equal(ui.get('paste-preview').hidden, true);
  assert.match(ui.get('message').children[0].textContent, /no tiene productos activos/);
});

const opening = (key, sku = 'SKU-1', area = 0, extra = {}) => ({
  key, sourceWeekId: 'source-week', sourceStart: '2026-09-14', sourceLineId: key,
  productId: sku === 'SKU-1' ? firstId : secondId, sku, unit: 'EA', allowsDecimals: false,
  sequence: 1, area, available: 12, selected: 0, prepared: 0, protected: extra.prepared > 0,
  fingerprint: 'fresh', ...extra
});
const carryCheck = (preview, sku) => visit(preview).find(node => node.tag === 'input' && node.attributes['aria-label'] === `Traer arrastre de ${sku}`);
const carryInputs = preview => visit(preview).filter(node => node.className === 'form-control form-control-sm production-opening-quantity');

test('copy can bring only selected carryover for a SKU without scheduled rows', async () => {
  const ui = setup([], { openingOptions: [opening('cut'), opening('sew', 'SKU-1', 1), opening('other', 'SKU-2')] });
  ui.get('source').value = 'source-week';
  ui.get('copy-products').checked = false; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  assert.equal(ui.get('paste-preview').hidden, false);
  assert.equal(visit(ui.get('paste-preview')).filter(node => node.textContent === 'Solo arrastre').length, 2);
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  assert.equal(ui.openingEditor.changes().length, 2);
  assert.equal(ui.prepared.get('cut'), '12');
  assert.equal(ui.prepared.has('other'), false);
});

test('combined copy keeps prior carryover and lets the operator reduce a new area', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }], { openingOptions: [
    opening('cut', 'SKU-1', 0, { prepared: 7 }), opening('sew', 'SKU-1', 1)
  ] });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  assert.equal(ui.get('paste-preview').hidden, false);
  const fields = carryInputs(ui.get('paste-preview'));
  assert.equal(fields[0].disabled, true);
  fields[1].value = '4'; fields[1].events.input();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.prepared.get('sew'), '4');
  assert.equal(ui.prepared.has('cut'), false);
  assert.equal(ui.get('rows').children.length, 1);
});

test('combined review sends product changes and carryover in one payload', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }], { openingOptions: [opening('cut')] });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  ui.get('copy-quantities').checked = true; await ui.get('copy').events.click();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  await ui.get('review').events.click();
  const payload = JSON.parse(ui.get('payload').value);
  assert.equal(payload.changes.length, 1);
  assert.equal(payload.openings.length, 1);
});

test('changed source leaves carryover preview intact for review', async () => {
  const ui = setup([], { openingOptions: [opening('cut')], staleOpening: true });
  ui.get('source').value = 'source-week'; ui.get('copy-products').checked = false;
  ui.get('copy-openings').checked = true; await ui.get('copy').events.click();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.openingEditor.changes().length, 0);
  assert.match(ui.get('message').children[0].textContent, /cambió/);
  assert.equal(ui.get('paste-preview').hidden, false);
});

test('draft or exhausted source offers no carryover to copy', async () => {
  const ui = setup([], { openingOptions: [] });
  ui.get('source').value = 'source-week'; ui.get('copy-products').checked = false;
  ui.get('copy-openings').checked = true; await ui.get('copy').events.click();
  assert.equal(ui.get('paste-preview').hidden, true);
  assert.match(ui.get('message').children[0].textContent, /no tiene pendientes disponibles/);
});

test('carryover copy enforces the combined 100-change limit before staging', async () => {
  const ui = setup([], { openingOptions: Array.from({ length: 101 }, (_, i) => opening(`line-${i}`)) });
  ui.get('source').value = 'source-week'; ui.get('copy-products').checked = false;
  ui.get('copy-openings').checked = true; await ui.get('copy').events.click();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.openingEditor.changes().length, 0);
  assert.match(ui.get('message').children[0].textContent, /máximo de 100/);
});

test('local recovery restores an unconfirmed carryover selection', async () => {
  const prior = { version: 0, updated: new Date().toISOString(), rows: [], preview: [],
    openingPreviewSourceId: 'source-week', openingPreviewSourceVersion: 3, openingColumnsPrepared: true,
    openingPreview: [{ productId: firstId, sku: 'SKU-1', selected: true, entries: [{
      key: 'cut', fingerprint: 'fresh', area: 0, sourceStart: '2026-09-14', sequence: 1,
      available: 12, prepared: 0, unit: 'EA', allowsDecimals: false, quantity: '8' }] }] };
  const ui = setup([], { openingOptions: [opening('cut')], prior });
  visit(ui.get('recovery')).find(node => node.tag === 'button').events.click();
  assert.equal(ui.get('paste-preview').hidden, false);
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.prepared.get('cut'), '8');
});

test('repeated SKU shows its carryover once and sums multiple origins in one area', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }, { sku: 'SKU-1', day: 0, quantity: 6 }],
    { openingOptions: [opening('cut-a'), opening('cut-b', 'SKU-1', 0, { sequence: 2, available: 8 })] });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  const preview = ui.get('paste-preview');
  assert.equal(visit(preview).filter(node => node.attributes['aria-label'] === 'Traer arrastre de SKU-1').length, 1);
  const summary = visit(preview).find(node => node.tag === 'summary');
  assert.match(summary.textContent, /Corte: 20 EA/);
  const fields = carryInputs(preview); fields[1].value = '3'; fields[1].events.input();
  assert.match(summary.textContent, /Corte: 15 EA/);
});

test('unchecking carryover hides columns and preserves amounts for rechecking', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }], { openingOptions: [opening('cut')] });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  const field = carryInputs(ui.get('paste-preview'))[0]; field.value = '4'; field.events.input();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  ui.get('copy-openings').checked = false; ui.get('copy-openings').events.change();
  assert.equal(carryInputs(ui.get('paste-preview')).length, 0);
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.openingEditor.changes().length, 0);
  assert.equal(ui.get('rows').children.length, 1);
  ui.get('copy-openings').checked = true; ui.get('copy-openings').events.change();
  assert.equal(carryInputs(ui.get('paste-preview'))[0].value, '4');
  assert.equal(carryCheck(ui.get('paste-preview'), 'SKU-1').checked, true);
});

test('changed source version stops combined incorporation before staging either selection', async () => {
  const options = { openingOptions: [opening('cut')], copyVersion: 3 };
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }], options);
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  options.copyVersion = 4;
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  assert.equal(ui.openingEditor.changes().length, 0);
  assert.match(ui.get('message').children[0].textContent, /semana origen cambió/);
});

test('stale carry origin stops combined incorporation before product rows are staged', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }],
    { openingOptions: [opening('cut')], staleOpening: true });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  const check = carryCheck(ui.get('paste-preview'), 'SKU-1'); check.checked = true; check.events.change();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  assert.equal(ui.openingEditor.changes().length, 0);
});

test('changing a copied SKU leaves carryover on its original SKU as a carry-only row', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 5 }],
    { openingOptions: [opening('cut')], products: [product(firstId, 'SKU-1'), product(secondId, 'SKU-2')] });
  ui.get('source').value = 'source-week'; ui.get('copy-openings').checked = true;
  await ui.get('copy').events.click();
  const picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'SKU-2'; picker.events.input(); await ui.flushSearch();
  ui.body.children.at(-1).children[1].events.click();
  const preview = ui.get('paste-preview');
  assert.equal(visit(preview).filter(node => node.textContent === 'Solo arrastre').length, 1);
  assert.ok(carryCheck(preview, 'SKU-1'));
  assert.equal(carryCheck(preview, 'SKU-2'), undefined);
});

test('copy groups seven distinct days but keeps an additional same-day line separate', async () => {
  const ui = setup([
    ...Array.from({ length: 7 }, (_, day) => ({ sku: 'SKU-1', day, quantity: day + 1 })),
    { sku: 'SKU-1', day: 0, quantity: 90 }
  ]);
  ui.get('source').value = 'source-week';
  ui.get('copy-quantities').checked = true;
  await ui.get('copy').events.click();
  const stored = JSON.parse([...ui.storage.values()][0]);
  assert.equal(stored.preview.length, 2);
  assert.deepEqual(stored.preview[0].suggestedDays, [0, 1, 2, 3, 4, 5, 6]);
  assert.deepEqual(stored.preview[0].days, ['1', '2', '3', '4', '5', '6', '7']);
  assert.deepEqual(stored.preview[1].suggestedDays, [0]);
  assert.equal(stored.preview[1].days[0], '90');
});

test('copy picker searches descriptions but displays only SKU and selection keeps days and quantities', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], {
    products: [product(secondId, 'SKU-2', 'KG', true)]
  });
  ui.get('source').value = 'source-week';
  ui.get('copy-quantities').checked = true;
  await ui.get('copy').events.click();
  const note = visit(ui.get('paste-preview')).find(node => node.attributes['aria-label'] === 'Notas, fila 1');
  note.value = 'Pedido prioritario'; note.events.input();
  const picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'descripción buscada'; picker.events.input();
  await ui.flushSearch();
  const option = ui.body.children.at(-1).children[0];
  assert.equal(option.textContent, 'SKU-2');
  assert.equal(option.attributes['aria-label'], 'SKU-2');
  option.events.click();
  const focusedAmount = visit(ui.get('paste-preview')).find(node => node.tag === 'input' && node.focused);
  assert.equal(focusedAmount.value, '10');
  const saved = JSON.parse([...ui.storage.values()][0]).preview[0];
  assert.equal(saved.productId, secondId);
  assert.equal(saved.sku, 'SKU-2');
  assert.equal(saved.unit, 'KG');
  assert.equal(saved.allowsDecimals, true);
  assert.equal(saved.days[0], '10');
  assert.deepEqual(saved.suggestedDays, [0]);
  assert.equal(saved.details.notes, 'Pedido prioritario');
});

test('copy picker rejects free text and Enter selects an active scanned SKU', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }]);
  ui.get('source').value = 'source-week';
  await ui.get('copy').events.click();
  let picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'no catalogado'; picker.events.input();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  assert.match(visit(ui.get('paste-preview')).map(node => node.textContent).join(' '), /selecciona un SKU activo/);
  picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'SKU-1'; picker.events.input();
  await picker.events.keydown({ key: 'Enter', preventDefault() {} });
  const stored = JSON.parse([...ui.storage.values()][0]).preview[0];
  assert.equal(stored.productId, firstId);
  assert.equal(stored.sku, 'SKU-1');
});

test('copy picker merges compatible different days and keeps same-day repeats separate', async () => {
  const ui = setup([
    { sku: 'SKU-1', day: 0, quantity: 10 },
    { sku: 'SKU-2', productId: secondId, day: 3, quantity: 20 },
    { sku: 'SKU-2', productId: secondId, day: 3, quantity: 30 }
  ], { products: [product(firstId, 'SKU-1'), product(secondId, 'SKU-2')] });
  ui.get('source').value = 'source-week';
  ui.get('copy-quantities').checked = true;
  await ui.get('copy').events.click();
  const picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'SKU-2'; picker.events.input(); await ui.flushSearch();
  await picker.events.keydown({ key: 'ArrowDown', preventDefault() {} });
  await picker.events.keydown({ key: 'ArrowDown', preventDefault() {} });
  await picker.events.keydown({ key: 'Enter', preventDefault() {} });
  const stored = JSON.parse([...ui.storage.values()][0]).preview;
  assert.equal(stored.length, 2);
  assert.deepEqual(stored[0].suggestedDays, [0, 3]);
  assert.equal(stored[0].days[0], '10');
  assert.equal(stored[0].days[3], '20');
  assert.deepEqual(stored[1].suggestedDays, [3]);
});

test('copy picker keeps rows with different notes separate and validates the selected product again', async () => {
  const ui = setup([
    { sku: 'SKU-1', day: 0, quantity: 10 },
    { sku: 'SKU-2', productId: secondId, day: 3, quantity: 20 }
  ], { products: [product(firstId, 'SKU-1'), product(secondId, 'SKU-2')] });
  ui.get('source').value = 'source-week';
  await ui.get('copy').events.click();
  const note = visit(ui.get('paste-preview')).find(node => node.attributes['aria-label'] === 'Notas, fila 1');
  note.value = 'Distinto'; note.events.input();
  const picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'SKU-2'; picker.events.input(); await ui.flushSearch();
  ui.body.children.at(-1).children[1].events.click();
  const stored = JSON.parse([...ui.storage.values()][0]).preview;
  assert.equal(stored.length, 2);
  assert.equal(stored[0].details.notes, 'Distinto');
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 2);
});

test('copy incorporation rejects a product that became inactive and an invalid quantity for its unit', async () => {
  const ui = setup([{ sku: 'SKU-1', day: 0, quantity: 1.5 }], {
    activeProducts: [product(firstId, 'SKU-1', 'EA', false)]
  });
  ui.get('source').value = 'source-week';
  ui.get('copy-quantities').checked = true;
  await ui.get('copy').events.click();
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  assert.match(visit(ui.get('paste-preview')).map(node => node.textContent).join(' '), /cantidad no válida/);

  const stale = setup([{ sku: 'SKU-1', day: 0, quantity: 10 }], { activeProducts: [] });
  stale.get('source').value = 'source-week';
  await stale.get('copy').events.click();
  await stale.get('paste-preview').lastElementChild.events.click();
  assert.equal(stale.get('rows').children.length, 0);
  assert.match(visit(stale.get('paste-preview')).map(node => node.textContent).join(' '), /selecciona un SKU activo/);
});

test('recovered copy without product ID requires catalog selection', async () => {
  const ui = setup([], { prior: { version: 0, updated: new Date().toISOString(),
    previewSource: { id: 'source-week', version: 3 }, rows: [], preview: [{
      selected: true, sourceRow: 1, sku: 'SKU-1', days: ['', '', '', '', '', '', ''],
      suggestedDays: [0], details: {}
    }] } });
  const restore = visit(ui.get('recovery')).find(node => node.tag === 'button');
  restore.events.click();
  assert.match(visit(ui.get('paste-preview')).map(node => node.textContent).join(' '), /Selecciona el SKU del catálogo/);
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 0);
  const picker = visit(ui.get('paste-preview')).find(node => node.attributes.role === 'combobox');
  picker.value = 'SKU-1'; picker.events.input();
  await picker.events.keydown({ key: 'Enter', preventDefault() {} });
  await ui.get('paste-preview').lastElementChild.events.click();
  assert.equal(ui.get('rows').children.length, 1);
});

test('Enter advances to the next quantity and changing the day view keeps both values', async () => {
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
  ui.views[1].events.click();
  ui.get('day').value = '1'; ui.get('day').events.change();
  assert.equal(inputs[0].value, '10');
  assert.equal(inputs[1].value, '5');
  assert.match(ui.get('day-summary').textContent, /Martes: 5 EA/);
  ui.views[0].events.click();
  ui.get('review').events.click();
  const changes = JSON.parse(ui.get('payload').value).changes;
  assert.equal(changes.length, 2);
  assert.equal(changes[0].line.notes, 'Preparar primero');
});
