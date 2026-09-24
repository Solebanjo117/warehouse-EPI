const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.join(__dirname,
  '../../src/WarehouseEPI.Web/wwwroot/js/production-daily.js'), 'utf8');

function setup(fetcher) {
  const timers = new Map();
  let nextTimer = 0;
  let document;
  const element = () => ({
    value: '', dataset: {}, events: {}, children: [], attributes: {}, textContent: '',
    classList: { toggle() {} },
    addEventListener(name, handler) { this.events[name] = handler; },
    setAttribute(name, value) { this.attributes[name] = value; },
    replaceChildren() { this.children = []; },
    append(...children) { this.children.push(...children); },
    focus() { document.activeElement = this; this.focused = true; },
    select() { this.selected = true; }
  });
  const input = element(), id = element(), results = element(), error = element(), quantity = element();
  quantity.value = '150';
  const form = { querySelector(selector) { return selector === '[name="Line.Quantity"]' ? quantity : null; } };
  const field = {
    dataset: { productValue: 'sku', productScanExact: 'true' },
    querySelector(selector) {
      return ({ '[data-product-input]': input, '[data-product-id]': id,
        '[data-product-results]': results, '[data-product-scan-error]': error })[selector];
    },
    closest() { return form; }
  };
  const root = {
    dataset: { productUrl: '/Operations/Lookup?handler=Products',
      productResolveUrl: '/Operations/Lookup?handler=ResolveProduct' },
    querySelectorAll() { return [field]; }
  };
  document = {
    activeElement: null,
    querySelector(selector) { return selector === '[data-production-daily]' ? root : null; },
    querySelectorAll() { return []; },
    createElement() { return element(); }
  };
  const requests = [];
  vm.runInNewContext(script, {
    document, URL, AbortController, window: { location: { href: 'https://localhost/Admin/Production/Schedule' } },
    setTimeout(handler) { const key = ++nextTimer; timers.set(key, handler); return key; },
    clearTimeout(key) { timers.delete(key); },
    fetch: async (url, options) => { requests.push({ url: String(url), options }); return fetcher(url, options); }
  });
  const flush = () => new Promise(resolve => setImmediate(resolve));
  return {
    input, id, error, results, quantity, requests,
    type(value) { input.value = value; input.focus(); input.events.input(); },
    key(key) {
      let prevented = false;
      input.events.keydown({ key, preventDefault() { prevented = true; } });
      return prevented;
    },
    async flush() { await flush(); },
    async search() {
      const pending = [...timers.values()];
      timers.clear();
      for (const handler of pending) await handler();
    },
    timerCount() { return timers.size; }
  };
}

const found = product => ({ ok: true, status: 200, json: async () => product });

test('fast scanner Enter resolves an exact SKU before suggestions and selects all quantity', async () => {
  const ui = setup(async () => found({ id: 'product-1', sku: 'SKU-42' }));
  ui.type('SKU-42');
  assert.equal(ui.timerCount(), 1);
  assert.equal(ui.key('Enter'), true);
  await ui.flush();
  assert.equal(ui.timerCount(), 0);
  assert.equal(new URL(ui.requests[0].url).searchParams.get('code'), 'SKU-42');
  assert.equal(ui.id.value, 'product-1');
  assert.equal(ui.input.value, 'SKU-42');
  assert.equal(ui.quantity.focused, true);
  assert.equal(ui.quantity.selected, true);
  if (ui.quantity.selected) ui.quantity.value = '9';
  assert.equal(ui.quantity.value, '9');
});

test('barcode Enter resolves to its SKU without submitting or selecting a partial suggestion', async () => {
  const ui = setup(async url => {
    if (new URL(url, 'https://localhost').searchParams.has('q'))
      return found([{ id: 'wrong', sku: '12345678-OTHER' }]);
    return found({ id: 'product-2', sku: 'ACTUAL-SKU' });
  });
  ui.type('12345678');
  await ui.search();
  assert.equal(ui.results.children.length, 1);
  assert.equal(ui.key('Enter'), true);
  await ui.flush();
  assert.equal(ui.id.value, 'product-2');
  assert.equal(ui.input.value, 'ACTUAL-SKU');
  assert.equal(ui.quantity.selected, true);
});

test('manual ArrowDown and Enter still choose the highlighted suggestion', async () => {
  const ui = setup(async () => found([
    { id: 'first', sku: 'SKU-1' }, { id: 'second', sku: 'SKU-2' }
  ]));
  ui.type('SKU');
  await ui.search();
  assert.equal(ui.key('ArrowDown'), true);
  assert.equal(ui.key('Enter'), true);
  assert.equal(ui.id.value, 'second');
  assert.equal(ui.quantity.selected, true);
  assert.equal(ui.requests.length, 1);
});

test('unknown code and network failure retain scanned text and do not advance', async () => {
  for (const response of [async () => ({ ok: false, status: 404 }), async () => { throw Error('offline'); }]) {
    const ui = setup(response);
    ui.type('UNKNOWN-123');
    assert.equal(ui.key('Enter'), true);
    await ui.flush();
    assert.equal(ui.id.value, '');
    assert.equal(ui.input.value, 'UNKNOWN-123');
    assert.equal(ui.quantity.focused, undefined);
    assert.match(ui.error.textContent, /No se encontró|Reintenta el escaneo/);
  }
});

test('a slower old scan cannot overwrite the latest product', async () => {
  let completeOld;
  const ui = setup(async url => {
    const code = new URL(url).searchParams.get('code');
    if (code === 'OLD') return new Promise(resolve => { completeOld = resolve; });
    return found({ id: 'new-id', sku: 'NEW' });
  });
  ui.type('OLD'); ui.key('Enter');
  await ui.flush();
  ui.type('NEW'); ui.key('Enter');
  await ui.flush();
  completeOld(found({ id: 'old-id', sku: 'OLD' }));
  await ui.flush();
  assert.equal(ui.id.value, 'new-id');
  assert.equal(ui.input.value, 'NEW');
});
