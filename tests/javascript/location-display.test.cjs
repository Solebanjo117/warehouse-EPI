const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const dictionary = require('./localization-dictionary.cjs');

const script = fs.readFileSync(path.join(__dirname, '../../src/WarehouseEPI.Web/wwwroot/js/location-display.js'), 'utf8');

function display(options = {}) {
  const element = () => {
    const classes = new Set();
    return { textContent: '', events: {}, style: { transform: '', removeProperty(name) { this[name] = ''; } },
      classList: { add(...names) { names.forEach(name => classes.add(name)); },
        remove(...names) { names.forEach(name => classes.delete(name)); }, contains(name) { return classes.has(name); } },
      addEventListener(name, handler) { this.events[name] = handler; },
      setAttribute(name, value) { this[name] = value; } };
  };
  const slides = [];
  let version = 0;
  const contentByRow = new Map([['A', 'A'], ['B', 'B']]);
  const slide = (row, content = row) => ({ ...element(), dataset: { displayRow: row, displayPart: '1', displayRacks: `${row}-1`, displayAsNext: `Sigue el rack ${row}-1` },
    innerHTML: content,
    version: version++, hidden: false,
    getAttribute(name) { return name === 'aria-label' ? `Fila ${row}, parte 1 de 1` : null; },
    before(...nodes) { slides.splice(slides.indexOf(this), 0, ...nodes); },
    remove() { slides.splice(slides.indexOf(this), 1); }
  });
  slides.push(slide('A'), slide('B'));
  const holder = element();
  const capturedPointers = new Set();
  holder.setPointerCapture = id => capturedPointers.add(id);
  holder.hasPointerCapture = id => capturedPointers.has(id);
  holder.releasePointerCapture = id => capturedPointers.delete(id);
  holder.clientWidth = 600;
  holder.querySelectorAll = () => slides;
  const controls = Object.fromEntries(['slides', 'progress', 'status', 'updated', 'live', 'pause', 'prev', 'next', 'fullscreen', 'upcoming', 'bar']
    .map(name => [name, name === 'slides' ? holder : element()]));
  const jumps = ['A', 'B'].map(row => ({ ...element(), dataset: { displayRowJump: row } }));
  const root = { style: { setProperty() {} }, dataset: { uiTexts: JSON.stringify(dictionary(options.language)), seconds: '20', displayOrientation: options.orientation || 'auto' }, querySelectorAll() { return jumps; }, querySelector(selector) {
    return controls[selector.match(/data-display-([^\]]+)/)[1]];
  } };
  const timers = new Map();
  const timerDelays = new Map();
  const intervals = [];
  const requests = [];
  let nextTimer = 0;
  let holdNextRequest = false;
  const media = new Map();
  const window = {
    PointerEvent: options.legacyTouch ? undefined : function PointerEvent() {},
    events: {}, addEventListener(name, callback) { this.events[name] = callback; },
    location: { href: 'https://example.test/Locations/Display?play=true' },
    matchMedia(query) {
      const value = { matches: query.includes('899') ? !!options.narrow : query.includes('portrait') ? !!options.portrait : true,
        addEventListener(name, handler) { this.change = handler; } };
      media.set(query, value);
      return value;
    },
    setTimeout(callback, delay) {
      const id = ++nextTimer;
      if (delay === 0) callback();
      else { timers.set(id, callback); timerDelays.set(id, delay); }
      return id;
    },
    clearTimeout(id) { timers.delete(id); timerDelays.delete(id); },
    setInterval(callback, delay) { intervals.push({ callback, delay }); }
  };
  const document = { body: {}, documentElement: {}, events: {}, addEventListener(name, callback) { this.events[name] = callback; },
    querySelector(selector) { return selector === '[data-location-display]' ? root : null; } };
  const DOMParser = class { parseFromString(row) { return { querySelector(selector) {
    if (selector === '[data-display-updated]') return { textContent: 'Actualizado ahora' };
    if (selector === '[data-display-slides]') return { querySelectorAll() { return [slide(row, contentByRow.get(row))]; } };
    return null;
  } }; } };
  const fetch = (url, options) => {
    requests.push(url);
    if (holdNextRequest) {
      holdNextRequest = false;
      return new Promise((_, reject) => options.signal.addEventListener('abort',
        () => reject(new Error('Tiempo agotado')), { once: true }));
    }
    return Promise.resolve({ ok: true, text: async () => new URL(url).searchParams.get('rows') });
  };
  vm.runInNewContext(script, { window, document, fetch, DOMParser, URL, AbortController });
  const touch = (x, y) => ({ identifier: 1, clientX: x, clientY: y });
  const pointer = (x, y, overrides = {}) => ({ pointerId: 1, pointerType: options.pointerType || 'touch',
    isPrimary: true, button: 0, buttons: 1, clientX: x, clientY: y, preventDefault() {}, ...overrides });
  return { slides, controls, jumps, timers, timerDelays, requests, root, document, window, capturedPointers, refreshInterval: intervals[0].delay,
    pointerEvent(name, x, y, overrides = {}) { holder.events[name](pointer(x, y, overrides)); },
    resize(narrow, portrait) {
      media.forEach((value, query) => {
        if (!value.change) return;
        value.matches = query.includes('899') ? narrow : portrait;
        value.change();
      });
    },
    setRowContent(row, content) { contentByRow.set(row, content); },
    holdNextRequest() { holdNextRequest = true; },
    refreshNow() { intervals[0].callback(); },
    start(x, y) {
      if (options.legacyTouch) holder.events.touchstart({ touches: [touch(x, y)], changedTouches: [touch(x, y)] });
      else this.pointerEvent('pointerdown', x, y);
    },
    move(x, y) {
      if (options.legacyTouch) holder.events.touchmove({ touches: [touch(x, y)], preventDefault() {} });
      else this.pointerEvent('pointermove', x, y);
    },
    end(x, y) {
      if (options.legacyTouch) holder.events.touchend({ changedTouches: [touch(x, y)] });
      else this.pointerEvent('pointerup', x, y, { buttons: 0 });
    },
    swipe(fromX, fromY, toX, toY) {
      this.start(fromX, fromY);
      this.move(toX, toY);
      this.end(toX, toY);
    } };
}

