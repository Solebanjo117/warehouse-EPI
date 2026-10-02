// Optional browser regression suite with an isolated HTML fixture and the real editor script.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const scriptPath = process.env.WAREHOUSE_MAP_EDITOR_SCRIPT || path.join(root, 'src/WarehouseEPI.Web/wwwroot/js/warehouse-map.js');

const architecture = (id, x, y, persisted = true, group = '', locked = false, kind = 'Rectangle') => `
  <g data-architecture-element="${id}" data-layer-code="STRUCTURE" data-kind="${kind}"
     data-x="${x}" data-y="${y}" data-width="200" data-height="150" data-rotation="0" data-radius="0"
     data-points="0,0 200,0 200,150 0,150 0,0" data-label="${id}" data-stroke-token="SECONDARY"
     data-fill-token="NONE" data-stroke-width="4" data-z="1" data-element-locked="${locked}"
     data-persisted="${persisted}" data-group-id="${group}" tabindex="0" role="button"></g>`;

const fixture = () => `<!doctype html><html><head><meta charset="utf-8">
  <style>
    body { margin: 16px; font: 16px sans-serif; }
    svg { display: block; width: 800px; height: 450px; margin: 12px 0; }
    .architecture-stroke-secondary { stroke: #555; }
    .architecture-fill-none { fill: none; }
    .editor-group-outline { fill: none; stroke: blue; stroke-dasharray: 4; pointer-events: none; }
    .editor-vertex-handle, .editor-architecture-resize-handle { fill: blue; }
    [hidden] { display: none; }
  </style></head><body>
  <div data-map-editor data-canvas-width="1600" data-canvas-height="900">
    ${['undo', 'redo', 'align-menu', 'distribute-menu', 'sort-row', 'size-menu', 'duplicate', 'group',
       'ungroup', 'element-lock', 'order-menu', 'archive', 'discard-new', 'finish', 'cancel',
       'zoom-in', 'zoom-out', 'fit'].map(name => `<button type="button" data-editor-${name}>${name}</button>`).join('')}
    <button type="button" data-editor-tool="select">Seleccionar</button>
    <button type="button" data-editor-layer-lock="STRUCTURE" aria-pressed="false">Editable</button>
    <input type="checkbox" data-editor-layer-visible="STRUCTURE" checked>
    <span data-editor-selection-count></span><span data-editor-selection-help></span>
    <span data-editor-status role="status"></span><span data-editor-zoom></span><span data-editor-coordinates></span>
    <input type="hidden" data-editor-geometry>
    <input type="hidden" data-editor-architecture value="[]">
    <input type="hidden" data-editor-layer-state>
    <select data-editor-grid-size><option>25</option></select>
    <input type="checkbox" data-editor-grid-visible checked><input type="checkbox" data-editor-snap checked>
    <svg viewBox="0 0 1600 900" data-editor-svg tabindex="0" aria-label="Editor del croquis">
      <defs><pattern data-editor-grid-pattern><path></path></pattern></defs>
      <rect data-editor-grid width="1600" height="900" fill="transparent"></rect>
      <g data-architecture-layer="STRUCTURE">
        ${architecture('saved', 100, 100, true, '', false, 'Polyline')}
        ${architecture('saved2', 400, 100)}
        ${architecture('new', 100, 500, false)}
        ${architecture('new2', 400, 500, false)}
        ${architecture('groupA', 750, 100, true, 'group')}
        ${architecture('groupB', 1050, 100, true, 'group')}
        ${architecture('locked', 750, 500, true, '', true)}
      </g>
      <g data-editor-element="rack" data-x="1050" data-y="500" data-width="100" data-height="100"
         data-visible="true" data-label="A-1" tabindex="0"><rect width="100" height="100"></rect></g>
      <g data-editor-guides></g><g data-editor-selection></g>
    </svg>
    <div data-editor-properties-empty></div>
    <div data-editor-properties-fields>
      <span data-property-kind></span><span data-property-layer></span>
      <input data-property="label" aria-label="Texto">
      <select data-property="strokeToken"><option>SECONDARY</option></select>
      <select data-property="fillToken"><option>NONE</option></select>
      <input type="number" data-property="strokeWidth" value="4">
      <input type="checkbox" data-property="dashed">
      <div data-property-vertex><input data-vertex-property="x"><input data-vertex-property="y"></div>
    </div>
    <div data-editor-archived-list></div>
  </div>
  <script src="/warehouse-map.js"></script>
</body></html>`;

