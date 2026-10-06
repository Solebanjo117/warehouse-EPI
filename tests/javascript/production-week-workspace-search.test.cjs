const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname,
  '../../src/WarehouseEPI.Web/wwwroot/js/production-week-workspace.js'), 'utf8');
// Exercise the actual search handlers without initializing the unrelated schedule editor.
const start = source.indexOf('  const searchProducts = async () => {');
const end = source.indexOf('  const review = async (forOpening = false) => {', start);
assert.ok(start >= 0 && end > start);
const handlers = source.slice(start, end);

function setup() {
  const element = () => ({ value: '', children: [], attributes: {}, events: {},
    setAttribute(key, value) { this.attributes[key] = value; },
    replaceChildren() { this.children = []; },
    append(...children) { this.children.push(...children); },
    addEventListener(key, handler) { this.events[key] = handler; } });
  const search = element(), results = element();
  const requests = [], messages = [], selected = [], timers = new Map();
  let timerId = 0;
  vm.runInNewContext('let searchGeneration = 0, searchTimer;\n' + handlers, {
    search, results, URL, location: { origin: 'https://localhost' },
    root: { dataset: { searchUrl: '/search', resolveUrl: '/resolve', resolveProductsUrl: '/details' } },
    text: (tag, value) => ({ ...element(), tag, textContent: value }),
    note: message => messages.push(message),
    insertProduct: product => {
      selected.push(product); search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    },
    setTimeout(handler) { const id = ++timerId; timers.set(id, handler); return id; },
    clearTimeout(id) { timers.delete(id); },
    fetch: url => new Promise((resolve, reject) => requests.push({ url: new URL(url), reject,
      resolve: body => resolve({ ok: true, json: async () => body }) }))
  });
  return { search, results, requests, messages, selected, timers,
    type(value) { search.value = value; search.events.input(); },
    runTimer() { const pending = [...timers.values()]; timers.clear(); return Promise.all(pending.map(handler => handler())); },
    enter() { let prevented = false; const pending = search.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } }); assert.equal(prevented, true); return pending; }
  };
}
const product = sku => ({ id: sku, sku });
const flush = () => new Promise(resolve => setImmediate(resolve));
const labels = ui => ui.results.children.map(child => child.attributes['aria-label']);

for (const query of ['', 'A', '   ']) {
  test(`late suggestions stay closed after changing to ${JSON.stringify(query)}`, async () => {
    const ui = setup(); ui.type('AB'); const pending = ui.runTimer();
    ui.type(query);
    assert.equal(ui.timers.size, 0);
    ui.requests[0].resolve([product('AB-old')]); await pending;
    assert.deepEqual(labels(ui), []);
    assert.equal(ui.search.attributes['aria-expanded'], 'false');
    assert.equal(ui.requests.length, 1);
  });
}

test('typing clears visible options immediately and rejects responses during debounce', async () => {
  const ui = setup(); ui.type('AB'); let pending = ui.runTimer();
  ui.requests[0].resolve([product('AB')]); await pending;
  assert.deepEqual(labels(ui), ['AB']);
  ui.type('ABC'); pending = ui.runTimer();
  ui.type('ABCD');
  assert.deepEqual(labels(ui), []);
  assert.equal(ui.search.attributes['aria-expanded'], 'false');
  ui.requests[1].resolve([product('ABC')]); await pending;
  assert.deepEqual(labels(ui), []);
  assert.equal(ui.timers.size, 1);
});

test('out-of-order responses retain only the latest options and selection', async () => {
  const ui = setup(); ui.type('AB'); const first = ui.runTimer();
  ui.type('CD'); const second = ui.runTimer();
  ui.requests[1].resolve([product('CD')]); await second;
  ui.requests[0].resolve([product('AB')]); await first;
  assert.deepEqual(labels(ui), ['CD']);
  assert.equal(ui.search.attributes['aria-expanded'], 'true');
  assert.equal(ui.results.children[0].attributes.role, 'option');
  ui.results.children[0].events.click();
  assert.deepEqual(ui.selected, [product('CD')]);
});

test('returning to the same text cannot revive an older request', async () => {
  const ui = setup(); ui.type('AB'); const pending = ui.runTimer();
  ui.type(''); ui.type('AB');
  ui.requests[0].resolve([product('AB-old')]); await pending;
  assert.deepEqual(labels(ui), []);
});

test('obsolete failures are silent while current network failures remain actionable', async () => {
  const ui = setup(); ui.type('AB'); const first = ui.runTimer();
  ui.type('CD'); ui.requests[0].reject(new Error('offline')); await first;
  assert.equal(ui.messages.length, 0);
  const second = ui.runTimer(); ui.requests[1].reject(new Error('offline')); await second;
  assert.equal(ui.messages.length, 1);
  assert.equal(ui.search.value, 'CD');
});

test('scanner Enter cancels suggestions and resolves the exact product', async () => {
  const ui = setup(); ui.type('AB'); const suggestions = ui.runTimer();
  const scan = ui.enter();
  assert.equal(ui.requests[1].url.searchParams.get('code'), 'AB');
  ui.requests[0].resolve([product('AB-old')]); await suggestions;
  assert.deepEqual(labels(ui), []);
  ui.requests[1].resolve(product('AB-exact')); await flush();
  assert.equal(ui.requests[2].url.searchParams.get('skus'), 'AB-exact');
  ui.requests[2].resolve([product('AB-exact')]); await scan;
  assert.deepEqual(ui.selected, [product('AB-exact')]);
});

for (const phase of ['resolve', 'details']) {
  test(`editing during scanner ${phase} prevents stale insertion`, async () => {
    const ui = setup(); ui.type('AB'); const scan = ui.enter();
    assert.equal(ui.timers.size, 0);
    if (phase === 'details') { ui.requests[0].resolve(product('AB')); await flush(); }
    ui.type('');
    if (phase === 'resolve') {
      ui.requests[0].resolve(product('AB')); await flush();
      // Settle any obsolete details request too, so a regression fails instead of hanging.
      ui.requests[1]?.resolve([product('AB')]);
    } else ui.requests[1].resolve([product('AB')]);
    await scan;
    assert.deepEqual(ui.selected, []);
    assert.deepEqual(ui.messages, []);
    assert.equal(ui.search.value, '');
  });
}
