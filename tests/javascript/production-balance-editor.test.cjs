const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/production-balance-editor.js'), 'utf8');

function setup(statusCell = null, balance = null, extraCells = [], metrics = [], breakdowns = []) {
  const el = (value = '') => ({ value, dataset: {}, events: {}, hidden: false, disabled: false, children: [],
    attributes: {}, classList: { toggle() {}, add() {}, remove() {} }, setAttribute(name, value) { this.attributes[name] = value; }, select() { this.selected = true; }, focus() { this.focused = true; }, blur() {}, getClientRects() { return [1]; },
    addEventListener(name, fn) { this.events[name] = fn; }, append(...children) { this.children.push(...children); },
    replaceChildren() { this.children = []; }, getAttribute() { return 'SKU Cutting T1'; } });
  const row = { dataset: { balanceProduct: 'product' } };
  const fields = balance ? ['pendingAfterShift1', 'netPending'].map(key => ({
    textContent: balance[key], dataset: { area: '0', balanceField: key }, parentElement: { hidden: false }
  })) : [];
  row.querySelectorAll = () => fields;
  const cell = el('2'); cell.dataset = { original: '2', editArea: '0', editShift: '1' };
  cell.closest = () => row; cell.nextElementSibling = el();
  const allCells = [cell, ...extraCells.map(definition => {
    const input = el(definition.value);
    input.dataset = { original: '0', editArea: '0', editShift: '1', ...definition.dataset };
    const productRow = definition.productId && definition.productId !== 'product'
      ? { dataset: { balanceProduct: definition.productId }, querySelectorAll: () => [] } : row;
    input.closest = () => productRow; input.nextElementSibling = el();
    return input;
  })];
  const nodes = Object.fromEntries(['status','errors','review-button','confirm','discard','review','auth'].map(x => [`[data-edit-${x}]`, el()]));
  nodes['#balance-edit-pin'] = el(); nodes['#balance-edit-reason'] = el(); nodes['[name="__RequestVerificationToken"]'] = el('csrf');
  nodes['[data-edit-reason-field]'] = el();
  nodes.products = el(); nodes.summary = el();
  nodes['[data-edit-review]'].querySelector = selector => selector === '[data-review-summary]' ? nodes.summary : nodes.products;
  const form = el(); form.querySelector = x => nodes[x]; form.reportValidity = () => true;
  form.dataset = { operation: 'operation', week: 'week', date: '2026-09-21', previewUrl: '/preview', confirmUrl: '/confirm', invalid: 'invalid', pending: 'unsaved', error: 'error', unsaved: 'discard?', previewLabel: 'Vista previa sin guardar', review: 'Revisa los totales y confirma con NIP.' };
  Object.assign(form.dataset, { reviewProduct: 'producto', reviewProducts: 'productos', reviewChange: 'cambio', reviewChanges: 'cambios',
    reviewDelta: 'Cambio', reviewProcess: 'Proceso', reviewArea0: 'Corte', reviewArea1: 'Costura', reviewArea2: 'Ready to Pack',
    reviewSchedule: 'Programación', reviewLine: 'Renglón {0}', newPlanLabel: 'Nueva programación' });
  const through = el('2026-09-28');
  const week = el('week-original');
  const document = { events: {}, querySelector: x => x === '#balance-editor' ? form : null,
    querySelectorAll: x => x === '.balance-edit-cell' ? allCells : x === '[data-balance-field]' ? fields : x === '[data-balance-metric]' ? metrics : x === '[data-balance-breakdown]' ? breakdowns : x === '.production-weekly-status' && statusCell ? [statusCell] : x === 'form[method="get"] input, form[method="get"] select, select[data-balance-week]' ? [through, week] : [], createElement: () => el(),
    addEventListener(name, fn) { this.events[name] = fn; } };
  const window = { events: {}, confirm: () => false, addEventListener(name, fn) { this.events[name] = fn; } };
  const requests = []; let timer, reloads = 0;
  const fetch = (url, options) => new Promise((resolve, reject) => requests.push({ url, options, body: JSON.parse(options.body), resolve: body => resolve({ ok: true, json: async () => body }), reject }));
  vm.runInNewContext(source, { document, window, fetch, AbortController, location: { reload() { reloads++; } }, setTimeout(fn) { timer = fn; return 1; }, clearTimeout() { timer = null; } });
  const response = { canConfirm: true, fingerprint: 'fingerprint', requiresAdmin: false, errors: [], balance: { products: [] },
    cells: [{ cell: { productId: 'product', area: 0, shift: 1, requested: 5 }, current: 2, sku: 'SKU' }] };
  return { cell, allCells, fields, nodes, form, window, document, through, week, requests, response, timer: () => timer?.(), reloads: () => reloads };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
const content = node => [node.textContent || '', ...(node.children || []).map(content)].join('');

for(const [ratio,band] of [[0,'low'],[.7499,'low'],[.75,'mid'],[.9999,'mid'],[1,'high'],[1.5,'high'],[null,null]]) {
 test(`compact indicators preserve threshold ${ratio} and update closed breakdowns`,async()=>{
  const strong={textContent:'saved percentage'};
  const metric={dataset:{balanceMetric:'daily',area:'0'},querySelector:key=>key==='strong'?strong:null};
  const detail={dataset:{balanceBreakdown:'daily',area:'0'},textContent:'saved breakdown'};
  const s=setup(null,null,[],[metric],[detail]);
  Object.assign(s.form.dataset,{dailyLabel:'Diario',noTarget:'Sin meta',coveredLabel:'Cubierto',baseLabel:'Base',targetLabel:'Meta',appliedAdvance:'Adelanto aplicado'});
  const query=s.document.querySelector;
  s.document.querySelector=selector=>selector==='[data-balance-product="product"]'?{querySelector:()=>null,querySelectorAll:key=>key==='[data-balance-metric]'?[metric]:key==='[data-balance-breakdown]'?[detail]:[]}:query(selector);
  s.cell.value='5';s.cell.events.input();s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({...s.response,balance:{products:[{productId:'product',cutting:{dailyCoverage:{ratio,covered:12.3456,beforeAdvance:30,target:20,advanceApplied:10}}}]}});
  await flush();
  assert.equal(metric.dataset.progressLevel,band??undefined);
  assert.match(detail.textContent,/Cubierto: 12.3456 · Base: 30 · Meta: 20 · Adelanto aplicado: 10/);
  if(ratio!==null&&ratio<1) assert.doesNotMatch(strong.textContent,/100[,.]0/);
  if(ratio===null) assert.match(strong.textContent,/Sin meta/);
  s.nodes['[data-edit-discard]'].events.click();
  assert.equal(strong.textContent,'saved percentage');assert.equal(detail.textContent,'saved breakdown');
 });
}

test('Enter skips folded planning inputs without dropping their pending values',()=>{
 const s=setup(null,null,[{value:'3',dataset:{planLine:'hidden-line'}},{value:'4',dataset:{editShift:'2'}}]);
 s.allCells[1].getClientRects=()=>[];
 s.cell.events.keydown({key:'Enter',preventDefault(){}});
 assert.ok(s.allCells[2].focused);assert.equal(s.allCells[1].value,'3');
});

test('review groups production by product and process with both shifts in a single table', async () => {
  const s = setup(null, null, [
    { value: '20', dataset: { editShift: '2' } },
    { value: '20', dataset: { editArea: '1' } }
  ]);
  s.cell.value = '20'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, cells: s.requests[0].body.cells.map(cell => ({ cell, current: Number(cell.observed), sku: 'SKU' })) });
  await flush();
  assert.equal(s.nodes.summary.textContent, '1 producto · 3 cambios');
  assert.match(s.nodes['[data-edit-status]'].textContent, /^Vista previa sin guardar · /);
  const product = s.nodes.products.children[0];
  assert.equal(s.nodes.products.children.length, 1);
  assert.equal(product.children[0].textContent, 'SKU');
  const rows = product.children[1].children[1].children;
  assert.equal(rows.length, 2);
  assert.equal(rows[0].children[0].textContent, 'Corte');
  assert.equal(rows[1].children[0].textContent, 'Costura');
  assert.match(content(rows[0].children[1]), /2 → 20Cambio: \+18/);
  assert.match(content(rows[0].children[2]), /0 → 20Cambio: \+20/);
  assert.equal(content(rows[1].children[2]), '—');
});

