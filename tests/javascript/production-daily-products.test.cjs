const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

class Element {
  constructor() { this.children = []; this.events = {}; this.attributes = {}; this.dataset = {}; this.value = ''; this.classList = { toggle() {} }; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren() { this.children = []; }
  addEventListener(name, fn) { this.events[name] = fn; }
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
  getAttribute(name) { return this.attributes[name]; }
  click() { this.events.click?.(); }
  focus() { this.focused = true; }
  scrollIntoView() {}
}
function setup(fetcher, existing = false, quick = false) {
  const field = new Element(), input = new Element(), id = new Element(), results = new Element(), add = new Element(), quantity = new Element();
  quantity.value = '150';
  const notes = { value: 'keep this note' };
  field.dataset = { url: '/Operations/Production?handler=DailyProducts', planned: 'Plan', pending: 'Prior', other: 'Catalog', more: 'More', empty: 'Empty', error: 'Retry' };
  if (quick) field.dataset.resolveUrl = '/Operations/Lookup?handler=ResolveProduct';
  field.querySelector = selector => ({ '[data-product-input]': input, '[data-product-id]': id, '[data-product-results]': results })[selector];
  field.contains = node => node === input || results.children.includes(node);
  const row = { dataset: { productRow: 'planned' }, hidden: quick, querySelector() { return quantity; } };
  let submissions = 0;
  const form = {
    querySelector(selector) { return ({ '[data-daily-product-add]': add, '[name="Group.Date"]': { value: '2026-07-06' }, '[name="Group.Area"]': { value: 'Sewing' },
      '[name="Group.Mode"]': { value: quick ? 'quick' : 'list' } })[selector]; },
    querySelectorAll() { return existing ? [row] : []; },
    requestSubmit(button) { assert.equal(button, add); submissions++; }
  };
  field.closest = () => form;
  const document = { querySelector(selector) { return selector === '[data-daily-product-field]' ? field : null; }, createElement() { return new Element(); } };
  let timer;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-daily-products.js'), 'utf8'), {
    document, AbortController, URL, window: { location: { origin: 'https://localhost' } }, fetch: fetcher,
    setTimeout(fn) { timer = fn; return 1; }, clearTimeout() { timer = null; }
  });
  return { input, id, results, row, quantity, notes, submissions: () => submissions,
    open: () => input.events.focus(), type: async value => { input.value = value; input.events.input(); await timer(); },
    key: key => input.events.keydown({ key, preventDefault() {} }) };
}
const planned = { id: 'planned', sku: 'SKU-P', description: 'Long description' };
const response = groups => ({ ok: true, json: async () => groups });

test('grouped picker shows planned first, focuses existing quantity and preserves the batch', async () => {
  let url;
  const ui = setup(async value => { url = value; return response([
    { group: 0, items: [planned], hasMore: false },
    { group: 2, items: [{ id: 'catalog', sku: 'CAT', description: '' }], hasMore: false }
  ]); }, true);
  await ui.open();
  assert.equal(url.searchParams.get('date'), '2026-07-06');
  assert.equal(url.searchParams.get('area'), 'Sewing');
  assert.equal(ui.results.children[0].children[0].textContent, 'Plan');
  assert.equal(ui.results.children[1].children[0].textContent, 'Catalog');
  ui.key('ArrowDown'); ui.key('Enter');
  assert.equal(ui.id.value, 'planned');
  assert.equal(ui.quantity.focused, true);
  assert.equal(ui.row.hidden, false);
  assert.equal(ui.quantity.value, '150');
  assert.equal(ui.notes.value, 'keep this note');
  assert.equal(ui.submissions(), 0);
});

test('selecting an unlisted product posts the existing batch once; Escape closes suggestions', async () => {
  const ui = setup(async () => response([{ group: 0, items: [planned], hasMore: false }]));
  await ui.open();
  ui.key('Enter');
  assert.equal(ui.submissions(), 1);
  assert.equal(ui.quantity.value, '150');
  await ui.open();
  ui.key('Escape');
  assert.equal(ui.results.children.length, 0);
  assert.equal(ui.input.attributes['aria-expanded'], 'false');
});

test('load more preserves previously loaded groups and searches retain quantities', async () => {
  let requested;
  const ui = setup(async url => {
    requested = url;
    return response([{ group: 0, offset: 0, items: [url.searchParams.has('offset') ? { id: 'next', sku: 'NEXT' } : planned], hasMore: !url.searchParams.has('offset') }]);
  });
  await ui.type('SKU');
  const more = ui.results.children[0].children.at(-1);
  await more.events.click();
  assert.equal(requested.searchParams.get('category'), '0');
  assert.equal(requested.searchParams.get('offset'), '1');
  assert.equal(ui.results.children[0].children.length, 3);
  assert.equal(ui.quantity.value, '150');
});

test('a stale network response cannot replace a newer search; network failures are visible', async () => {
  let resolveOld;
  const ui = setup(async url => {
    if (url.searchParams.get('q') === '') return new Promise(resolve => { resolveOld = resolve; });
    if (url.searchParams.get('q') === 'FAIL') throw new Error('offline');
    return response([{ group: 2, items: [{ id: 'new', sku: 'NEW' }], hasMore: false }]);
  });
  const first = ui.open();
  await ui.type('NEW');
  resolveOld(response([{ group: 0, items: [planned], hasMore: false }]));
  await first;
  assert.equal(ui.results.children[0].children[1].children[0].textContent, 'NEW');
  await ui.type('FAIL');
  assert.equal(ui.results.children[0].textContent, 'Retry');
  assert.equal(ui.quantity.value, '150');
});

test('quick scan resolves an exact barcode and focuses an existing row without duplicating it', async () => {
  let resolvedCode;
  const ui = setup(async url => {
    if (url.searchParams.has('code')) {
      resolvedCode = url.searchParams.get('code');
      return response(planned);
    }
    return response([]);
  }, true, true);
  ui.input.value = 'BAR-123';
  await ui.key('Enter');
  assert.equal(resolvedCode, 'BAR-123');
  assert.equal(ui.id.value, 'planned');
  assert.equal(ui.row.hidden, false, 'a hidden quick-mode row becomes visible before focus');
  assert.equal(ui.quantity.focused, true);
  assert.equal(ui.quantity.value, '150');
  assert.equal(ui.submissions(), 0);
});

test('quick scan adds one resolved SKU while keeping the current batch', async () => {
  const ui = setup(async url => response(url.searchParams.has('code') ?
    { id: 'new', sku: 'NEW' } : []), false, true);
  ui.input.value = 'NEW-BARCODE';
  await ui.key('Enter');
  assert.equal(ui.id.value, 'new');
  assert.equal(ui.submissions(), 1);
  assert.equal(ui.quantity.value, '150');
});
