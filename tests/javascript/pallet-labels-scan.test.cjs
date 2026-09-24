const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const palletScript = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/pallet-labels.js'), 'utf8');

function page({ url = 'https://example.test/Operations/PalletLabels', selectedProduct = '',
  selectedLocation = '', storage = new Map(), reply = () => null, action = null } = {}) {
  const node = (tagName = 'DIV') => ({ tagName, dataset: {}, value: '', children: [], events: {},
    addEventListener(name, handler) { this.events[name] = handler; },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    querySelector(selector) { return selector === 'button' ? this.children.find(x => x.tagName === 'BUTTON') : null; },
    focus() { this.focused = true; }, click() { return this.events.click?.(); } });
  const field = kind => {
    const input = node('INPUT'), results = node(), selectedId = node('INPUT');
    return { input, results, selectedId, wrapper: {
      dataset: { palletLookup: kind }, querySelector(selector) {
        return ({ '[data-pallet-lookup-input]': input, '[data-pallet-lookup-results]': results,
          '[data-pallet-selected-id]': kind === 'product' ? selectedId : null })[selector] || null;
      }
    } };
  };
  const product = field('product'), location = field('location');
  product.selectedId.value = selectedProduct;
  product.input.value = selectedProduct ? 'OLD-SKU' : '';
  location.input.value = selectedLocation ? 'OLD-LOCATION' : '';
  const related = node(), status = node(), form = node('FORM');
  const shell = node();
  shell.dataset = { productOptionsUrl: '/Operations/PalletLabels?handler=ProductOptions',
    locationOptionsUrl: '/Operations/PalletLabels?handler=LocationOptions',
    resolveCodeUrl: '/Operations/Lookup?handler=ResolveCode', selectedLocationId: selectedLocation,
    productStockTitle: 'Productos', locationStockTitle: 'Ubicaciones', scanAmbiguous: 'Ambiguo',
    scanUnknown: 'Desconocido', scanError: 'Error de red', scanNoLocations: 'Sin ubicaciones',
    scanNoProducts: 'Sin productos' };
  shell.querySelector = selector => ({ '[data-pallet-finder-form]': form,
    '[data-pallet-related]': related, '[data-pallet-scan-status]': status })[selector] || null;
  shell.querySelectorAll = () => [product.wrapper, location.wrapper];
  const row = action ? { dataset: { palletProductId: selectedProduct }, querySelector: () => action } : null;
  const document = { body: node('BODY'), addEventListener() {},
    querySelector(selector) { return selector === '[data-pallet-finder]' ? shell :
      selector === '[data-pallet-product-id]' ? row : null; },
    createElement(tag) { return node(tag.toUpperCase()); } };
  const requests = [], visits = [], replacements = [], timers = {};
  let scanOptions;
  const window = { location: { href: url, assign(value) { visits.push(value); } },
    history: { replaceState(_state, _title, value) { replacements.push(value); } },
    sessionStorage: { getItem(key) { return storage.get(key) || null; },
      setItem(key, value) { storage.set(key, value); }, removeItem(key) { storage.delete(key); } },
    WarehouseEpiHidCapture: { listen(options) { scanOptions = options; } },
    setTimeout(callback) { const id = Object.keys(timers).length + 1; timers[id] = callback; return id; },
    clearTimeout(id) { delete timers[id]; } };
  const fetch = async requestUrl => {
    requests.push(requestUrl.toString());
    const result = reply(new URL(requestUrl));
    if (result instanceof Error) throw result;
    return { ok: result !== null, json: async () => result };
  };
  vm.runInNewContext(palletScript, { window, document, URL, AbortController, fetch, console });
  return { product, location, related, status, form, requests, visits, replacements, storage,
    scan: code => scanOptions.onScan(code), scanOptions };
}

test('a scanned barcode starts a new search and prepares the only product-location pair', async () => {
  const h = page({ selectedProduct: 'old-product', selectedLocation: 'old-location', reply: url => {
    if (url.searchParams.get('handler') === 'ResolveCode')
      return { product: { id: 'new-product', sku: "20' W X 144' L" }, location: null };
    return [{ id: 'only-location', code: 'L-5-1', quantity: 25 }];
  } });
  assert.equal(h.scanOptions.allowSpaces, true);
  await h.scan('BARCODE-123');
  assert.equal(h.product.selectedId.value, 'new-product');
  assert.equal(h.location.input.value, '');
  assert.equal(new URL(h.requests[0]).searchParams.get('code'), 'BARCODE-123');
  assert.equal(new URL(h.requests[1]).searchParams.get('productId'), 'new-product');
  assert.equal(new URL(h.requests[1]).searchParams.has('locationId'), false);
  const destination = new URL(h.visits[0]);
  assert.equal(destination.searchParams.get('productId'), 'new-product');
  assert.equal(destination.searchParams.get('location'), 'L-5-1');
  assert.ok(destination.searchParams.get('scanToken'));
});

test('a scanned location with several products shows its unfiltered result page', async () => {
  const h = page({ selectedProduct: 'old-product', reply: url =>
    url.searchParams.get('handler') === 'ResolveCode'
      ? { product: null, location: { id: 'new-location', code: 'B-2-1' } }
      : [{ id: 'one', sku: 'SKU-1' }, { id: 'two', sku: 'SKU-2' }] });
  await h.scan('B-2-1');
  assert.equal(h.product.selectedId.value, '');
  assert.equal(new URL(h.requests[1]).searchParams.get('locationId'), 'new-location');
  assert.equal(new URL(h.visits[0]).searchParams.get('location'), 'B-2-1');
  assert.equal(new URL(h.visits[0]).searchParams.has('productId'), false);
  assert.equal(new URL(h.visits[0]).searchParams.has('scanToken'), false);
});

