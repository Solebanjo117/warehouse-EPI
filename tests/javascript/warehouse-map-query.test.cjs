const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const script = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/warehouse-map-query.js', 'utf8');

function setup({ width = 1000, height = 700, browserWidth = 1366, search = false, mapTop = 100 } = {}) {
  const callbacks = [];
  let active, now = 1000;
  function element(tagName = 'DIV') {
    const classes = new Set();
    return {
      tagName, dataset: {}, hidden: false, disabled: false, inert: false, attrs: {}, handlers: {}, children: [],
      style: { setProperty(name, value) { this[name] = value; } },
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); }, contains(name) { return classes.has(name); },
        toggle(name, on) { on ??= !classes.has(name); on ? classes.add(name) : classes.delete(name); return on; }
      },
      setAttribute(name, value) { this.attrs[name] = value; }, removeAttribute(name) { delete this.attrs[name]; },
      addEventListener(name, handler, options) { (this.handlers[name] ||= []).push({ handler, options }); },
      fire(name, event = {}) { for (const { handler } of this.handlers[name] || []) handler(event); },
      click() { this.fire('click'); }, focus() { active = this; },
      querySelector() { return null; }, querySelectorAll() { return []; },
      append(child) { child.parent?.children.splice(child.parent.children.indexOf(child), 1); this.children.push(child); child.parent = this; },
      before(marker) { this.parent.children.splice(this.parent.children.indexOf(this), 0, marker); marker.parent = this.parent; },
      replaceWith(child) { child.parent.children.splice(child.parent.children.indexOf(child), 1); this.parent.children.splice(this.parent.children.indexOf(this), 1, child); child.parent = this.parent; }
    };
  }
  const body = element(), page = element(), alreadyInert = element(), frame = element(), root = element(), svg = element('svg');
  const viewport = element(), stage = element(), detail = element(), hint = element();
  const buttons = Object.fromEntries(['in', 'out', 'fit', 'focus', 'expand', 'close'].map(key => [key, element('BUTTON')]));
  const rack = element('g'), panel = element(), position = element('BUTTON'), positionDetail = element();
  rack.dataset = { mapOpen: 'rack-1', mapKind: 'Rack' };
  position.dataset.mapPosition = 'position-1';
  positionDetail.dataset.positionDetail = 'position-1';
  panel.dataset.mapDetail = 'rack-1';
  root.dataset = { canvasWidth: '2000', canvasHeight: '1000', highlightLocation: search ? 'position-1' : '' };
  detail.hidden = panel.hidden = true;
  alreadyInert.inert = true;
  body.append(page); body.append(alreadyInert); page.append(frame);
  root.closest = () => frame;
  viewport.scrollLeft = viewport.scrollTop = detail.scrollTop = 0;
  const window = {
    innerWidth: browserWidth, innerHeight: height + 116, scrollX: 0, scrollY: 240, handlers: {},
    addEventListener: element().addEventListener,
    scrollTo(x, y) { this.scrollX = typeof x === 'object' ? x.left : x; this.scrollY = typeof x === 'object' ? x.top : y; }
  };
  Object.defineProperties(viewport, {
    clientWidth: { get: () => width - (!detail.hidden && window.innerWidth >= 1200 ? 320 : 0) },
    clientHeight: { get: () => parseFloat(viewport.style['--map-expanded-viewport-height']) || height }
  });
  viewport.getBoundingClientRect = () => ({ top: frame.classList.contains('is-expanded') ? 60 : mapTop - (window.scrollY - 240), left: 10 });
  detail.getBoundingClientRect = () => ({ top: window.innerHeight - 250 });
  svg.getBoundingClientRect = () => ({ left: 10 + parseFloat(svg.style.left || 0) - viewport.scrollLeft,
    top: viewport.getBoundingClientRect().top + parseFloat(svg.style.top || 0) - viewport.scrollTop });
  rack.getBoundingClientRect = () => {
    const scale = parseFloat(svg.style.width) / 2000, rect = svg.getBoundingClientRect();
    return { left: rect.left + 1400 * scale, top: rect.top + 700 * scale, width: 60 * scale, height: 40 * scale };
  };
  const rootOne = { svg, '[data-map-viewport]': viewport, '[data-map-stage]': stage, '.warehouse-map-detail': detail,
    '[data-map-detail="rack-1"]': panel };
  root.querySelector = s => rootOne[s] || null;
  root.querySelectorAll = s => ({ '[data-map-detail]': [panel], '[data-map-position]': [position], '[data-map-close]': [buttons.close] }[s] || []);
  const frameOne = { '[data-map-focus]': buttons.focus, '[data-map-expand]': buttons.expand, '[data-map-hint]': hint,
    "[data-map-zoom='in']": buttons.in, "[data-map-zoom='out']": buttons.out, '[data-map-fit]': buttons.fit, '.warehouse-map-toolbar': element() };
  frame.querySelector = s => frameOne[s] || null;
  frame.querySelectorAll = s => s === '[data-map-open]' ? [rack] : [];
  svg.querySelector = s => s === '[data-map-open="rack-1"]' || (search && s === "[data-map-target='true']") ? rack : null;
  panel.querySelector = s => s === '[data-map-close]' ? buttons.close : s.startsWith('[data-map-position') ? position : null;
  panel.querySelectorAll = s => s === '[data-position-detail]' ? [positionDetail] : s === '[data-map-position]' ? [position] : [];
  position.closest = () => panel;
  const document = { body, querySelector: s => s === '[data-warehouse-map]' ? root : null, querySelectorAll: () => [], createComment: () => element('COMMENT') };
  vm.runInNewContext(script, {
    document, window, CSS: { escape: value => value }, performance: { now: () => now },
    requestAnimationFrame: callback => { callbacks.push(callback); return callbacks.length; },
    ResizeObserver: class { observe() {} }
  });
  const scale = () => parseFloat(svg.style.width) / 2000;
  const flush = () => callbacks.splice(0).forEach(callback => callback());
  return { root, viewport, svg, stage, detail, panel, positionDetail, hint, buttons, rack, frame, page, alreadyInert, body, window, scale,
    active: () => active, setNow: value => { now = value; }, resize: (nextWidth, nextBrowserWidth = window.innerWidth) => {
      width = nextWidth; window.innerWidth = nextBrowserWidth;
      window.handlers.resize[0].handler();
      flush();
    }, scroll: nextY => {
      window.scrollY = nextY;
      for (const { handler } of window.handlers.scroll || []) handler();
      flush();
    } };
}
const near = (actual, expected) => assert.ok(Math.abs(actual - expected) < .001, `${actual} != ${expected}`);
const touch = (clientX, clientY) => ({ clientX, clientY });
const event = touches => ({ touches, prevented: false, stopped: false, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } });

test('overview fits both dimensions, centers the canvas and leaves no empty detail', () => {
  const h = setup({ width: 1000, height: 320 });
  near(h.scale(), .32);
  near(parseFloat(h.svg.style.left), 180);
  assert.equal(h.detail.hidden, true);
  assert.equal(h.viewport.scrollTop, 0);
  h.resize(500);
  near(h.scale(), .25);
});

test('zoom controls clamp to full fit and four times the width scale', () => {
  const h = setup();
  for (let i = 0; i < 30; i++) h.buttons.in.click();
  near(h.scale(), 2);
  for (let i = 0; i < 40; i++) h.buttons.out.click();
  near(h.scale(), .5);
  h.buttons.in.click(); h.buttons.fit.click();
  near(h.scale(), .5);
});

test('selection shows detail, closes, restores focus and reopens the same rack', () => {
  const h = setup();
  h.rack.click();
  assert.equal(h.detail.hidden, false);
  assert.equal(h.panel.hidden, false);
  assert.equal(h.positionDetail.hidden, false);
  assert.equal(h.buttons.focus.disabled, false);
  assert.equal(h.active(), h.buttons.close);
  h.buttons.close.click();
  assert.equal(h.detail.hidden, true);
  assert.equal(h.buttons.focus.disabled, true);
  assert.equal(h.active(), h.rack);
  h.rack.click();
  assert.equal(h.detail.hidden, false);
});

test('opening and closing a desktop detail retains zoom in a zoomed view', () => {
  const h = setup();
  h.buttons.in.click(); h.buttons.in.click(); h.buttons.in.click(); h.buttons.in.click();
  const scale = h.scale();
  h.rack.click(); near(h.scale(), scale);
  const x = (h.viewport.scrollLeft + h.viewport.clientWidth / 2) / scale;
  h.buttons.close.click(); near(h.scale(), scale);
  near((h.viewport.scrollLeft + h.viewport.clientWidth / 2) / scale, x);
});

test('selection focus and search approach the rack at a readable scale', () => {
  const h = setup();
  h.rack.click(); h.buttons.focus.click();
  near(h.scale(), 1);
  const searched = setup({ search: true });
  near(searched.scale(), 1);
  assert.equal(searched.detail.hidden, false);
  assert.equal(searched.positionDetail.hidden, false);
});

test('tablet sheet leaves the searched rack visible above it', () => {
  const h = setup({ width: 700, browserWidth: 768, search: true });
  const rect = h.rack.getBoundingClientRect();
  assert.ok(rect.top >= 100);
  assert.ok(rect.top + rect.height <= h.detail.getBoundingClientRect().top);
  assert.ok(parseFloat(h.stage.style.height) > parseFloat(h.svg.style.height));
});