test('review quantities come from validated strings instead of rounded server numbers, including zero reductions', async () => {
  const s = setup(null, null, [{ value: '0', dataset: { original: '20', editShift: '2' } }]);
  s.cell.dataset.original = '99999999999999.9998'; s.cell.value = '99999999999999.9999';
  s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, requiresReason: true,
    cells: s.requests[0].body.cells.map(cell => ({ cell: { ...cell, requested: Number(cell.requested) }, current: Number(cell.observed), sku: 'SKU' })) });
  await flush();
  assert.match(content(s.nodes.products), /99999999999999.9998 → 99999999999999.9999Cambio: \+0.0001/);
  assert.match(content(s.nodes.products), /20 → 0Cambio: −20/);
  assert.equal(s.nodes['[data-edit-reason-field]'].hidden, false);
  assert.equal(s.nodes['#balance-edit-reason'].focused, true);
  s.nodes['#balance-edit-reason'].value = 'Correct total';
  s.cell.value = '99999999999999.9997'; s.cell.events.input();
  assert.equal(s.nodes['[data-edit-review]'].hidden, true);
  assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  assert.equal(s.nodes['#balance-edit-reason'].value, 'Correct total');
});

test('mixed review preserves each planning line and groups by identifier even with identical SKUs', async () => {
  const s = setup(null, null, [
    { value: '40', dataset: { planLine: 'line-a', original: '10', planSequence: '2' } },
    { value: '30', dataset: { planLine: 'line-b', original: '20', planSequence: '1' } },
    { productId: 'other-product', value: '50', dataset: { newPlan: 'new-a' } }
  ]);
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  const submitted = s.requests[0].body;
  s.requests[0].resolve({ ...s.response, plans: submitted.planChanges.map(change => ({ sku: 'SKU', current: 0, change })),
    newPlans: submitted.newPlans.map(change => ({ sku: 'SKU', change })) });
  await flush();
  assert.equal(s.nodes.summary.textContent, '2 productos · 4 cambios');
  assert.equal(s.nodes.products.children.length, 2);
  const text = content(s.nodes.products);
  assert.ok(text.indexOf('Renglón 1') < text.indexOf('Renglón 2'));
  assert.match(text, /20 → 30Cambio: \+10/);
  assert.match(text, /10 → 40Cambio: \+30/);
  assert.match(text, /Nueva programación0 → 50Cambio: \+50/);
  s.nodes['#balance-edit-pin'].value = '1234';
  const confirming = s.form.events.submit({ preventDefault() {} });
  assert.deepEqual(s.requests[1].body.cells, submitted.cells);
  assert.deepEqual(s.requests[1].body.planChanges, submitted.planChanges);
  assert.deepEqual(s.requests[1].body.newPlans, submitted.newPlans);
  s.requests[1].resolve({ success: true }); await confirming;
});