test('a scanned location with one product prepares the matching pair', async () => {
  const h = page({ reply: url => url.searchParams.get('handler') === 'ResolveCode'
    ? { product: null, location: { id: 'location-1', code: 'C-1-1' } }
    : [{ id: 'product-1', sku: 'SKU-1' }] });
  await h.scan('C-1-1');
  const destination = new URL(h.visits[0]);
  assert.equal(destination.searchParams.get('location'), 'C-1-1');
  assert.equal(destination.searchParams.get('productId'), 'product-1');
  assert.ok(destination.searchParams.get('scanToken'));
});

test('a scanned product with several locations shows its location choices', async () => {
  const h = page({ reply: url => url.searchParams.get('handler') === 'ResolveCode'
    ? { product: { id: 'product-1', sku: 'SKU-1' }, location: null }
    : [{ id: 'l1', code: 'A-1-1' }, { id: 'l2', code: 'A-1-2' }] });
  await h.scan('SKU-1');
  const destination = new URL(h.visits[0]);
  assert.equal(destination.searchParams.get('productId'), 'product-1');
  assert.equal(destination.searchParams.has('location'), false);
  assert.equal(destination.searchParams.has('scanToken'), false);
});

test('ambiguous, unknown, empty-stock, and failed scans stay on the page with feedback', async () => {
  for (const [resolution, options, expected] of [
    [{ product: { id: 'p' }, location: { id: 'l' } }, null, 'Ambiguo'],
    [{ product: null, location: null }, null, 'Desconocido'],
    [{ product: { id: 'p', sku: 'P' }, location: null }, [], 'Sin ubicaciones'],
    [{ product: null, location: { id: 'l', code: 'L' } }, [], 'Sin productos'],
    [new Error('offline'), null, 'Error de red']
  ]) {
    const h = page({ reply: url => url.searchParams.get('handler') === 'ResolveCode' ? resolution : options });
    await h.scan('CODE');
    assert.equal(h.visits.length, 0);
    assert.equal(h.status.textContent, expected);
    assert.equal(h.status.hidden, false);
  }
});

test('the matching preview intent is consumed before one existing form submit', () => {
  const storage = new Map([['warehouse-pallet-scan-preview', JSON.stringify({
    token: 'single-use', productId: 'p', locationId: 'l' })]]);
  const action = { tagName: 'FORM', requestSubmit() { this.submits = (this.submits || 0) + 1; } };
  const h = page({ url: 'https://example.test/Operations/PalletLabels?productId=p&location=L&scanToken=single-use',
    selectedProduct: 'p', selectedLocation: 'l', storage, action });
  assert.equal(action.submits, 1);
  assert.equal(storage.size, 0);
  assert.equal(new URL(h.replacements[0]).searchParams.has('scanToken'), false);
  page({ url: 'https://example.test/Operations/PalletLabels?productId=p&location=L&scanToken=single-use',
    selectedProduct: 'p', selectedLocation: 'l', storage, action });
  assert.equal(action.submits, 1);
});

test('a preview intent with different selected IDs cannot submit a form', () => {
  const storage = new Map([['warehouse-pallet-scan-preview', JSON.stringify({
    token: 'single-use', productId: 'other-product', locationId: 'l' })]]);
  const action = { tagName: 'FORM', requestSubmit() { this.submits = (this.submits || 0) + 1; } };
  page({ url: 'https://example.test/Operations/PalletLabels?scanToken=single-use',
    selectedProduct: 'p', selectedLocation: 'l', storage, action });
  assert.equal(action.submits, undefined);
  assert.equal(storage.size, 0);
});

test('Enter resolves an exact code before using a partial suggestion', async () => {
  const h = page({ reply: url => url.searchParams.get('handler') === 'ResolveCode'
    ? { product: { id: 'p', sku: 'EXACT' }, location: null }
    : [{ id: 'l', code: 'L-1' }] });
  const first = { clicked: 0, click() { this.clicked++; } };
  h.product.results.children = [{ tagName: 'BUTTON', ...first }];
  h.product.input.value = 'EXACT';
  let prevented = false;
  h.product.input.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(prevented, true);
  assert.equal(h.product.results.children.length, 0);
  assert.equal(h.visits.length, 1);
});

test('Enter keeps the suggested choice when the typed text is not an exact code', async () => {
  const h = page({ reply: url => url.searchParams.get('handler') === 'ResolveCode'
    ? { product: null, location: null } : [] });
  let clicked = 0;
  h.product.results.children = [{ tagName: 'BUTTON', click() { clicked++; } }];
  h.product.input.value = 'PARTIAL';
  h.product.input.events.keydown({ key: 'Enter', preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(clicked, 1);
  assert.equal(h.visits.length, 0);
});

test('HID capture accepts spaces only when requested and ignores editable fields', async () => {
  class Element {
    constructor(editable = false) { this.editable = editable; }
    contains(target) { return target === this; }
    closest() { return this.editable ? this : null; }
  }
  const body = new Element(), input = new Element(true);
  let listener, now = 0;
  const document = { body, querySelector: () => null, addEventListener(_event, handler) { listener = handler; } };
  const window = { setTimeout: () => 1, clearTimeout() {}, addEventListener() {} };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/hid-capture.js'), 'utf8'),
    { window, document, Element, performance: { now: () => now }, console });
  const scanned = [];
  window.WarehouseEpiHidCapture.listen({ root: body, allowSpaces: true, onScan: code => scanned.push(code) });
  const send = (key, target = body) => { now += 5; listener({ key, target, preventDefault() {}, stopPropagation() {} }); };
  for (const key of 'SKU A') send(key);
  send('Enter');
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(scanned, ['SKU A']);
  for (const key of 'SKU B') send(key, input);
  send('Enter', input);
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(scanned, ['SKU A']);
});