test('English controls stay translated through navigation, orientation, refresh errors and fullscreen', async () => {
  const view = display({ language: 'en' });
  assert.equal(view.controls.progress.textContent, '1 of 2');
  assert.equal(view.controls.upcoming.textContent, 'Next row: B');
  view.controls.pause.events.click();
  assert.equal(view.controls.pause.textContent, 'Resume');
  assert.equal(view.controls.status.textContent, 'Paused');
  view.resize(false, true);
  view.controls.next.events.click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.controls.status.textContent, 'Paused');
  assert.equal(view.controls.upcoming.textContent, 'Next row: A');
  view.controls.pause.events.click();
  assert.equal(view.controls.pause.textContent, 'Pause');
  view.holdNextRequest(); view.refreshNow();
  const timeout = [...view.timerDelays].find(([, delay]) => delay === 8000)[0];
  view.timers.get(timeout)();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.controls.status.textContent, 'Could not refresh · showing the last query');
  view.refreshNow(); await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.controls.status.textContent, 'Playing');
  await view.controls.fullscreen.events.click();
  assert.equal(view.controls.live.textContent, 'Could not open full screen. Use the browser option.');
  view.document.fullscreenElement = {};
  view.document.events.fullscreenchange();
  assert.equal(view.controls.fullscreen.textContent, 'Exit full screen');
  view.document.fullscreenElement = null;
  view.document.events.fullscreenchange();
  assert.equal(view.controls.fullscreen.textContent, 'Full screen');
});

test('orientation applies to single racks and changes without losing pause or the visible rack', () => {
  const view = display();
  assert.equal(view.root.dataset.displayEffectiveOrientation, 'landscape');
  view.controls.next.events.click();
  view.controls.pause.events.click();
  view.resize(false, true);
  assert.equal(view.root.dataset.displayEffectiveOrientation, 'portrait');
  assert.equal(view.slides[1].hidden, false);
  assert.equal(view.controls.pause['aria-pressed'], 'true');
  view.resize(false, false);
  assert.equal(view.root.dataset.displayEffectiveOrientation, 'landscape');
});

