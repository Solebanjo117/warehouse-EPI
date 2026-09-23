const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-balance-editor.js'), 'utf8');

function setup() {
  const el = (value = '') => ({ value, dataset: {}, events: {}, hidden: false, disabled: false, children: [],
    classList: { toggle() {} }, setAttribute() {}, select() {}, focus() {}, blur() {},
    addEventListener(name, fn) { this.events[name] = fn; }, append(child) { this.children.push(child); },
    replaceChildren() { this.children = []; }, getAttribute() { return 'SKU Cutting T1'; } });
  const row = { dataset: { balanceProduct: 'product' } };
  const cell = el('2'); cell.dataset = { original: '2', editArea: '0', editShift: '1' };
  cell.closest = () => row; cell.nextElementSibling = el();
  const nodes = Object.fromEntries(['status','errors','review-button','confirm','discard','review','auth'].map(x => [`[data-edit-${x}]`, el()]));
  nodes['#balance-edit-pin'] = el(); nodes['#balance-edit-reason'] = el(); nodes['[name="__RequestVerificationToken"]'] = el('csrf');
  nodes['[data-edit-review]'].querySelector = () => nodes.tbody ||= el();
  const form = el(); form.querySelector = x => nodes[x]; form.reportValidity = () => true;
  form.dataset = { operation: 'operation', week: 'week', date: '2026-09-21', previewUrl: '/preview', confirmUrl: '/confirm', invalid: 'invalid', pending: 'unsaved', error: 'error', unsaved: 'discard?' };
  const document = { events: {}, querySelector: x => x === '#balance-editor' ? form : null,
    querySelectorAll: x => x === '.balance-edit-cell' ? [cell] : [], createElement: () => el(),
    addEventListener(name, fn) { this.events[name] = fn; } };
  const window = { events: {}, confirm: () => false, addEventListener(name, fn) { this.events[name] = fn; } };
  const requests = []; let timer, reloads = 0;
  const fetch = (url, options) => new Promise((resolve, reject) => requests.push({ url, options, body: JSON.parse(options.body), resolve: body => resolve({ ok: true, json: async () => body }), reject }));
  vm.runInNewContext(source, { document, window, fetch, location: { reload() { reloads++; } }, setTimeout(fn) { timer = fn; return 1; }, clearTimeout() { timer = null; } });
  const response = { canConfirm: true, fingerprint: 'fingerprint', requiresAdmin: false, errors: [], balance: { products: [] },
    cells: [{ cell: { productId: 'product', area: 0, shift: 1, requested: 5 }, current: 2, sku: 'SKU' }] };
  return { cell, nodes, form, window, document, requests, response, timer: () => timer?.(), reloads: () => reloads };
}
const flush = () => new Promise(resolve => setImmediate(resolve));

test('blank is invalid, zero is an explicit reduction, preview never sends PIN', async () => {
  const s = setup(); s.cell.value = ''; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 0); assert.equal(s.nodes['[data-edit-errors]'].textContent, 'invalid');
  s.cell.value = '0'; s.cell.events.input(); const pending = s.timer();
  assert.equal(s.requests[0].body.cells[0].requested, '0'); assert.equal(s.requests[0].body.pin, '');
  assert.equal(s.requests[0].options.headers.RequestVerificationToken, 'csrf');
  s.requests[0].resolve(s.response); await pending;
});

test('stale previews cannot restore a confirmation after a newer edit', async () => {
  const s = setup(); s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.cell.value = '7'; s.cell.events.input();
  s.requests[0].resolve(s.response); await flush();
  assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  assert.equal(s.cell.value, '7');
});

test('Escape cancels the active cell and discard clears unsaved navigation guard', () => {
  const s = setup(); s.cell.events.focus(); s.cell.value = '8'; s.cell.events.input();
  let prevented = false;
  s.window.events.beforeunload({ preventDefault() { prevented = true; } }); assert.ok(prevented);
  s.cell.events.keydown({ key: 'Escape', preventDefault() {} }); assert.equal(s.cell.value, '2');
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-discard]'].events.click();
  assert.equal(s.cell.value, '2'); prevented = false;
  s.window.events.beforeunload({ preventDefault() { prevented = true; } }); assert.equal(prevented, false);
});

test('uncertain confirmation retries identical content and clears PIN', async () => {
  const s = setup(); s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve(s.response); await flush();
  s.nodes['#balance-edit-pin'].value = '1234'; const first = s.form.events.submit({ preventDefault() {} });
  assert.equal(s.nodes['#balance-edit-pin'].value, ''); s.requests[1].reject(new Error('network')); await first;
  assert.equal(s.cell.disabled, true); assert.equal(s.nodes['[data-edit-confirm]'].hidden, false);
  s.nodes['#balance-edit-pin'].value = '1234'; const retry = s.form.events.submit({ preventDefault() {} });
  assert.deepEqual(s.requests[1].body, s.requests[2].body);
  s.requests[2].resolve({ success: true }); await retry; assert.equal(s.reloads(), 1);
});
