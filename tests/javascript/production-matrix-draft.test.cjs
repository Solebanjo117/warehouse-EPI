const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-matrix-draft.js'), 'utf8');
const op = '11111111-1111-4111-8111-111111111111', product = '22222222-2222-4222-8222-222222222222', shiftId = '33333333-3333-4333-8333-333333333333';
const key = `warehouse-epi.production-capture.v2:2026-09-23:${shiftId}`;
const el = () => ({ events: {}, dataset: {}, value: '', hidden: false, children: [], addEventListener(n, f) { this.events[n] = f; }, querySelector() { return null; }, querySelectorAll() { return []; }, replaceChildren() { this.children = []; }, append(x) { this.children.push(x); }, requestSubmit() { this.submitted = true; const e = { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }; this.events.submit?.(e); this.lastEvent = e; } });
const draft = () => ({ version: 2, operationId: op, date: '2026-09-23', shiftId, mode: 'list', focusArea: '1', onlyWithQuantity: false, rows: [{ productId: product, area: '1', quantity: '3,5', notes: 'Costura' }] });
function setup({ stored, serverPost = false, blocked = false, conflict = false, closed = false, legacy = false, receipt, store = new Map() } = {}) {
  const legacyKey = `warehouse-epi.production-capture.v1:2026-09-23:Sewing:${shiftId}`;
  if (stored) store.set(legacy ? legacyKey : key, typeof stored === 'string' ? stored : JSON.stringify(stored));
  const form = el(), context = el(), entry = el(), week = el(), shift = el(), day = el(), banner = el(), summary = el(), warning = el(), restore = el(), recover = el(), discard = el(), fieldset = el(), fields = el();
  form.dataset = { matrix: 'true', serverPost: String(serverPost), restoreConflict: String(conflict) };
  entry.dataset = { date: '2026-09-23', shift: shiftId, contextState: closed ? 'Closed' : 'Open', contextLabel: '23 sep · T1' };
  warning.dataset = { storageError: 'Unavailable', corrupt: 'Corrupt' };
  const quantities = ['4', '6', '8'].map(value => ({ value }));
  const cells = quantities.map((quantity, index) => ({ dataset: { area: String(index) }, closest: () => ({ dataset: { productRow: product } }), querySelector: s => s === '[data-group-quantity]' ? quantity : { value: `note ${index}` } }));
  const values = { OperationId: op, Mode: 'list', FocusArea: '0', Pin: 'SECRET', Fingerprint: 'REVIEW' };
  form.querySelector = s => s === 'fieldset' ? fieldset : s === '[data-only-quantity]' ? { checked: false } : { value: values[s.match(/Group\.(\w+)/)?.[1]] || '' };
  form.querySelectorAll = () => cells; form.closest = () => entry;
  entry.querySelector = s => ({ '[data-draft-banner]': banner, '[data-draft-summary]': summary, '[data-draft-warning]': warning, '[data-draft-restore]': restore, '[data-draft-recover]': recover, '[data-draft-discard]': discard, '[data-receipt-operation]': receipt ? { dataset: { receiptOperation: receipt } } : null })[s];
  week.value = 'week1'; week.selectedOptions = [{ dataset: { start: '2026-09-28' } }]; shift.value = shiftId; day.value = '2026-09-23';
  context.querySelector = s => ({ '[data-capture-week]': week, '[data-capture-shift]': shift, '[data-capture-day-value]': day })[s];
  const dayButton = el(); dayButton.dataset.captureDay = '2026-09-24'; context.querySelectorAll = () => [dayButton];
  restore.querySelector = () => fields;
  const document = { querySelector: s => s === '[data-capture-group][data-matrix="true"]' ? form : context, querySelectorAll: () => [], createElement: el };
  const window = { events: {}, addEventListener(n, f) { this.events[n] = f; }, location: { pathname: '/Operations/Production', assign(v) { this.assigned = v; } }, sessionStorage: { getItem: k => store.get(k) || null, removeItem: k => store.delete(k), setItem(k, v) { if (blocked) throw Error('blocked'); store.set(k, v); } } };
  vm.runInNewContext(script, { document, window, URLSearchParams, queueMicrotask });
  return { form, context, week, shift, day, dayButton, restore, recover, discard, banner, warning, fields, quantities, store, window, fieldset, legacyKey };
}
test('matrix saves every area separately without PIN or reviewed fingerprint', () => {
  const ui = setup(); ui.form.events.input();
  const saved = JSON.parse(ui.store.get(key));
  assert.deepEqual(saved.rows.map(x => [x.area, x.quantity]), [['0', '4'], ['1', '6'], ['2', '8']]);
  assert.doesNotMatch(ui.store.get(key), /SECRET|REVIEW|Fingerprint|Pin|VerificationToken/);
});
test('a returning context automatically restores raw input and never submits confirmation', () => {
  const ui = setup({ stored: draft() });
  assert.equal(ui.restore.submitted, true);
  const fields = Object.fromEntries(ui.fields.children.map(x => [x.name, x.value]));
  assert.equal(fields['Group.AllAreas'], 'true'); assert.equal(fields['Group.Rows[0].Area'], '1');
  assert.equal(fields['Group.Rows[0].Quantity'], '3,5'); assert.equal(fields['Group.Pin'], undefined);
  assert.equal(ui.form.submitted, undefined);
});
test('server restore and blocked/conflicting responses do not create redirect loops or overwrite drafts', () => {
  for (const options of [{}, { closed: true }, { conflict: true }]) {
    const ui = setup({ stored: draft(), serverPost: true, ...options });
    assert.equal(ui.restore.submitted, undefined);
    if (options.closed || options.conflict) assert.equal(JSON.parse(ui.store.get(key)).rows[0].quantity, '3,5');
  }
});
test('week changes preserve weekday and context submission first saves current quantities', () => {
  const ui = setup(); ui.week.value = 'week2'; ui.week.events.change();
  assert.equal(ui.day.value, '2026-09-30'); assert.equal(ui.context.submitted, true);
  assert.equal(JSON.parse(ui.store.get(key)).date, '2026-09-23');
});
test('cancelled navigation restores week day and shift selectors', async () => {
  const ui = setup({ blocked: true }); ui.week.value = 'week2'; ui.shift.value = 'new'; ui.day.value = '2026-09-30';
  const event = { defaultPrevented: false }; ui.context.events.submit(event); event.defaultPrevented = true;
  await Promise.resolve();
  assert.equal(ui.week.value, 'week1'); assert.equal(ui.day.value, '2026-09-23'); assert.equal(ui.shift.value, shiftId);
  assert.equal(ui.warning.textContent, 'Unavailable');
});
test('legacy recovery is explicit and leaves the original until successful conversion', () => {
  const old = { ...draft(), version: 1, area: 'Sewing' }; const ui = setup({ stored: old, legacy: true });
  assert.equal(ui.restore.submitted, undefined); assert.equal(ui.banner.hidden, false);
  ui.restore.requestSubmit(); const fields = Object.fromEntries(ui.fields.children.map(x => [x.name, x.value]));
  assert.equal(fields['Group.ImportLegacy'], 'true'); assert.equal(fields['Group.Area'], 'Sewing'); assert.equal(ui.store.has(ui.legacyKey), true);
});
test('receipt removes only the matching operation and corrupt storage never auto submits', () => {
  assert.equal(setup({ stored: draft(), receipt: op }).store.has(key), false);
  assert.equal(setup({ stored: draft(), receipt: product }).store.has(key), true);
  const corrupt = setup({ stored: '{broken' }); assert.equal(corrupt.restore.submitted, undefined); assert.equal(corrupt.recover.disabled, true);
});
