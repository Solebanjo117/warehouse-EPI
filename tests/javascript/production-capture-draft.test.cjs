const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const operationId = '11111111-1111-4111-8111-111111111111';
const productId = '22222222-2222-4222-8222-222222222222';
const shiftId = '33333333-3333-4333-8333-333333333333';
const key = `warehouse-epi.production-capture.v1:2026-09-25:Cutting:${shiftId}`;
const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-capture-draft.js'), 'utf8');
const draft = () => ({ version: 1, operationId, date: '2026-09-25', area: 'Cutting', shiftId,
  mode: 'list', onlyWithQuantity: false, rows: [{ productId, quantity: '3,5', notes: 'Keep me' }] });
const element = () => ({ dataset: {}, events: {}, hidden: true, disabled: false, children: [], textContent: '',
  addEventListener(name, fn) { this.events[name] = fn; }, replaceChildren() { this.children = []; },
  append(child) { this.children.push(child); }, focus() { this.focused = true; } });

function setup({ stored, serverPost = false, state = 'Open', conflict = false, receipt = null, blocked = false, store = new Map(), area = 'Cutting' } = {}) {
  if (stored !== undefined) store.set(key, typeof stored === 'string' ? stored : JSON.stringify(stored));
  const quantity = { value: '4', focus() { this.focused = true; } }, notes = { value: 'exact note' };
  const row = { dataset: { productRow: productId }, querySelector(selector) {
    return ({ '[data-group-quantity]': quantity, 'input[name$=".Notes"]': notes })[selector];
  } };
  const fieldset = element(), banner = element(), summary = element(), warning = element(), restore = element(), recover = element(), discard = element(), fields = element();
  warning.dataset = { storageError: 'Storage unavailable', corrupt: 'Corrupt draft' };
  restore.querySelector = () => fields;
  const entry = { dataset: { contextState: state, date: '2026-09-25', area, shift: shiftId, contextLabel: '25 sep 2026 · Corte · T1' },
    querySelector(selector) { return ({ '[data-draft-banner]': banner, '[data-draft-summary]': summary,
      '[data-draft-warning]': warning, '[data-draft-restore]': restore, '[data-draft-recover]': recover,
      '[data-draft-discard]': discard, '[data-receipt-operation]': receipt ? { dataset: { receiptOperation: receipt } } : null })[selector]; } };
  const form = element();
  form.dataset = { serverPost: String(serverPost), restoreConflict: String(conflict) };
  form.querySelector = selector => ({ '[name="Group.OperationId"]': { value: operationId },
    '[name="Group.Mode"]': { value: 'list' }, '[name="Group.Pin"]': { value: 'SECRET' },
    '[name="Group.Fingerprint"]': { value: 'STALE-REVIEW' }, '[data-only-quantity]': { checked: false }, fieldset,
    '[data-group-quantity]': quantity })[selector];
  form.querySelectorAll = () => [row];
  const context = element(), link = element();
  const document = { querySelector(selector) { return ({ '[data-capture-entry]': entry, '[data-capture-group]': form,
    '[data-context-form]': context })[selector]; }, querySelectorAll() { return [link]; }, createElement: element };
  const window = { events: {}, addEventListener(name, fn) { this.events[name] = fn; },
    location: { href: 'https://localhost/Operations/Production', assign(url) { this.assigned = url; } },
    sessionStorage: { getItem: name => store.get(name) ?? null, removeItem(name) { store.delete(name); },
      setItem(name, value) { if (blocked) throw Error('QuotaExceededError'); store.set(name, value); } } };
  vm.runInNewContext(script, { document, window, URL, URLSearchParams, queueMicrotask });
  return { form, entry, banner, summary, warning, restore, recover, discard, fields, fieldset, quantity, notes, store, context, link, window };
}

test('draft saves raw quantities and notes, never PIN, review or antiforgery', () => {
  const ui = setup();
  ui.quantity.value = '3,5';
  ui.form.events.input();
  const saved = JSON.parse(ui.store.get(key));
  assert.equal(saved.rows[0].quantity, '3,5');
  assert.equal(saved.rows[0].notes, 'exact note');
  assert.equal(saved.operationId, operationId);
  assert.equal(ui.form.dataset.draftSaved, 'true');
  assert.doesNotMatch(ui.store.get(key), /SECRET|STALE-REVIEW|Pin|Fingerprint|VerificationToken/);
  ui.quantity.value = ''; ui.notes.value = '';
  ui.form.events.input();
  assert.equal(ui.store.has(key), false);
});

