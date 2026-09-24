const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const code = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/production-week-openings.js', 'utf8');
class Element {
  constructor(tag = 'div') { this.tag = tag; this.children = []; this.events = {}; this.style = {}; this.dataset = {}; this.value = ''; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  addEventListener(event, handler) { this.events[event] = handler; }
  dispatchEvent(event) { this.events[event.type]?.(event); }
  setAttribute() {}
  focus() { this.focused = true; }
  remove() { this.removed = true; }
  querySelectorAll(selector) { return this.children.flatMap(n => [n, ...n.querySelectorAll(selector)]).filter(n => selector.split(',').map(s => s.trim()).includes(n.tag)); }
}
const option = (area, selected = 0) => ({ sourceWeekId: 'source', sourceLineId: 'line', sourceStart: '2026-09-14',
  productId: 'product', sku: 'SKU-A', unit: 'EA', allowsDecimals: false, sequence: 1, area, available: 152,
  selected, provisional: true, fingerprint: 'version' });
async function setup(options, correction = false, respond) {
  const root = new Element(), nodes = new Map();
  for (const name of ['rows', 'status', 'reason', 'pin', 'preview', 'auth', 'review', 'confirm', 'panel', 'summary']) nodes.set(`[data-opening-${name}]`, new Element());
  root.dataset = { url: '/options', userId: 'admin', weekId: 'week', weekVersion: '2', correction: String(correction), reviewUrl: '/review', confirmUrl: '/confirm', operationUrl: '/operation' };
  root.querySelector = key => nodes.get(key) || (key.startsWith('input[') ? { value: 'antiforgery' } : null);
  const storage = new Map(), window = { addEventListener() {} }; let reloads = 0;
  vm.runInNewContext(code, { document: { querySelectorAll: () => [root], createElement: tag => new Element(tag) },
    window, location: { origin: 'https://localhost', reload: () => { reloads++; } }, URL, crypto: webcrypto,
    CustomEvent: class { constructor(type) { this.type = type; } },
    localStorage: { setItem: (k, v) => storage.set(k, v), getItem: k => storage.get(k), removeItem: k => storage.delete(k) },
    fetch: async (url, args) => url === '/options' ? { ok: true, json: async () => options.map(row => ({ ...row })) } : respond(url, args) });
  const api = window.ProductionWeekOpenings.get(root); await api.ready;
  return { api, root, nodes, storage, reloads: () => reloads,
    inputs: () => nodes.get('[data-opening-rows]').querySelectorAll('input') };
}
test('opening starts unselected and keeps area quantities independent with undo', async () => {
  const ui = await setup([option(0), option(1), option(2)]);
  assert.equal(ui.api.changes().length, 0); assert.equal(ui.inputs().length, 3);
  assert.match(ui.nodes.get('[data-opening-summary]').textContent, /0 selecciones de 3 pendientes/);
  const input = ui.inputs()[0]; input.value = '40'; input.events.input();
  assert.equal(ui.api.changes()[0].quantity, '40'); assert.equal(ui.api.changes()[0].area, 0);
  assert.match(ui.nodes.get('[data-opening-summary]').textContent, /1 selección de 3 pendientes/);
  assert.equal(ui.inputs()[1].value, ''); assert.equal(ui.api.errors().length, 0);
  ui.nodes.get('[data-opening-rows]').querySelectorAll('button')[0].events.click();
  assert.equal(ui.api.changes().length, 0);
});

test('copy preparation revalidates the source and preserves an existing opening', async () => {
  const options = [option(0, 20), option(1)];
  const ui = await setup(options);
  const candidates = ui.api.options();
  assert.equal(candidates[0].prepared, 20);
  assert.equal(await ui.api.prepare(candidates.map(row => ({ key: row.key, fingerprint: row.fingerprint, quantity: '30' }))), 1);
  assert.equal(ui.inputs()[0].value, '20');
  assert.equal(ui.inputs()[1].value, '30');
  assert.equal(ui.nodes.get('[data-opening-panel]').open, true);
  options[1].fingerprint = 'changed';
  await assert.rejects(ui.api.prepare([{ key: candidates[1].key, fingerprint: 'version', quantity: '40' }]), /cambió/);
  options[1].fingerprint = 'version'; options[1].selected = 5;
  await assert.rejects(ui.api.prepare([{ key: candidates[1].key, fingerprint: 'version', quantity: '40' }]), /cambió/);
});

test('recovered opening expands its panel', async () => {
  const ui = await setup([option(0)]);
  ui.api.restore({ 'source:line:0': '12' });
  assert.equal(ui.nodes.get('[data-opening-panel]').open, true);
});

test('copy does not overwrite a zero entered manually for an origin', async () => {
  const ui = await setup([option(0)]);
  ui.inputs()[0].value = '0'; ui.inputs()[0].events.input();
  const candidate = ui.api.options()[0];
  assert.equal(candidate.protected, true);
  assert.equal(await ui.api.prepare([{ key: candidate.key, fingerprint: candidate.fingerprint, quantity: '12' }]), 0);
  assert.equal(ui.inputs()[0].value, '0');
});
test('rejects excess and incompatible decimals while preserving what was entered', async () => {
  const ui = await setup([option(0)]), input = ui.inputs()[0];
  for (const value of ['153', '2.5', '-1', 'NaN']) {
    input.value = value; input.events.input(); assert.equal(ui.api.errors().length, 1); assert.equal(input.value, value);
  }
});
test('local recovery preserves partial selections and flags missing origins', async () => {
  const ui = await setup([option(0)]);
  ui.api.restore({ 'source:line:0': '40' });
  assert.equal(ui.inputs()[0].value, '40'); assert.equal(ui.api.errors().length, 0);
  ui.api.restore({ 'source:line:0': '40', 'missing:line:1': '10' });
  assert.match(ui.api.errors()[0], /origen recuperado/);
  ui.nodes.get('[data-opening-rows]').querySelectorAll('button').at(-1).events.click();
  assert.equal(ui.api.errors().length, 0); assert.equal(ui.inputs()[0].value, '40');
});
test('Enter advances to the next area without confirming the preparation', async () => {
  const ui = await setup([option(0), option(1)]); let prevented = false;
  ui.inputs()[0].events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  assert.ok(prevented); assert.ok(ui.inputs()[1].focused);
});
test('source version change requires review even when the selected quantity is unchanged', async () => {
  const ui = await setup([{ ...option(0, 40), needsReview: true }]);
  assert.equal(ui.api.changes().length, 1); assert.equal(ui.api.changes()[0].expectedFingerprint, 'version');
});
test('recovery shows a changed source without replacing the quantity or origin', async () => {
  const ui = await setup([option(0)]);
  ui.api.restore({ values: { 'source:line:0': '40' }, sources: { 'source:line:0': { fingerprint: 'old', available: 160 } } });
  assert.equal(ui.inputs()[0].value, '40');
  assert.match(ui.nodes.get('[data-opening-status]').textContent, /160 → actual 152/);
  assert.equal(ui.api.changes()[0].sourceLineId, 'line');
});
test('lost confirmation checks the same operation and removes recovery without storing PIN', async () => {
  let sent;
  const ui = await setup([option(0, 40)], true, async (url, args) => {
    if (url === '/review') return { ok: true, json: async () => ({ canConfirm: true, fingerprint: 'review', before: { products: [] }, after: { products: [] } }) };
    if (url === '/confirm') { sent = JSON.parse(args.body); throw Error('lost response'); }
    assert.equal(new URL(url).searchParams.get('operationId'), sent.operationId);
    return { ok: true, json: async () => ({ saved: true }) };
  });
  ui.inputs()[0].value = '45'; ui.inputs()[0].events.input();
  ui.nodes.get('[data-opening-reason]').value = 'Corregir selección';
  await ui.nodes.get('[data-opening-review]').events.click();
  ui.nodes.get('[data-opening-pin]').value = '4826';
  assert.ok([...ui.storage.values()].every(value => !value.includes('4826')));
  await ui.nodes.get('[data-opening-confirm]').events.click();
  assert.equal(ui.reloads(), 1); assert.equal(ui.storage.size, 0); assert.equal(ui.nodes.get('[data-opening-pin]').value, '');
});
