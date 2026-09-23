const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const script = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/warehouse-location.js', 'utf8');

function element() {
  const classes = new Set(['d-none']);
  return {
    attrs: {}, handlers: {}, textContent: '', className: '',
    classList: { add: x => classes.add(x), remove: x => classes.delete(x), toggle(x, on) { on ? classes.add(x) : classes.delete(x); }, contains: x => classes.has(x) },
    setAttribute(name, value) { this.attrs[name] = String(value); },
    addEventListener(name, callback) { (this.handlers[name] ||= []).push(callback); },
    async fire(name) { await Promise.all((this.handlers[name] || []).map(callback => callback({}))); }
  };
}

function setup(translations = {}) {
  const start = element(), center = element(), stop = element(), status = element();
  const layer = element(), accuracy = element(), dot = element();
  const frame = { querySelector: selector => ({
    '[data-location-start]': start, '[data-location-center]': center,
    '[data-location-stop]': stop, '[data-location-status]': status
  })[selector] || null };
  const mapRoot = {
    dataset: { canvasWidth: '1600', canvasHeight: '900' }, dispatched: [], closest: () => frame,
    querySelector: selector => ({ '[data-location-layer]': layer, '[data-location-accuracy]': accuracy, '[data-location-dot]': dot })[selector] || null,
    dispatchEvent(event) { this.dispatched.push(event); }
  };
  const document = { hidden: false, handlers: {}, querySelector: selector => selector === '[data-warehouse-map]' ? mapRoot : null,
    addEventListener(name, callback) { (this.handlers[name] ||= []).push(callback); } };
  const window = {
    isSecureContext: true,
    warehouseText(key, ...args) {
      return (translations[key] || key).replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match);
    },
    handlers: {}, addEventListener(name, callback) { (this.handlers[name] ||= []).push(callback); }
  };
  let success, watchOptions, clears = [];
  const navigator = { geolocation: {
    watchPosition(ok, error, options) { success = ok; watchOptions = options; return 9; },
    clearWatch(id) { clears.push(id); }
  } };
  const fetch = async () => ({ ok: true, json: async () => ({ exists: true, isCurrent: true, revision: 3,
    transform: { originLatitude: 0, originLongitude: 0, a11: 0, a12: 0, a13: 100,
      a21: 0, a22: 0, a23: 50, checkErrorMeters: 3, maximumScaleSvgPerMeter: 2 } }) });
  vm.runInNewContext(script, { document, window, navigator, fetch, Date, Math,
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    setInterval: () => 1, clearInterval: () => {} });
  return { start, center, stop, status, layer, accuracy, dot, mapRoot, navigator,
    success: () => success, options: () => watchOptions, clears };
}

test('Mi ubicación requests an accurate watch and renders position without posting coordinates', async () => {
  const h = setup();
  await h.start.fire('click');
  assert.equal(h.options().enableHighAccuracy, true);
  assert.equal(h.options().maximumAge, 0);
  assert.equal(h.options().timeout, 20000);
  h.success()({ coords: { latitude: 0, longitude: 0, accuracy: 5 }, timestamp: Date.now() });
  assert.equal(h.dot.attrs.cx, '100');
  assert.equal(h.dot.attrs.cy, '50');
  assert.equal(h.accuracy.attrs.r, '16');
  assert.equal(h.layer.classList.contains('d-none'), false);
  assert.equal(h.mapRoot.dispatched[0].type, 'warehouse-map-center');
  assert.match(h.status.textContent, /Ubicación aproximada/);
});

test('Detener ubicación clears the active browser watch and hides the marker', async () => {
  const h = setup();
  await h.start.fire('click');
  await h.stop.fire('click');
  assert.deepEqual(h.clears, [9]);
  assert.equal(h.layer.classList.contains('d-none'), true);
});

test('visible location status uses the localized client dictionary without changing map events', async () => {
  const key = 'Ubicación aproximada · precisión reportada {0} m · hace {1} s.';
  const h = setup({ [key]: 'Approximate location · reported accuracy {0} m · {1} s ago.' });
  await h.start.fire('click');
  h.success()({ coords: { latitude: 0, longitude: 0, accuracy: 5 }, timestamp: Date.now() });
  assert.match(h.status.textContent, /^Approximate location/);
  assert.equal(h.mapRoot.dispatched[0].type, 'warehouse-map-center');
});
