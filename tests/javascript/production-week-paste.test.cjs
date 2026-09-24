const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parse, dayOf } = require('../../src/WarehouseEPI.Web/wwwroot/js/production-week-paste.js');

test('weekly matrix keeps separate products and optional order fields', () => {
  const rows = parse('SKU\tLunes\tMartes\tMiércoles\tJueves\tViernes\tSábado\tDomingo\tPedido 1\tNotas\nA-1\t10\t20\t\t\t\t\t\tPO-1\tUrgente\nA-1\t5\t\t\t\t\t\t\tPO-2\t', '2026-09-21');
  assert.equal(rows.length, 2);
  assert.deepEqual(rows[0].days.slice(0, 3), ['10', '20', '']);
  assert.equal(rows[0].details.orderReference1, 'PO-1');
  assert.equal(rows[0].details.notes, 'Urgente');
  assert.equal(rows[1].days[0], '5');
  assert.equal(rows[1].details.orderReference1, 'PO-2');
});

test('daily list maps names and ISO dates only inside the selected week', () => {
  const rows = parse('Día\tSKU\tCantidad\tPedido 2\nMiércoles\tB-1\t12\tPO-2\n2026-09-27\tC-1\t3\t\n2026-09-28\tD-1\t1\t', '2026-09-21');
  assert.equal(rows[0].days[2], '12');
  assert.equal(rows[0].details.orderReference2, 'PO-2');
  assert.equal(rows[1].days[6], '3');
  assert.match(rows[2].error, /Fila 4.*día fuera/);
  assert.equal(dayOf('lunes', '2026-09-21'), 0);
  assert.equal(dayOf('2026-09-20', '2026-09-21'), -1);
});

test('missing required headings fail before any rows enter the preparation', () => {
  assert.throws(() => parse('Producto\tLunes\nA-1\t10', '2026-09-21'), /SKU/);
  assert.throws(() => parse('SKU\tLunes\nA-1\t10', '2026-09-21'), /siete columnas/);
});
