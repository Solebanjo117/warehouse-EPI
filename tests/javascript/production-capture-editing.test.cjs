const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-capture-workspace.js'), 'utf8');

function setup({ reviewed = false, matrix = false } = {}) {
  const node = (value = '') => ({ value, dataset: {}, events: {}, hidden: false, focused: false, textContent: '',
    classList: { toggle() {} }, setAttribute(name, value) { this[name] = value; },
    addEventListener(name, fn) { this.events[name] = fn; }, focus() { this.focused = true; } });
  const mode = node('list'), only = node(), filterEmpty = node(), editor = node(), reviewPanel = reviewed ? node() : null;
  const search = node(), pin = node('1234'), fingerprint = node('review-hash'), confirm = node(), back = node();
  const table = node(), count = node(), units = node(), review = node(), actions = node();
  actions.dataset.countLabel = 'Products';
  actions.querySelector = selector => ({ '[data-capture-count]': count, '[data-capture-units]': units, '[data-group-preview]': review })[selector];
  const makeRow = (value, unit) => {
    const input = node(value), notes = node('memo'), error = node(), clear = node(), undo = node(), total = node();
    const row = { dataset: { unit }, hidden: false, input, notes, error, clear, undo, total,
      querySelector(selector) { return ({ '[data-group-quantity]': input, 'input[name$=".Notes"]': notes,
        '[data-quantity-error]': error, '[data-clear-row]': clear, '[data-undo-row]': undo,
        '[data-result-total]': total, '[data-registered]': { dataset: { registered: '80' } } })[selector]; } };
    return row;
  };
  const rows = [makeRow('20', 'PC'), makeRow('2.5', 'KG'), makeRow('', 'PC')];
  const products = matrix ? [{ dataset: { productRow: 'one-sku' }, hidden: false,
    querySelectorAll: () => rows, querySelector: () => rows[0].input }] : rows;
  if (matrix) rows.forEach((row, i) => {
    row.dataset.area = String(i); row.dataset.areaLabel = ['Corte', 'Costura', 'Ready to Pack'][i];
    row.closest = () => products[0];
  });
  const form = node();
  form.dataset = { matrix: String(matrix), quantityError: 'Invalid format', inactiveError: 'Inactive product', rangeError: 'Too large' };
  form.querySelector = selector => ({ '[name="Group.Mode"]': mode, '[data-capture-actions]': actions,
    '.production-entry__rows': table, '#group-add-product': search, '[data-only-quantity]': only,
    '[data-capture-filter-empty]': filterEmpty, '[data-capture-editor]': editor, '[data-group-review]': reviewPanel,
    '[data-back-to-capture]': back, '[data-group-confirm]': confirm, '[name="Group.Pin"]': pin,
    '[name="Group.Fingerprint"]': fingerprint })[selector];
  form.querySelectorAll = selector => selector === '[data-product-row]' ? products : selector === '[data-capture-cell]' ? rows : [];
  rows.forEach(row => { row.input.dispatchEvent = () => form.events.input({ target: { matches: () => true } }); });
  const document = { querySelector: selector => selector === '[data-capture-group]' ? form : null };
  vm.runInNewContext(script, { document, Event: class Event {} });
  return { rows, products, form, mode, only, count, units, review, editor, reviewPanel, pin, fingerprint, confirm, back, filterEmpty };
}

test('matrix counts a SKU once, separates area totals and validates every area', () => {
  const ui = setup({ matrix: true });
  assert.equal(ui.count.textContent, 'Products: 1');
  assert.equal(ui.units.textContent, '20 Corte · PC · 2.5 Costura · KG');
  ui.rows[2].input.value = '2,5'; ui.rows[2].input.events.blur();
  assert.equal(ui.review.disabled, true);
  assert.equal(ui.rows[2].error.textContent, 'Invalid format');
  assert.equal(ui.rows[0].total.textContent, '100');
});

test('matrix filter and area clear preserve the other areas and notes', () => {
  const ui = setup({ matrix: true });
  ui.only.checked = true; ui.only.events.change();
  ui.rows[0].clear.events.click();
  assert.equal(ui.products[0].hidden, false);
  assert.equal(ui.rows[1].input.value, '2.5');
  assert.equal(ui.rows[1].notes.value, 'memo');
  ui.rows[0].undo.events.click();
  assert.equal(ui.rows[0].input.value, '20');
  assert.equal(ui.rows[0].notes.value, 'memo');
});

test('invalid formats, out of range and inactive products block review with a field error', () => {
  for (const value of ['3,5', '-1', '1e3', '1.00001', ' 20', '100000000000000']) {
    const ui = setup();
    const row = ui.rows[0];
    row.input.value = value;
    row.input.events.blur();
    assert.equal(ui.review.disabled, true, value);
    assert.equal(row.input['aria-invalid'], 'true');
    assert.notEqual(row.error.textContent, '');
    assert.equal(row.total.textContent, '—');
    let prevented = false;
    ui.form.events.submit({ submitter: { matches: () => true }, preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(row.input.focused, true);
  }
  const ui = setup();
  ui.rows[0].dataset.inactive = 'true';
  ui.rows[0].input.events.blur();
  assert.equal(ui.rows[0].error.textContent, 'Inactive product');
});

test('only-with-quantity preserves hidden notes; clear and undo restore exact input', () => {
  const ui = setup();
  assert.equal(ui.units.textContent, '20 PC · 2.5 KG');
  ui.only.checked = true; ui.only.events.change();
  assert.equal(ui.rows[2].hidden, true);
  assert.equal(ui.rows[2].notes.value, 'memo');
  const row = ui.rows[0];
  row.clear.events.click();
  assert.equal(row.input.value, '');
  assert.equal(row.notes.value, '');
  assert.equal(row.undo.hidden, false);
  assert.equal(row.hidden, false, 'Undo stays reachable after clearing the last quantity');
  assert.equal(ui.units.textContent, '2.5 KG');
  row.undo.events.click();
  assert.equal(row.input.value, '20');
  assert.equal(row.notes.value, 'memo');
  assert.equal(ui.units.textContent, '20 PC · 2.5 KG');
});

test('back to editing invalidates the review and PIN while preserving quantities', () => {
  const ui = setup({ reviewed: true });
  assert.equal(ui.editor.hidden, true);
  ui.back.events.click();
  assert.equal(ui.editor.hidden, false);
  assert.equal(ui.reviewPanel.hidden, true);
  assert.equal(ui.confirm.disabled, true);
  assert.equal(ui.pin.value, '');
  assert.equal(ui.fingerprint.value, '');
  assert.equal(ui.rows[0].input.value, '20');
  assert.equal(ui.rows[0].input.focused, true);
});

test('zero and empty quantities are excluded and four decimals remain exact by unit', () => {
  const ui = setup();
  ui.rows[0].input.value = '0';
  ui.rows[1].input.value = '0.0001';
  ui.form.events.input({ target: { matches: () => true } });
  assert.equal(ui.count.textContent, 'Products: 1');
  assert.equal(ui.units.textContent, '0.0001 KG');
  assert.equal(ui.rows[1].total.textContent, '80.0001');
  ui.rows[1].input.value = '';
  ui.form.events.input({ target: { matches: () => true } });
  assert.equal(ui.review.disabled, true);
});
