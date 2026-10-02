(() => {
  const entry = document.querySelector('[data-capture-entry]');
  const form = document.querySelector('[data-capture-group]');
  if (!entry || !form || form.dataset.matrix === 'true') return;
  const banner = entry.querySelector('[data-draft-banner]');
  const summary = entry.querySelector('[data-draft-summary]');
  const warning = entry.querySelector('[data-draft-warning]');
  const restore = entry.querySelector('[data-draft-restore]');
  const recover = entry.querySelector('[data-draft-recover]');
  const discard = entry.querySelector('[data-draft-discard]');
  const fieldset = form.querySelector('fieldset');
  const key = `warehouse-epi.production-capture.v1:${entry.dataset.date}:${entry.dataset.area}:${entry.dataset.shift}`;
  const open = entry.dataset.contextState === 'Open';
  const conflict = form.dataset.restoreConflict === 'true';
  const value = name => form.querySelector(`[name="Group.${name}"]`)?.value || '';
  const operation = value('OperationId');
  let storage, pending = null, corrupt = false, leaving = false;
  const warn = message => { warning.textContent = message; warning.hidden = false; };
  const storageFailed = () => {
    storage = null;
    form.dataset.draftSaved = 'false';
    warn(warning.dataset.storageError);
  };
  const isGuid = text => typeof text === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(text);
  const valid = draft => draft && draft.version === 1 && isGuid(draft.operationId) &&
    draft.date === entry.dataset.date && draft.area === entry.dataset.area && draft.shiftId === entry.dataset.shift &&
    ['list', 'quick'].includes(draft.mode) && typeof draft.onlyWithQuantity === 'boolean' &&
    Array.isArray(draft.rows) && draft.rows.length > 0 && draft.rows.length <= 100 &&
    new Set(draft.rows.map(row => row?.productId)).size === draft.rows.length &&
    draft.rows.every(row => row && isGuid(row.productId) && typeof row.quantity === 'string' && row.quantity.length <= 128 &&
      typeof row.notes === 'string' && row.notes.length <= 500);
  const snapshot = () => ({
    version: 1, operationId: operation, date: entry.dataset.date, area: entry.dataset.area,
    shiftId: entry.dataset.shift, mode: value('Mode') === 'quick' ? 'quick' : 'list',
    onlyWithQuantity: Boolean(form.querySelector('[data-only-quantity]')?.checked),
    rows: [...form.querySelectorAll('[data-product-row]')].map(row => ({
      productId: row.dataset.productRow,
      quantity: row.querySelector('[data-group-quantity]')?.value || '',
      notes: row.querySelector('input[name$=".Notes"]')?.value || ''
    })).filter(row => row.quantity.trim() || row.notes.trim())
  });
  const hasChanges = () => snapshot().rows.length > 0;
  const save = () => {
    if (!storage || pending || corrupt || conflict || !open || leaving) return;
    try {
      const draft = snapshot();
      if (draft.rows.length) storage.setItem(key, JSON.stringify(draft));
      else storage.removeItem(key);
      form.dataset.draftSaved = 'true';
    } catch { storageFailed(); }
  };
  try {
    storage = window.sessionStorage;
    // A read may succeed while writes are blocked (private mode / quota).
    const probe = `${key}:probe`;
    storage.setItem(probe, '1');
    storage.removeItem(probe);
    const raw = storage.getItem(key);
    if (raw) {
      try { pending = JSON.parse(raw); corrupt = !valid(pending); }
      catch { corrupt = true; }
    }
    const receipt = entry.querySelector('[data-receipt-operation]');
    if (!corrupt && pending && receipt?.dataset.receiptOperation === pending.operationId) {
      storage.removeItem(key);
      pending = null;
    }
    if (!corrupt && pending && form.dataset.serverPost === 'true' && pending.operationId === operation && open && !conflict)
      pending = null;
  } catch { storageFailed(); }

  const renderPending = () => {
    banner.hidden = !pending && !corrupt;
    if (pending || corrupt) {
      if (fieldset) fieldset.disabled = true;
      summary.textContent = `${entry.dataset.contextLabel}${corrupt ? '' : ` · ${pending.rows.length} SKU`}`;
      recover.disabled = corrupt;
      if (corrupt) warn(warning.dataset.corrupt);
    }
  };
  renderPending();
  discard.addEventListener('click', () => {
    try { storage?.removeItem(key); }
    catch { storageFailed(); return; }
    pending = null;
    corrupt = false;
    banner.hidden = true;
    warning.hidden = Boolean(storage);
    // A conflicted server response remains read-only; start again with a fresh GET.
    if (conflict) {
      leaving = true;
      const url = new URL(window.location.href);
      url.search = new URLSearchParams({ Tab: 'capture', Day: entry.dataset.date, Area: entry.dataset.area, ShiftId: entry.dataset.shift }).toString();
      window.location.assign(url.href);
      return;
    }
    if (fieldset) fieldset.disabled = !open;
    form.querySelector('[data-group-quantity]')?.focus();
  });
  restore.addEventListener('submit', event => {
    if (!valid(pending) || leaving) { event.preventDefault(); return; }
    const fields = restore.querySelector('[data-draft-fields]');
    fields.replaceChildren();
    const add = (name, content) => {
      const input = document.createElement('input');
      input.type = 'hidden'; input.name = `Group.${name}`; input.value = content;
      fields.append(input);
    };
    add('OperationId', pending.operationId); add('Date', pending.date); add('Area', pending.area);
    add('ShiftId', pending.shiftId); add('Mode', pending.mode); add('OnlyWithQuantity', String(pending.onlyWithQuantity));
    pending.rows.forEach((row, index) => {
      add(`Rows[${index}].ProductId`, row.productId);
      add(`Rows[${index}].Quantity`, row.quantity);
      add(`Rows[${index}].Notes`, row.notes);
    });
    leaving = true;
  });
  form.addEventListener('input', save);
  form.addEventListener('change', save);
  form.addEventListener('submit', event => {
    if (event.defaultPrevented || form.dataset.submitting) return;
    save();
    // Later validation listeners can still cancel this submission.
    queueMicrotask(() => { leaving = !event.defaultPrevented; });
  }, true);
  document.querySelector('[data-context-form]')?.addEventListener('submit', event => {
    save();
    // The existing leave guard can still cancel when storage is unavailable.
    queueMicrotask(() => { leaving = !event.defaultPrevented; });
  }, true);
  document.querySelectorAll('[data-capture-context-link]').forEach(link => link.addEventListener('click', event => {
    save();
    queueMicrotask(() => { leaving = !event.defaultPrevented; });
  }, true));
  window.addEventListener('beforeunload', event => {
    if (leaving) return;
    save();
    if (!storage && hasChanges()) { event.preventDefault(); event.returnValue = ''; }
  });
  window.addEventListener('pageshow', () => { leaving = false; });
  if (form.dataset.serverPost === 'true') save();
})();
