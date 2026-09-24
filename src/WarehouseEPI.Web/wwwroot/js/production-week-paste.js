(function (scope) {
  'use strict';
  const days = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'];
  const normalize = value => String(value || '').trim().toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '');
  const dayOf = (value, weekStart) => {
    const normalized = normalize(value);
    const found = days.map(normalize).indexOf(normalized);
    if (found >= 0) return found;
    if (!/^\d{4}-\d{2}-\d{2}$/.test(normalized)) return -1;
    const target = Date.parse(`${normalized}T00:00:00Z`);
    const start = Date.parse(`${weekStart}T00:00:00Z`);
    const offset = (target - start) / 86400000;
    return Number.isInteger(offset) && offset >= 0 && offset <= 6 ? offset : -1;
  };
  const parse = (raw, weekStart) => {
    const lines = raw.replace(/\r/g, '').split('\n').filter(line => line.trim());
    if (lines.length < 2) throw new Error('Pega encabezados y al menos un renglón.');
    const headers = lines[0].split('\t').map(normalize);
    const skuIndex = headers.indexOf('sku');
    if (skuIndex < 0) throw new Error('Falta la columna SKU.');
    const long = headers.includes('dia') && headers.includes('cantidad');
    if (!long && !days.every(day => headers.includes(normalize(day))))
      throw new Error('Usa Día, SKU, Cantidad o SKU con las siete columnas de lunes a domingo.');
    const value = (fields, name) => fields[headers.indexOf(name)]?.trim() || '';
    return lines.slice(1).map((line, index) => {
      const fields = line.split('\t');
      const item = { selected: true, sku: fields[skuIndex]?.trim() || '',
        days: days.map(() => ''), sourceRow: index + 2,
        details: { orderReference1: value(fields, 'pedido 1'), orderReference2: value(fields, 'pedido 2'),
          orderReference3: value(fields, 'pedido 3'), notes: value(fields, 'notas') } };
      if (long) {
        const day = dayOf(value(fields, 'dia'), weekStart);
        if (day < 0) item.error = `Fila ${index + 2}: día fuera de esta semana.`;
        else item.days[day] = value(fields, 'cantidad');
      } else days.forEach((day, n) => { item.days[n] = value(fields, normalize(day)); });
      return item;
    });
  };
  const api = { parse, dayOf };
  scope.ProductionWeekPaste = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(globalThis);
