(() => {
  const form = document.querySelector('[data-capture-group][data-matrix="true"]');
  if (!form) return;
  const entry = form.closest('[data-capture-entry]');
  const context = document.querySelector('[data-matrix-context]');
  const week = context.querySelector('[data-capture-week]');
  const shift = context.querySelector('[data-capture-shift]');
  const day = context.querySelector('[data-capture-day-value]');
  const banner = entry.querySelector('[data-draft-banner]');
  const summary = entry.querySelector('[data-draft-summary]');
  const warning = entry.querySelector('[data-draft-warning]');
  const restore = entry.querySelector('[data-draft-restore]');
  const recover = entry.querySelector('[data-draft-recover]');
  const discard = entry.querySelector('[data-draft-discard]');
  const fieldset = form.querySelector('fieldset');
  const value = name => form.querySelector(`[name="Group.${name}"]`)?.value || '';
  const key = `warehouse-epi.production-capture.v2:${entry.dataset.date}:${entry.dataset.shift}`;
  const legacyKeys = ['Cutting', 'Sewing', 'ReadyToPack'].map(area => `warehouse-epi.production-capture.v1:${entry.dataset.date}:${area}:${entry.dataset.shift}`);
  const open = entry.dataset.contextState === 'Open';
  const conflict = form.dataset.restoreConflict === 'true';
  const operationId = value('OperationId');
  let storage, pending, pendingKey, corrupt = false, leaving = false;
  const guid = value => typeof value === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
  const valid = draft => draft && [1, 2].includes(draft.version) && guid(draft.operationId) &&
    draft.date === entry.dataset.date && draft.shiftId === entry.dataset.shift && ['list', 'quick'].includes(draft.mode) &&
    Array.isArray(draft.rows) && draft.rows.length > 0 && draft.rows.length <= (draft.version === 2 ? 300 : 100) &&
    new Set(draft.rows.map(row => row?.productId)).size <= 100 &&
    new Set(draft.rows.map(row => `${row?.productId}:${draft.version === 2 ? row?.area : draft.area}`)).size === draft.rows.length &&
    (draft.version === 2 || ['Cutting', 'Sewing', 'ReadyToPack'].includes(draft.area)) &&
    draft.rows.every(row => row && guid(row.productId) && (draft.version === 1 || ['0', '1', '2'].includes(String(row.area))) &&
      typeof row.quantity === 'string' && row.quantity.length <= 128 && typeof row.notes === 'string' && row.notes.length <= 500);
  const warnStorage = () => { storage = null; form.dataset.draftSaved = 'false'; warning.textContent = warning.dataset.storageError; warning.hidden = false; };
  const snapshot = () => ({
    version: 2, operationId, date: entry.dataset.date, shiftId: entry.dataset.shift,
    mode: value('Mode'), focusArea: value('FocusArea'), onlyWithQuantity: Boolean(form.querySelector('[data-only-quantity]')?.checked),
    legacySource: value('LegacySource'), legacyOperationId: value('LegacyOperationId'),
    rows: [...form.querySelectorAll('[data-capture-cell]')].map(cell => ({
      productId: cell.closest('[data-product-row]').dataset.productRow, area: cell.dataset.area,
      quantity: cell.querySelector('[data-group-quantity]').value, notes: cell.querySelector('input[name$=".Notes"]').value
    })).filter(row => row.quantity.trim() || row.notes.trim())
  });
  const save = () => {
    if (!storage || pending || corrupt || conflict || !open || leaving) return;
    try {
      const draft = snapshot();
      if (draft.rows.length) storage.setItem(key, JSON.stringify(draft));
      else storage.removeItem(key);
      form.dataset.draftSaved = 'true';
    } catch { warnStorage(); }
  };
  const clearLegacy = draft => {
    if (!legacyKeys.includes(draft?.legacySource) || !guid(draft.legacyOperationId)) return;
    let old;
    try { old = JSON.parse(storage.getItem(draft.legacySource) || 'null'); }
    catch { return; }
    if (old?.operationId === draft.legacyOperationId) storage.removeItem(draft.legacySource);
  };
  try {
    storage = window.sessionStorage;
    storage.setItem(`${key}:probe`, '1'); storage.removeItem(`${key}:probe`);
    const receipt = entry.querySelector('[data-receipt-operation]')?.dataset.receiptOperation;
    // A legacy receipt may be recovered directly, before conversion to the matrix.
    if (receipt) for (const oldKey of legacyKeys) {
      try { if (JSON.parse(storage.getItem(oldKey) || 'null')?.operationId === receipt) storage.removeItem(oldKey); } catch { /* Keep corrupt drafts available for explicit discard. */ }
    }
    pendingKey = storage.getItem(key) ? key : legacyKeys.find(oldKey => storage.getItem(oldKey));
    if (pendingKey) {
      try { pending = JSON.parse(storage.getItem(pendingKey)); corrupt = !valid(pending); } catch { corrupt = true; }
      if (!corrupt && receipt === pending.operationId) { clearLegacy(pending); storage.removeItem(pendingKey); pending = null; pendingKey = null; }
    }
    if (form.dataset.serverPost === 'true' && !conflict && open &&
        (pending?.operationId === operationId || pendingKey === value('LegacySource'))) {
      pending = null; pendingKey = null;
    }
  } catch { warnStorage(); }
  const render = () => {
    banner.hidden = !pending && !corrupt;
    if (pending || corrupt) {
      if (fieldset) fieldset.disabled = true;
      const areaLabel = { Cutting: entry.dataset.cuttingLabel, Sewing: entry.dataset.sewingLabel, ReadyToPack: entry.dataset.readyLabel };
      summary.textContent = `${entry.dataset.contextLabel}${pending?.version === 1 ? ` · ${areaLabel[pending.area] || pending.area}` : ''}`;
      recover.disabled = corrupt;
      if (corrupt) { warning.textContent = warning.dataset.corrupt; warning.hidden = false; }
    }
  };
  render();
  restore.addEventListener('submit', event => {
    if (!valid(pending) || leaving) { event.preventDefault(); return; }
    const fields = restore.querySelector('[data-draft-fields]'); fields.replaceChildren();
    const add = (name, value) => { const input = document.createElement('input'); input.type = 'hidden'; input.name = `Group.${name}`; input.value = value ?? ''; fields.append(input); };
    add('AllAreas', String(pending.version === 2)); add('ImportLegacy', String(pending.version === 1));
    add('OperationId', pending.operationId); add('Date', pending.date); add('ShiftId', pending.shiftId);
    add('Mode', pending.mode); add('OnlyWithQuantity', String(Boolean(pending.onlyWithQuantity)));
    add('FocusArea', ['0', '1', '2'].includes(pending.focusArea) ? pending.focusArea : '0');
    if (pending.version === 1) add('Area', pending.area);
    else if (legacyKeys.includes(pending.legacySource)) { add('LegacySource', pending.legacySource); add('LegacyOperationId', pending.legacyOperationId); }
    pending.rows.forEach((row, i) => {
      add(`Rows[${i}].ProductId`, row.productId); add(`Rows[${i}].Quantity`, row.quantity); add(`Rows[${i}].Notes`, row.notes);
      if (pending.version === 2) add(`Rows[${i}].Area`, row.area);
    });
    leaving = true;
  });
  discard.addEventListener('click', () => {
    try { if (pendingKey === key && !corrupt) clearLegacy(pending); storage?.removeItem(pendingKey || key); }
    catch { warnStorage(); return; }
    leaving = true;
    window.location.assign(`${window.location.pathname}?${new URLSearchParams({ Tab: 'capture', Day: entry.dataset.date, ShiftId: entry.dataset.shift })}`);
  });
  form.addEventListener('input', save); form.addEventListener('change', save);
  form.addEventListener('submit', event => {
    if (event.defaultPrevented || form.dataset.submitting) return;
    save(); queueMicrotask(() => { leaving = !event.defaultPrevented; });
  }, true);
  const original = { week: week.value, shift: shift.value, day: day.value };
  context.addEventListener('submit', event => {
    save();
    queueMicrotask(() => {
      leaving = !event.defaultPrevented;
      if (!leaving) { week.value = original.week; shift.value = original.shift; day.value = original.day; }
    });
  }, true);
  week.addEventListener('change', () => {
    const start = week.selectedOptions[0]?.dataset.start;
    if (!start) { week.value = original.week; return; }
    const old = new Date(`${day.value}T12:00:00Z`);
    const date = new Date(`${start}T12:00:00Z`);
    date.setUTCDate(date.getUTCDate() + (old.getUTCDay() + 6) % 7);
    day.value = date.toISOString().slice(0, 10); context.requestSubmit();
  });
  shift.addEventListener('change', () => context.requestSubmit());
  context.querySelectorAll('[data-capture-day]').forEach(button => button.addEventListener('click', () => {
    day.value = button.dataset.captureDay; context.requestSubmit();
  }));
  document.querySelectorAll('[data-capture-context-link]').forEach(link => link.addEventListener('click', event => {
    save(); queueMicrotask(() => { leaving = !event.defaultPrevented; });
  }, true));
  window.addEventListener('beforeunload', event => {
    if (leaving) return;
    save();
    if (!storage && snapshot().rows.length) { event.preventDefault(); event.returnValue = ''; }
  });
  window.addEventListener('pageshow', () => { leaving = false; });
  if (form.dataset.serverPost === 'true') save();
  else if (pending?.version === 2 && !corrupt && !entry.querySelector('[data-receipt-operation]')) restore.requestSubmit(recover);
})();