test('pending updates immediately before the server request, retains negatives and undo restores it', () => {
  const s = setup(null, { pendingAfterShift1: '50', netPending: '20' });
  s.cell.value = '32'; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['20', '-10']);
  assert.equal(s.requests.length, 0);
  assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  s.cell.value = '42'; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['10', '-20']);
  s.cell.nextElementSibling.events.click();
  assert.deepEqual(s.fields.map(x => x.textContent), ['50', '20']);
});

test('fixed-point previews preserve four decimals even at the input limit', () => {
  const s = setup(null, { pendingAfterShift1: '0.0001', netPending: '-0.0001' });
  // Capture originals and balance values must describe the same server snapshot.
  s.cell.value = '99999999999999.9999'; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['-99999999999997.9998', '-99999999999998']);
});

test('T2 affects final pending only; invalid and planning changes wait for server validation', () => {
  const s = setup(null, { pendingAfterShift1: '50', netPending: '20' });
  s.cell.dataset.editShift = '2';
  s.cell.value = '32'; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['50', '-10']);
  s.cell.value = ''; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['50', '20']);
  s.cell.dataset.planLine = 'line'; s.cell.value = '32'; s.cell.events.input();
  assert.deepEqual(s.fields.map(x => x.textContent), ['50', '20']);
});

test('new edits abort obsolete requests and keep immediate pending despite late responses', async () => {
  const s = setup(null, { pendingAfterShift1: '50', netPending: '20' });
  s.cell.value = '12'; s.cell.events.input(); const old = s.timer();
  s.cell.value = '32'; s.cell.events.input();
  assert.ok(s.requests[0].options.signal.aborted);
  s.requests[0].resolve(s.response); await old;
  assert.deepEqual(s.fields.map(x => x.textContent), ['20', '-10']);
  const next = s.timer(); s.requests[1].reject(new Error('network')); await next;
  assert.deepEqual(s.fields.map(x => x.textContent), ['20', '-10']);
  assert.equal(s.nodes['[data-edit-errors]'].textContent, 'error');
  assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
});

