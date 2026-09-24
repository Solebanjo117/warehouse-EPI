const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-balance-editor.js'), 'utf8');

function setup() {
  const el = (value = '') => ({ value, dataset: {}, events: {}, hidden: false, disabled: false, children: [],
    classList: { toggle() {} }, setAttribute() {}, select() { this.selected = true; }, focus() { this.focused = true; }, blur() {}, getClientRects() { return [1]; },
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
  const through = el('2026-09-28');
  const document = { events: {}, querySelector: x => x === '#balance-editor' ? form : null,
    querySelectorAll: x => x === '.balance-edit-cell' ? [cell] : x === 'form[method="get"] input, form[method="get"] select' ? [through] : [], createElement: () => el(),
    addEventListener(name, fn) { this.events[name] = fn; } };
  const window = { events: {}, confirm: () => false, addEventListener(name, fn) { this.events[name] = fn; } };
  const requests = []; let timer, reloads = 0;
  const fetch = (url, options) => new Promise((resolve, reject) => requests.push({ url, options, body: JSON.parse(options.body), resolve: body => resolve({ ok: true, json: async () => body }), reject }));
  vm.runInNewContext(source, { document, window, fetch, location: { reload() { reloads++; } }, setTimeout(fn) { timer = fn; return 1; }, clearTimeout() { timer = null; } });
  const response = { canConfirm: true, fingerprint: 'fingerprint', requiresAdmin: false, errors: [], balance: { products: [] },
    cells: [{ cell: { productId: 'product', area: 0, shift: 1, requested: 5 }, current: 2, sku: 'SKU' }] };
  return { cell, nodes, form, window, document, through, requests, response, timer: () => timer?.(), reloads: () => reloads };
}
const flush = () => new Promise(resolve => setImmediate(resolve));

test('day filter cancellation restores date and quantities; acceptance navigates without another prompt', () => {
  const s = setup(); s.cell.value = '8'; s.cell.events.input();
  s.through.value = '2026-10-04';
  let blocked = false;
  const event = { target: {}, preventDefault() { blocked = true; }, stopImmediatePropagation() {} };
  s.document.events.submit(event);
  assert.ok(blocked);
  assert.equal(s.through.value, '2026-09-28');
  assert.equal(s.cell.value, '8');
  s.window.confirm = () => true;
  s.through.value = '2026-10-04'; blocked = false;
  s.document.events.submit(event);
  assert.equal(blocked, false);
  assert.equal(s.through.value, '2026-10-04');
  s.window.events.beforeunload({ preventDefault() { blocked = true; } });
  assert.equal(blocked, false);
  assert.equal(s.requests.length, 0);
});

test('blank is invalid, zero is an explicit reduction, preview never sends PIN', async () => {
  const s = setup(); s.cell.value = ''; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 0); assert.equal(s.nodes['[data-edit-errors]'].textContent, 'invalid');
  s.cell.value = '0'; s.cell.events.input(); const pending = s.timer();
  assert.equal(s.requests[0].body.cells[0].requested, '0'); assert.equal(s.requests[0].body.pin, '');
  assert.equal(s.requests[0].options.headers.RequestVerificationToken, 'csrf');
  s.requests[0].resolve(s.response); await pending;
});

test('preview refreshes Status % from the server result', async () => {
  const s = setup();
  const status = { textContent: '' };
  const row = { querySelector: selector => selector === '[data-balance-status]' ? status : null,
    querySelectorAll: () => [] };
  const query = s.document.querySelector;
  s.document.querySelector = selector => selector === '[data-balance-product="product"]' ? row : query(selector);
  s.cell.value = '5'; s.cell.events.input();
  s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, balance: { products: [{ productId: 'product', planned: 10,
    statusRatio: 0.5 }] } });
  await flush();
  assert.match(status.textContent, /50/);
  assert.match(status.textContent, /%/);
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

test('plan quantities are reviewed separately from turn totals in the same payload', async () => {
  const s = setup();
  Object.assign(s.cell.dataset, { planLine: 'line', lineVersion: '4', weekVersion: '7' });
  s.cell.value = '8'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells.length, 0);
  assert.equal(s.requests[0].body.planChanges[0].lineId, 'line');
  assert.equal(s.requests[0].body.planChanges[0].expectedWeekVersion, 7);
  assert.equal(s.requests[0].body.pin, '');
  s.requests[0].resolve({ ...s.response, requiresAdmin: true, requiresReason: false, cells: [],
    plans: [{ sku: 'SKU', current: 2, change: { lineId: 'line', requested: 8 } }] });
  await flush();
  assert.equal(s.nodes['#balance-edit-reason'].required, false);
  assert.equal(s.nodes['#balance-edit-pin'].focused, true);
  assert.equal(s.nodes.tbody.children.length, 1);
});

test('Enter advances to and selects the next editable cell without submitting', () => {
  const s = setup();
  const next = { disabled: false, getClientRects: () => [1], focus() { this.focused = true; }, select() { this.selected = true; } };
  const original = s.document.querySelectorAll;
  s.document.querySelectorAll = selector => selector === '.balance-edit-cell' ? [s.cell, next] : original(selector);
  let prevented = false;
  s.cell.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  assert.ok(prevented); assert.ok(next.focused); assert.ok(next.selected);
  assert.equal(s.requests.length, 0);
});

test('zero plan stages only a new row, returning to zero and discard create nothing', async () => {
  const s = setup();
  Object.assign(s.cell.dataset, { original: '0', newPlan: 'new-row', weekVersion: '7' });
  s.cell.value = '0'; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 0);
  s.cell.events.focus(); assert.ok(s.cell.selected);
  s.cell.value = '50'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells.length, 0);
  assert.equal(s.requests[0].body.planChanges.length, 0);
  assert.deepEqual(s.requests[0].body.newPlans, [{ operationId: 'new-row', productId: 'product', requested: '50', expectedWeekVersion: 7 }]);
  s.requests[0].resolve({ ...s.response, cells: [], requiresAdmin: true,
    newPlans: [{ sku: 'SKU', change: { operationId: 'new-row', requested: 50 } }] });
  await flush(); assert.equal(s.nodes.tbody.children.length, 1);
  s.cell.value = '0'; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 1); assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  s.cell.value = '50'; s.cell.events.input(); s.nodes['[data-edit-discard]'].events.click();
  assert.equal(s.cell.value, '0'); assert.equal(s.requests.length, 1);
});
