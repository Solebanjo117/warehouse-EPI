const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const code = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/production-initial-balances.js', 'utf8');
const product = { productId: 'product', sku: 'SKU-A', unit: 'EA', allowsDecimals: false };
const balance = (area, quantity = 0, version = 0) => ({ ...product, area, quantity, version });
async function setup(data = [], respond = () => data) {
  const status = {}, root = { dataset: { totals: 'true', url: '/totals', legacyUrl: '/legacy' },
    querySelector: () => status, dispatchEvent() {} };
  const window = {};
  vm.runInNewContext(code, { window, document: { querySelectorAll: () => [root] },
    CustomEvent: class { constructor(type) { this.type = type; } },
    fetch: async url => ({ ok: true, json: async () => url === '/totals' ? respond() : respond(url) }) });
  const api = window.ProductionWeekOpenings.get(root); await api.ready;
  return api;
}

test('all three areas accept totals without previous weeks, including an explicit zero', async () => {
  const api = await setup(); api.register(product);
  api.setArea('product', 0, '300'); api.setArea('product', 1, '0'); api.setArea('product', 2, '125');
  assert.equal(api.changes().length, 3); assert.equal(api.errors().length, 0);
  assert.equal(api.groups().length, 1); assert.equal(api.groups()[0].entries.length, 3);
  assert.equal(api.changes()[1].quantity, '0');
  assert.equal(Object.hasOwn(api.changes()[0], 'sourceLineId'), false);
});

test('copy only supplies defaults, preserves edits and can exceed its suggestion', async () => {
  const api = await setup();
  await api.prepare([{ ...product, area: 0, quantity: 40, sourceWeekId: 'source' }]);
  api.setArea('product', 0, '250');
  await api.prepare([{ ...product, area: 0, quantity: 10, sourceWeekId: 'source' }]);
  assert.equal(api.changes()[0].quantity, '250'); assert.equal(api.errors().length, 0);
});

test('saved zero stays authoritative after another copy and local recovery', async () => {
  const api = await setup([balance(0, 0, 2)]);
  await api.prepare([{ ...product, area: 0, quantity: 152 }]);
  assert.equal(api.changes().length, 0);
  api.setArea('product', 1, '0');
  const restored = await setup([balance(0, 0, 2)]); await restored.restore(api.snapshot());
  assert.equal(restored.changes()[0].quantity, '0'); assert.equal(restored.changes()[0].area, 1);
  assert.equal(restored.groups().length, 1);
});

test('whole piece quantities reject negatives, fractions and out-of-range precision without clipping', async () => {
  const api = await setup(); api.register(product);
  for (const value of ['-1', '1.5', '0.00001', '100000000000000', 'bad']) {
    api.setArea('product', 0, value); assert.equal(api.errors().length, 1);
    assert.equal(api.snapshot().values['product:0'], value);
  }
  api.setArea('product', 0, '99999999999999'); assert.equal(api.errors().length, 0);
});

test('decimal units retain four places and normalize commas at the wire boundary', async () => {
  const api = await setup(); api.register({ ...product, allowsDecimals: true });
  api.setArea('product', 2, '12,3456');
  assert.equal(api.changes()[0].quantity, '12.3456'); assert.equal(api.errors().length, 0);
});

test('changed server versions preserve quantities and their observed versions', async () => {
  let current = [balance(0, 40, 1)];
  const api = await setup(current, () => current); api.setArea('product', 0, '20');
  current = [balance(0, 35, 2)]; assert.equal(await api.revalidate(), true);
  assert.equal(api.changes()[0].quantity, '20'); assert.equal(api.changes()[0].expectedVersion, 1);
});

test('legacy root drafts consolidate into one total per product and area', async () => {
  const roots = [1, 2].map(id => ({ ...product, sourceWeekId: 'source', sourceLineId: 'line' + id, area: 0 }));
  const api = await setup([], url => url === '/legacy' ? roots : []);
  await api.restore({ values: { 'source:line1:0': '40', 'source:line2:0': '60' } });
  assert.equal(api.changes().length, 1); assert.equal(api.changes()[0].quantity, '100');
  assert.equal(api.canonicalKey('source:line1:0'), 'product:0');
  assert.equal(api.missing().length, 0);
});

test('excluding and recovering a copied total retains its entered zero', async () => {
  const api = await setup(); await api.prepare([{ ...product, area: 1, quantity: 0 }]);
  api.setEnabled(['product:1'], false); assert.equal(api.changes().length, 0);
  const restored = await setup(); await restored.restore(api.snapshot());
  assert.equal(restored.changes().length, 0); restored.setEnabled(['product:1'], true);
  assert.equal(restored.changes()[0].quantity, '0');
});


test('manual edits override copy exclusions by area and survive repeated toggles and recovery', async () => {
  const api = await setup();
  await api.prepare([0, 1, 2].map(area => ({ ...product, area, quantity: 40 })));
  const keys = [0, 1, 2].map(area => 'product:' + area);
  api.setEnabled(keys, false);
  api.setArea('product', 0, '135'); api.setArea('product', 1, '0');
  api.setEnabled(keys, false);
  assert.equal(api.changes().length, 2);
  assert.equal(api.changes()[0].quantity, '135'); assert.equal(api.changes()[1].quantity, '0');
  const restored = await setup(); await restored.restore(api.snapshot());
  restored.setEnabled(keys, false);
  await restored.prepare([0, 1, 2].map(area => ({ ...product, area, quantity: 90 })));
  assert.equal(restored.changes().length, 2);
  assert.equal(restored.changes()[0].quantity, '135'); assert.equal(restored.changes()[1].quantity, '0');
  assert.equal(restored.options().find(row => row.area === 2).excluded, true);
  restored.setEnabled(keys, true);
  assert.equal(restored.changes().find(row => row.area === 2).quantity, '40');
});

test('older excluded preparations remain excluded until manually edited', async () => {
  const api = await setup([balance(0)]);
  await api.restore({ format: 2, values: { 'product:0': '0' }, touched: ['product:0'], excluded: ['product:0'] });
  assert.equal(api.changes().length, 0);
  api.setArea('product', 0, '12'); api.setEnabled(['product:0'], false);
  assert.equal(api.changes()[0].quantity, '12');
  api.reset(['product:0']);
  await api.prepare([{ ...product, area: 0, quantity: 20 }]); api.setEnabled(['product:0'], false);
  assert.equal(api.changes().length, 0);
});
