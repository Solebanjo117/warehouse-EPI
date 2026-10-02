const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), vm = require('node:vm');
const runtime = { window: {}, document: { querySelector: () => null } };
vm.runInNewContext(fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/production-schedule-progress.js', 'utf8'), runtime);
const calculate = runtime.window.ProductionScheduleProgress.calculateTotals;
const carry = values => values.map((quantity, area) => ({ area, quantity }));
test('620 FT plus independent carryover gives 820, 920 and 720', () => {
  const result = calculate(['200', '100', '300', '20'], carry(['200', '300', '100']));
  assert.equal(result.total, 6200000n);
  assert.deepEqual([...result.areas], [8200000n, 9200000n, 7200000n]);
});
test('multiple order quantities share one initial total and decimal arithmetic stays exact', () => {
  const result = calculate(['0.1', '0.2', '0.0001'], carry(['0.0001', '0', '2']));
  assert.deepEqual([...result.areas], [3002n, 3001n, 23001n]);
});
test('carry-only products and zero initial totals are independent', () => {
  assert.deepEqual([...calculate([], carry(['200', '0', '100'])).areas], [2000000n, 0n, 1000000n]);
  assert.deepEqual([...calculate(['620'], []).areas], [6200000n, 6200000n, 6200000n]);
});
test('invalid planning or undistributed quantity invalidates all totals', () => {
  for (const result of [calculate(['bad'], []), calculate(['2'], [], true), calculate(['1.5'], [], false, false)]) {
    assert.equal(result.total, null); assert.ok(result.areas.every(value => value === null));
  }
});
test('invalid carryover affects only that area and does not hide the planned total', () => {
  const result = calculate(['620'], carry(['200', '-1', '100']));
  assert.equal(result.total, 6200000n);
  assert.deepEqual([...result.areas], [8200000n, null, 7200000n]);
});
