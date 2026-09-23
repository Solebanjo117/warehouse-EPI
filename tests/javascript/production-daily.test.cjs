const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

for (const [language, missingDescription] of [['en', 'No description'], ['es', 'Sin descripción']]) {
  for (const skuOnly of [false, true]) {
  test(`daily SKU lookup preserves selection and Enter focus (${language}, filter=${skuOnly})`, async () => {
    const input = { value: '', dataset: {}, events: {}, setAttribute() {}, addEventListener(name, listener) { this.events[name] = listener; } };
    const id = { value: 'previous-id' };
    const results = { children: [], replaceChildren() { this.children = []; }, append(button) { this.children.push(button); } };
    const field = { dataset: { productValue: skuOnly ? 'sku' : '' }, querySelector(selector) { return ({ '[data-product-input]': input, '[data-product-id]': skuOnly ? null : id, '[data-product-results]': results })[selector]; } };
    let focused = false;
    let timer;
    let requested;
    const root = { dataset: { noDescription: missingDescription, productUrl: '/Operations/Lookup?handler=Products' },
      querySelectorAll() { return [field]; },
      querySelector(selector) { return selector === '[data-product-id]' ? null : selector === '[data-product-field]' ? field : { focus() { focused = true; } }; } };
    const document = { querySelector(selector) { return selector === '[data-production-daily]' ? root : null; }, querySelectorAll() { return []; }, createElement() { return { classList: { toggle() {} }, setAttribute() {}, append(...children) { this.children = children; }, addEventListener() {} }; } };
    const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8');
    vm.runInNewContext(script, { document, AbortController, setTimeout(callback) { timer = callback; return 1; }, clearTimeout() {},
      fetch: async (url) => { requested = url; return { ok: true, json: async () => [{ id: 'product-guid', sku: 'SKU-Ñ', description: null }] }; } });
    input.value = 'SKU-Ñ';
    input.events.input();
    assert.equal(id.value, skuOnly ? 'previous-id' : '');
    await timer();
    assert.match(requested, /q=SKU-%C3%91$/);
    assert.equal(results.children[0].children[1].textContent, missingDescription);
    let prevented = false;
    input.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
    assert.equal(id.value, skuOnly ? 'previous-id' : 'product-guid');
    assert.equal(input.value, skuOnly ? 'SKU-Ñ' : `SKU-Ñ · ${missingDescription}`);
    assert.equal(prevented, true);
    assert.equal(focused, true);
    assert.equal(results.children.length, 0);
    input.value = 'free text';
    input.events.input();
    assert.equal(id.value, skuOnly ? 'previous-id' : '');
  });
  }
}

test('several SKU pickers on one page keep their own ProductId', async () => {
  const picker = () => {
    const input = { value: '', dataset: {}, events: {}, setAttribute() {}, addEventListener(name, listener) { this.events[name] = listener; } };
    const id = { value: '' };
    const results = { children: [], replaceChildren() { this.children = []; }, append(button) { this.children.push(button); } };
    const field = { querySelector(selector) { return ({ '[data-product-input]': input, '[data-product-id]': id, '[data-product-results]': results })[selector]; } };
    return { input, id, field };
  };
  const first = picker();
  const second = picker();
  const timers = [];
  const root = { dataset: { noDescription: 'Sin descripción', productUrl: '/Operations/Lookup?handler=Products' },
    querySelectorAll() { return [first.field, second.field]; },
    querySelector() { return null; } };
  const document = { querySelector(selector) { return selector === '[data-production-daily]' ? root : null; }, querySelectorAll() { return []; }, createElement() { return { classList: { toggle() {} }, setAttribute() {}, append(...children) { this.children = children; }, addEventListener() {} }; } };
  const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8');
  vm.runInNewContext(script, { document, AbortController, setTimeout(callback) { timers.push(callback); return timers.length; }, clearTimeout() {},
    fetch: async (url) => ({ ok: true, json: async () => [{ id: url.endsWith('AA') ? 'guid-a' : 'guid-b', sku: 'X', description: 'Y' }] }) });
  first.input.value = 'AA';
  first.input.events.input();
  await timers.pop()();
  first.input.events.keydown({ key: 'Enter', preventDefault() {} });
  second.input.value = 'BB';
  second.input.events.input();
  await timers.pop()();
  second.input.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(first.id.value, 'guid-a');
  assert.equal(second.id.value, 'guid-b');
});

test('forms with data-confirm post only after the user accepts', () => {
  const form = { dataset: { confirm: '¿Eliminar el borrador plan.xlsx?' }, events: {}, addEventListener(name, listener) { this.events[name] = listener; } };
  const document = { querySelector() { return null; }, querySelectorAll(selector) { return selector === 'form[data-confirm]' ? [form] : []; } };
  const answers = [false, true];
  const asked = [];
  const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8');
  vm.runInNewContext(script, { document, window: { confirm(message) { asked.push(message); return answers.shift(); } } });
  const submit = () => { let prevented = false; form.events.submit({ preventDefault() { prevented = true; } }); return prevented; };
  assert.equal(submit(), true);
  assert.equal(submit(), false);
  assert.deepEqual(asked, ['¿Eliminar el borrador plan.xlsx?', '¿Eliminar el borrador plan.xlsx?']);
});