test('four-decimal changes near the numeric limit are not rounded away locally', () => {
  const s = setup();
  s.cell.dataset.original = '99999999999999.9998';
  s.cell.value = '99999999999999.9999';
  s.cell.events.input(); s.timer();
  assert.equal(s.requests.length, 1);
  assert.equal(s.requests[0].body.cells[0].requested, '99999999999999.9999');
});

test('filter cancellation restores the external week selector, date and quantities; acceptance navigates without another prompt', () => {
  const s = setup(); s.cell.value = '8'; s.cell.events.input();
  s.through.value = '2026-10-04';
  s.week.value = 'week-requested';
  let blocked = false;
  const event = { target: {}, preventDefault() { blocked = true; }, stopImmediatePropagation() {} };
  s.document.events.submit(event);
  assert.ok(blocked);
  assert.equal(s.through.value, '2026-09-28');
  assert.equal(s.week.value, 'week-original');
  assert.equal(s.cell.value, '8');
  s.window.confirm = () => true;
  s.through.value = '2026-10-04'; blocked = false;
  s.document.events.submit(event);
  assert.equal(blocked, false);
  assert.equal(s.through.value, '2026-10-04');
  s.window.events.beforeunload({ preventDefault() { blocked = true; } });
  assert.equal(blocked, false);
  assert.equal(s.requests.length, 0);
});

test('blank is invalid, zero is an explicit reduction, preview never sends PIN', async () => {
  const s = setup(); s.cell.value = ''; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 0); assert.equal(s.nodes['[data-edit-errors]'].textContent, 'invalid');
  s.cell.value = '0'; s.cell.events.input(); const pending = s.timer();
  assert.equal(s.requests[0].body.cells[0].requested, '0'); assert.equal(s.requests[0].body.pin, '');
  assert.equal(s.requests[0].options.headers.RequestVerificationToken, 'csrf');
  s.requests[0].resolve(s.response); await pending;
});

test('preview refreshes Status % from the server result', async () => {
  const status = { textContent: '20.0%' };
  const label = { textContent: 'Menos de 75 %' };
  const statusCell = { dataset: { statusBand: 'low' }, querySelector: selector => selector === '[data-balance-status]' ? status :
    selector === '[data-balance-status-label]' ? label : null };
  const s = setup(statusCell);
  Object.assign(s.form.dataset, { statusLow: 'Menos de 75 %', statusMid: '75 % a menos de 100 %', statusHigh: '100 % o más' });
  const row = { querySelector: selector => selector === '.production-weekly-status' ? statusCell : null,
    querySelectorAll: () => [] };
  const query = s.document.querySelector;
  s.document.querySelector = selector => selector === '[data-balance-product="product"]' ? row : query(selector);
  s.cell.value = '5'; s.cell.events.input();
  s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, balance: { products: [{ productId: 'product', planned: 10,
    statusRatio: 0.8 }] } });
  await flush();
  assert.match(status.textContent, /80/);
  assert.match(status.textContent, /%/);
  assert.equal(statusCell.dataset.statusBand, 'mid');
  assert.equal(label.textContent, '75 % a menos de 100 %');
  s.cell.value = '6'; s.cell.events.input();
  assert.equal(status.textContent, '20.0%');
  assert.equal(statusCell.dataset.statusBand, 'low');
  assert.equal(label.textContent, 'Menos de 75 %');
});