test('forced orientations override the physical orientation while narrow windows stay vertical', () => {
  assert.equal(display({ orientation: 'portrait' }).root.dataset.displayEffectiveOrientation, 'portrait');
  const view = display({ orientation: 'landscape', portrait: true });
  assert.equal(view.root.dataset.displayEffectiveOrientation, 'landscape');
  view.resize(true, false);
  assert.equal(view.root.dataset.displayEffectiveOrientation, 'portrait');
});

test('refresh includes the saved orientation', async () => {
  const view = display({ orientation: 'portrait' });
  view.refreshNow();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(view.requests[0]).searchParams.get('orientation'), 'portrait');
});

test('slide follows the finger and previews the adjacent slide before release', () => {
  const view = display();
  view.start(300, 100);
  view.move(200, 105);
  assert.equal(view.slides[0].style.transform, 'translate3d(-100px, 0, 0)');
  assert.equal(view.slides[1].style.transform, 'translate3d(500px, 0, 0)');
  assert.equal(view.slides[1].hidden, false);
  view.end(200, 105);
  assert.equal(view.slides[1].hidden, false);
});

test('a short drag returns to the current slide', () => {
  const view = display();
  view.start(300, 100);
  view.move(270, 100);
  assert.equal(view.slides[0].style.transform, 'translate3d(-30px, 0, 0)');
  view.end(270, 100);
  assert.equal(view.slides[0].hidden, false);
  assert.equal(view.slides[1].hidden, true);
  assert.equal(view.slides[0].style.transform, '');
});

for (const pointerType of ['mouse', 'pen', 'touch']) {
  test(`${pointerType} drag follows the pointer, advances once and releases capture`, () => {
    const view = display({ pointerType });
    view.start(300, 100);
    assert.equal(view.capturedPointers.has(1), true);
    view.move(190, 100);
    assert.equal(view.slides[0].style.transform, 'translate3d(-110px, 0, 0)');
    view.end(190, 100);
    assert.equal(view.slides[1].hidden, false);
    assert.equal(view.capturedPointers.size, 0);
    assert.equal(view.controls.slides.classList.contains('display-pointer-active'), false);
    view.swipe(190, 100, 300, 100);
    assert.equal(view.slides[0].hidden, false);
  });
}

test('mouse hover and secondary buttons do not navigate; losing the held button cancels', () => {
  const view = display({ pointerType: 'mouse' });
  view.pointerEvent('pointermove', 190, 100, { buttons: 0 });
  view.pointerEvent('pointerdown', 300, 100, { button: 2, buttons: 2 });
  view.pointerEvent('pointermove', 190, 100, { buttons: 2 });
  view.pointerEvent('pointerup', 190, 100, { button: 2, buttons: 0 });
  assert.equal(view.slides[0].hidden, false);
  assert.equal(view.capturedPointers.size, 0);
  view.start(300, 100);
  view.move(190, 100);
  view.pointerEvent('pointermove', 180, 100, { buttons: 0 });
  assert.equal(view.slides[0].hidden, false);
  assert.equal(view.slides[1].hidden, true);
  assert.equal(view.capturedPointers.size, 0);
});

test('events from another pointer cannot move or finish an active drag', () => {
  const view = display({ pointerType: 'mouse' });
  view.start(300, 100);
  view.pointerEvent('pointermove', 100, 100, { pointerId: 2 });
  view.pointerEvent('pointerup', 100, 100, { pointerId: 2 });
  assert.equal(view.slides[0].style.transform, '');
  assert.equal(view.capturedPointers.has(1), true);
  view.move(190, 100);
  view.end(190, 100);
  assert.equal(view.slides[1].hidden, false);
});

test('adding a second finger cancels the drag so pinch gestures do not change racks', () => {
  const view = display();
  view.start(300, 100);
  view.move(190, 100);
  view.pointerEvent('pointerdown', 400, 100, { pointerId: 2, isPrimary: false });
  view.end(190, 100);
  assert.equal(view.slides[0].hidden, false);
  assert.equal(view.slides[1].hidden, true);
  assert.equal(view.capturedPointers.size, 0);
});