test('reload offers explicit recovery without overwriting or mixing the old operation', () => {
  const stored = draft();
  const ui = setup({ stored });
  assert.equal(ui.banner.hidden, false);
  assert.equal(ui.fieldset.disabled, true);
  ui.form.events.input();
  assert.deepEqual(JSON.parse(ui.store.get(key)), stored);
  ui.restore.events.submit({ preventDefault() { assert.fail('valid draft'); } });
  const fields = Object.fromEntries(ui.fields.children.map(input => [input.name, input.value]));
  assert.equal(fields['Group.OperationId'], operationId);
  assert.equal(fields['Group.Rows[0].Quantity'], '3,5');
  assert.equal(fields['Group.Rows[0].Notes'], 'Keep me');
  assert.equal(fields['Group.Pin'], undefined);
  assert.equal(fields['Group.Fingerprint'], undefined);
});

test('receipt clears only the matching operation; another pending draft remains', () => {
  const successful = setup({ stored: draft(), receipt: operationId });
  assert.equal(successful.store.has(key), false);
  assert.equal(successful.banner.hidden, true);
  const other = setup({ stored: draft(), receipt: productId });
  assert.equal(other.store.has(key), true);
  assert.equal(other.banner.hidden, false);
});

test('closed week and restore conflicts retain the draft and prevent editing', () => {
  for (const options of [{ state: 'Closed' }, { conflict: true, serverPost: true }]) {
    const ui = setup({ stored: draft(), ...options });
    ui.form.events.input();
    assert.equal(ui.banner.hidden, false);
    assert.equal(ui.fieldset.disabled, true);
    assert.equal(JSON.parse(ui.store.get(key)).rows[0].quantity, '3,5');
  }
});

test('successful restore response retains operation and allows correction before fresh review', () => {
  const ui = setup({ stored: draft(), serverPost: true });
  assert.equal(ui.banner.hidden, true);
  assert.equal(ui.fieldset.disabled, false);
  ui.quantity.value = '3.5'; ui.form.events.input();
  assert.equal(JSON.parse(ui.store.get(key)).operationId, operationId);
  assert.equal(JSON.parse(ui.store.get(key)).rows[0].quantity, '3.5');
});

test('corrupt and oversized drafts offer discard without submitting malformed fields', () => {
  for (const stored of ['{broken', { ...draft(), rows: Array(101).fill(draft().rows[0]) }, { ...draft(), date: '2020-01-01' }]) {
    const ui = setup({ stored });
    assert.equal(ui.warning.textContent, 'Corrupt draft');
    assert.equal(ui.recover.disabled, true);
    let prevented = false;
    ui.restore.events.submit({ preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    ui.discard.events.click();
    assert.equal(ui.store.has(key), false);
    assert.equal(ui.fieldset.disabled, false);
  }
});

test('blocked session storage warns and keeps normal entry available', () => {
  const ui = setup({ blocked: true });
  assert.equal(ui.warning.hidden, false);
  assert.equal(ui.warning.textContent, 'Storage unavailable');
  assert.equal(ui.fieldset.disabled, false);
  let prevented = false;
  ui.window.events.beforeunload({ preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
});

test('a different area has a separate draft and cannot inherit quantities', () => {
  const store = new Map([[key, JSON.stringify(draft())]]);
  const ui = setup({ store, area: 'Sewing' });
  assert.equal(ui.banner.hidden, true);
  ui.form.events.input();
  assert.equal(store.size, 2);
  assert.equal(JSON.parse(store.get(key)).rows[0].quantity, '3,5');
  assert.equal(JSON.parse(store.get(key.replace(':Cutting:', ':Sewing:'))).rows[0].quantity, '4');
});

test('submitting saves before leaving and late validation cancellation preserves later edits', async () => {
  const ui = setup();
  const event = { defaultPrevented: false };
  ui.form.events.submit(event);
  assert.equal(JSON.parse(ui.store.get(key)).rows[0].quantity, '4');
  event.defaultPrevented = true;
  await Promise.resolve();
  ui.quantity.value = '7';
  ui.form.events.input();
  assert.equal(JSON.parse(ui.store.get(key)).rows[0].quantity, '7');
  ui.form.events.submit({ defaultPrevented: true });
  assert.equal(ui.store.has(key), true);
});