test('preview refreshes and restores daily and accumulated area indicators', async () => {
  const metric = kind => {
    const strong = { textContent: `${kind} old` }, small = { textContent: 'old quantities' };
    return { dataset: { balanceMetric: kind, area: '0', progressLevel: 'low' },
      querySelector: key => key === 'strong' ? strong : small, strong, small };
  };
  const daily = metric('daily'), accumulated = metric('accumulated');
  const s = setup(null, null, [], [daily, accumulated]);
  Object.assign(s.form.dataset, { dailyLabel: 'Diario', accumulatedLabel: 'Acumulado', appliedAdvance: 'Adelanto aplicado' });
  const query = s.document.querySelector;
  s.document.querySelector = selector => selector === '[data-balance-product="product"]' ?
    { querySelector: () => null, querySelectorAll: key => key === '[data-balance-metric]' ? [daily, accumulated] : [] } : query(selector);
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, balance: { products: [{ productId: 'product',
    cutting: { dailyCoverage: { ratio: 1, covered: 300, beforeAdvance: 300, advanceApplied: 200 },
      accumulatedCoverage: { ratio: 1.5, covered: 300, target: 200 } } }] } });
  await flush();
  assert.match(daily.strong.textContent, /100[,.]0\s*%/);
  assert.match(accumulated.strong.textContent, /150[,.]0\s*%/);
  assert.equal(daily.dataset.progressLevel, 'high');
  assert.equal(accumulated.dataset.progressLevel, 'high');
  s.cell.value = '6'; s.cell.events.input();
  assert.equal(daily.strong.textContent, 'daily old');
  assert.equal(accumulated.strong.textContent, 'accumulated old');
  assert.equal(daily.dataset.progressLevel, 'low');
});

test('zero status without a comparison baseline is not labeled as low output', async () => {
  const status = { textContent: '' };
  const label = { textContent: '' };
  const statusCell = { dataset: {}, querySelector: selector => selector === '[data-balance-status]' ? status :
    selector === '[data-balance-status-label]' ? label : null };
  const s = setup(statusCell);
  s.form.dataset.statusNoBase = 'Sin base de comparación';
  const query = s.document.querySelector;
  s.document.querySelector = selector => selector === '[data-balance-product="product"]' ?
    { querySelector: key => key === '.production-weekly-status' ? statusCell : null, querySelectorAll: () => [] } : query(selector);
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, balance: { products: [{ productId: 'product', planned: 0,
    readyToPack: { opening: 0 }, statusRatio: 0 }] } });
  await flush();
  assert.equal(statusCell.dataset.statusBand, 'no-base');
  assert.equal(label.textContent, 'Sin base de comparación');
});

test('status just below 100 percent does not display as completed', async () => {
  const status = { textContent: '' };
  const label = { textContent: '' };
  const statusCell = { dataset: {}, querySelector: selector => selector === '[data-balance-status]' ? status :
    selector === '[data-balance-status-label]' ? label : null };
  const s = setup(statusCell);
  s.form.dataset.statusMid = '75 % a menos de 100 %';
  const query = s.document.querySelector;
  s.document.querySelector = selector => selector === '[data-balance-product="product"]' ?
    { querySelector: key => key === '.production-weekly-status' ? statusCell : null, querySelectorAll: () => [] } : query(selector);
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve({ ...s.response, balance: { products: [{ productId: 'product', planned: 100,
    readyToPack: { opening: 0 }, statusRatio: 0.9996 }] } });
  await flush();
  assert.match(status.textContent, /99[.,]9/);
  assert.equal(statusCell.dataset.statusBand, 'mid');
  assert.equal(label.textContent, '75 % a menos de 100 %');
});

test('stale previews cannot restore a confirmation after a newer edit', async () => {
  const s = setup(); s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.cell.value = '7'; s.cell.events.input();
  s.requests[0].resolve(s.response); await flush();
  assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  assert.equal(s.cell.value, '7');
});