test('pointer cancellation, lost capture and window blur restore the rack and automatic advance', () => {
  for (const cancellation of ['pointercancel', 'lostpointercapture', 'blur']) {
    const view = display({ pointerType: 'mouse' });
    view.start(300, 100);
    view.move(190, 100);
    if (cancellation === 'blur') view.window.events.blur();
    else view.pointerEvent(cancellation, 190, 100);
    assert.equal(view.slides[0].hidden, false, cancellation);
    assert.equal(view.slides[1].hidden, true, cancellation);
    assert.equal(view.capturedPointers.size, 0, cancellation);
    assert.ok([...view.timerDelays.values()].includes(20000), cancellation);
    view.swipe(300, 100, 190, 100);
    assert.equal(view.slides[1].hidden, false, cancellation);
  }
});

test('legacy touch input still supports dragging and cancellation without Pointer Events', () => {
  const view = display({ legacyTouch: true });
  view.swipe(300, 100, 190, 100);
  assert.equal(view.slides[1].hidden, false);
  view.start(300, 100);
  view.move(190, 100);
  view.controls.slides.events.touchcancel();
  assert.equal(view.slides[1].hidden, false);
  assert.equal(view.slides[0].hidden, true);
});

test('data refresh waits until the finger is released', async () => {
  const view = display();
  view.start(300, 100);
  view.move(270, 100);
  view.refreshNow();
  assert.equal(view.requests.length, 0);
  view.end(270, 100);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(view.requests[0]).searchParams.get('rows'), 'A');
});

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

test('manual navigation restarts the automatic advance timer', async () => {
  const view = display();
  const firstTimer = [...view.timers.keys()][0];
  view.swipe(300, 100, 190, 100);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.timers.has(firstTimer), false);
  assert.equal(view.timers.size, 1);
  [...view.timers.values()][0]();
  assert.equal(view.slides[0].hidden, false);
});

test('showing a row requests fresh data for that row and replaces its slide', async () => {
  const view = display();
  const original = view.slides[1];
  view.setRowContent('B', 'B: nuevo saldo y croquis');
  view.controls.next.events.click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(view.requests[0]).searchParams.getAll('rows').join(','), 'B');
  assert.notEqual(view.slides[1], original);
  assert.equal(view.slides[1].hidden, false);
  assert.equal(view.controls.updated.textContent, 'Actualizado ahora');
});

test('polls the visible row every two seconds without replacing unchanged content', async () => {
  const view = display();
  const original = view.slides[0];
  assert.equal(view.refreshInterval, 2000);
  view.refreshNow();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(new URL(view.requests[0]).searchParams.get('rows'), 'A');
  assert.equal(view.slides[0], original);
  assert.equal(view.controls.status.textContent, 'Reproduciendo');

  view.setRowContent('A', 'A: nuevo saldo y rack destacado');
  view.refreshNow();
  await new Promise(resolve => setImmediate(resolve));
  assert.notEqual(view.slides[0], original);
  assert.equal(view.slides[0].innerHTML, 'A: nuevo saldo y rack destacado');
});

test('a slow refresh times out and the next poll can retry', async () => {
  const view = display();
  view.holdNextRequest();
  view.refreshNow();
  view.refreshNow();
  assert.equal(view.requests.length, 1);
  const timeoutId = [...view.timerDelays].find(([, delay]) => delay === 8000)[0];
  view.timers.get(timeoutId)();
  await new Promise(resolve => setImmediate(resolve));
  assert.match(view.controls.status.textContent, /No se pudo actualizar/);
  view.refreshNow();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.requests.length, 2);
  assert.equal(view.controls.status.textContent, 'Reproduciendo');
});

test('row shortcuts update upcoming row and active navigation', () => {
  const view = display();
  assert.equal(view.controls.upcoming.textContent, 'Sigue la fila B');
  assert.equal(view.jumps[0]['aria-current'], 'true');
  view.jumps[1].events.click();
  assert.equal(view.slides[1].hidden, false);
  assert.equal(view.jumps[0]['aria-current'], 'false');
  assert.equal(view.jumps[1]['aria-current'], 'true');
  assert.equal(view.controls.upcoming.textContent, 'Sigue la fila A');
});

test('poll requests lightweight refresh while retaining display configuration', async () => {
  const view = display();
  view.refreshNow();
  await new Promise(resolve => setImmediate(resolve));
  const url = new URL(view.requests[0]);
  assert.equal(url.searchParams.get('refresh'), 'true');
  assert.equal(url.searchParams.get('play'), 'true');
});
