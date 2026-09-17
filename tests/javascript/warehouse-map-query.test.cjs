const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const script = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/warehouse-map-query.js', 'utf8');

function touch(clientX, clientY) {
  return { clientX, clientY };
}

function setup() {
  const handlers = {};
  const style = {
    values: {},
    setProperty(name, value) { this.values[name] = value; }
  };
  const svg = { style };
  const viewport = {
    clientWidth: 600,
    clientHeight: 400,
    scrollLeft: 200,
    scrollTop: 100,
    addEventListener(name, handler, options) { (handlers[name] ||= []).push({ handler, options }); },
    getBoundingClientRect() { return { left: 10, top: 20 }; }
  };
  const root = {
    dataset: {},
    querySelector(selector) {
      if (selector === 'svg') return svg;
      if (selector === '[data-map-viewport]') return viewport;
      return null;
    },
    querySelectorAll() { return []; }
  };
  let now = 1000;
  const document = {
    querySelector(selector) { return selector === '[data-warehouse-map]' ? root : null; },
    querySelectorAll() { return []; }
  };
  vm.runInNewContext(script, { document, performance: { now: () => now } });

  const fire = (name, event) => {
    for (const entry of handlers[name] || []) entry.handler(event);
  };
  return { viewport, style, handlers, fire, setNow(value) { now = value; } };
}

test('two-finger pinch zooms around the midpoint and keeps native one-finger panning', () => {
  const harness = setup();
  const start = { touches: [touch(210, 220), touch(410, 220)], prevented: false, preventDefault() { this.prevented = true; } };
  harness.fire('touchstart', start);
  assert.equal(start.prevented, true);
  assert.equal(harness.handlers.touchstart[0].options.passive, false);

  const move = { touches: [touch(110, 220), touch(510, 220)], prevented: false, preventDefault() { this.prevented = true; } };
  harness.fire('touchmove', move);

  assert.equal(move.prevented, true);
  assert.equal(harness.style.values['--warehouse-map-query-zoom'], '200%');
  assert.equal(harness.viewport.scrollLeft, 700);
  assert.equal(harness.viewport.scrollTop, 400);

  const oneFingerMove = { touches: [touch(210, 220)], prevented: false, preventDefault() { this.prevented = true; } };
  harness.fire('touchmove', oneFingerMove);
  assert.equal(oneFingerMove.prevented, false);
});

test('pinch stays within zoom limits and suppresses the synthetic rack click', () => {
  const harness = setup();
  const start = { touches: [touch(260, 220), touch(360, 220)], preventDefault() {} };
  harness.fire('touchstart', start);
  harness.fire('touchmove', { touches: [touch(-240, 220), touch(860, 220)], preventDefault() {} });
  assert.equal(harness.style.values['--warehouse-map-query-zoom'], '400%');

  harness.fire('touchend', { touches: [] });
  const click = {
    prevented: false,
    stopped: false,
    preventDefault() { this.prevented = true; },
    stopPropagation() { this.stopped = true; }
  };
  harness.fire('click', click);
  assert.equal(click.prevented, true);
  assert.equal(click.stopped, true);

  harness.setNow(1600);
  const laterClick = {
    prevented: false,
    preventDefault() { this.prevented = true; },
    stopPropagation() {}
  };
  harness.fire('click', laterClick);
  assert.equal(laterClick.prevented, false);
});
