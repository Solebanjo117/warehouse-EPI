(() => {
  const root = document.querySelector('[data-week-workspace]');
  if (!root) return;
  if (root.dataset.readonly === 'true') { window.ProductionScheduleProgress.mountReadOnly(root); return; }
  const uuid = () => {
    if (crypto.randomUUID) return crypto.randomUUID();
    const bytes = new Uint8Array(16); crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  };
  const $ = selector => root.querySelector(selector);
  const days = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'];
  const isNew = root.dataset.newWeek === 'true';
  const weekId = root.dataset.weekId;
  const weekVersion = Number(root.dataset.weekVersion);
  const storageKey = `warehouse-epi:schedule:${root.dataset.userId}:${isNew ? 'new' : weekId}`;
  const tbody = $('[data-workspace-rows]');
  const message = $('[data-workspace-message]');
  const search = $('[data-workspace-search]');
  const results = $('[data-workspace-results]');
  const form = $('[data-workspace-form]');
  const reviewPanel = $('[data-workspace-review-panel]');
  const openingEditor = window.ProductionWeekOpenings?.get($('[data-opening-editor]'));
  if (!openingEditor) {
    $('[data-workspace-copy-openings]').disabled = true;
    const status = $('[data-workspace-copy-status]');
    if (status && !$('[data-workspace-carry-unavailable]')) status.textContent = window.warehouseText?.(
      'No se pudo cargar el editor de arrastre. Actualiza la página antes de preparar la copia.') ||
      'No se pudo cargar el editor de arrastre. Actualiza la página antes de preparar la copia.';
  }
  let excludedProducts = new Set();
  const openingChanges = () => (openingEditor?.changes() || []).filter(change => !excludedProducts.has(change.productId));
  const saved = [...root.querySelectorAll('[data-line]')].map(node => JSON.parse(node.dataset.line));
  let rows = [], openingPreview = [];
  let preview = [], submitting = false, operationId = uuid();
  let lastPayload = '', needsReconcile = false, searchTimer, searchGeneration = 0;
  let reviewGeneration = 0;
  let uncertainSave = false, completedSave = false;
  let discardingAll = false, preparingCopy = false;
  let copyPanelOpen = false, continueToOpen = null, recoveryPending = false;
  let skuDayTargets = {};
  // Empty carryover editors are presentation only until the operator edits them.
  const carryEditors = new Map();
  const rowFields = new Map();
  const copyToggle = $('[data-workspace-copy-toggle]'), copyPanel = $('[data-workspace-copy-panel]');
  const setCopyPanel = (open, focus = false) => {
    copyPanelOpen = open;
    copyPanel.hidden = !open;
    copyToggle.setAttribute('aria-expanded', String(open));
    if (focus) (open ? $('[data-workspace-source]') : copyToggle)?.focus();
  };
  const legacyStorageKey = `${storageKey}:legacy-paste`;
  let legacyText = '';
  const renderLegacyText = () => {
    $('[data-workspace-legacy-paste]').hidden = !legacyText.trim();
    $('[data-workspace-legacy-text]').textContent = legacyText;
  };
  // Archive only old, unprocessed text. It never participates in schedule validation or POSTs.
  const archiveLegacyText = prior => {
    const raw = prior?.pasteText || '';
    if (raw.trim() && !legacyText.includes(raw)) {
      const combined = [legacyText, raw].filter(Boolean).join('\n\n');
      localStorage.setItem(legacyStorageKey, combined);
      legacyText = combined;
    }
    renderLegacyText();
  };
  const hasCopyPreparation = prior => !!(prior?.previewSource || prior?.openingPreview?.length ||
    Object.keys(prior?.copySources || {}).length || Object.keys(prior?.copyOpeningKeys || {}).length ||
    prior?.rows?.some(row => row.preparedKind === 'copy' || row.cells?.some(cell => cell.copyOrigin)));
  const frozen = new Map();
  const freezePreparation = () => {
    if (submitting || uncertainSave || discardingAll) root.querySelectorAll('input:not([type="hidden"]), select, textarea, button').forEach(el => {
      if (el === $('[data-workspace-save]') && !discardingAll) return;
      if (el === $('[data-workspace-pin]') && uncertainSave && !submitting && !$('[data-workspace-pin-panel]').hidden) {
        el.disabled = false; frozen.delete(el); return;
      }
      if (!frozen.has(el)) frozen.set(el, el.disabled);
      el.disabled = true;
    });
    else { frozen.forEach((disabled, el) => { el.disabled = disabled; }); frozen.clear(); }
  };
  const text = (tag, value, cls = '') => {
    const node = document.createElement(tag); node.textContent = value; if (cls) node.className = cls; return node;
  };
  const say = (key, ...args) => window.warehouseText ? window.warehouseText(key, ...args) :
    key.replace(/\{(\d+)\}/g, (_, index) => args[Number(index)] ?? '');
  const dayLabel = day => window.ProductionScheduleProgress?.label(days[day]) || say(days[day]);
  const displayDate = value => {
    const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value || ''));
    return match ? `${match[3]}/${match[2]}/${match[1]}` : String(value || '');
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
    if (value < 0n) return `-${formatScaled(-value)}`;
    const integer = value / 10000n, fraction = String(value % 10000n).padStart(4, '0').replace(/0+$/, '');
    return `${integer}${fraction ? '.' + fraction : ''}`;
  };
  const addExisting = (line, recovering = false) => {
    const details = Object.fromEntries(['orderReference1', 'orderReference2', 'orderReference3', 'notes',
      'originalType', 'originalAnnotation1', 'originalAnnotation2', 'originalAnnotation1Kind',
      'originalAnnotation2Kind'].map(key => [key, line[key[0].toUpperCase() + key.slice(1)] || '']));
    const day = Number(line.Day);
    let row = rows.find(candidate => (!recovering || !candidate.preparedKind) && candidate.productId === line.ProductId &&
      detailsKey(candidate.details) === detailsKey(details) && !candidate.cells[day]?.lineId);
    if (!row) { row = fresh({ id: line.ProductId, sku: line.Sku, unit: line.Unit, allowsDecimals: line.AllowsDecimals }, details); rows.push(row); }
    row.originalDetails = structuredClone(details);
    row.cells[day] = { value: String(line.Quantity), original: String(line.Quantity),
      lineId: line.Id, version: line.Version, removed: false };
  };
  saved.forEach(line => addExisting(line));
  const referenceKeys = ['orderReference1', 'orderReference2', 'orderReference3'];
  const consolidateRows = () => {
    const groups = new Map();
    rows.forEach(row => {
      const key = row.productId || row.key;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(row);
    });
    rows = [...groups.values()].map(group => {
      const row = group[0];
      const references = [...new Set(group.flatMap(item => item.referenceCandidates || referenceKeys.map(key => item.details[key])).filter(Boolean))];
      if (group.length > 1 || !row.skuConsolidated) {
        const merged = { ...row.details, notes: row.metadataManual ? row.details.notes : [...new Set(group.map(item => item.details.notes).filter(Boolean))].join('\n') };
        if (!row.metadataManual) referenceKeys.forEach((key, index) => { merged[key] = references[index] || ''; });
        const metadataEdited = group.some(item => item.originalDetails && detailsKey(item.details) !== detailsKey(item.originalDetails));
        row.details = merged;
        row.referenceCandidates = references;
        row.referencesResolved = row.metadataManual && row.referencesResolved || references.length <= 3;
        if (group.some(item => item.originalDetails)) row.originalDetails = metadataEdited ? row.originalDetails || {} : structuredClone(merged);
        row.suggestedDays = [...new Set(group.flatMap(item => item.suggestedDays || []))];
        row.cells = days.map((_, day) => {
          const cells = group.map(item => item.cells[day]);
          const sources = [...new Map(cells.flatMap(cell => cell.sources || (cell.lineId ? [{ id: cell.lineId, version: cell.version, original: cell.original }] : []))
            .map(source => [source.id, source])).values()];
          const allExcluded = group.every(item => item.included === false);
          const included = cells.filter((cell, index) => (allExcluded || group[index].included !== false && cell.copyIncluded !== false) && !cell.removed);
          const invalid = included.find(cell => String(cell.value).trim() && !validQuantity(cell.value, row.allowsDecimals));
          const value = invalid ? invalid.value : included.some(cell => String(cell.value).trim())
            ? formatScaled(included.reduce((sum, cell) => sum + (scaled(cell.value) || 0n), 0n)) : '';
          const copies = Object.assign({}, ...cells.map(cell => cell.copyAmounts || (cell.copyOrigin ? { [cell.copyOrigin]: cell.copyPreparedValue ?? cell.value } : {})));
          return { ...cells[0], value, sources, lineId: sources[0]?.id || null, version: sources[0]?.version,
            original: sources.length ? formatScaled(sources.reduce((sum, source) => sum + (scaled(source.original) || 0n), 0n)) : '',
            copyAmounts: copies, manual: cells.some(cell => cell.manual || cell.copyOrigin && cell.value !== (cell.copyPreparedValue ?? cell.value)),
            removed: cells.every(cell => cell.removed), copyIncluded: row.included !== false };
        });
        row.included = group.some(item => item.included !== false);
        row.skuConsolidated = true;
      }
      for (const [day, value] of Object.entries(skuDayTargets[row.productId] || {})) {
        row.cells[day].value = value; row.cells[day].manual = true;
      }
      delete skuDayTargets[row.productId];
      return row;
    });
  };
  consolidateRows();
  const snapshot = () => {
    const openings = openingEditor?.snapshot();
    return { version: weekVersion, updated: new Date().toISOString(), operationId,
    excludedProducts: [...excludedProducts], weekStart: root.dataset.weekStart, pendingPayload: lastPayload, pendingSent: uncertainSave, rows, skuDayTargets, preview, previewSource, openingPreview, copySources, copyOpeningKeys, copyOpeningIncluded,
    openingPreviewSourceId, openingPreviewSourceVersion, openingColumnsPrepared,
    copyOpeningsChecked: $('[data-workspace-copy-openings]').checked,
    copyProductsChecked: $('[data-workspace-copy-products]').checked,
    copyQuantitiesChecked: $('[data-workspace-copy-quantities]').checked,
    copySourceId: $('[data-workspace-source]').value,
    deferredPreparation: (openings?.excluded || []).some(key => positive(openings.values[key])) ||
      Object.values(copyOpeningKeys).flat().some(entry => Object.hasOwn(openings?.values || {}, entry.key) && !positive(openings.values[entry.key])) ||
      rows.some(row => row.included === false || row.cells.some(cell => cell.copyOrigin && (cell.copyIncluded === false || !positive(cell.value)))),
    openings, openingChangeCount: openingChanges().length,
    copyPanelOpen, continueToOpen, needsReconcile,
    reason: $('[data-workspace-reason]')?.value || '' };
  };
  const state = () => window.ProductionSchedulePreparation.inspect({ ...snapshot(), saving: submitting, recoveryPending });
  const notifyState = () => {
    const pending = state();
    const warning = $('[data-workspace-progress-warning]');
    if (warning) warning.hidden = isNew || !pending.blocksOpening;
    const clear = $('[data-workspace-clear-all]');
    if (clear) {
      clear.hidden = !pending.pending;
      clear.disabled = submitting || uncertainSave || recoveryPending || preparingCopy || discardingAll;
    }
    window.dispatchEvent(new CustomEvent('schedulepreparationchange'));
  };
  const persist = () => {
    // Do not recreate discarded recovery while a previous async read finishes before navigation.
    if (discardingAll || recoveryPending) return;
    try { localStorage.setItem(storageKey, JSON.stringify(snapshot())); }
    catch { note('No se pudo guardar la preparación en este navegador. No cierres la página.', 'warning'); }
    notifyState();
  };
  const clearConfirmed = (count, state = snapshot(), targetStorageKey = storageKey) => {
    archiveLegacyText(state);
    const omitted = new Set(state.excludedProducts || []);
    if (omitted.size) {
      const opening = state.openings || {};
      const keepKey = key => !omitted.has(key.split(':')[0]);
      state = { ...state, excludedProducts: [],
        rows: (state.rows || []).filter(row => !omitted.has(row.productId)),
        copyOpeningKeys: Object.fromEntries(Object.entries(state.copyOpeningKeys || {}).map(([source, entries]) =>
          [source, entries.filter(entry => !omitted.has(entry.productId))])),
        openings: { ...opening,
          values: Object.fromEntries(Object.entries(opening.values || {}).filter(([key]) => keepKey(key))),
          products: (opening.products || []).filter(product => !omitted.has(product.productId)),
          touched: (opening.touched || []).filter(keepKey), manual: (opening.manual || []).filter(keepKey),
          excluded: (opening.excluded || []).filter(keepKey) } };
    }
    if (state.copySources) {
      const remainingRows = (state.rows || []).flatMap(row => {
        if (state.openings?.format === 2 && row.included !== false && !row.suggestedDays?.length &&
            row.cells.every(cell => !cell.lineId && !String(cell.value).trim()) &&
            Object.keys(state.openings.values || {}).some(key => key.startsWith(row.productId + ':') && !(state.openings.excluded || []).includes(key))) return [];
        const remainingDays = row.cells.map((cell, day) => !cell.lineId && (row.included === false || cell.copyIncluded === false ||
          row.suggestedDays?.includes(day) && !positive(cell.value)) ? day : -1).filter(day => day >= 0);
        if (!remainingDays.length) return !row.originalDetails && row.cells.every(cell => !cell.lineId && !positive(cell.value)) ? [row] : [];
        return [{ ...row, originalDetails: null, suggestedDays: remainingDays,
          cells: row.cells.map((cell, day) => remainingDays.includes(day) ? cell : blankCell()) }];
      });
      const excluded = state.openings?.excluded || [];
      const retainedKeys = [...new Set([...excluded, ...Object.values(state.copyOpeningKeys || {}).flat()
        .filter(entry => state.openings?.format !== 2 && Object.hasOwn(state.openings?.values || {}, entry.key) && !positive(state.openings.values[entry.key])).map(entry => entry.key)])];
      const remainingOpenings = { ...state.openings, values: Object.fromEntries(retainedKeys.map(key => [key, state.openings.values[key]])), excluded };
      if (remainingRows.length || retainedKeys.length) {
        localStorage.setItem(targetStorageKey, JSON.stringify({ ...state, version: state.version + count,
          rows: remainingRows, skuDayTargets: {}, openings: remainingOpenings, openingChangeCount: 0, operationId: uuid(),
          pendingPayload: '', pendingSent: false, continueToOpen: null, confirmedRemainder: true, deferredPreparation: true, updated: new Date().toISOString() }));
      } else localStorage.removeItem(targetStorageKey);
      return;
    }
    if (state.preview?.length || state.openingPreview?.length)
      localStorage.setItem(targetStorageKey, JSON.stringify({ version: state.version + count,
        updated: new Date().toISOString(), operationId: uuid(), rows: [],
        preview: state.preview || [], previewSource: state.previewSource || null,
        openingPreview: state.openingPreview || [], openingPreviewSourceId: state.openingPreviewSourceId || null,
        openingPreviewSourceVersion: state.openingPreviewSourceVersion ?? null,
        openingColumnsPrepared: !!state.openingColumnsPrepared,
        copyOpeningsChecked: !!state.copyOpeningsChecked,
        copyPanelOpen: state.copyPanelOpen ?? state.inputMode === 'copy',
        openingChangeCount: 0 }));
    else localStorage.removeItem(targetStorageKey);
  };
  const invalidateReview = () => { reviewGeneration++; reviewPanel.hidden = true; if ($('[data-workspace-pin]')) $('[data-workspace-pin]').value = ''; lastPayload = ''; continueToOpen = null; operationId = uuid(); persist(); };
  $('[data-workspace-clear-all]')?.addEventListener('click', () => {
    if (submitting || uncertainSave || recoveryPending || preparingCopy || discardingAll || !state().pending) return;
    if (!window.confirm(say('¿Descartar todos los cambios sin guardar de esta semana? El programa guardado se conservará.'))) return;
    try { localStorage.removeItem(storageKey); }
    catch { note(say('No se pudieron limpiar los cambios. Reintenta.'), 'warning'); return; }
    discardingAll = true; reviewGeneration++; searchGeneration++;
    lastPayload = ''; continueToOpen = null; $('[data-workspace-payload]').value = ''; reviewPanel.hidden = true;
    if ($('[data-workspace-pin]')) $('[data-workspace-pin]').value = '';
    freezePreparation();
    // Reload the server baseline without repeating edit/delete links or clearing the legacy text backup.
    location.replace(root.dataset.programUrl);
  });
  copyToggle.addEventListener('click', () => { setCopyPanel(!copyPanelOpen); persist(); });
  copyPanel.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    event.preventDefault(); setCopyPanel(false, true); persist();
  });
  $('[data-workspace-discard-legacy]').addEventListener('click', () => {
    try {
      localStorage.removeItem(legacyStorageKey);
      legacyText = ''; renderLegacyText(); search.focus();
    } catch { note(say('No se pudo descartar el texto anterior. Reintenta.'), 'warning'); }
  });
  root.addEventListener('openingchange', () => {
    if (openingEditor?.totals) rows.forEach(row => {
      if (row.productId && openingChanges().some(change => change.productId === row.productId) &&
          row.error === say('Completa las cantidades de los productos preparados antes de continuar.')) {
        row.error = ''; row.suggestedDays = []; row.initialOnly = true;
      }
    });
    invalidateReview(); refreshSummary(); refreshOpeningCells();
  });
  const quantityChanges = () => {
    const changes = [], errors = [];
    rows.forEach((row, rowIndex) => {
      if (excludedProducts.has(row.productId) || row.included === false) return;
      if (!row.productId || row.error) { errors.push(`${row.sku}: ${row.error || say('Selecciona un SKU activo del catálogo.')}`); return; }
      if (row.referenceCandidates?.length > 3 && !row.referencesResolved)
        errors.push(say('{0}: elige hasta tres pedidos para empaque.', row.sku));
      const metadataChanged = row.originalDetails && detailsKey(row.details) !== detailsKey(row.originalDetails);
      row.cells.forEach((cell, day) => {
        if (cell.copyOrigin && cell.copyIncluded === false) return;
        const value = normalizeQuantity(cell.value);
        if (cell.lineId) {
          if (cell.removed) { changes.push({ kind: 'remove', lineId: cell.lineId, expectedLineVersion: cell.version, line: null,
            sku: row.sku, day, before: cell.original, after: 'Quitado', unit: row.unit }); return; }
          if (value === '' || equalQuantity(value, '0')) {
            changes.push({ kind: 'remove', lineId: cell.lineId, expectedLineVersion: cell.version, line: toLine(row, day, '0'), sku: row.sku, day, before: cell.original, after: '0', unit: row.unit }); return;
          }
          if (!positive(value) || !validQuantity(value, row.allowsDecimals)) {
            errors.push(say('{0}, {1}: escribe una cantidad positiva.', row.sku, dayLabel(day))); return;
          }
          if (!equalQuantity(value, cell.original) || metadataChanged)
            changes.push({ kind: 'edit', lineId: cell.lineId, expectedLineVersion: cell.version,
              line: toLine(row, day, value), sku: row.sku, day, before: cell.original, after: value, unit: row.unit });
        } else if (value && Number(value) !== 0) {
          if (!positive(value) || !validQuantity(value, row.allowsDecimals))
            errors.push(say('{0}, {1}: cantidad no válida.', row.sku, dayLabel(day)));
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
      if (row.included === false || cell.copyIncluded === false || cell.removed || !positive(cell.value)) return;
      sum.set(row.unit, (sum.get(row.unit) || 0n) + scaled(cell.value));
    }));
    return [...sum].map(([unit, value]) => `${formatScaled(value)} ${unit}`).join(' · ');
  };
  const dayTotals = day => {
    const sum = new Map();
    rows.forEach(row => {
      const cell = row.cells[day];
      if (row.included === false || cell.copyIncluded === false || cell.removed || !positive(cell.value)) return;
      sum.set(row.unit, (sum.get(row.unit) || 0n) + scaled(cell.value));
    });
    return [...sum].map(([unit, value]) => `${formatScaled(value)} ${unit}`).join(' · ') || 'sin cantidad';
  };
  const refreshSummary = () => {
    const warning = $('[data-workspace-progress-warning]');
    if (warning) warning.hidden = isNew || !state().blocksOpening;
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
    const countNode = $('[data-workspace-count]');
    const countText = totalChanges === 1 ? root.dataset.textCountOne || '{0} cambio' : root.dataset.textCountMany || '{0} cambios';
    countNode.textContent = countText.replace('{0}', String(totalChanges));
    countNode.classList.toggle('text-bg-secondary', totalChanges === 0);
    countNode.classList.toggle('text-bg-warning', totalChanges > 0);
    $('[data-workspace-summary]').textContent = `${count('add')} nuevos · ${count('edit')} modificados · ${count('remove')} quitados · ${openingChanges().length} arrastres · ${totals() || 'sin cantidad'}${carryTotals ? ' · Arrastre: ' + carryTotals : ''}`;
    $('[data-workspace-empty]').hidden = rows.some(row => !row.rowDeletionSnapshot) || openingGroups().length > 0 || root.querySelectorAll('[data-progress]').length > 0;
    $('[data-workspace-empty]').textContent = rows.some(row => row.rowDeletionSnapshot)
      ? 'Hay eliminaciones pendientes. Revísalas antes de guardar.'
      : say('Busca un SKU o copia una semana para empezar.');
    root.querySelectorAll('[data-workspace-total]').forEach(node => {
      const row = rows.find(item => item.key === node.dataset.workspaceTotal);
      node.textContent = row ? `${formatScaled(row.cells.filter(x => row.included !== false && x.copyIncluded !== false && !x.removed && positive(x.value)).reduce((sum, x) => sum + scaled(x.value), 0n))} ${row.unit}` : '';
    });
    $('[data-workspace-day-summary]').textContent = days.map((day, index) => `${day}: ${dayTotals(index)}`).join(' · ');
    window.ProductionScheduleProgress?.update(root, rows, openingGroups(), skuDayTargets, excludedProducts);
  };
  const showView = () => {
    root.dataset.scheduleView = 'week';
    root.querySelectorAll('[data-workspace-day-head], [data-workspace-day-cell], [data-workspace-order-day]').forEach(node => { node.hidden = false; });
    refreshSummary();
  };
  const openingGroups = () => openingEditor?.groups?.() || [];
  const promote = row => {
    if (rows.includes(row)) return;
    if (Object.values(copyOpeningKeys).flat().some(entry => entry.productId === row.productId) ||
        rows.some(item => item.productId === row.productId && (item.preparedKind === 'copy' || item.cells.some(cell => cell.copyOrigin)))) row.preparedKind = 'copy';
    carryEditors.delete(row.productId); rows.push(row);
  };
  const nextQuantity = input => {
    const fields = [...tbody.querySelectorAll('input.production-week-workspace__quantity, input.production-opening-quantity')]
      .filter(field => !field.disabled && !field.hidden && !field.closest('td')?.hidden);
    const next = fields[fields.indexOf(input) + 1];
    if (next) next.focus();
  };
  const groupedDay = (product, day) => {
    const input = document.createElement('input'); input.type = 'text'; input.inputMode = 'decimal';
    input.className = 'form-control production-week-workspace__quantity';
    input.dataset.workspaceSkuInput = product.productId; input.dataset.workspaceSkuDay = day;
    input.setAttribute('aria-label', product.sku + ', ' + dayLabel(day) + ', ' + product.unit);
    input.addEventListener('input', () => {
      let row = rows.find(row => row.productId === product.productId && !row.rowDeletionSnapshot);
      if (!row) { row = fresh({ id: product.productId, sku: product.sku, unit: product.unit,
        allowsDecimals: openingGroups().find(group => group.productId === product.productId)?.entries[0]?.allowsDecimals }); promote(row); }
      row.cells[day].value = input.value; row.cells[day].manual = true;
      invalidateReview(); render();
      const replacement = [...root.querySelectorAll('[data-workspace-sku-input]')].find(field => field.dataset.workspaceSkuInput === product.productId && Number(field.dataset.workspaceSkuDay) === day);
      replacement?.focus();
    });
    input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); nextQuantity(input); } });
    return [input];
  };
  const openingCells = product => { openingEditor?.register?.(product); return [0, 1, 2].map(area => {
    const entries = openingGroups().find(group => group.productId === product.productId)?.entries.filter(entry => entry.area === area) || [];
    const td = document.createElement('td'); td.dataset.workspaceOpeningArea = area; td.dataset.productId = product.productId;
    td.className = 'production-week-workspace__carry-cell'; td.dataset.label = `${say('Arrastre inicial')} · ${say(['Corte', 'Costura', 'Ready to Pack'][area])}`;
    const quantity = entries.some(entry => scaled(entry.quantity) === null) ? entries.find(entry => scaled(entry.quantity) === null).quantity
      : formatScaled(entries.reduce((sum, entry) => sum + scaled(entry.quantity), 0n));
    if (entries.length || openingEditor?.totals && product.productId) {
      const input = document.createElement('input'); input.type = 'text'; input.inputMode = 'decimal'; input.value = quantity;
      input.className = 'form-control production-opening-quantity'; input.dataset.workspaceOpeningInput = area;
      input.setAttribute('aria-label', `${product.sku} · ${say('arrastre de {0}', say(['Corte', 'Costura', 'Ready to Pack'][area]))}`);
      input.disabled = excludedProducts.has(product.productId) || !openingEditor?.totals && entries.length > 0 && entries.every(entry => entry.excluded);
      input.addEventListener('input', () => { openingEditor.setArea(product.productId, area, input.value); });
      input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); nextQuantity(input); } });
      td.append(input);
    } else td.append(text('span', '0', 'production-week-workspace__planned'));
    td.classList.toggle('production-week-workspace__carry-positive', positive(quantity));
    return td;
  }); };
  const openingOrigin = (sku, product) => {
    const candidates = rows.filter(row => row.productId === product.productId && !row.rowDeletionSnapshot &&
      (row.preparedKind || row.cells.some(cell => cell.copyOrigin)));
    if (candidates.length) {
      const wrapper = text('label', '', 'production-week-workspace__include');
      const check = document.createElement('input'); check.type = 'checkbox';
      check.checked = !excludedProducts.has(product.productId) && candidates.every(row => row.included !== false && row.cells.filter(cell => cell.copyOrigin).every(cell => cell.copyIncluded !== false));
      check.setAttribute('aria-label', say('Incluir producto {0}', product.sku));
      check.addEventListener('change', () => {
        if (check.checked) excludedProducts.delete(product.productId); else excludedProducts.add(product.productId);
        rows.filter(row => row.productId === product.productId).forEach(row => { row.included = check.checked; });
        candidates.forEach(row => {
          if (row.cells.some(cell => cell.copyOrigin)) row.cells.filter(cell => cell.copyOrigin).forEach(cell => { cell.copyIncluded = check.checked; });
          else row.included = check.checked;
        });
        invalidateReview(); render();
      });
      wrapper.append(check, text('span', say('Incluir producto'))); sku.append(wrapper);
    }
    const group = openingGroups().find(group => group.productId === product.productId);
    if (!group) return;
    if (openingEditor?.totals) return;
    const details = document.createElement('details');
    window.ProductionScheduleProgress?.restore(root, details, `origin:${product.productId}`);
    details.append(text('summary', say('Ver origen')));
    group.entries.forEach(entry => {
      const line = text('div', '', 'production-week-workspace__carry-origin');
      const description = `${say(['Corte', 'Costura', 'Ready to Pack'][entry.area])} · ${displayDate(entry.sourceStart)} · ${say('Renglón {0}', entry.sequence)}`;
      line.append(text('span', description, 'small'));
      if (entry.originalStart && entry.originalStart !== entry.sourceStart) line.append(text('span', say('origen inicial {0}', displayDate(entry.originalStart)), 'small'));
      ['orderReference1', 'orderReference2', 'orderReference3', 'notes'].forEach((key, index) => {
        if (entry[key]) line.append(text('span', `${say(['Pedido 1', 'Pedido 2', 'Pedido 3', 'Notas'][index])}: ${entry[key]}`, 'small'));
      });
      const input = document.createElement('input'); input.value = entry.cachedQuantity; input.inputMode = 'decimal'; input.disabled = entry.excluded;
      input.dataset.workspaceOrigin = entry.key;
      input.className = 'form-control production-opening-quantity'; input.setAttribute('aria-label', `${product.sku} · ${description}`);
      input.addEventListener('input', () => openingEditor.setQuantity(entry.key, input.value));
      line.append(input, text('small', say('Disponible: {0} {1}', entry.available, entry.unit), 'text-body-secondary')); details.append(line);
    });
    sku.append(details);
  };
  const refreshOpeningCells = () => {
    root.querySelectorAll('[data-workspace-opening-area]').forEach(td => {
      const entries = openingGroups().find(group => group.productId === td.dataset.productId)?.entries.filter(entry => entry.area === Number(td.dataset.workspaceOpeningArea)) || [];
      const invalid = entries.find(entry => scaled(entry.quantity) === null);
      const quantity = invalid ? invalid.quantity : formatScaled(entries.reduce((sum, entry) => sum + scaled(entry.quantity), 0n));
      const input = td.querySelector('[data-workspace-opening-input]');
      if (input && input !== document.activeElement) input.value = quantity;
      td.classList.toggle('production-week-workspace__carry-positive', positive(quantity));
    });
    const values = openingEditor?.snapshot().values || {};
    root.querySelectorAll('[data-workspace-origin]').forEach(input => { if (input !== document.activeElement) input.value = values[input.dataset.workspaceOrigin] || '0'; });
  };
  const render = () => {
    consolidateRows();
    rows.forEach(row => { if (excludedProducts.has(row.productId)) row.included = false; });
    window.ProductionScheduleProgress?.remember(root);
    rowFields.clear();
    tbody.replaceChildren();
    const editors = [...rows];
    openingGroups().filter(group => !rows.some(row => row.productId === group.productId && !row.rowDeletionSnapshot)).forEach(group => {
      if (!carryEditors.has(group.productId)) carryEditors.set(group.productId, fresh({ id: group.productId,
        sku: group.sku, unit: group.unit, allowsDecimals: group.entries[0]?.allowsDecimals }));
      editors.push(carryEditors.get(group.productId));
    });
    editors.forEach(row => {
      if (row.rowDeletionSnapshot) return;
      const tr = document.createElement('tr');
      tr.dataset.workspaceRow = row.key;
      const sku = document.createElement('th'); sku.scope = 'row';
      sku.append(text('strong', row.sku), text('small', ` ${row.unit}`, 'd-block text-body-secondary'));
      if (!row.productId) {
        const correction = document.createElement('input'); correction.type = 'search'; correction.value = row.sku;
        correction.className = 'form-control'; correction.setAttribute('aria-label', say('Corregir SKU'));
        correction.addEventListener('change', async () => {
          try { const item = { sku: correction.value }; await resolvePrepared([item]);
            Object.assign(row, { sku: item.sku, productId: item.productId, unit: item.unit, allowsDecimals: item.allowsDecimals, error: item.error }); invalidateReview(); render();
          } catch { note(say('No se pudieron validar los SKU. Reintenta.'), 'warning'); }
        }); sku.append(correction);
      }
      const rowError = row.error ? text('small', row.error, 'd-block text-danger') : null;
      if (rowError) sku.append(rowError);
      tr.append(sku);
      tr.append(...openingCells(row));
      openingOrigin(sku, row);
      row.cells.forEach((cell, day) => {
        const td = document.createElement('td'); td.dataset.workspaceDayCell = day;
        td.dataset.label = window.ProductionScheduleProgress?.label(days[day]) || days[day];
        const label = `${row.sku}, ${window.ProductionScheduleProgress?.label(days[day]) || days[day]}, ${row.unit}`;
        const input = document.createElement('input'); input.type = 'text'; input.inputMode = 'decimal';
        input.className = 'form-control production-week-workspace__quantity';
        input.dataset.workspaceSkuInput = row.productId; input.dataset.workspaceSkuDay = day;
        input.value = cell.value; input.setAttribute('aria-label', label);
        if (row.suggestedDays?.includes(day) && !cell.value) input.placeholder = 'Programar';
        input.disabled = excludedProducts.has(row.productId) || cell.removed || row.included === false || cell.copyIncluded === false;
        const error = text('small', '', 'd-block text-danger');
        const validate = () => {
          const invalid = String(input.value).trim() && !validQuantity(input.value, row.allowsDecimals);
          input.setAttribute('aria-invalid', String(!!invalid));
          error.textContent = invalid ? say('Cantidad no válida.') : '';
        };
        rowFields.set(`${row.key}:${day}`, { input, validate });
        input.addEventListener('input', () => { promote(row); cell.value = input.value; cell.manual = true; validate();
          if (row.error && row.productId && positive(cell.value)) { row.error = ''; rowError?.remove(); }
          invalidateReview(); refreshSummary(); });
        input.addEventListener('keydown', event => {
          if (event.key !== 'Enter') return;
          event.preventDefault();
          nextQuantity(input);
        });
        input.id = `workspace-${row.key}-day-${day}`;
        input.setAttribute('aria-describedby', 'week-workspace-quantity-label');
        validate(); td.append(input, error);
        tr.append(td);
      });
      const total = document.createElement('td'); total.dataset.workspaceTotal = row.key;
      total.dataset.label = window.ProductionScheduleProgress?.label('Total') || 'Total';
      total.className = 'production-week-workspace__total'; tr.append(total);
      tr.append(window.ProductionScheduleProgress?.carryTotalCell(row) || document.createElement('td'));
      const actions = document.createElement('td');
      const details = document.createElement('details');
      window.ProductionScheduleProgress?.restore(root, details, `row:${row.key}`);
      details.append(text('summary', window.ProductionScheduleProgress?.label('Pedidos y notas') || 'Pedidos y notas'));
      if (row.referenceCandidates?.length > 3 && !row.referencesResolved) {
        details.open = true;
        details.append(text('p', say('{0}: elige hasta tres pedidos para empaque.', row.sku), 'text-warning-emphasis'));
        const chosen = new Set(referenceKeys.map(key => row.details[key]).filter(Boolean));
        row.referenceCandidates.forEach(reference => {
          const label = text('label', '', 'd-block py-2');
          const check = document.createElement('input'); check.type = 'checkbox'; check.className = 'form-check-input me-2'; check.checked = chosen.has(reference);
          check.addEventListener('change', () => { if (check.checked) chosen.add(reference); else chosen.delete(reference); });
          label.append(check, text('span', reference)); details.append(label);
        });
        const accept = text('button', say('Usar estos pedidos'), 'btn btn-outline-primary'); accept.type = 'button';
        accept.addEventListener('click', () => {
          if (chosen.size > 3) { note(say('{0}: elige hasta tres pedidos para empaque.', row.sku), 'warning'); return; }
          referenceKeys.forEach((key, index) => { row.details[key] = [...chosen][index] || ''; });
          row.referencesResolved = true; row.metadataManual = true;
          if (row.originalDetails) row.originalDetails = {};
          invalidateReview(); render();
        }); details.append(accept);
      }
      [['orderReference1', 'Pedido 1'], ['orderReference2', 'Pedido 2'], ['orderReference3', 'Pedido 3'],
        ['notes', 'Notas']].forEach(([key, label]) => {
        const wrapper = document.createElement('div'); wrapper.className = 'mt-2';
        const field = document.createElement(key === 'notes' ? 'textarea' : 'input'); field.className = 'form-control'; field.value = row.details[key];
        field.maxLength = key === 'notes' ? 500 : 120; field.id = `workspace-${row.key}-${key}`;
        const labelNode = text('label', window.ProductionScheduleProgress?.label(label) || label, 'form-label'); labelNode.htmlFor = field.id;
        field.addEventListener('input', () => { promote(row); row.details[key] = field.value; row.metadataManual = true; invalidateReview(); refreshSummary(); });
        wrapper.append(labelNode, field);
        details.append(wrapper);
      });
      row.cells.forEach((cell, day) => {
        if (!cell.lineId) return;
        const wrapper = text('div', '', 'mt-2'); wrapper.dataset.workspaceOrderDay = day;
        const remove = text('button', say(cell.removed ? 'Deshacer' : 'Quitar día'), 'btn btn-outline-danger btn-sm'); remove.type = 'button';
        remove.textContent = say('{0}: {1}, {2}', remove.textContent, row.sku, say(days[day]));
        remove.disabled = !cell.removed && (cell.sources || [{ id: cell.lineId }]).some(source => saved.find(line => line.Id === source.id)?.CanRemove === false);
        remove.addEventListener('click', () => {
          if (!cell.removed && !window.confirm(say('¿Eliminar {0} del {1}? Se aplicará al guardar los cambios.', row.sku, say(days[day])))) return;
          cell.removed = !cell.removed; invalidateReview(); render();
        }); wrapper.append(remove); details.append(wrapper);
      });
      actions.append(details);
      const discard = text('button', 'Eliminar fila', 'btn btn-outline-danger btn-sm mt-2');
      discard.type = 'button';
      discard.hidden = !rows.includes(row);
      discard.disabled = row.cells.some(cell => (cell.sources || []).some(source => saved.find(line => line.Id === source.id)?.CanRemove === false));
      discard.setAttribute('aria-label', `${discard.textContent}: ${row.sku}`);
      discard.addEventListener('click', () => {
        const savedCount = row.cells.filter(cell => cell.lineId).length;
        const question = savedCount
          ? `¿Eliminar la fila de ${row.sku} y sus ${savedCount} cantidades guardadas? Se aplicará al guardar los cambios.`
          : `¿Eliminar la fila de ${row.sku} de la preparación?`;
        if (!window.confirm(question)) return;
        const position = rows.indexOf(row);
        if (!savedCount) {
          rows = rows.filter(item => item !== row);
        } else {
          row.rowDeletionSnapshot = row.cells.map(cell => ({ value: cell.value, removed: cell.removed }));
          row.cells.forEach(cell => {
            if (cell.lineId) cell.removed = true;
            else cell.value = '';
          });
        }
        invalidateReview(); render();
        note(savedCount ? `${row.sku}: eliminación pendiente de guardar.` : `${row.sku}: fila retirada de la preparación.`, 'warning');
        const undo = text('button', 'Deshacer', 'btn btn-outline-secondary btn-sm ms-2'); undo.type = 'button';
        undo.addEventListener('click', () => {
          if (savedCount) {
            row.cells.forEach((cell, day) => {
              cell.value = row.rowDeletionSnapshot[day].value;
              cell.removed = row.rowDeletionSnapshot[day].removed;
            });
            row.rowDeletionSnapshot = null;
          } else rows.splice(Math.min(position, rows.length), 0, row);
          invalidateReview(); render(); note(`${row.sku}: eliminación deshecha.`, 'info');
        });
        message.children[0].append(undo);
      });
      actions.append(discard);
      tr.append(actions); tbody.append(tr);
    });
    if (window.ProductionScheduleProgress) window.ProductionScheduleProgress.decorate(root, editors, openingGroups(), openingCells, openingOrigin, groupedDay, Object.keys(skuDayTargets));
    showView(); refreshSummary();
  };
  const insertProduct = product => {
    const focusRow = row => {
      const node = [...tbody.querySelectorAll('[data-workspace-row]')].find(node => node.dataset.workspaceRow === row.key);
      node?.querySelectorAll('input.production-week-workspace__quantity')[0]?.focus();
    };
    const matches = rows.filter(row => !row.rowDeletionSnapshot && row.productId === product.id);
    if (matches.length === 1) {
      focusRow(matches[0]);
    }
    if (matches.length > 0) {
      focusRow(matches[0]); search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false'); return;
    }
    rows.push(fresh(product)); invalidateReview(); render(); search.value = ''; results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    focusRow(rows.at(-1));
  };
  const searchProducts = async () => {
    const generation = ++searchGeneration;
    const term = search.value.trim(); results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    if (term.length < 2) return;
    const url = new URL(root.dataset.searchUrl, location.origin); url.searchParams.set('q', term);
    try {
      const response = await fetch(url); if (!response.ok) throw new Error();
      const products = await response.json(); if (generation !== searchGeneration || term !== search.value.trim()) return;
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
    } catch {
      if (generation === searchGeneration && term === search.value.trim()) note('No se pudo buscar el producto. Reintenta.', 'warning');
    }
  };
  search.addEventListener('input', () => {
    clearTimeout(searchTimer); searchGeneration++;
    results.replaceChildren(); search.setAttribute('aria-expanded', 'false');
    if (search.value.trim().length >= 2) searchTimer = setTimeout(searchProducts, 180);
  });
  search.addEventListener('keydown', async event => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    clearTimeout(searchTimer); searchGeneration++;
    const generation = searchGeneration;
    const code = search.value.trim(); if (!code) return;
    try {
      const url = new URL(root.dataset.resolveUrl, location.origin); url.searchParams.set('code', code);
      const response = await fetch(url); if (!response.ok) throw new Error();
      const product = await response.json();
      if (generation !== searchGeneration || code !== search.value.trim()) return;
      if (!product?.id) throw new Error();
      const detailUrl = new URL(root.dataset.resolveProductsUrl, location.origin); detailUrl.searchParams.set('skus', product.sku);
      const details = await fetch(detailUrl).then(x => x.json());
      if (generation !== searchGeneration || code !== search.value.trim()) return;
      if (!details[0]) throw new Error();
      insertProduct(details[0]);
    } catch {
      if (generation === searchGeneration && code === search.value.trim()) note('No se pudo resolver el código escaneado. Selecciona el SKU de la lista.', 'warning');
    }
  });
  const review = async (forOpening = false) => {
    await window.ProductionWeekWorkspace?.ready;
    if (uncertainSave) { note(say('No se recibió la confirmación. Comprueba la conexión y reintenta con esta misma revisión.'), 'warning'); return; }
    if (submitting || discardingAll || isNew && root.dataset.targetReady !== 'true') return;
    if (isNew) forOpening = true;
    if (openingEditor) await openingEditor.ready;
    if (forOpening && recoveryPending) { note(root.dataset.textRecovery, 'warning'); recovery.scrollIntoView({ block: 'nearest' }); recovery.querySelector('button')?.focus(); return; }
    if (needsReconcile) { note('Concilia la preparación recuperada con la semana actual antes de revisar.', 'warning'); return; }
    if (Object.keys(copySources).length || openingEditor) try {
      if (await revalidateSources()) { note(say('El origen cambió. Se conservaron tus cantidades; revisa nuevamente antes de guardar.'), 'warning'); return; }
    } catch (error) {
      note(error.message || say('No se pudo revalidar la semana origen.'), 'warning');
      if (isNew) Object.keys(copySources).filter(id => ![...$('[data-workspace-source]').options].some(option => option.value === id)).forEach(id => {
        const detach = text('button', say('Conservar productos y cantidades como preparación manual'), 'btn btn-outline-secondary'); detach.type = 'button';
        detach.addEventListener('click', () => {
          delete copySources[id];
          rows.forEach(row => {
            row.cells.forEach(cell => { if (cell.copySourceId === id) { delete cell.copyOrigin; delete cell.copySourceId; delete cell.copyPreparedValue; } });
            if (!row.cells.some(cell => cell.copySourceId)) { delete row.preparedKind; row.suggestedDays = []; }
          });
          invalidateReview(); render(); note(say('Revisa nuevamente los cambios del programa.'), 'info');
        }); message.append(detach);
      });
      return;
    }
    if (discardingAll) return;
    const { changes, errors } = quantityChanges();
    const openings = openingChanges();
    errors.push(...(openingEditor?.errors(excludedProducts) || []));
    if (errors.length) {
      note(errors.join(' '), 'danger');
      (openingEditor?.missing?.() || []).forEach(key => {
        const button = text('button', say('Quitar selección de origen no disponible'), 'btn btn-outline-danger'); button.type = 'button';
        button.addEventListener('click', () => { openingEditor.removeMissing(key); invalidateReview(); render(); note(say('Revisa nuevamente los cambios del programa.'), 'info'); });
        message.children[0].append(button);
      });
      return;
    }
    if (!isNew && !changes.length && !openings.length) {
      if (forOpening && !state().blocksOpening) { if (excludedProducts.size) clearConfirmed(0); completedSave = true; location.assign(root.dataset.reviewUrl); }
      else note(forOpening ? root.dataset.textInvalid : 'Agrega o modifica una cantidad antes de revisar.', 'warning');
      return;
    }
    const list = $('[data-workspace-review-list]'); list.replaceChildren();
    const openingGroups = new Map();
    openings.forEach(change => openingGroups.set(`${change.sku}:${change.unit}:${change.area}`, change));
    const openingOptions = openingEditor?.options() || [];
    openingGroups.forEach(change => {
      const entries = openingOptions.filter(row => row.sku === change.sku && row.unit === change.unit && row.area === change.area);
      const before = entries.reduce((sum, row) => sum + (Number(row.selected) < 0 ? -scaled(String(row.selected).substring(1)) : scaled(String(row.selected || 0))), 0n);
      const after = entries.reduce((sum, row) => sum + scaled(String(row.prepared || 0)), 0n);
      list.append(text('div', say('{0} · {1} · Arrastre inicial: {2} → {3} {4}', change.sku,
        say(['Corte', 'Costura', 'Ready to Pack'][change.area]), formatScaled(before), formatScaled(after), change.unit), 'border-bottom py-2'));
    });
    changes.sort((a, b) => a.day - b.day).forEach(change => {
      const key = change.kind === 'add' ? '{0} · {1}: {2} → {3} {4} (nuevo)' : change.kind === 'edit'
        ? '{0} · {1}: {2} → {3} {4} (cambio)' : '{0} · {1}: {2} → {3} {4} (quitar)';
      list.append(text('div', say(key, dayLabel(change.day), change.sku,
        change.before, change.after, change.unit), 'border-bottom py-2'));
    });
    if (isNew) {
      list.prepend(text('p', document.querySelector('[data-week-picker-summary]')?.textContent || root.dataset.weekStart, 'fw-semibold'));
      if (!changes.length && !openings.length) list.append(text('p', say('La semana se abrirá sin programación ni arrastre.')));
    }
    const payload = { operationId, weekStart: root.dataset.weekStart, weekId: weekId || undefined, expectedWeekVersion: weekVersion,
      ...(openingEditor?.totals ? { initialBalances: openings } : { openings }),
      changes: isNew ? changes.filter(change => change.kind !== 'remove').map(({ kind, lineId, expectedLineVersion, line }) => ({ kind, lineId, expectedLineVersion, line })) : [],
      ...(!isNew ? { skuTotals: changes.map(change => toLine(rows.find(row => row.sku === change.sku), change.day, change.kind === 'remove' ? '0' : change.after)) } : {}) };
    const needsOpeningReview = root.dataset.weekStatus === 'Open' && openings.length > 0;
    const reason = $('[data-workspace-reason]'), reasonPanel = $('[data-workspace-reason-panel]');
    if (reasonPanel) reasonPanel.hidden = !needsOpeningReview;
    if (needsOpeningReview || isNew || payload.skuTotals?.length) {
      reviewPanel.hidden = false;
      $('[data-workspace-save]').disabled = true;
      payload.reason = reason?.value.trim() || '';
      if (needsOpeningReview && !payload.reason) { reviewPanel.scrollIntoView({ block: 'nearest' }); reason?.focus(); return; }
      const generation = reviewGeneration;
      try {
        const response = await fetch(root.dataset.workspaceReviewUrl, { method: 'POST',
          headers: { 'Content-Type': 'application/json', RequestVerificationToken: form.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
          body: JSON.stringify(payload) });
        if (!response.ok) throw Error();
        const result = await response.json();
        if (generation !== reviewGeneration) return;
        if (!result.canConfirm) { note(result.errors?.join(' ') || say('Revisa la preparación antes de guardar.'), 'warning'); return; }
        payload.reviewedFingerprint = result.fingerprint;
      } catch { note(say('No se pudo validar la revisión. Reintenta sin perder la preparación.'), 'warning'); return; }
    }
    $('[data-workspace-save]').disabled = false;
    lastPayload = JSON.stringify(payload); $('[data-workspace-payload]').value = lastPayload;
    continueToOpen = forOpening ? { weekId, operationId } : null;
    $('[data-workspace-save]').textContent = forOpening && !isNew ? root.dataset.textContinue : root.dataset.textSave;
    const pin = $('[data-workspace-pin]'), pinPanel = $('[data-workspace-pin-panel]');
    if (pin && pinPanel) {
      const requiresPin = isNew || root.dataset.weekStatus === 'Open' && (payload.skuTotals?.length > 0 || openings.length > 0 || changes.some(change => change.kind === 'add' ||
        change.kind === 'remove' && saved.find(line => line.Id === change.lineId)?.RemovalRequiresPin));
      pinPanel.hidden = !requiresPin; pin.disabled = !requiresPin; pin.required = requiresPin; pin.value = '';
    }
    persist();
    reviewPanel.hidden = false;
    if (pin && !pin.disabled && !pinPanel.hidden) {
      pin.scrollIntoView({ block: 'center', behavior: 'instant' });
      pin.focus({ preventScroll: true });
    } else { reviewPanel.scrollIntoView({ block: 'nearest' }); reviewPanel.focus(); }
  };
  $('[data-workspace-review]').addEventListener('click', () => review());
  $('[data-workspace-recheck]')?.addEventListener('click', () => review());
  $('[data-workspace-reason]')?.addEventListener('input', () => {
    reviewGeneration++; lastPayload = ''; $('[data-workspace-save]').disabled = true;
    if ($('[data-workspace-pin]')) $('[data-workspace-pin]').value = '';
    persist();
  });
  $('[data-workspace-back]').addEventListener('click', () => { reviewPanel.hidden = true; if ($('[data-workspace-pin]')) $('[data-workspace-pin]').value = ''; continueToOpen = null; persist(); });
  const completeNewWeek = (url, confirmedState = snapshot()) => {
    const destination = new URL(url, location.origin);
    const destinationId = destination.searchParams.get('WeekId');
    if (!destinationId) throw Error('Missing confirmed week');
    const payload = JSON.parse(confirmedState.pendingPayload);
    clearConfirmed((payload.skuTotals?.length || payload.changes.length) + ((payload.initialBalances || payload.openings)?.length || 0), confirmedState,
      'warehouse-epi:schedule:' + root.dataset.userId + ':' + destinationId);
    localStorage.removeItem(storageKey);
    completedSave = true; location.assign(url);
  };
  const completeSave = (count, confirmedState = snapshot(), version = null) => {
    completedSave = true;
    if (version != null) confirmedState = { ...confirmedState, version: version - count };
    clearConfirmed(count, confirmedState);
    sessionStorage.setItem(`${storageKey}:saved`, String(count));
    const intent = confirmedState.continueToOpen;
    if (intent?.weekId === weekId && intent.operationId === confirmedState.operationId &&
        !confirmedState.preview?.length && !confirmedState.openingPreview?.length)
      location.assign(root.dataset.reviewUrl);
    else location.reload();
  };
  form.addEventListener('submit', async event => {
    event.preventDefault(); if (submitting || discardingAll || !lastPayload) return;
    if (isNew && root.dataset.targetReady !== 'true' || !form.checkValidity()) { form.reportValidity(); return; }
    const body = new FormData(form);
    if (isNew) { body.set('NewWeek.OperationId', operationId); body.set('NewWeek.WeekStart', root.dataset.weekStart); }
    if ($('[data-workspace-pin]')) $('[data-workspace-pin]').value = '';
    submitting = true; uncertainSave = true; freezePreparation(); $('[data-workspace-save]').disabled = true;
    persist();
    try {
      const response = await fetch(form.action, { method: 'POST', body });
      const result = await response.json();
      if (result.saved) {
        if (isNew) { completeNewWeek(result.url); return; }
        completeSave(result.count, snapshot(), result.version); return;
      }
      uncertainSave = false;
      if (response.status === 409) {
        if (isNew) {
          invalidateReview(); note(result.errors?.join(' ') || say('Revisa nuevamente los cambios del programa.'), 'warning');
          if (result.viewUrl) { const link = text('a', say('Ver semana'), 'btn btn-outline-secondary'); link.href = result.viewUrl; message.append(link); }
          return;
        }
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
      if (!isNew) url.searchParams.set('weekId', weekId); url.searchParams.set('operationId', operationId);
      try {
        const status = await fetch(url).then(x => x.json());
        if (status.saved) { if (isNew) { completeNewWeek(status.url); return; } const payload = JSON.parse(lastPayload); completeSave((payload.skuTotals?.length || payload.changes.length) + ((payload.initialBalances || payload.openings)?.length || 0), snapshot(), status.version); return; }
      } catch { /* El borrador local permanece disponible. */ }
      note(say('No se recibió la confirmación. Comprueba la conexión y reintenta con esta misma revisión.'), 'warning');
    } finally { submitting = false; freezePreparation(); $('[data-workspace-save]').disabled = false; if (!completedSave) persist(); }
  });
  // New rows come from copying; old prepared previews are accepted only during recovery.
  let previewSource = null;
  let openingPreviewSourceId = null, openingPreviewSourceVersion = null, openingColumnsPrepared = false;
  let copySources = {}, copyOpeningKeys = {}, copyOpeningIncluded = {};
  const applyOpeningInclusion = () => {
    Object.entries(copyOpeningKeys).forEach(([sourceId, entries]) => {
      const enabled = sourceId !== openingPreviewSourceId || $('[data-workspace-copy-openings]').checked;
      const included = entries.filter(entry => enabled && copyOpeningIncluded[entry.productId] !== false).map(entry => entry.key);
      const excluded = entries.filter(entry => !included.includes(entry.key)).map(entry => entry.key);
      openingEditor?.setEnabled?.(included, true); openingEditor?.setEnabled?.(excluded, false);
    });
  };
  const attachProgram = (items, source = null) => {
    consolidateRows();
    const batches = new Map();
    items.forEach((item, index) => {
      const id = item.productId || item.sku;
      if (!batches.has(id)) batches.set(id, []);
      batches.get(id).push({ ...item, copyIndex: index });
    });
    batches.forEach(items => {
      const item = items[0];
      let row = rows.find(candidate => !candidate.rowDeletionSnapshot && candidate.productId === item.productId);
      if (!row) {
        row = fresh({ id: item.productId, sku: item.sku, unit: item.unit, allowsDecimals: item.allowsDecimals }, item.details || {});
        row.preparedKind = source ? 'copy' : 'recovered'; row.included = item.selected !== false; row.error = item.error || '';
        rows.push(row);
      }
      const references = [...new Set([...referenceKeys.map(key => row.details[key]), ...items.flatMap(item => referenceKeys.map(key => item.details?.[key]))].filter(Boolean))];
      if (!row.metadataManual) {
        referenceKeys.forEach((key, index) => { row.details[key] = references[index] || ''; });
        row.referenceCandidates = references; row.referencesResolved = references.length <= 3;
        row.details.notes = [...new Set([row.details.notes, ...items.map(item => item.details?.notes)].filter(Boolean))].join('\n');
      }
      days.forEach((_, day) => {
        const entries = items.filter(item => item.suggestedDays?.includes(day) || String(item.days[day] ?? '').trim());
        if (!entries.length) return;
        if (!row.suggestedDays.includes(day)) row.suggestedDays.push(day);
        const cell = row.cells[day];
        const copies = cell.copyAmounts ||= {};
        const before = Object.values(copies).reduce((sum, value) => sum + (scaled(value) || 0n), 0n);
        entries.forEach(item => {
          const origin = source ? source.id + ':' + (item.sourceIds?.[day] || item.copyIndex + ':' + day) : 'legacy:' + item.copyIndex + ':' + day;
          copies[origin] = item.days[day] || '';
        });
        const after = Object.values(copies).reduce((sum, value) => sum + (scaled(value) || 0n), 0n);
        if (!cell.manual) {
          const invalid = entries.find(item => String(item.days[day] ?? '').trim() && !validQuantity(item.days[day], row.allowsDecimals));
          cell.value = invalid ? invalid.days[day] : (cell.lineId || (scaled(cell.value) || 0n) - before !== 0n || Object.values(copies).some(value => String(value).trim()))
            ? formatScaled((scaled(cell.value) || 0n) - before + after) : '';
        }
        cell.copyOrigin ||= Object.keys(copies)[0]; cell.copySourceId = source?.id;
        cell.copyPreparedValue = cell.value; cell.copyIncluded = row.included !== false;
      });
    });
    if (source) copySources[source.id] = source.version;
  };
  const resolveSkus = async items => {
    const skus = [...new Set(items.map(item => item.sku.trim()))], found = [];
    for (let index = 0; index < skus.length; index += 30) {
      const url = new URL(root.dataset.resolveProductsUrl, location.origin);
      url.searchParams.set('skus', skus.slice(index, index + 30).join('\n'));
      const response = await fetch(url); if (!response.ok) throw Error(say('No se pudieron validar los SKU. Reintenta.'));
      found.push(...await response.json());
    }
    return found;
  };
  const resolvePrepared = async items => {
    const products = await resolveSkus(items), catalog = new Map(products.map(product => [product.sku.toUpperCase(), product]));
    items.forEach(item => {
      const product = catalog.get(item.sku.trim().toUpperCase());
      Object.assign(item, { productId: product?.id || null, unit: product?.unit || '', allowsDecimals: product?.allowsDecimals !== false,
        error: product ? item.error || '' : say('Selecciona un SKU activo del catálogo.') });
    });
  };
  const focusTable = (ids = []) => {
    const fields = [...tbody.querySelectorAll('input.production-week-workspace__quantity')].filter(input =>
      !input.disabled && !input.closest('[hidden]') && !input.closest('details:not([open])'));
    const first = fields.find(input => ids.includes(input.dataset.workspaceSkuInput) && Number(input.dataset.workspaceSkuDay) === 0) || fields[0];
    first?.scrollIntoView({ block: 'nearest' }); first?.focus();
  };
  const preparationResult = () => {
    note(say('Copia preparada en la planeación. Ajusta las cantidades y revisa antes de guardar.'), 'success');
  };
  $('[data-workspace-copy-openings]').addEventListener('change', () => { applyOpeningInclusion(); invalidateReview(); render(); });
  ['[data-workspace-copy-products]', '[data-workspace-copy-quantities]', '[data-workspace-source]']
    .forEach(selector => $(selector).addEventListener('change', persist));
  $('[data-workspace-copy]').addEventListener('click', async () => {
    if (submitting || uncertainSave || recoveryPending || preparingCopy || discardingAll) return;
    const sourceId = $('[data-workspace-source]').value;
    if (!sourceId) { note(say('No hay una semana anterior para copiar.'), 'warning'); return; }
    const copyProducts = $('[data-workspace-copy-products]').checked, copyOpenings = $('[data-workspace-copy-openings]').checked;
    const copyQuantities = $('[data-workspace-copy-quantities]').checked;
    if (!copyProducts && !copyOpenings) { note(say('Elige copiar productos, traer arrastre o ambas opciones.'), 'warning'); return; }
    if (copyOpenings && !openingEditor) { note(say('Esta semana no admite arrastre inicial.'), 'warning'); return; }
    const button = $('[data-workspace-copy]'); button.disabled = true; preparingCopy = true; notifyState();
    try {
      const url = new URL(root.dataset.copyUrl, location.origin);
      if (isNew) url.searchParams.set('weekStart', root.dataset.weekStart); else url.searchParams.set('weekId', weekId); url.searchParams.set('sourceWeekId', sourceId);
      const response = await fetch(url); if (!response.ok) throw Error(say('No se pudo consultar la semana origen. Reintenta.'));
      const source = await response.json();
      if (copyOpenings) {
        await openingEditor.ready;
        await openingEditor.revalidate?.();
        const eligible = openingEditor.totals ? (source.initialBalances || []).map(row => ({ ...row, key: row.productId + ':' + row.area }))
          : openingEditor.options().filter(row => row.sourceWeekId === sourceId && row.available > 0);
        const entries = eligible.filter(row => !openingEditor.options().some(option => option.key === row.key && option.protected))
          .map(row => ({ ...row, quantity: String(row.quantity ?? row.available) }));
        await openingEditor.prepare(entries);
        const tracked = copyOpeningKeys[sourceId] ||= [];
        entries.forEach(row => { if (!tracked.some(entry => entry.key === row.key)) tracked.push({ key: row.key, productId: row.productId }); });
        openingPreviewSourceId = sourceId; openingPreviewSourceVersion = source.version; openingColumnsPrepared = true;
        applyOpeningInclusion();
      }
      if (copyProducts) {
        const items = source.rows.map((line, index) => ({ productId: line.productId, sku: line.sku, unit: line.unit, allowsDecimals: line.allowsDecimals,
          selected: true, suggestedDays: [line.day], sourceIds: { [line.day]: line.id || `row-${index}` },
          details: Object.fromEntries([...referenceKeys, 'notes'].map(key => [key, line[key] || ''])),
          days: days.map((_, day) => day === line.day && copyQuantities ? String(line.quantity) : '') }));
        attachProgram(items, source);
      }
      copySources[sourceId] = source.version;
      const status = $('[data-workspace-copy-status]');
      if (status) status.textContent = source.status === 'Open' && copyOpenings ? say(openingEditor?.totals ? 'La copia precarga el arrastre inicial. Puedes ajustar libremente cada área.' : 'La semana origen sigue abierta. Sus pendientes se validarán al guardar.')
        : source.status === 'Draft' && copyOpenings ? say('La semana origen está en borrador; solo se pueden copiar sus productos.') : '';
      invalidateReview(); render(); focusTable([...new Set([...source.rows.map(row => row.productId), ...(copyOpeningKeys[sourceId] || []).map(row => row.productId)])]); preparationResult();
      if (!source.rows.length && copyProducts && !copyOpenings) note(say('La semana origen no tiene productos activos para copiar.'), 'info');
      else if (copyOpenings && !copyProducts && !(copyOpeningKeys[sourceId] || []).length)
        note(say(source.status === 'Draft' ? 'La semana origen está en borrador; solo se pueden copiar sus productos.' : 'No hay pendientes disponibles en la semana seleccionada.'), 'info');
    } catch (error) { note(error.message || say('No se pudo preparar la copia. Reintenta sin perder los ajustes.'), 'warning'); }
    finally { button.disabled = false; preparingCopy = false; notifyState(); }
  });
  const recoverOldPreview = async () => {
    if (preview.length) {
      await resolvePrepared(preview); attachProgram(preview, previewSource);
    }
    if (openingPreview.length && openingEditor) {
      await openingEditor.ready;
      const state = openingEditor.snapshot(), values = { ...state.values };
      const tracked = copyOpeningKeys[openingPreviewSourceId] ||= [];
      openingPreview.forEach(group => {
        copyOpeningIncluded[group.productId] = group.selected !== false;
        group.entries.filter(entry => !entry.protected).forEach(entry => {
          values[entry.key] = entry.quantity;
          if (!tracked.some(row => row.key === entry.key)) tracked.push({ key: entry.key, productId: group.productId });
        });
      });
      await openingEditor.restore({ ...state, values }); applyOpeningInclusion();
    }
    preview = []; openingPreview = []; previewSource = null;
    invalidateReview(); render();
  };
  const revalidateSources = async () => {
    let changed = false;
    for (const sourceId of Object.keys(copySources)) {
      const url = new URL(root.dataset.copyUrl, location.origin);
      if (isNew) url.searchParams.set('weekStart', root.dataset.weekStart); else url.searchParams.set('weekId', weekId); url.searchParams.set('sourceWeekId', sourceId);
      const response = await fetch(url); if (!response.ok) throw Error(say('No se pudo revalidar la semana origen.'));
      const current = await response.json();
      if (current.version !== copySources[sourceId]) { if (!openingEditor?.totals) changed = true; copySources[sourceId] = current.version; }
    }
    if (openingEditor && await openingEditor.revalidate?.()) changed = true;
    if (changed) { invalidateReview(); render(); }
    return changed;
  };
  const recovery = $('[data-workspace-recovery]');
  let resumePreparation = async () => {};
  try {
    legacyText = localStorage.getItem(legacyStorageKey) || '';
    const prior = JSON.parse(localStorage.getItem(storageKey) || 'null');
    archiveLegacyText(prior);
    if (prior?.pasteText) {
      delete prior.pasteText;
      localStorage.setItem(storageKey, JSON.stringify(prior));
    }
    if (window.ProductionSchedulePreparation.inspect(prior).pending) {
      setCopyPanel(prior.copyPanelOpen ?? (prior.inputMode === 'copy' || hasCopyPreparation(prior)));
      recoveryPending = true;
      const restorePreparation = async () => {
        setCopyPanel(hasCopyPreparation(prior) || prior.copyPanelOpen === true);
        if (isNew && prior.weekStart) await window.ProductionWeekPicker.selectDate(prior.weekStart);
        excludedProducts = new Set(prior.excludedProducts || []);
        if (!prior.excludedProducts) {
          const products = new Set((prior.rows || []).map(row => row.productId));
          products.forEach(id => {
            const productRows = prior.rows.filter(row => row.productId === id);
            if (productRows.every(row => row.included === false || row.cells.some(cell => cell.copyOrigin) &&
                row.cells.filter(cell => cell.copyOrigin).every(cell => cell.copyIncluded === false))) excludedProducts.add(id);
          });
        }
        rows = prior.rows || []; preview = prior.preview || []; previewSource = prior.previewSource || null;
        skuDayTargets = prior.skuDayTargets || {}; carryEditors.clear();
        rows.forEach(row => {
          if (row.preparedKind === 'paste') row.preparedKind = 'recovered';
          if (row.productId && row.error === say('Completa las cantidades de los productos preparados antes de continuar.')) row.error = '';
        });
        saved.forEach(line => { if (!rows.some(row => row.cells.some(cell => cell.lineId === line.Id || cell.sources?.some(source => source.id === line.Id)))) addExisting(line, true); });
        openingPreview = prior.openingPreview || [];
        openingPreview.forEach(group => { group.selected ??= true; });
        openingPreviewSourceId = prior.openingPreviewSourceId || prior.previewSource?.id || $('[data-workspace-source]').value;
        if (prior.copySourceId || openingPreviewSourceId) $('[data-workspace-source]').value = prior.copySourceId || openingPreviewSourceId;
        openingPreviewSourceVersion = prior.openingPreviewSourceVersion ?? prior.previewSource?.version ?? null;
        openingColumnsPrepared = prior.openingColumnsPrepared ?? openingPreview.length > 0;
        if (openingColumnsPrepared) $('[data-workspace-copy-openings]').checked = prior.copyOpeningsChecked ?? true;
        if (prior.copyProductsChecked != null) $('[data-workspace-copy-products]').checked = prior.copyProductsChecked;
        if (prior.copyQuantitiesChecked != null) $('[data-workspace-copy-quantities]').checked = prior.copyQuantitiesChecked;
        preview.forEach(item => { if (prior.previewSource && !item.sourceProductId) item.sourceProductId = item.productId; });
        copySources = prior.copySources || {}; copyOpeningKeys = prior.copyOpeningKeys || {}; copyOpeningIncluded = prior.copyOpeningIncluded || {};
        await openingEditor?.restore(prior.openings);
        Object.values(copyOpeningKeys).flat().forEach(entry => { entry.key = openingEditor?.canonicalKey?.(entry.key) || entry.key; });
        if ($('[data-workspace-reason]')) $('[data-workspace-reason]').value = prior.reason || '';
        operationId = prior.operationId || uuid();
        continueToOpen = prior.continueToOpen || null;
        $('[data-workspace-save]').textContent = continueToOpen ? root.dataset.textContinue : root.dataset.textSave;
        lastPayload = ''; recovery.hidden = true; recovery.replaceChildren(); render();

        if (prior.pendingSent && prior.pendingPayload) {
          lastPayload = prior.pendingPayload; uncertainSave = true;
          if (isNew) {
            root.dataset.weekStart = JSON.parse(lastPayload).weekStart;
            root.dataset.targetReady = 'true'; $('[data-workspace-controls]').disabled = false;
            $('[data-workspace-pin-panel]').hidden = false;
            $('[data-workspace-pin]').disabled = false; $('[data-workspace-pin]').required = true;
            $('[data-workspace-save]').textContent = root.dataset.textSave;
          }
          $('[data-workspace-payload]').value = lastPayload; reviewPanel.hidden = false; freezePreparation();
          recoveryPending = false; persist();
          note('Reintenta la misma operación pendiente antes de preparar otra confirmación.', 'warning');
          return;
        }
        if (preview.length || openingPreview.length) await recoverOldPreview();
        else applyOpeningInclusion();
        if (!isNew && (prior.needsReconcile || prior.version !== weekVersion && window.ProductionSchedulePreparation.inspect(prior).blocksOpening)) {
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
            rows.forEach(row => row.cells.forEach((cell, day) => {
              const existing = saved.filter(line => line.ProductId === row.productId && Number(line.Day) === day);
              cell.sources = existing.map(line => ({ id: line.Id, version: line.Version, original: String(line.Quantity) }));
              cell.lineId = cell.sources[0]?.id || null; cell.version = cell.sources[0]?.version;
              cell.original = existing.length ? formatScaled(existing.reduce((sum, line) => sum + scaled(String(line.Quantity)), 0n)) : '';
            }));
            needsReconcile = false; recovery.hidden = true; recovery.replaceChildren(); invalidateReview(); render();
            note('Preparación conciliada. Revisa los cambios antes de guardar.', 'info');
          });
          recovery.append(accept);
        }
        recoveryPending = false;
        persist();
        notifyState();
      };
      resumePreparation = async () => {
        if (prior.pendingPayload && prior.operationId) {
          const url = new URL(root.dataset.operationUrl, location.origin);
          if (!isNew) url.searchParams.set('weekId', weekId);
          url.searchParams.set('operationId', prior.operationId);
          try {
            const response = await fetch(url);
            if (!response.ok) throw Error('operation');
            const status = await response.json();
            if (status.saved) {
              if (isNew) { completeNewWeek(status.url, prior); return; }
              const payload = JSON.parse(prior.pendingPayload);
              completeSave((payload.skuTotals?.length || payload.changes.length) + ((payload.initialBalances || payload.openings)?.length || 0), prior, status.version);
              return;
            }
          } catch {
            note('No se pudo comprobar el último guardado. Reintenta cuando vuelva la conexión.', 'warning');
          }
        }
        await restorePreparation();
      };
    }
  } catch {
    recoveryPending = true;
    note('No se pudo leer la preparación local.', 'warning');
    recovery.hidden = false;
    const discard = text('button', root.dataset.textDiscard, 'btn btn-outline-secondary'); discard.type = 'button';
    discard.addEventListener('click', () => { localStorage.removeItem(storageKey); recoveryPending = false; recovery.hidden = true; notifyState(); });
    recovery.append(discard);
  }
  window.addEventListener('beforeunload', event => {
    if (completedSave || submitting || discardingAll || !state().pending) return;
    event.preventDefault(); event.returnValue = '';
  });
  let targetGeneration = 0, targetSignature = '', targetLoading = false;
  const setTarget = async (date, available, force = false) => {
    if (!isNew || submitting || uncertainSave) return;
    const signature = `${date}:${available}`;
    if (!force && signature === targetSignature) return;
    targetSignature = signature;
    const generation = ++targetGeneration;
    root.dataset.targetReady = 'false';
    $('[data-workspace-controls]').disabled = true;
    invalidateReview();
    const status = $('[data-workspace-target-status]');
    if (!date || !available) {
      targetLoading = false;
      status.textContent = say('Selecciona una semana disponible para introducir el programa.');
      return;
    }
    targetLoading = true; status.textContent = say('Consultando semanas…');
    try {
      const url = new URL(root.dataset.contextUrl, location.href); url.searchParams.set('weekStart', date);
      const response = await fetch(url, { cache: 'no-store' }); if (!response.ok) throw Error();
      const context = await response.json();
      if (generation !== targetGeneration) return;
      if (context.viewUrl || !context.configurationReady) {
        status.replaceChildren(text('span', say(context.viewUrl ? 'Esta semana ya está creada.' : 'Configura las tres áreas y los dos turnos activos para capturar.')));
        if (context.viewUrl) { const link = text('a', say('Ver semana'), 'btn btn-outline-secondary ms-2'); link.href = context.viewUrl; status.append(link); }
        return;
      }
      root.dataset.weekStart = date;
      const source = $('[data-workspace-source]'), previousSource = source.value;
      source.replaceChildren(...context.sources.map(item => { const option = text('option', `${displayDate(item.weekStart)}–${displayDate(item.weekEnd)}`); option.value = item.id; return option; }));
      if ([...source.options].some(option => option.value === previousSource)) source.value = previousSource;
      source.disabled = !context.sources.length;
      $('[data-workspace-copy]').disabled = !context.sources.length;
      const openingUrl = new URL($('[data-opening-editor]').dataset.url, location.href);
      openingUrl.searchParams.delete('weekId'); openingUrl.searchParams.set('weekStart', date);
      await openingEditor?.retarget(openingUrl.href);
      if (generation !== targetGeneration) return;
      root.dataset.targetReady = 'true'; $('[data-workspace-controls]').disabled = false;
      status.textContent = say('Prepara el programa antes de confirmar la apertura.');
      render(); persist();
      if (openingEditor?.errors().length) note(openingEditor.errors().join(' '), 'warning');
    } catch {
      if (generation !== targetGeneration) return;
      status.replaceChildren(text('span', say('No se pudo consultar la semana. Reintenta sin perder la preparación.')));
      const retry = text('button', say('Reintentar'), 'btn btn-outline-secondary ms-2'); retry.type = 'button';
      retry.addEventListener('click', () => setTarget(date, available, true)); status.append(retry);
    } finally { if (generation === targetGeneration) targetLoading = false; }
  };
  window.ProductionWeekWorkspace = { state, openReview: () => review(true), setTarget,
    canChangeTarget: () => !submitting && !uncertainSave && !preparingCopy && !discardingAll };
  window.ProductionWeekWorkspace.ready = (async () => {
    // The week picker is loaded after this script; wait until it can restore the target date.
    if (document.readyState === 'loading')
      await new Promise(resolve => document.addEventListener('DOMContentLoaded', resolve, { once: true }));
    await openingEditor?.ready;
    try { await resumePreparation(); }
    catch {
      recoveryPending = true;
      note('No se pudo leer la preparación local.', 'warning');
      notifyState();
    }
  })();
  if (root.dataset.requestMode === 'copy') setCopyPanel(true);
  else setCopyPanel(copyPanelOpen);
  render();
  if (!recoveryPending && root.dataset.focusLine) {
    const row = rows.find(row => row.cells.some(cell => cell.lineId === root.dataset.focusLine));
    const day = row?.cells.findIndex(cell => cell.lineId === root.dataset.focusLine);
    if (row && day >= 0) {
      if (root.dataset.requestDelete === 'true') {
        const line = saved.find(line => line.Id === root.dataset.focusLine);
        if (line?.CanRemove === false) note(line.RemovalReason || say('Este renglón no admite eliminación.'), 'warning');
        else if (window.confirm(say('¿Eliminar {0} del {1}? Se aplicará al guardar los cambios.', row.sku, days[day]))) {
          row.cells[day].removed = true; invalidateReview(); render();
          note(say('Eliminación pendiente de guardar.'), 'warning');
          const undo = text('button', say('Deshacer'), 'btn btn-outline-secondary'); undo.type = 'button';
          undo.addEventListener('click', () => { row.cells[day].removed = false; invalidateReview(); render(); }); message.children[0].append(undo);
        }
      }
    }
  }
  const focusRequestedAction = () => {
    if (recoveryPending) return;
    if (root.dataset.focusLine) {
      const row = rows.find(row => row.cells.some(cell => cell.lineId === root.dataset.focusLine));
      const day = row?.cells.findIndex(cell => cell.lineId === root.dataset.focusLine);
      const node = [...tbody.querySelectorAll('[data-workspace-row]')].find(node => node.dataset.workspaceRow === row?.key);

      if (day >= 0) node?.querySelectorAll('input.production-week-workspace__quantity')[day]?.focus();
    } else if (root.dataset.requestAdd === 'true') search.focus();
  };
  notifyState();
  if (openingEditor) openingEditor.ready.then(() => { render(); focusRequestedAction(); });
  else focusRequestedAction();
})();
