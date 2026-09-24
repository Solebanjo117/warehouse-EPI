const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('quick mode retains typed rows and calculates the preview by unit', () => {
  const makeRow = (registered, amount, unit, notes = '') => {
    const input = { value: amount };
    const memo = { value: notes };
    const result = { textContent: '' };
    const prior = { dataset: { registered: String(registered) } };
    return {
      dataset: { unit }, hidden: false,
      querySelector(selector) { return ({ '[data-group-quantity]': input,
        'input[name$=".Notes"]': memo, '[data-result-total]': result, '[data-registered]': prior })[selector]; },
      input, memo, result
    };
  };
  const first = makeRow(80, '20', 'PC');
  const second = makeRow(4, '', 'KG', 'keep');
  const third = makeRow(0, '', 'PC');
  const mode = { value: 'list' };
  const buttons = ['list', 'quick'].map(captureMode => ({ dataset: { captureMode },
    events: {}, setAttribute(name, value) { this[name] = value; },
    addEventListener(name, listener) { this.events[name] = listener; } }));
  const count = { textContent: '' }, units = { textContent: '' }, review = { disabled: true };
  const actions = { dataset: { countLabel: 'Productos con cantidad' },
    querySelector(selector) { return ({ '[data-capture-count]': count, '[data-capture-units]': units,
      '[data-group-preview]': review })[selector]; } };
  const search = { focused: false, focus() { this.focused = true; } };
  const guidance = { hidden: true }, listCount = { hidden: false };
  const table = { hidden: false };
  const modeSubmit = {};
  let modeSubmissions = 0;
  const form = { events: {}, addEventListener(name, listener) { this.events[name] = listener; },
    querySelector(selector) { return ({ '[name="Group.Mode"]': mode, '[data-capture-actions]': actions,
      '#group-add-product': search, '[data-quick-guidance]': guidance,
      '[data-list-count]': listCount, '[data-group-mode-submit]': modeSubmit,
      '.production-entry__rows': table })[selector]; },
    querySelectorAll(selector) { return ({ '[data-capture-mode]': buttons,
      '[data-product-row]': [first, second, third] })[selector] || []; },
    requestSubmit(button) { assert.equal(button, modeSubmit); modeSubmissions++; } };
  const document = { querySelector(selector) { return selector === '[data-capture-group]' ? form : null; } };
  const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-capture-workspace.js'), 'utf8');
  vm.runInNewContext(script, { document });
  assert.equal(first.result.textContent, '100');
  assert.equal(count.textContent, 'Productos con cantidad: 1');
  assert.equal(units.textContent, '20 PC');
  assert.equal(review.disabled, false);
  buttons[1].events.click();
  assert.equal(mode.value, 'quick');
  assert.equal(first.hidden, false);
  assert.equal(second.hidden, false, 'a note stays with its row');
  assert.equal(third.hidden, true);
  assert.equal(search.focused, true);
  assert.equal(guidance.hidden, false);
  assert.equal(listCount.hidden, true);
  buttons[0].events.click();
  assert.equal(first.input.value, '20');
  assert.equal(second.memo.value, 'keep');
  assert.equal(modeSubmissions, 1, 'list mode reloads available rows while keeping entered values');
  second.input.value = '2.5';
  form.events.input({ target: { matches() { return true; } } });
  assert.equal(units.textContent, '20 PC · 2.5 KG');
  assert.equal(second.result.textContent, '6.5');
  buttons[1].events.click();
  first.input.value = '';
  second.input.value = '';
  second.memo.value = '';
  form.events.input({ target: { matches() { return true; } } });
  assert.equal(table.hidden, true, 'quick capture starts without an empty table');
});
