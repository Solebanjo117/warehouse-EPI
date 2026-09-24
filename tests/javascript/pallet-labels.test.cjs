const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('editing location clears the previous product and allows products at the new location', async () => {
  const element = (dataset = {}) => ({
    dataset, value: '', children: [], events: {},
    addEventListener(name, handler) { this.events[name] = handler; },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    focus() { this.focused = true; },
    requestSubmit() { this.submitted = true; },
    querySelector(selector) {
      if (selector === 'button') return this.children.find(child => child.type === 'button') || null;
      return null;
    },
    click() { return this.events.click?.(); }
  });
  const makeField = kind => {
    const input = element();
    const results = element();
    const selectedId = element();
    const wrapper = { dataset: { palletLookup: kind }, querySelector(selector) {
      return ({ '[data-pallet-lookup-input]': input, '[data-pallet-lookup-results]': results,
        '[data-pallet-selected-id]': kind === 'product' ? selectedId : null })[selector] || null;
    } };
    return { input, results, selectedId, wrapper };
  };
  const product = makeField('product');
  const location = makeField('location');
  product.input.value = 'OLD-SKU';
  product.selectedId.value = 'old-product-id';
  location.input.value = 'OLD-LOCATION';
  const related = element();
  const form = element();
  const shell = element({ productOptionsUrl: '/labels?handler=ProductOptions',
    locationOptionsUrl: '/labels?handler=LocationOptions', productStockTitle: 'Productos con stock',
    locationStockTitle: 'Ubicaciones con stock', stockLabel: 'Stock' });
  shell.querySelector = selector => ({ '[data-pallet-finder-form]': form, '[data-pallet-related]': related })[selector] || null;
  shell.querySelectorAll = () => [product.wrapper, location.wrapper];
  const timers = {};
  const requested = [];
  const document = { querySelector: () => shell, addEventListener() {}, createElement: tag => {
    const item = element(); item.tagName = tag.toUpperCase();
    Object.defineProperty(item, 'className', { set(value) { this._className = value; }, get() { return this._className; } });
    return item;
  } };
  const window = { location: { href: 'https://example.test/labels' },
    setTimeout(callback) { const id = Object.keys(timers).length + 1; timers[id] = callback; return id; },
    clearTimeout(id) { delete timers[id]; } };
  const fetch = async url => {
    requested.push(url.toString());
    if (url.searchParams.get('handler') === 'LocationOptions')
      return { ok: true, json: async () => [{ id: 'new-location-id', code: 'NEW-LOCATION', quantity: 8 }] };
    return { ok: true, json: async () => [{ id: 'other-product-id', sku: 'OTHER-SKU', quantity: 8, unitCode: 'RL' }] };
  };
  const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/pallet-labels.js'), 'utf8');
  vm.runInNewContext(script, { document, window, URL, AbortController, fetch });

  location.input.value = 'NEW';
  location.input.events.input();
  assert.equal(product.input.value, '');
  assert.equal(product.selectedId.value, '');
  assert.equal(related.children.length, 0);
  await timers[Object.keys(timers).at(-1)]();
  assert.equal(new URL(requested.at(-1)).searchParams.has('productId'), false);

  await location.results.children[0].click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(requested.at(-1)).searchParams.get('locationId'), 'new-location-id');
  assert.equal(related.children[1].children[0].children[0].textContent, 'OTHER-SKU');
});