test('batch entry advances with Enter, invalidates review and prevents duplicate submits', () => {
  const field = () => ({ value: '', events: {}, focused: false,
    addEventListener(name, fn) { this.events[name] = fn; }, focus() { this.focused = true; } });
  const first = field(), second = field(), notes = field(), pin = field(), review = field();
  const confirm = { disabled: false };
  pin.value = '0123';
  const form = { dataset: {}, events: {}, addEventListener(name, fn) { this.events[name] = fn; },
    querySelectorAll(selector) { return selector === '[data-group-quantity]' ? [first, second] : [first, second, notes]; },
    querySelector(selector) { return ({ '[data-group-confirm]': confirm, 'input[type="password"]': pin,
      'button[formaction*="GroupPreview"]': review })[selector]; } };
  const context = field();
  const area = field(); area.value = 'Cutting';
  let contextSubmissions = 0;
  context.requestSubmit = () => {
    let prevented = false;
    context.events.submit({ preventDefault() { prevented = true; } });
    if (!prevented) contextSubmissions++;
  };
  const document = { querySelectorAll(selector) { return selector === '[data-refresh-context]' ? [area] : []; }, querySelector(selector) {
    return ({ '[data-capture-group]': form, '[data-context-form]': context })[selector] ?? null;
  } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8'),
    { document, window: { confirm() { return false; } } });
  first.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(second.focused, true);
  second.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(review.focused, true);
  notes.events.input();
  assert.equal(confirm.disabled, true);
  assert.equal(pin.value, '');
  area.value = 'Sewing';
  area.events.change();
  assert.equal(contextSubmissions, 1);
  first.value = '3';
  area.value = 'ReadyToPack';
  area.events.change();
  assert.equal(contextSubmissions, 1);
  assert.equal(area.value, 'Cutting');
  let blocked = false;
  context.events.submit({ preventDefault() { blocked = true; } });
  assert.equal(blocked, true);
  blocked = false;
  form.events.submit({ preventDefault() { blocked = true; } });
  assert.equal(blocked, false);
  form.events.submit({ preventDefault() { blocked = true; } });
  assert.equal(blocked, true);
});


test('Enter in batch search filters using the same form and preserves typed quantities', () => {
  const search = { events: {}, addEventListener(name, fn) { this.events[name] = fn; } };
  const quantity = { value: '150', addEventListener() {} };
  const filter = {};
  let submitted;
  const form = { dataset: {}, querySelectorAll(selector) { return selector === '[data-group-quantity]' ? [quantity] : []; },
    querySelector() { return null; }, addEventListener() {}, requestSubmit(button) { submitted = button; } };
  const document = { querySelectorAll() { return []; }, querySelector(selector) {
    return ({ '[data-capture-group]': form, '#group-search': search, 'button[formaction*="GroupFilter"]': filter })[selector] ?? null;
  } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8'), { document });
  search.events.keydown({ key: 'Enter', defaultPrevented: true, preventDefault() {} });
  assert.equal(submitted, undefined, 'Choosing a suggestion must not also submit the form');
  let prevented = false;
  search.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(submitted, filter);
  assert.equal(quantity.value, '150');
});

for (const hasError of [false, true]) {
  test(`batch response focuses ${hasError ? 'validation errors' : 'review and PIN step'}`, () => {
    let focused;
    const review = { focus() { focused = 'review'; } };
    const errors = { focus() { focused = 'errors'; } };
    const form = { dataset: {}, querySelectorAll() { return []; }, addEventListener() {},
      querySelector(selector) { return selector === '[data-group-review]' ? review : null; } };
    const document = { querySelectorAll() { return []; }, querySelector(selector) {
      if (selector === '[data-capture-group]') return form;
      if (selector === '[data-production-errors].validation-summary-errors') return hasError ? errors : null;
      return null;
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8'), { document });
    assert.equal(focused, hasError ? 'errors' : 'review');
  });
}

test('balance date refreshes automatically and changing week clears the old date bounds', () => {
  let submissions = 0;
  const through = { value: '2026-09-22', events: {}, attributes: { min: '2026-09-21', max: '2026-09-26' },
    addEventListener(name, fn) { this.events[name] = fn; }, checkValidity() { return this.value !== 'invalid'; },
    removeAttribute(name) { delete this.attributes[name]; }, closest() { return form; } };
  const week = { events: {}, addEventListener(name, fn) { this.events[name] = fn; }, closest() { return form; } };
  const form = { requestSubmit() { submissions++; }, querySelector(selector) { assert.equal(selector, '[name="Through"]'); return through; } };
  const document = { querySelectorAll() { return []; }, querySelector(selector) {
    return ({ '[data-balance-date]': through, '[data-balance-week]': week })[selector] ?? null;
  } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8'), { document });
  through.events.change();
  assert.equal(submissions, 1);
  through.value = '';
  through.events.change();
  through.value = 'invalid';
  through.events.change();
  assert.equal(submissions, 1);
  week.events.change();
  assert.equal(through.value, '');
  assert.deepEqual(through.attributes, {});
  assert.equal(submissions, 2);
});