test('Escape cancels the active cell and discard clears unsaved navigation guard', () => {
  const s = setup(); s.cell.events.focus(); s.cell.value = '8'; s.cell.events.input();
  let prevented = false;
  s.window.events.beforeunload({ preventDefault() { prevented = true; } }); assert.ok(prevented);
  s.cell.events.keydown({ key: 'Escape', preventDefault() {} }); assert.equal(s.cell.value, '2');
  s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-discard]'].events.click();
  assert.equal(s.cell.value, '2'); prevented = false;
  s.window.events.beforeunload({ preventDefault() { prevented = true; } }); assert.equal(prevented, false);
});

test('uncertain confirmation retries identical content and clears PIN', async () => {
  const s = setup(); s.cell.value = '5'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  s.requests[0].resolve(s.response); await flush();
  s.nodes['#balance-edit-pin'].value = '1234'; const first = s.form.events.submit({ preventDefault() {} });
  assert.equal(s.nodes['#balance-edit-pin'].value, ''); s.requests[1].reject(new Error('network')); await first;
  assert.equal(s.cell.disabled, true); assert.equal(s.nodes['[data-edit-confirm]'].hidden, false);
  s.nodes['#balance-edit-pin'].value = '1234'; const retry = s.form.events.submit({ preventDefault() {} });
  assert.deepEqual(s.requests[1].body, s.requests[2].body);
  s.requests[2].resolve({ success: true }); await retry; assert.equal(s.reloads(), 1);
});

test('plan quantities are reviewed separately from turn totals in the same payload', async () => {
  const s = setup();
  Object.assign(s.cell.dataset, { planLine: 'line', lineVersion: '4', weekVersion: '7' });
  s.cell.value = '8'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells.length, 0);
  assert.equal(s.requests[0].body.planChanges[0].lineId, 'line');
  assert.equal(s.requests[0].body.planChanges[0].expectedWeekVersion, 7);
  assert.equal(s.requests[0].body.pin, '');
  s.requests[0].resolve({ ...s.response, requiresAdmin: true, requiresReason: false, cells: [],
    plans: [{ sku: 'SKU', current: 2, change: { lineId: 'line', requested: 8 } }] });
  await flush();
  assert.equal(s.nodes['#balance-edit-reason'].required, false);
  assert.equal(s.nodes['#balance-edit-pin'].focused, true);
  assert.equal(s.nodes.products.children.length, 1);
  assert.equal(s.nodes['[data-edit-reason-field]'].hidden, true);
});

test('Enter advances to the next production cell without selecting its value or submitting', () => {
  const s = setup();
  const next = { disabled: false, getClientRects: () => [1], focus() { this.focused = true; }, select() { this.selected = true; } };
  const original = s.document.querySelectorAll;
  s.document.querySelectorAll = selector => selector === '.balance-edit-cell' ? [s.cell, next] : original(selector);
  let prevented = false;
  s.cell.events.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
  assert.ok(prevented); assert.ok(next.focused); assert.equal(next.selected, undefined);
  assert.equal(s.requests.length, 0);
});

test('production arithmetic waits for blur, preserves normal focus and submits only its numeric result', async () => {
  const s = setup(null, { pendingAfterShift1: '50', netPending: '20' });
  s.cell.events.focus();
  assert.equal(s.cell.selected, undefined);
  s.cell.value = '2+20-5'; s.cell.events.input();
  assert.equal(s.nodes['[data-edit-review-button]'].disabled, false);
  assert.equal(s.cell.attributes['aria-invalid'], 'false');
  await s.timer();
  assert.equal(s.requests.length, 0);
  assert.equal(s.cell.value, '2+20-5');
  s.cell.events.blur();
  assert.equal(s.cell.value, '17');
  assert.deepEqual(s.fields.map(field => field.textContent), ['35', '5']);
  s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells[0].requested, '17');
  s.requests[0].resolve(s.response); await flush();
  s.cell.nextElementSibling.events.click();
  assert.equal(s.cell.value, '2');
  assert.deepEqual(s.fields.map(field => field.textContent), ['50', '20']);
});

