const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/location-display.js'), 'utf8');

function display() {
  const element = () => ({ textContent: '', events: {}, addEventListener(name, handler) { this.events[name] = handler; },
    setAttribute(name, value) { this[name] = value; } });
  const slides = [];
  let version = 0;
  const slide = row => ({
    dataset: { displayRow: row, displayPart: '1' }, version: version++, hidden: false,
    getAttribute(name) { return name === 'aria-label' ? `Fila ${row}, parte 1 de 1` : null; },
    before(...nodes) { slides.splice(slides.indexOf(this), 0, ...nodes); },
    remove() { slides.splice(slides.indexOf(this), 1); }
  });
  slides.push(slide('A'), slide('B'));
  const holder = element();
  holder.querySelectorAll = () => slides;
  const controls = Object.fromEntries(['slides', 'progress', 'status', 'updated', 'live', 'pause', 'prev', 'next', 'fullscreen']
    .map(name => [name, name === 'slides' ? holder : element()]));
  const root = { dataset: { seconds: '20' }, querySelector(selector) {
    return controls[selector.match(/data-display-([^\]]+)/)[1]];
  } };
  const timers = new Map();
  const requests = [];
  let nextTimer = 0;
  const window = {
    location: { href: 'https://example.test/Locations/Display?play=true' },
    setTimeout(callback) { const id = ++nextTimer; timers.set(id, callback); return id; },
    clearTimeout(id) { timers.delete(id); },
    setInterval() {}
  };
  const document = { body: {}, documentElement: {}, addEventListener() {},
    querySelector(selector) { return selector === '[data-location-display]' ? root : null; } };
  const DOMParser = class { parseFromString(row) { return { querySelector(selector) {
    if (selector === '[data-display-updated]') return { textContent: 'Actualizado ahora' };
    if (selector === '[data-display-slides]') return { querySelectorAll() { return [slide(row)]; } };
    return null;
  } }; } };
  const fetch = async url => {
    requests.push(url);
    return { ok: true, text: async () => new URL(url).searchParams.get('rows') };
  };
  vm.runInNewContext(script, { window, document, fetch, DOMParser, URL });
  const touch = (x, y) => ({ identifier: 1, screenX: x, screenY: y });
  return { slides, controls, timers, requests,
    swipe(fromX, fromY, toX, toY) {
      holder.events.touchstart({ touches: [touch(fromX, fromY)], changedTouches: [touch(fromX, fromY)] });
      holder.events.touchend({ changedTouches: [touch(toX, toY)] });
    } };
}

test('horizontal swipe and large edge controls change slides; vertical movement does not', () => {
  const view = display();
  assert.equal(view.slides[0].hidden, false);
  view.swipe(300, 100, 190, 112);
  assert.equal(view.slides[1].hidden, false);
  view.swipe(300, 100, 230, 220);
  assert.equal(view.slides[1].hidden, false);
  view.controls.prev.events.click();
  assert.equal(view.slides[0].hidden, false);
  view.controls.next.events.click();
  assert.equal(view.slides[1].hidden, false);
});

test('manual navigation restarts the automatic advance timer', () => {
  const view = display();
  const firstTimer = [...view.timers.keys()][0];
  view.swipe(300, 100, 190, 100);
  assert.equal(view.timers.has(firstTimer), false);
  assert.equal(view.timers.size, 1);
  [...view.timers.values()][0]();
  assert.equal(view.slides[0].hidden, false);
});

test('showing a row requests fresh data for that row and replaces its slide', async () => {
  const view = display();
  const original = view.slides[1];
  view.controls.next.events.click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(view.requests[0]).searchParams.getAll('rows').join(','), 'B');
  assert.notEqual(view.slides[1], original);
  assert.equal(view.slides[1].hidden, false);
  assert.equal(view.controls.updated.textContent, 'Actualizado ahora');
});