(async () => {
  const pageSource = fs.readFileSync(path.join(root, 'src/WarehouseEPI.Web/Pages/Admin/Catalogs/Locations/Map/Edit.cshtml'), 'utf8');
  assert.match(pageSource, /data-editor-svg\s+tabindex="0"/, 'The actual editor canvas must receive keyboard focus');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await context.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.hostname !== 'map-editor.test') return route.abort();
      return url.pathname === '/warehouse-map.js'
        ? route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(scriptPath, 'utf8') })
        : route.fulfill({ contentType: 'text/html', body: fixture() });
    });
    const page = await context.newPage();
    page.setDefaultTimeout(5000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.stack));
    const element = id => page.locator(`[data-architecture-element="${id}"]`);
    const states = () => page.locator('[data-editor-architecture]').evaluate(input => JSON.parse(input.value).sort((a, b) => a.Id.localeCompare(b.Id)));
    const state = async id => (await states()).find(item => item.Id === id);
    const reset = async () => {
      await page.goto('http://map-editor.test/editor');
      await page.waitForFunction(() => document.querySelector('[data-editor-architecture]').value !== '[]');
      assert.deepEqual(errors, []);
    };
    // Click the visible top edge of the outline, as in the attached screenshot.
    const select = async (id, modifiers = []) => {
      const point = await element(id).evaluate(item => {
        const value = new DOMPoint(Number(item.dataset.x) + 40, Number(item.dataset.y));
        const screen = value.matrixTransform(item.ownerSVGElement.getScreenCTM());
        return { x: screen.x, y: screen.y };
      });
      for (const modifier of modifiers) await page.keyboard.down(modifier);
      await page.mouse.click(point.x, point.y);
      for (const modifier of modifiers) await page.keyboard.up(modifier);
    };
    const results = [];

    await reset();
    const initial = await states();
    await select('saved');
    assert.equal(await element('saved').evaluate(item => item.classList.contains('is-selected')), true);
    await page.keyboard.press('Delete');
    assert.equal(await element('saved').count(), 0, 'Delete removes a saved outline from the canvas');
    assert.equal((await state('saved')).IsArchived, true, 'Saved IDs remain in the payload for reversible archiving');
    assert.equal(await page.locator('[data-editor-restore="saved"]').count(), 1);
    await page.keyboard.press('Control+z');
    assert.deepEqual(await states(), initial, 'One undo restores the entire original payload');
    await page.keyboard.press('Control+Shift+z');
    assert.equal((await state('saved')).IsArchived, true);
    await page.locator('[data-editor-restore="saved"]').click();
    assert.equal((await state('saved')).IsArchived, false);
    assert.equal(await element('saved').count(), 1);
    results.push('Saved polyline: Delete, undo, redo and restore');

    await reset();
    await select('new');
    await page.keyboard.press('Delete');
    assert.equal(await state('new'), undefined, 'New objects are discarded, not submitted as archives');
    await page.keyboard.press('Control+z');
    assert.equal(await element('new').getAttribute('data-persisted'), 'false');
    results.push('New object: Delete and undo');

    for (const ids of [['saved', 'saved2'], ['new', 'new2'], ['saved', 'new']]) {
      await reset();
      const before = await states();
      await select(ids[0]);
      await select(ids[1], ['Control']);
      await page.keyboard.press('Delete');
      for (const id of ids) {
        assert.equal(await element(id).count(), 0, `${id} is removed in a multiple selection`);
        assert.equal((await state(id))?.IsArchived, id.startsWith('saved') ? true : undefined);
      }
      await page.keyboard.press('Control+z');
      assert.deepEqual(await states(), before, 'Multiple removal is one undo operation');
      results.push(`Multiple selection: ${ids.join(' + ')}`);
    }

    await reset();
    await select('groupA');
    await page.keyboard.press('Delete');
    assert.equal((await state('groupA')).IsArchived, true);
    assert.equal((await state('groupB')).IsArchived, true);
    await page.locator('[data-editor-restore="groupA"]').click();
    assert.equal((await state('groupA')).IsArchived, false);
    assert.equal((await state('groupB')).IsArchived, false);
    await page.keyboard.press('Escape');
    await select('groupA', ['Alt']);
    assert.equal(await page.locator('[data-architecture-element].is-selected').count(), 1);
    await page.keyboard.press('Delete');
    assert.equal((await state('groupA')).IsArchived, true);
    assert.equal((await state('groupB')).IsArchived, true, 'Removing part of a group preserves the server group contract');
    results.push('Groups: Delete, restore and individual Alt selection');

    await reset();
    await element('groupB').evaluate(item => { item.dataset.persisted = 'false'; });
    await select('groupA');
    await page.keyboard.press('Delete');
    assert.equal((await state('groupA')).IsArchived, true);
    assert.equal((await state('groupB')).IsArchived, true, 'New members of saved groups remain in the archive to preserve the group definition');
    await page.locator('[data-editor-restore="groupA"]').click();
    assert.equal((await state('groupB')).IsArchived, false);
    assert.equal(await element('groupB').getAttribute('data-persisted'), 'false');
    results.push('Mixed saved/new group: complete archive and restore');

    await reset();
    await element('groupB').evaluate(item => { item.dataset.elementLocked = 'true'; });
    await select('groupA', ['Alt']);
    const lockedGroup = await states();
    await page.keyboard.press('Delete');
    assert.deepEqual(await states(), lockedGroup, 'A locked group member prevents partial removal');
    await select('locked');
    const lockedElement = await states();
    await page.keyboard.press('Delete');
    assert.deepEqual(await states(), lockedElement, 'Locked elements remain intact');
    await page.locator('[data-editor-layer-lock="STRUCTURE"]').click();
    await select('saved');
    const lockedLayer = await states();
    await page.keyboard.press('Delete');
    assert.deepEqual(await states(), lockedLayer, 'Locked layers remain intact');
    results.push('Locked element, layer and group member protections');

    await reset();
    await page.locator('[data-editor-element="rack"]').click();
    const operations = await page.locator('[data-editor-geometry]').inputValue();
    await page.keyboard.press('Delete');
    assert.equal(await page.locator('[data-editor-geometry]').inputValue(), operations);
    assert.equal(await page.locator('[data-editor-element="rack"]').count(), 1);
    results.push('Operational racks are preserved');

    await reset();
    await select('saved');
    const label = page.locator('[data-property="label"]');
    await label.focus();
    await label.selectText();
    await page.keyboard.press('Delete');
    assert.equal(await label.inputValue(), '', 'Delete keeps its normal behavior in property inputs');
    assert.equal(await element('saved').count(), 1);
    await select('saved');
    assert.equal(await page.locator('[data-editor-svg]').evaluate(svg => document.activeElement === svg), true);
    await page.keyboard.press('Backspace');
    assert.equal((await state('saved')).IsArchived, true, 'Clicking the canvas restores keyboard shortcuts after editing a property');
    results.push('Input editing, focus return and Backspace');

    await reset();
    await select('saved');
    await page.locator('[data-editor-archive]').click();
    assert.equal((await state('saved')).IsArchived, true);
    await select('new');
    await page.locator('[data-editor-discard-new]').click();
    assert.equal(await state('new'), undefined);
    results.push('Existing Archive and Discard buttons');

    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ browser: browser.version(), fixture: 'Isolated HTML; real warehouse-map.js; no database writes', results }, null, 2));
    await context.close();
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
