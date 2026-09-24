(() => {
  const root = document.querySelector('[data-week-workspace]');
  if (!root) return;
  const uuid = () => {
    if (crypto.randomUUID) return crypto.randomUUID();
    const bytes = new Uint8Array(16); crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  };
  const $ = selector => root.querySelector(selector);
  const days = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'];
  const weekId = root.dataset.weekId;
  const weekVersion = Number(root.dataset.weekVersion);
  const storageKey = `warehouse-epi:schedule:${root.dataset.userId}:${weekId}`;
  const tbody = $('[data-workspace-rows]');
  const message = $('[data-workspace-message]');
  const search = $('[data-workspace-search]');
  const results = $('[data-workspace-results]');
  const form = $('[data-workspace-form]');
  const reviewPanel = $('[data-workspace-review-panel]');
  const daySelect = $('[data-workspace-day]');
  const openingEditor = window.ProductionWeekOpenings?.get($('[data-opening-editor]'));
  if (!openingEditor) $('[data-workspace-copy-openings]').disabled = true;
  const openingChanges = () => openingEditor?.changes() || [];
  const saved = [...root.querySelectorAll('[data-line]')].map(node => JSON.parse(node.dataset.line));
  let rows = [], openingPreview = [], view = matchMedia('(max-width: 900px)').matches ? 'day' : 'week';
  let activeDay = 0, preview = [], previewDuplicatesAcknowledged = false, submitting = false, operationId = uuid();
  let lastPayload = '', needsReconcile = false, searchTimer, searchGeneration = 0;
  let uncertainSave = false, completedSave = false;
  const frozen = new Map();
  const freezePreparation = () => {
    if (submitting || uncertainSave) root.querySelectorAll('input:not([type="hidden"]), select, textarea, button').forEach(el => {
      if (el === $('[data-workspace-save]')) return;
      if (!frozen.has(el)) frozen.set(el, el.disabled);
      el.disabled = true;
    });
    else { frozen.forEach((disabled, el) => { el.disabled = disabled; }); frozen.clear(); }
  };
  const text = (tag, value, cls = '') => {
    const node = document.createElement(tag); node.textContent = value; if (cls) node.className = cls; return node;
  };
  const note = (value, type = 'info') => {
    message.replaceChildren(text('div', value, `alert alert-${type} mt-2`));
  };
  const blankCell = () => ({ value: '', original: '', lineId: null, version: null, removed: false });
  const fresh = (product, details = {}) => ({
    key: uuid(), productId: product.id, sku: product.sku,
    unit: product.unit || '', allowsDecimals: product.allowsDecimals !== false,
    details: { orderReference1: '', orderReference2: '', orderReference3: '', notes: '',
      originalType: '', originalAnnotation1: '', originalAnnotation2: '',
      originalAnnotation1Kind: '', originalAnnotation2Kind: '', ...details },
    originalDetails: null, suggestedDays: [], cells: days.map(blankCell)
  });
  const detailsKey = details => JSON.stringify(details);
  const normalizeQuantity = value => String(value ?? '').trim().replace(',', '.');
  const scaled = value => {
    const normalized = normalizeQuantity(value);
    if (!/^(?:\d{1,14})(?:\.\d{1,4})?$/.test(normalized)) return null;
    const [integer, fraction = ''] = normalized.split('.');
    return BigInt(integer) * 10000n + BigInt(fraction.padEnd(4, '0'));
  };
  const validQuantity = (value, decimal = true) => {
    const result = scaled(value);
    return result !== null && result <= 999999999999999999n && (decimal || result % 10000n === 0n);
  };
  const positive = value => validQuantity(value) && scaled(value) > 0n;
  const equalQuantity = (a, b) => scaled(a) === scaled(b);
  const formatScaled = value => {
    const integer = value / 10000n, fraction = String(value % 10000n).padStart(4, '0').replace(/0+$/, '');
    return `${integer}${fraction ? '.' + fraction : ''}`;
  };
  const addExisting = line => {
    const details = Object.fromEntries(['orderReference1', 'orderReference2', 'orderReference3', 'notes',
      'originalType', 'originalAnnotation1', 'originalAnnotation2', 'originalAnnotation1Kind',
      'originalAnnotation2Kind'].map(key => [key, line[key[0].toUpperCase() + key.slice(1)] || '']));
    const day = Number(line.Day);
    let row = rows.find(candidate => candidate.productId === line.ProductId &&
      detailsKey(candidate.details) === detailsKey(details) && !candidate.cells[day]?.lineId);
    if (!row) { row = fresh({ id: line.ProductId, sku: line.Sku, unit: line.Unit }, details); rows.push(row); }
    row.originalDetails = structuredClone(details);
    row.cells[day] = { value: String(line.Quantity), original: String(line.Quantity),
      lineId: line.Id, version: line.Version, removed: false };
  };
  saved.forEach(addExisting);
  const snapshot = () => ({ version: weekVersion, updated: new Date().toISOString(), operationId,
    pendingPayload: lastPayload, pendingSent: uncertainSave, rows, preview, previewSource, openingPreview,
    openingPreviewSourceId, openingPreviewSourceVersion, openingColumnsPrepared,
    openings: openingEditor?.snapshot(), pasteText: $('[data-workspace-paste]').value });
  const persist = () => {
    try { localStorage.setItem(storageKey, JSON.stringify(snapshot())); }
    catch { note('No se pudo guardar la preparación en este navegador. No cierres la página.', 'warning'); }
  };
  const clearConfirmed = (count, state = snapshot()) => {
    if (state.preview?.length || state.openingPreview?.length || state.pasteText?.trim())
      localStorage.setItem(storageKey, JSON.stringify({ version: state.version + count,
        updated: new Date().toISOString(), operationId: uuid(), rows: [],
        preview: state.preview || [], previewSource: state.previewSource || null,
        openingPreview: state.openingPreview || [], openingPreviewSourceId: state.openingPreviewSourceId || null,
        openingPreviewSourceVersion: state.openingPreviewSourceVersion ?? null,
        openingColumnsPrepared: !!state.openingColumnsPrepared,
        pasteText: state.pasteText || '' }));
    else localStorage.removeItem(storageKey);
  };
  const invalidateReview = () => { reviewPanel.hidden = true; lastPayload = ''; operationId = uuid(); persist(); };
  root.addEventListener('openingchange', () => { invalidateReview(); refreshSummary(); });
  const quantityChanges = () => {
    const changes = [], errors = [];
    rows.forEach((row, rowIndex) => {
      const metadataChanged = row.originalDetails && detailsKey(row.details) !== detailsKey(row.originalDetails);
      row.cells.forEach((cell, day) => {
        const value = normalizeQuantity(cell.value);
        if (cell.lineId) {
          if (cell.removed) { changes.push({ kind: 'remove', lineId: cell.lineId, expectedLineVersion: cell.version, line: null,
            sku: row.sku, day, before: cell.original, after: 'Quitado', unit: row.unit }); return; }
          if (!positive(value) || !validQuantity(value, row.allowsDecimals)) {
            errors.push(`${row.sku}, ${days[day]}: usa una cantidad positiva o pulsa «Quitar».`); return;
          }
          if (!equalQuantity(value, cell.original) || metadataChanged)
            changes.push({ kind: 'edit', lineId: cell.lineId, expectedLineVersion: cell.version,
              line: toLine(row, day, value), sku: row.sku, day, before: cell.original, after: value, unit: row.unit });
        } else if (value && Number(value) !== 0) {
          if (!positive(value) || !validQuantity(value, row.allowsDecimals))
            errors.push(`${row.sku}, ${days[day]}: cantidad no válida.`);
          else changes.push({ kind: 'add', lineId: null, expectedLineVersion: null,
            line: toLine(row, day, value), sku: row.sku, day, before: '—', after: value, unit: row.unit });
        }
      });
      ['orderReference1', 'orderReference2', 'orderReference3', 'originalType'].forEach(key => {
        if (row.details[key].length > 120) errors.push(`Fila ${rowIndex + 1}: ${key} supera 120 caracteres.`);
      });
      ['notes', 'originalAnnotation1', 'originalAnnotation2'].forEach(key => {
        if (row.details[key].length > 500) errors.push(`Fila ${rowIndex + 1}: ${key} supera 500 caracteres.`);
      });
    });
    if (changes.length + openingChanges().length > 100) errors.push(`Hay ${changes.length + openingChanges().length} cambios. Confirma un máximo de 100 por grupo.`);
    return { changes, errors };
  };
  const toLine = (row, day, quantity) => {
    const date = new Date(`${root.dataset.weekStart}T12:00:00`);
    date.setDate(date.getDate() + day);
    return { plannedDate: `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`,
      productId: row.productId, quantity, ...row.details };
  };
  const totals = () => {
    const sum = new Map();
    rows.forEach(row => row.cells.forEach(cell => {
      if (cell.removed || !positive(cell.value)) return;
      sum.set(row.unit, (sum.get(row.unit) || 0n) + scaled(cell.value));
    }));
    return [...sum].map(([unit, value]) => `${formatScaled(value)} ${unit}`).join(' · ');
  };
  const dayTotals = day => {
    const sum = new Map();
    rows.forEach(row => {
      const cell = row.cells[day];
      if (cell.removed || !positive(cell.value)) return;
      sum.set(row.unit, (sum.get(row.unit) || 0n) + scaled(cell.value));
    });
    return [...sum].map(([unit, value]) => `${formatScaled(value)} ${unit}`).join(' · ') || 'sin cantidad';
  };
  const refreshSummary = () => {
    const { changes } = quantityChanges();
    const count = kind => changes.filter(change => change.kind === kind).length;
    const totalChanges = changes.length + openingChanges().length;
    const carried = new Map();
    openingChanges().forEach(change => {
      if (!positive(change.quantity)) return;
      const key = `${['Corte', 'Costura', 'Ready to Pack'][change.area]} (${change.unit})`;
      carried.set(key, (carried.get(key) || 0n) + scaled(change.quantity));
    });
    const carryTotals = [...carried].map(([key, value]) => `${key}: ${formatScaled(value)}`).join(' · ');
    $('[data-workspace-count]').textContent = `${totalChanges} ${totalChanges === 1 ? 'cambio' : 'cambios'}`;
    $('[data-workspace-summary]').textContent = `${count('add')} nuevos · ${count('edit')} modificados · ${count('remove')} quitados · ${openingChanges().length} arrastres · ${totals() || 'sin cantidad'}${carryTotals ? ' · Arrastre: ' + carryTotals : ''}`;
    $('[data-workspace-empty]').hidden = rows.length > 0;
    root.querySelectorAll('[data-workspace-total]').forEach(node => {
      const row = rows.find(item => item.key === node.dataset.workspaceTotal);
      node.textContent = row ? `${formatScaled(row.cells.filter(x => !x.removed && positive(x.value)).reduce((sum, x) => sum + scaled(x.value), 0n))} ${row.unit}` : '';
    });
    $('[data-workspace-day-summary]').textContent = view === 'day'
      ? `${days[activeDay]}: ${dayTotals(activeDay)}`
      : days.map((day, index) => `${day}: ${dayTotals(index)}`).join(' · ');
  };
  const showView = () => {
    root.querySelectorAll('[data-workspace-view]').forEach(button => button.classList.toggle('active', button.dataset.workspaceView === view));
    daySelect.hidden = view !== 'day';
    root.querySelectorAll('[data-workspace-day-head], [data-workspace-day-cell]').forEach(node => {
      node.hidden = view === 'day' && Number(node.dataset.workspaceDayHead ?? node.dataset.workspaceDayCell) !== activeDay;
    });
    refreshSummary();
  };
  const render = () => {
    tbody.replaceChildren();
    rows.forEach(row => {
      const tr = document.createElement('tr');
      const sku = document.createElement('th'); sku.scope = 'row';
      sku.append(text('strong', row.sku), text('small', ` ${row.unit}`, 'd-block text-body-secondary'));
      sku.append(text('small', [row.details.orderReference1, row.details.orderReference2,
        row.details.orderReference3].some(Boolean) ? 'Pedido' : 'Inventario', 'd-block'));
      tr.append(sku);
      row.cells.forEach((cell, day) => {
        const td = document.createElement('td'); td.dataset.workspaceDayCell = day;
        const label = `${row.sku}, ${days[day]}, ${row.unit}`;
        const input = document.createElement('input'); input.type = 'text'; input.inputMode = 'decimal';
        input.className = 'form-control production-week-workspace__quantity';
        input.value = cell.value; input.setAttribute('aria-label', label);
        if (row.suggestedDays?.includes(day) && !cell.value) input.placeholder = 'Programar';
        input.disabled = cell.removed;
        input.addEventListener('input', () => { cell.value = input.value; invalidateReview(); refreshSummary(); });
        input.addEventListener('keydown', event => {
          if (event.key !== 'Enter') return;
          event.preventDefault();
          const visible = [...tbody.querySelectorAll('input.production-week-workspace__quantity')].filter(x => !x.disabled && !x.closest('td').hidden);
          visible[(visible.indexOf(input) + 1) % visible.length]?.focus();
        });
        td.append(input);
        if (cell.lineId) {
          const remove = document.createElement('button'); remove.type = 'button';
          remove.className = 'btn btn-outline-danger btn-sm mt-1';
          remove.textContent = cell.removed ? 'Deshacer' : 'Quitar';
          remove.setAttribute('aria-label', `${cell.removed ? 'Deshacer eliminación' : 'Quitar del programa'}: ${label}`);
          remove.addEventListener('click', () => { cell.removed = !cell.removed; invalidateReview(); render(); });
          td.append(remove);
        }
        tr.append(td);
      });
      const total = document.createElement('td'); total.dataset.workspaceTotal = row.key; tr.append(total);
      const actions = document.createElement('td');
      const details = document.createElement('details'); details.append(text('summary', 'Detalles'));
      [['orderReference1', 'Pedido 1'], ['orderReference2', 'Pedido 2'], ['orderReference3', 'Pedido 3'],
        ['notes', 'Notas']].forEach(([key, label]) => {
        const wrapper = document.createElement('div'); wrapper.className = 'mt-2';
        const field = document.createElement('input'); field.className = 'form-control'; field.value = row.details[key];
        field.maxLength = key === 'notes' ? 500 : 120; field.id = `workspace-${row.key}-${key}`;
        const labelNode = text('label', label, 'form-label'); labelNode.htmlFor = field.id;
        field.addEventListener('input', () => { row.details[key] = field.value; invalidateReview(); refreshSummary(); });
        wrapper.append(labelNode, field); details.append(wrapper);
      });
      actions.append(details);
      const duplicate = text('button', 'Duplicar fila', 'btn btn-outline-secondary btn-sm mt-2'); duplicate.type = 'button';
      duplicate.addEventListener('click', () => { rows.push(fresh(row, structuredClone(row.details))); invalidateReview(); render(); });
      actions.append(duplicate);
      if (row.cells.every(cell => !cell.lineId)) {
        const discard = text('button', 'Quitar fila', 'btn btn-outline-danger btn-sm mt-2 ms-1'); discard.type = 'button';
        discard.addEventListener('click', () => { rows = rows.filter(item => item !== row); invalidateReview(); render(); });
        actions.append(discard);
      }
      tr.append(actions); tbody.append(tr);
    });
    showView(); refreshSummary();
  };
  days.forEach((day, index) => { const option = text('option', day); option.value = String(index); daySelect.append(option); });
  daySelect.value = '0'; daySelect.addEventListener('change', () => { activeDay = Number(daySelect.value); showView(); });
  root.querySelectorAll('[data-workspace-view]').forEach(button => button.addEventListener('click', () => { view = button.dataset.workspaceView; showView(); }));
  const insertProduct = product => {
    const matches = rows.filter(row => row.productId === product.id);
    if (matches.length === 1) {
      const inputs = [...tbody.querySelectorAll('tr')][rows.indexOf(matches[0])]?.querySelectorAll('input.production-week-workspace__quantity');
      if (view === 'day') inputs?.[activeDay]?.focus(); else inputs?.[0]?.focus();
      search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false'); return;
    }
    if (matches.length > 1) {
      results.replaceChildren(text('p', 'Este SKU tiene varias filas. Elige una o crea otra.', 'small p-2'));
      matches.forEach(row => {
        const button = text('button', `${row.sku} · ${row.details.orderReference1 || 'Inventario'}`, 'list-group-item list-group-item-action');
        button.type = 'button'; button.addEventListener('click', () => {
          const index = rows.indexOf(row); [...tbody.children][index]?.querySelectorAll('input.production-week-workspace__quantity')[activeDay]?.focus();
          search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
        }); results.append(button);
      });
      const create = text('button', 'Crear otra fila', 'list-group-item list-group-item-action'); create.type = 'button';
      create.addEventListener('click', () => { rows.push(fresh(product)); invalidateReview(); render(); search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false'); tbody.lastElementChild?.querySelector('input')?.focus(); });
      results.append(create); return;
    }
    rows.push(fresh(product)); invalidateReview(); render(); search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    tbody.lastElementChild?.querySelectorAll('input.production-week-workspace__quantity')[view === 'day' ? activeDay : 0]?.focus();
  };
  const searchProducts = async () => {
    const term = search.value.trim(); results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    if (term.length < 2) return;
    const generation = ++searchGeneration;
    const url = new URL(root.dataset.searchUrl, location.origin); url.searchParams.set('q', term);
    try {
      const response = await fetch(url); if (!response.ok) throw new Error();
      const products = await response.json(); if (generation !== searchGeneration) return;
      products.forEach(product => {
        const button = text('button', '', 'list-group-item list-group-item-action'); button.type = 'button';
        button.setAttribute('role', 'option');
        button.append(text('strong', product.sku));
        button.title = product.sku;
        button.setAttribute('aria-label', product.sku);
        button.addEventListener('click', () => insertProduct(product)); results.append(button);
      });
      if (!products.length) results.append(text('div', 'No se encontró el SKU.', 'list-group-item'));
      search.setAttribute('aria-expanded', 'true');
    } catch { note('No se pudo buscar el producto. Reintenta.', 'warning'); }
  };
  search.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(searchProducts, 180); });
  search.addEventListener('keydown', async event => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    clearTimeout(searchTimer); searchGeneration++;
    const code = search.value.trim(); if (!code) return;
    try {
      const url = new URL(root.dataset.resolveUrl, location.origin); url.searchParams.set('code', code);
      const response = await fetch(url); if (!response.ok) throw new Error();
      const product = await response.json();
      if (!product?.id) throw new Error();
      const detailUrl = new URL(root.dataset.pasteUrl, location.origin); detailUrl.searchParams.set('skus', product.sku);
      const details = await fetch(detailUrl).then(x => x.json());
      if (!details[0]) throw new Error();
      insertProduct(details[0]);
    } catch { note('No se pudo resolver el código escaneado. Selecciona el SKU de la lista.', 'warning'); }
  });
  const review = async () => {
    if (submitting || uncertainSave) return;
    if (openingEditor) await openingEditor.ready;
    if (needsReconcile) { note('Concilia la preparación recuperada con la semana actual antes de revisar.', 'warning'); return; }
    const { changes, errors } = quantityChanges();
    const openings = openingChanges();
    errors.push(...(openingEditor?.errors() || []));
    if (!changes.length && !openings.length) { note('Agrega o modifica una cantidad antes de revisar.', 'warning'); return; }
    if (errors.length) { if (openingEditor?.errors().length) openingEditor.reveal(); note(errors.join(' '), 'danger'); return; }
    const list = $('[data-workspace-review-list]'); list.replaceChildren();
    openings.forEach(change => list.append(text('div', openingEditor.describe(change), 'border-bottom py-2')));
    changes.sort((a, b) => a.day - b.day).forEach(change => {
      list.append(text('div', `${days[change.day]} · ${change.sku}: ${change.before} → ${change.after} ${change.unit} (${change.kind === 'add' ? 'nuevo' : change.kind === 'edit' ? 'cambio' : 'quitar'})`, 'border-bottom py-2'));
    });
    const payload = { operationId, weekId, expectedWeekVersion: weekVersion, openings,
      changes: changes.map(({ kind, lineId, expectedLineVersion, line }) => ({ kind, lineId, expectedLineVersion, line })) };
    lastPayload = JSON.stringify(payload); $('[data-workspace-payload]').value = lastPayload;
    persist();
    reviewPanel.hidden = false; reviewPanel.scrollIntoView({ block: 'nearest' });
  };
  $('[data-workspace-review]').addEventListener('click', review);
  $('[data-workspace-back]').addEventListener('click', () => { reviewPanel.hidden = true; });
  form.addEventListener('submit', async event => {
    event.preventDefault(); if (submitting || !lastPayload) return;
    submitting = true; uncertainSave = true; freezePreparation(); $('[data-workspace-save]').disabled = true;
    persist();
    try {
      const response = await fetch(form.action, { method: 'POST', body: new FormData(form) });
      const result = await response.json();
      if (result.saved) {
        completedSave = true;
        clearConfirmed(result.count);
        sessionStorage.setItem(`${storageKey}:saved`, String(result.count));
        location.reload(); return;
      }
      uncertainSave = false;
      if (response.status === 409) {
        needsReconcile = true;
        note(result.errors?.join(' ') || 'La semana cambió. Los datos propuestos siguen aquí.', 'warning');
        const current = new Map((result.current?.lines || []).map(line => [line.id, line]));
        const comparison = text('div', '', 'border rounded p-2 mt-2');
        JSON.parse(lastPayload).changes.forEach(change => {
          const now = change.lineId ? current.get(change.lineId) : null;
          if (change.lineId)
            comparison.append(text('p', `${now?.sku || 'Renglón'}: actual ${now?.quantity ?? 'quitado'} → propuesto ${change.kind === 'remove' ? 'quitar' : change.line.quantity}`, 'mb-1'));
        });
        const refresh = text('button', 'Actualizar y conciliar', 'btn btn-outline-primary'); refresh.type = 'button';
        refresh.addEventListener('click', () => { submitting = true; location.reload(); });
        comparison.append(refresh); message.append(comparison);
        reviewPanel.hidden = true;
      } else note(result.errors?.join(' ') || 'No se guardó el grupo.', 'danger');
    } catch {
      const url = new URL(root.dataset.operationUrl, location.origin);
      url.searchParams.set('weekId', weekId); url.searchParams.set('operationId', operationId);
      try {
        const status = await fetch(url).then(x => x.json());
        if (status.saved) { completedSave = true; const payload = JSON.parse(lastPayload); clearConfirmed(payload.changes.length + (payload.openings?.length || 0)); location.reload(); return; }
      } catch { /* El borrador local permanece disponible. */ }
      note('No se recibió la confirmación. Comprueba la conexión y reintenta con esta misma revisión.', 'warning');
    } finally { submitting = false; freezePreparation(); $('[data-workspace-save]').disabled = false; if (!completedSave) persist(); }
  });
  const previewArea = $('[data-workspace-paste-preview]');
  let previewSource = null;
  let openingPreviewSourceId = null, openingPreviewSourceVersion = null, openingColumnsPrepared = false;
  const parsePaste = raw => window.ProductionWeekPaste.parse(raw, root.dataset.weekStart);
  let copyPicker = null;
  const closeCopyPicker = () => {
    if (!copyPicker) return;
    copyPicker.list.remove();
    copyPicker.input.setAttribute('aria-expanded', 'false');
    copyPicker = null;
  };
  const previewDays = item => new Set([
    ...(item.suggestedDays || []),
    ...item.days.map((value, day) => String(value || '').trim() ? day : -1).filter(day => day >= 0)
  ]);
  const mergeCopyPreview = () => {
    const compatibleDetails = details => JSON.stringify(['orderReference1', 'orderReference2',
      'orderReference3', 'notes'].map(key => details?.[key] || ''));
    for (let index = 0; index < preview.length; index++) {
      const item = preview[index];
      if (!item.productId) continue;
      for (let otherIndex = index + 1; otherIndex < preview.length;) {
        const other = preview[otherIndex];
        const occupied = previewDays(item);
        if (other.productId !== item.productId ||
            (other.sourceProductId !== item.sourceProductId && openingPreview.some(group =>
              group.productId === other.sourceProductId || group.productId === item.sourceProductId)) ||
            item.selected !== other.selected ||
            compatibleDetails(item.details) !== compatibleDetails(other.details) ||
            [...previewDays(other)].some(day => occupied.has(day))) { otherIndex++; continue; }
        other.days.forEach((value, day) => { if (value) item.days[day] = value; });
        item.suggestedDays = [...new Set([...(item.suggestedDays || []), ...(other.suggestedDays || [])])].sort((a, b) => a - b);
        preview.splice(otherIndex, 1);
      }
    }
    preview.forEach((item, index) => { item.sourceRow = index + 1; });
  };
  const selectCopyProduct = (item, product) => {
    if (!product?.id || !product.sku) return;
    item.productId = product.id;
    item.sku = product.sku;
    item.unit = product.unit || '';
    item.allowsDecimals = product.allowsDecimals !== false;
    item.error = '';
    previewDuplicatesAcknowledged = false;
    mergeCopyPreview();
    renderPreview(item);
    persist();
  };
  const copySkuPicker = item => {
    const input = document.createElement('input');
    input.type = 'search'; input.className = 'form-control form-control-sm production-week-workspace__copy-sku';
    input.value = item.sku || ''; input.autocomplete = 'off';
    input.setAttribute('aria-label', `SKU, fila ${item.sourceRow}`);
    input.setAttribute('role', 'combobox'); input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-expanded', 'false');
    const open = () => {
      closeCopyPicker();
      const list = document.createElement('div');
      list.className = 'production-week-workspace__copy-options list-group';
      list.setAttribute('role', 'listbox');
      document.body.append(list);
      copyPicker = { input, list, generation: 0, products: [], active: -1 };
      const rect = input.getBoundingClientRect();
      const viewportHeight = window.innerHeight || 800;
      const above = viewportHeight - rect.bottom < 160 && rect.top > 160;
      const height = Math.min(280, Math.max(80, above ? rect.top - 12 : viewportHeight - rect.bottom - 12));
      list.style.left = `${Math.max(8, rect.left)}px`;
      list.style.top = `${above ? rect.top - height - 2 : rect.bottom + 2}px`;
      list.style.width = `${Math.max(rect.width, 220)}px`;
      list.style.maxHeight = `${height}px`;
      input.setAttribute('aria-expanded', 'true');
      return copyPicker;
    };
    const show = (picker, products) => {
      if (copyPicker !== picker) return;
      picker.products = products; picker.active = -1; picker.list.replaceChildren();
      products.forEach((product, index) => {
        const option = text('button', product.sku, 'list-group-item list-group-item-action');
        option.type = 'button'; option.setAttribute('role', 'option'); option.setAttribute('aria-label', product.sku);
        option.addEventListener('pointerdown', event => event.preventDefault());
        option.addEventListener('click', () => selectCopyProduct(item, product));
        picker.list.append(option);
      });
      if (!products.length) picker.list.append(text('div', 'No se encontró el SKU.', 'list-group-item'));
    };
    const lookup = async () => {
      const term = input.value.trim();
      const picker = copyPicker?.input === input ? copyPicker : open();
      const generation = ++picker.generation;
      if (term.length < 2) { show(picker, []); return; }
      const url = new URL(root.dataset.searchUrl, location.origin); url.searchParams.set('q', term);
      try {
        const response = await fetch(url); if (!response.ok) throw new Error();
        const products = await response.json();
        if (picker.generation === generation && input.value.trim() === term) show(picker, products);
      } catch { if (copyPicker === picker) show(picker, []); note('No se pudo buscar el producto. Reintenta.', 'warning'); }
    };
    let timer;
    input.addEventListener('input', () => {
      item.sku = input.value; item.productId = null; item.unit = ''; item.allowsDecimals = null;
      item.error = ''; previewDuplicatesAcknowledged = false; persist();
      clearTimeout(timer); timer = setTimeout(lookup, 180);
    });
    input.addEventListener('blur', () => {
      setTimeout(() => { if (copyPicker?.input === input) closeCopyPicker(); }, 150);
    });
    input.addEventListener('keydown', async event => {
      const picker = copyPicker?.input === input ? copyPicker : null;
      if (event.key === 'Escape') { closeCopyPicker(); return; }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        if (!picker?.products.length) return;
        picker.active = (picker.active + (event.key === 'ArrowDown' ? 1 : -1) + picker.products.length) % picker.products.length;
        [...picker.list.children].forEach((option, index) => option.classList.toggle('active', index === picker.active));
        return;
      }
      if (event.key !== 'Enter') return;
      event.preventDefault(); clearTimeout(timer);
      if (picker?.products[picker.active]) { selectCopyProduct(item, picker.products[picker.active]); return; }
      const code = input.value.trim(); if (!code) return;
      try {
        const url = new URL(root.dataset.resolveUrl, location.origin); url.searchParams.set('code', code);
        const response = await fetch(url); if (!response.ok) throw new Error();
        const match = await response.json(); if (!match?.sku) throw new Error();
        const products = await resolveSkus([{ sku: match.sku }]);
        const product = products.find(candidate => candidate.id === match.id);
        if (!product || input.value.trim() !== code) throw new Error();
        selectCopyProduct(item, product);
      } catch { note('Selecciona un SKU activo del catálogo.', 'warning'); }
    });
    return input;
  };
  const renderPreview = focusItem => {
    closeCopyPicker();
    previewArea.replaceChildren();
    const showOpenings = openingColumnsPrepared && $('[data-workspace-copy-openings]').checked;
    const attached = new Map();
    if (showOpenings) openingPreview.forEach(group => {
      const index = preview.findIndex(item => item.sourceProductId === group.productId && item.productId === group.productId);
      if (index >= 0) attached.set(index, group);
    });
    const carryOnly = showOpenings ? openingPreview.filter(group => ![...attached.values()].includes(group)) : [];
    previewArea.hidden = preview.length === 0 && carryOnly.length === 0;
    if (previewArea.hidden) return;
    previewArea.append(text('h4', previewSource || showOpenings ? 'Revisar copia de semana' : 'Revisar productos pegados', 'h6'));
    previewArea.append(text('p', `${preview.length} filas de producto y ${showOpenings ? openingPreview.length : 0} SKU con arrastre. Selecciona cada producto y arrastre por separado; abre el total de un área para ajustar sus orígenes.`, 'small text-body-secondary'));
    const wrapper = document.createElement('div'); wrapper.className = 'table-responsive';
    const table = document.createElement('table'); table.className = 'table table-sm align-middle production-week-workspace__copy-table';
    const head = document.createElement('thead'); const header = document.createElement('tr');
    ['Incluir', 'Fila', 'SKU', ...days, ...(showOpenings ? ['Corte', 'Costura', 'Ready to Pack'] : []), 'Pedido 1', 'Pedido 2', 'Pedido 3', 'Notas', 'Validación'].forEach(label => header.append(text('th', label)));
    head.append(header); table.append(head); const body = document.createElement('tbody');
    const openingCell = (tr, group, area) => {
      const label = ['Corte', 'Costura', 'Ready to Pack'][area];
      const td = document.createElement('td'); td.className = 'production-week-workspace__carry-cell'; td.dataset.label = label;
      const entries = group?.entries.filter(entry => entry.area === area) || [];
      if (entries.length) {
        const details = document.createElement('details');
        const summary = document.createElement('summary');
        const updateTotal = () => {
          let total = 0n, invalid = false;
          entries.forEach(entry => { const value = scaled(entry.protected ? entry.prepared : entry.quantity); if (value === null) invalid = true; else total += value; });
          summary.textContent = `${label}: ${invalid ? 'Revisar cantidad' : formatScaled(total)} ${entries[0].unit}`;
        };
        updateTotal(); details.append(summary);
        entries.forEach(entry => {
          const field = document.createElement('label'); field.className = 'production-week-workspace__carry-origin';
          field.append(text('span', `${entry.sourceStart} · renglón ${entry.sequence}`, 'small'));
          const input = document.createElement('input'); input.className = 'form-control form-control-sm production-opening-quantity';
          input.inputMode = 'decimal'; input.value = entry.protected ? String(entry.prepared) : entry.quantity;
          input.disabled = entry.protected || entry.available <= 0;
          input.setAttribute('aria-label', `${group.sku} · ${label} · ${entry.sourceStart} · renglón ${entry.sequence}`);
          input.addEventListener('input', () => { entry.quantity = input.value; updateTotal(); persist(); });
          field.append(input, text('small', entry.protected ? `Ya preparado o en edición: ${entry.prepared} ${entry.unit}` : `Disponible: ${entry.available} ${entry.unit}`, 'text-body-secondary'));
          if (entry.needsReview) field.append(text('small', 'El origen cambió; revisa la selección en Arrastre inicial.', 'text-warning-emphasis'));
          details.append(field);
        });
        td.append(details);
      } else td.append(text('span', '—', 'text-body-secondary'));
      tr.append(td);
    };
    const carrySelector = (cell, group) => {
      if (!group) return;
      const label = document.createElement('label'); label.className = 'production-week-workspace__carry-select';
      const check = document.createElement('input'); check.type = 'checkbox'; check.checked = group.selected;
      check.disabled = !group.entries.some(entry => entry.available > 0 && !entry.protected);
      check.setAttribute('aria-label', `Traer arrastre de ${group.sku}`);
      check.addEventListener('change', () => { group.selected = check.checked; persist(); });
      label.append(check, text('span', 'Traer arrastre', 'small')); cell.append(label);
    };
    preview.forEach((item, index) => {
      const tr = document.createElement('tr');
      const selected = document.createElement('input'); selected.type = 'checkbox'; selected.checked = item.selected;
      selected.setAttribute('aria-label', `Incluir fila ${item.sourceRow || index + 1}`);
      selected.addEventListener('change', () => { item.selected = selected.checked; persist(); });
      const selectorCell = document.createElement('td'); selectorCell.append(selected); tr.append(selectorCell);
      tr.append(text('td', String(item.sourceRow || index + 1)));
      const editable = (parent, value, onChange, label, width = '110px', placeholder = '') => {
        const td = document.createElement('td'); const input = document.createElement('input');
        input.className = 'form-control form-control-sm'; input.value = value ?? '';
        if (placeholder) input.placeholder = placeholder;
        input.style.minWidth = width; input.setAttribute('aria-label', `${label}, fila ${item.sourceRow || index + 1}`);
        input.addEventListener('input', () => { onChange(input.value); item.error = ''; previewDuplicatesAcknowledged = false; persist(); });
        td.append(input); parent.append(td);
      };
      if (previewSource) { const skuCell = document.createElement('td'); skuCell.append(copySkuPicker(item)); if (showOpenings) carrySelector(skuCell, attached.get(index)); tr.append(skuCell); }
      else editable(tr, item.sku, value => { item.sku = value; }, 'SKU', '180px');
      item.days.forEach((value, day) => editable(tr, value, changed => { item.days[day] = changed; }, days[day], '82px',
        previewSource && item.suggestedDays?.includes(day) && !value ? 'Programar' : ''));
      if (showOpenings) for (let area = 0; area < 3; area++) openingCell(tr, attached.get(index), area);
      [['orderReference1', 'Pedido 1'], ['orderReference2', 'Pedido 2'], ['orderReference3', 'Pedido 3'], ['notes', 'Notas']]
        .forEach(([key, label]) => editable(tr, item.details[key], value => { item.details[key] = value; }, label));
      const status = text('td', item.error || (previewSource && !item.productId ? 'Selecciona el SKU del catálogo' :
        item.duplicate ? 'Repetido · quedará separado' : 'Pendiente de validar'));
      status.dataset.previewStatus = index; tr.append(status);
      body.append(tr);
    });
    carryOnly.forEach(group => {
      const tr = document.createElement('tr'); tr.className = 'production-week-workspace__carry-only';
      tr.append(text('td', '—'), text('td', '—'));
      const sku = text('td', group.sku); sku.append(text('span', 'Solo arrastre', 'badge text-bg-secondary ms-2')); carrySelector(sku, group); tr.append(sku);
      days.forEach(() => tr.append(text('td', '—', 'text-body-secondary')));
      for (let area = 0; area < 3; area++) openingCell(tr, group, area);
      for (let index = 0; index < 4; index++) tr.append(text('td', '—', 'text-body-secondary'));
      tr.append(text('td', 'Pendiente de validar')); body.append(tr);
    });
    table.append(body); wrapper.append(table); previewArea.append(wrapper);
    wrapper.addEventListener('scroll', closeCopyPicker);
    const incorporate = text('button', 'Validar e incorporar seleccionados', 'btn btn-primary'); incorporate.type = 'button';
    incorporate.addEventListener('click', incorporatePreview); previewArea.append(incorporate);
    if (focusItem) {
      let index = preview.indexOf(focusItem);
      if (index < 0) index = preview.findIndex(candidate => candidate.productId === focusItem.productId &&
        [...previewDays(focusItem)].every(day => previewDays(candidate).has(day)));
      const day = Math.min(...previewDays(focusItem));
      body.children[index]?.children[3 + (Number.isFinite(day) ? day : 0)]?.children[0]?.focus();
    }
  };
  const resolveSkus = async items => {
    const skus = [...new Set(items.map(item => item.sku.trim()))]; const found = [];
    for (let index = 0; index < skus.length; index += 30) {
      const url = new URL(root.dataset.pasteUrl, location.origin);
      url.searchParams.set('skus', skus.slice(index, index + 30).join('\n'));
      const response = await fetch(url); if (!response.ok) throw new Error('No se pudieron validar los SKU.');
      found.push(...await response.json());
    }
    return found;
  };
  const incorporatePreview = async () => {
    const selected = preview.filter(item => item.selected);
    const selectedOpenings = openingColumnsPrepared && $('[data-workspace-copy-openings]').checked
      ? openingPreview.filter(group => group.selected) : [];
    if (!selected.length && !selectedOpenings.length) { note('Selecciona al menos un producto o un SKU con arrastre.', 'warning'); return; }
    let products = [];
    if (selected.length) try { products = await resolveSkus(selected); }
    catch { note('No se pudieron validar los SKU. Reintenta.', 'warning'); return; }
    const normalized = new Map(products.map(product => [product.sku.toUpperCase(), product]));
    let additions = 0, errors = 0;
    selected.forEach(item => {
      const product = normalized.get(item.sku.trim().toUpperCase());
      item.error = !product ? `Fila ${item.sourceRow}: SKU no activo o desconocido.` : '';
      if (previewSource && (!item.productId || product?.id !== item.productId))
        item.error = `Fila ${item.sourceRow}: selecciona un SKU activo del catálogo.`;
      if (!previewSource && item.days.every(value => !positive(value))) item.error ||= `Fila ${item.sourceRow}: indica una cantidad positiva.`;
      item.days.forEach((value, day) => {
        if (!value) return;
        if (!validQuantity(value, product?.allowsDecimals !== false)) item.error = `Fila ${item.sourceRow}, ${days[day]}: cantidad no válida.`;
        if (positive(value)) additions++;
      });
      ['orderReference1', 'orderReference2', 'orderReference3'].forEach(key => {
        if (item.details[key]?.length > 120) item.error = `Fila ${item.sourceRow}: pedido supera 120 caracteres.`;
      });
      if (item.details.notes?.length > 500) item.error = `Fila ${item.sourceRow}: notas superan 500 caracteres.`;
      if (item.error) errors++;
    });
    if (previewSource || selectedOpenings.length) {
      try {
        const url = new URL(root.dataset.copyUrl, location.origin);
        url.searchParams.set('weekId', weekId); url.searchParams.set('sourceWeekId', previewSource?.id || openingPreviewSourceId);
        const current = await fetch(url).then(response => response.ok ? response.json() : Promise.reject());
        if (current.version !== (previewSource?.version ?? openingPreviewSourceVersion)) { note('La semana origen cambió. Prepara de nuevo la copia.', 'warning'); return; }
      } catch { note('No se pudo revalidar la semana origen.', 'warning'); return; }
    }
    const openingEntries = [];
    for (const group of selectedOpenings) for (const entry of group.entries.filter(row => !row.protected)) {
      const quantity = normalizeQuantity(entry.quantity);
      if (!validQuantity(quantity, entry.allowsDecimals) || scaled(quantity) > scaled(String(entry.available))) {
        note(`${group.sku} · ${['Corte', 'Costura', 'Ready to Pack'][entry.area]}: usa una cantidad entre 0 y ${entry.available} ${entry.unit}.`, 'danger');
        return;
      }
      if (scaled(quantity) > 0n) openingEntries.push(entry);
    }
    if (!selected.length && !openingEntries.length) { note('Los SKU elegidos ya tienen su arrastre preparado o quedaron en cero.', 'info'); return; }
    const planned = previewSource ? selected.reduce((count, item) => count + new Set([
      ...(item.suggestedDays || []), ...item.days.map((value, day) => positive(value) ? day : -1).filter(day => day >= 0)
    ]).size, 0) : additions;
    if (planned + quantityChanges().changes.length + openingChanges().length + openingEntries.length > 100) {
      note('La preparación supera el máximo de 100 cambios. Selecciona menos productos o arrastres.', 'warning');
      return;
    }
    if (errors) { renderPreview(); note(`${errors} filas requieren corrección.`, 'danger'); return; }
    selected.forEach(item => {
      item.duplicate = item.days.some((value, day) => positive(value) &&
        (rows.some(row => row.sku.toUpperCase() === item.sku.toUpperCase() && positive(row.cells[day].value)) ||
         selected.some(other => other !== item && other.sku.toUpperCase() === item.sku.toUpperCase() && positive(other.days[day]))));
    });
    if (selected.some(item => item.duplicate) && !previewDuplicatesAcknowledged) {
      previewDuplicatesAcknowledged = true; renderPreview();
      note('Hay SKU repetidos en el mismo día. Se conservarán como renglones separados. Pulsa otra vez para incorporarlos.', 'warning');
      return;
    }
    const importedRows = [];
    selected.forEach(item => {
      const product = normalized.get(item.sku.trim().toUpperCase());
      const usedDays = item.days.map((value, day) => positive(value) || item.suggestedDays?.includes(day) ? day : -1).filter(day => day >= 0);
      let row = importedRows.find(candidate => candidate.productId === product.id &&
        detailsKey(candidate.details) === detailsKey(fresh(product, item.details).details) &&
        usedDays.every(day => !candidate.suggestedDays.includes(day) && !positive(candidate.cells[day].value)));
      if (!row) { row = fresh(product, item.details); importedRows.push(row); }
      row.suggestedDays.push(...usedDays);
      item.days.forEach((value, day) => { if (value) row.cells[day].value = value; });
    });
    if (openingEntries.length) try { await openingEditor.prepare(openingEntries); }
    catch (error) { note(error.message || 'No se pudo preparar el arrastre. Reintenta.', 'warning'); openingEditor.reveal(); return; }
    rows.push(...importedRows);
    preview = preview.filter(item => !item.selected);
    openingPreview = openingPreview.filter(group => !selectedOpenings.includes(group));
    if (!preview.length) { previewSource = null; $('[data-workspace-paste]').value = ''; }
    previewDuplicatesAcknowledged = false;
    renderPreview(); invalidateReview(); render();
    note(`${selected.length} filas de producto y ${openingEntries.length} orígenes de arrastre incorporados. Revisa los cambios antes de guardar.`, 'success');
  };
  $('[data-workspace-preview-paste]').addEventListener('click', () => {
    try { preview = parsePaste($('[data-workspace-paste]').value); previewSource = null; previewDuplicatesAcknowledged = false; renderPreview(); persist(); }
    catch (error) { note(error.message, 'danger'); }
  });
  $('[data-workspace-copy-openings]').addEventListener('change', () => { renderPreview(); persist(); });
  $('[data-workspace-copy]').addEventListener('click', async () => {
    const sourceId = $('[data-workspace-source]').value;
    if (!sourceId) { note('No hay una semana anterior para copiar.', 'warning'); return; }
    const copyProducts = $('[data-workspace-copy-products]').checked;
    const copyOpenings = $('[data-workspace-copy-openings]').checked;
    if (!copyProducts && !copyOpenings) { note('Elige copiar productos, traer arrastre o ambas opciones.', 'warning'); return; }
    if (copyOpenings && !openingEditor) { note('Esta semana no admite arrastre inicial.', 'warning'); return; }
    try {
      const url = new URL(root.dataset.copyUrl, location.origin);
      url.searchParams.set('weekId', weekId); url.searchParams.set('sourceWeekId', sourceId);
      const response = await fetch(url); if (!response.ok) throw new Error();
      const source = await response.json();
      if (copyOpenings) await openingEditor.ready;
      const availableOpenings = copyOpenings ? openingEditor.options().filter(row => row.sourceWeekId === sourceId && (row.available > 0 || row.prepared > 0)) : [];
      const previousOpenings = openingPreviewSourceId === sourceId ? new Map(openingPreview.map(group => [group.productId, group])) : new Map();
      if (copyOpenings || openingPreviewSourceId !== sourceId) openingPreview = [];
      const groups = new Map();
      availableOpenings.forEach(row => {
        if (!groups.has(row.productId)) groups.set(row.productId, { productId: row.productId, sku: row.sku, selected: false, entries: [] });
        groups.get(row.productId).entries.push({ key: row.key, fingerprint: row.fingerprint,
          area: row.area, sourceStart: row.sourceStart, sequence: row.sequence, available: row.available,
          prepared: row.prepared, protected: row.protected, needsReview: row.needsReview,
          unit: row.unit, allowsDecimals: row.allowsDecimals,
          quantity: previousOpenings.get(row.productId)?.entries.find(entry => entry.key === row.key)?.quantity ?? String(row.available) });
      });
      if (copyOpenings) {
        openingPreview = [...groups.values()];
        openingPreview.forEach(group => { group.selected = !!previousOpenings.get(group.productId)?.selected; });
        openingPreviewSourceId = sourceId;
        openingPreviewSourceVersion = source.version;
      } else if (openingPreviewSourceId !== sourceId) {
        openingPreviewSourceId = null;
        openingPreviewSourceVersion = null;
      }
      openingColumnsPrepared = copyOpenings || openingColumnsPrepared && openingPreviewSourceId === sourceId;
      previewSource = copyProducts ? { id: source.id, version: source.version } : null;
      previewDuplicatesAcknowledged = false;
      preview = [];
      if (copyProducts) source.rows.forEach(line => {
        // A cell represents one daily line. Never combine two lines of the same day.
        let item = preview.find(candidate => candidate.productId === line.productId && !candidate.suggestedDays.includes(line.day));
        if (!item) {
          item = { selected: true, sourceRow: preview.length + 1, productId: line.productId, sourceProductId: line.productId,
            sku: line.sku, unit: line.unit, allowsDecimals: line.allowsDecimals,
            days: days.map(() => ''), suggestedDays: [], details: {} };
          preview.push(item);
        }
        item.suggestedDays.push(line.day);
        item.suggestedDays.sort((a, b) => a - b);
        item.days[line.day] = $('[data-workspace-copy-quantities]').checked ? String(line.quantity) : '';
      });
      renderPreview();
      persist();
      const destination = previewArea.hidden ? null : previewArea;
      if (destination) { destination.scrollIntoView({ block: 'nearest' }); destination.focus(); }
      if (preview.length || openingPreview.length)
        note(`${preview.length} renglones de producto y ${openingPreview.length} SKU con arrastre disponibles. Selecciona lo que incorporarás.`, 'info');
      else note(copyOpenings && !copyProducts
        ? 'La semana elegida no tiene pendientes disponibles para arrastrar.'
        : 'La semana elegida no tiene productos activos ni pendientes disponibles para estas opciones.', 'warning');
    } catch { note('No se pudo preparar la copia. Reintenta.', 'warning'); }
  });
  const success = sessionStorage.getItem(`${storageKey}:saved`);
  if (success) { note(`${success} cambios guardados en el programa semanal.`, 'success'); sessionStorage.removeItem(`${storageKey}:saved`); }
  const recovery = $('[data-workspace-recovery]');
  try {
    const prior = JSON.parse(localStorage.getItem(storageKey) || 'null');
    if (prior && (prior.rows?.length || prior.preview?.length || prior.openingPreview?.length || prior.pasteText || prior.openings && Object.keys(prior.openings).length)) {
      recovery.hidden = false;
      recovery.append(text('p', `Hay una preparación de este dispositivo del ${new Date(prior.updated).toLocaleString()}. ${prior.version === weekVersion ? '' : 'La semana cambió; compara antes de guardar.'}`));
      const restore = text('button', 'Recuperar', 'btn btn-primary me-2'); restore.type = 'button';
      restore.addEventListener('click', () => {
        rows = prior.rows || []; preview = prior.preview || []; previewSource = prior.previewSource || null;
        openingPreview = prior.openingPreview || [];
        openingPreviewSourceId = prior.openingPreviewSourceId || prior.previewSource?.id || $('[data-workspace-source]').value;
        openingPreviewSourceVersion = prior.openingPreviewSourceVersion ?? prior.previewSource?.version ?? null;
        openingColumnsPrepared = prior.openingColumnsPrepared ?? openingPreview.length > 0;
        if (openingColumnsPrepared) $('[data-workspace-copy-openings]').checked = true;
        preview.forEach(item => { if (prior.previewSource && !item.sourceProductId) item.sourceProductId = item.productId; });
        openingEditor?.restore(prior.openings);
        $('[data-workspace-paste]').value = prior.pasteText || '';
        operationId = prior.operationId || uuid();
        lastPayload = ''; recovery.hidden = true; recovery.replaceChildren(); render();
        renderPreview();
        if (prior.pendingSent && prior.pendingPayload) {
          lastPayload = prior.pendingPayload; uncertainSave = true;
          $('[data-workspace-payload]').value = lastPayload; reviewPanel.hidden = false; freezePreparation();
          note('Reintenta la misma operación pendiente antes de preparar otra confirmación.', 'warning');
          return;
        }
        if (prior.version !== weekVersion) {
          needsReconcile = true; recovery.hidden = false;
          recovery.append(text('p', 'La semana cambió. Compara los valores actuales y los propuestos antes de continuar.'));
          const current = new Map(saved.map(line => [line.Id, line]));
          rows.forEach(row => row.cells.forEach((cell, day) => {
            if (!cell.lineId) return;
            const now = current.get(cell.lineId);
            if (!now || now.Version !== cell.version)
              recovery.append(text('p', `${row.sku} · ${days[day]}: actual ${now?.Quantity ?? 'quitado'} → propuesto ${cell.removed ? 'quitar' : cell.value}`));
          }));
          const accept = text('button', 'Usar la semana actual como base y revisar', 'btn btn-outline-primary'); accept.type = 'button';
          accept.addEventListener('click', () => {
            rows.forEach(row => row.cells.forEach(cell => {
              if (!cell.lineId) return;
              const now = current.get(cell.lineId);
              if (now) { cell.original = String(now.Quantity); cell.version = now.Version; }
              else { cell.lineId = null; cell.version = null; cell.original = ''; cell.removed = false; }
            }));
            needsReconcile = false; recovery.hidden = true; recovery.replaceChildren(); invalidateReview(); render();
            note('Preparación conciliada. Revisa los cambios antes de guardar.', 'info');
          });
          recovery.append(accept);
        }
      });
      const discard = text('button', 'Descartar', 'btn btn-outline-secondary'); discard.type = 'button';
      discard.addEventListener('click', () => { localStorage.removeItem(storageKey); recovery.hidden = true; recovery.replaceChildren(); });
      recovery.append(restore, discard);
      if (prior.pendingPayload && prior.operationId) {
        restore.disabled = true;
        if (prior.pendingSent) discard.disabled = true;
        const url = new URL(root.dataset.operationUrl, location.origin);
        url.searchParams.set('weekId', weekId); url.searchParams.set('operationId', prior.operationId);
        fetch(url).then(response => response.json()).then(status => {
          if (status.saved) {
            const payload = JSON.parse(prior.pendingPayload); clearConfirmed(payload.changes.length + (payload.openings?.length || 0), prior);
            location.reload();
          }
          else restore.disabled = false;
        }).catch(() => { restore.disabled = false; note('No se pudo comprobar el último guardado. Reintenta cuando vuelva la conexión.', 'warning'); });
      }
    }
  } catch { note('No se pudo leer la preparación local.', 'warning'); }
  window.addEventListener('beforeunload', event => {
    if (completedSave || submitting || (!uncertainSave && !quantityChanges().changes.length && !openingChanges().length && !preview.length && !openingPreview.length && !$('[data-workspace-paste]').value.trim())) return;
    event.preventDefault(); event.returnValue = '';
  });
  render();
  openingEditor?.ready.then(refreshSummary);
})();