test('resizing a zoomed map retains its scale and center away from canvas edges', () => {
  const h = setup();
  h.buttons.in.click(); h.buttons.in.click(); h.buttons.in.click();
  h.viewport.scrollLeft = 300;
  const x = (h.viewport.scrollLeft + h.viewport.clientWidth / 2) / h.scale();
  const scale = h.scale();
  h.resize(900);
  near(h.scale(), scale);
  near((h.viewport.scrollLeft + h.viewport.clientWidth / 2) / scale, x);
});

test('page scroll never changes viewport height, scale, rendered size or camera center', () => {
  const assertStableAfterScroll = (h, nextY) => {
    const snapshot = {
      height: h.viewport.clientHeight,
      scale: h.scale(),
      width: h.svg.style.width,
      svgHeight: h.svg.style.height,
      centerX: (h.viewport.scrollLeft + h.viewport.clientWidth / 2) / h.scale(),
      centerY: (h.viewport.scrollTop + h.viewport.clientHeight / 2) / h.scale()
    };
    h.scroll(nextY);
    assert.equal(h.viewport.clientHeight, snapshot.height);
    near(h.scale(), snapshot.scale);
    assert.equal(h.svg.style.width, snapshot.width);
    assert.equal(h.svg.style.height, snapshot.svgHeight);
    near((h.viewport.scrollLeft + h.viewport.clientWidth / 2) / h.scale(), snapshot.centerX);
    near((h.viewport.scrollTop + h.viewport.clientHeight / 2) / h.scale(), snapshot.centerY);
  };

  const overview = setup({ mapTop: 700 });
  assertStableAfterScroll(overview, 840);

  const zoomed = setup({ mapTop: 700 });
  zoomed.buttons.in.click();
  assertStableAfterScroll(zoomed, 840);

  const selected = setup({ mapTop: 700 });
  selected.rack.click();
  assertStableAfterScroll(selected, 840);

  const searched = setup({ mapTop: 700, search: true });
  assertStableAfterScroll(searched, 920);

  const located = setup({ mapTop: 700 });
  located.root.fire('warehouse-map-center', { detail: { x: 1200, y: 600 } });
  assertStableAfterScroll(located, 840);
});

test('a search below tall tablet filters scrolls immediately before positioning the rack', () => {
  const h = setup({ width: 700, browserWidth: 768, search: true, mapTop: 700 });
  near(h.viewport.getBoundingClientRect().top, 80);
  const rect = h.rack.getBoundingClientRect();
  assert.ok(rect.top >= 80);
  assert.ok(rect.top + rect.height <= h.detail.getBoundingClientRect().top);
});

test('expanded mode isolates background and Escape closes detail before restoring page', () => {
  const h = setup();
  h.buttons.expand.click();
  assert.equal(h.page.inert, true);
  assert.equal(h.frame.parent, h.body);
  assert.equal(h.frame.attrs['aria-modal'], 'true');
  h.rack.click();
  const first = { ...event([]), key: 'Escape' }; h.frame.fire('keydown', first);
  assert.equal(h.detail.hidden, true);
  assert.equal(h.frame.classList.contains('is-expanded'), true);
  const second = { ...event([]), key: 'Escape' }; h.frame.fire('keydown', second);
  assert.equal(h.frame.parent, h.page);
  assert.equal(h.page.inert, false);
  assert.equal(h.alreadyInert.inert, true);
  assert.equal(h.window.scrollY, 240);
  assert.equal(h.active(), h.buttons.expand);
});

test('pinch preserves the point under the fingers and allows native one-finger pan', () => {
  const h = setup();
  h.buttons.in.click(); h.buttons.in.click(); h.buttons.in.click(); h.buttons.in.click();
  h.viewport.scrollLeft = 500; h.viewport.scrollTop = 200;
  const oldScale = h.scale();
  const contentX = (500 + 400) / oldScale;
  const contentY = (200 + 250) / oldScale;
  h.viewport.fire('touchstart', event([touch(310, 350), touch(510, 350)]));
  const move = event([touch(210, 350), touch(610, 350)]);
  h.viewport.fire('touchmove', move);
  assert.equal(move.prevented, true);
  near((h.viewport.scrollLeft + 400) / h.scale(), contentX);
  near((h.viewport.scrollTop + 250) / h.scale(), contentY);
  const single = event([touch(210, 350)]); h.viewport.fire('touchmove', single);
  assert.equal(single.prevented, false);
});

test('pinch clamps and suppresses the synthetic click only briefly', () => {
  const h = setup();
  h.viewport.fire('touchstart', event([touch(310, 350), touch(410, 350)]));
  h.viewport.fire('touchmove', event([touch(-500, 350), touch(1500, 350)]));
  near(h.scale(), 2);
  h.viewport.fire('touchend', event([]));
  const click = event([]); h.viewport.fire('click', click);
  assert.equal(click.prevented, true); assert.equal(click.stopped, true);
  h.setNow(1600);
  const later = event([]); h.viewport.fire('click', later);
  assert.equal(later.prevented, false);
});