test('Enter and explicit review calculate production expressions before advancing or previewing', async () => {
  const s = setup(null, null, [{ value: '3', dataset: { editShift: '2' } }]);
  s.cell.value = '150 + 20 - 10'; s.cell.events.input();
  s.cell.events.keydown({ key: 'Enter', preventDefault() {} });
  assert.equal(s.cell.value, '160');
  assert.equal(s.allCells[1].focused, true);
  assert.equal(s.allCells[1].selected, undefined);
  s.allCells[1].value = '10-10'; s.allCells[1].events.input();
  s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.allCells[1].value, '0');
  assert.deepEqual(s.requests[0].body.cells.map(cell => cell.requested), ['160']);
  s.requests[0].resolve(s.response); await flush();
});

test('production arithmetic preserves comma decimals and exact four-place precision at the limit', async () => {
  const s = setup();
  for (const [expression, result] of [['0,1+0,2', '0.3'], ['1.0001-0.0002', '0.9999'],
    ['99999999999999.9999-0.0001', '99999999999999.9998']]) {
    s.cell.value = expression; s.cell.events.input(); s.cell.events.blur();
    assert.equal(s.cell.value, result);
    assert.equal(s.cell.attributes['aria-invalid'], 'false');
  }
  s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells[0].requested, '99999999999999.9998');
  s.requests[0].resolve(s.response); await flush();
});

test('invalid or malicious production expressions stay editable and never reach the preview endpoint', async () => {
  const s = setup();
  for (const expression of ['10-20', '10+', '10++2', '10--2', '-10+20', '10*2', '10/2',
    '2e3+1', '0.00001+1', '99999999999999.9999+0.0001', '1+alert(1)']) {
    s.cell.value = expression; s.cell.events.input(); s.cell.events.blur();
    assert.equal(s.cell.value, expression);
    assert.equal(s.cell.attributes['aria-invalid'], 'true');
    assert.equal(s.nodes['[data-edit-review-button]'].disabled, true);
    s.nodes['[data-edit-review-button]'].events.click();
    assert.equal(s.requests.length, 0);
  }
});

test('arithmetic is limited to production totals and Escape restores the value entered at focus', () => {
  const s = setup(null, null, [{ value: '10', dataset: { planLine: 'plan', original: '10' } }]);
  s.cell.events.focus();
  s.cell.value = '2+20'; s.cell.events.input();
  s.cell.events.keydown({ key: 'Escape', preventDefault() {} });
  assert.equal(s.cell.value, '2');
  const plan = s.allCells[1];
  plan.value = '10+2'; plan.events.input(); plan.events.blur();
  assert.equal(plan.value, '10+2');
  assert.equal(s.nodes['[data-edit-review-button]'].disabled, true);
  s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests.length, 0);
});

test('zero plan stages only a new row, returning to zero and discard create nothing', async () => {
  const s = setup();
  Object.assign(s.cell.dataset, { original: '0', newPlan: 'new-row', weekVersion: '7' });
  s.cell.value = '0'; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 0);
  s.cell.events.focus(); assert.ok(s.cell.selected);
  s.cell.value = '50'; s.cell.events.input(); s.nodes['[data-edit-review-button]'].events.click();
  assert.equal(s.requests[0].body.cells.length, 0);
  assert.equal(s.requests[0].body.planChanges.length, 0);
  assert.deepEqual(s.requests[0].body.newPlans, [{ operationId: 'new-row', productId: 'product', requested: '50', expectedWeekVersion: 7 }]);
  s.requests[0].resolve({ ...s.response, cells: [], requiresAdmin: true,
    newPlans: [{ sku: 'SKU', change: { operationId: 'new-row', requested: 50 } }] });
  await flush(); assert.equal(s.nodes.products.children.length, 1);
  s.cell.value = '0'; s.cell.events.input(); await s.timer();
  assert.equal(s.requests.length, 1); assert.equal(s.nodes['[data-edit-confirm]'].hidden, true);
  s.cell.value = '50'; s.cell.events.input(); s.nodes['[data-edit-discard]'].events.click();
  assert.equal(s.cell.value, '0'); assert.equal(s.requests.length, 1);
});
