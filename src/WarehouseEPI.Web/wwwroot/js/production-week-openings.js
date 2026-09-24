(() => {
  const controllers = new WeakMap();
  const areas = ['Corte', 'Costura', 'Ready to Pack'];
  const key = row => `${row.sourceWeekId}:${row.sourceLineId}:${row.area}`;
  const normalized = value => String(value || '0').trim().replace(',', '.');
  const node = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value; if (cls) el.className = cls; return el; };
  document.querySelectorAll('[data-opening-editor]').forEach(root => {
    const body = root.querySelector('[data-opening-rows]'), status = root.querySelector('[data-opening-status]');
    const panel = root.querySelector('[data-opening-panel]'), summary = root.querySelector('[data-opening-summary]');
    let options = [], values = {}, restored = null, restoredSources = {}, loaded = false, loadFailed = false;
    const readOnly = root.dataset.readonly === 'true';
    const notify = () => root.dispatchEvent(new CustomEvent('openingchange', { bubbles: true }));
    const changes = () => options.filter(row => Number(normalized(values[key(row)])) !== row.selected || row.needsReview)
      .map(row => ({ sourceWeekId: row.sourceWeekId, sourceLineId: row.sourceLineId, area: row.area,
        quantity: normalized(values[key(row)]), expectedFingerprint: row.fingerprint, sku: row.sku, before: row.selected, unit: row.unit }));
    const errors = () => {
      if (!loaded) return ['Espera a que se consulten los pendientes de arrastre.'];
      if (loadFailed) return ['No se pudieron consultar los pendientes. Actualiza antes de guardar.'];
      const result = [];
      for (const row of options) {
        const value = normalized(values[key(row)]);
        if (!/^\d{1,14}(\.\d{1,4})?$/.test(value) || Number(value) > row.available || (!row.allowsDecimals && !Number.isInteger(Number(value))))
          result.push(`${row.sku} · ${areas[row.area]}: usa una cantidad entre 0 y ${row.available} ${row.unit}.`);
      }
      if (restored && Object.keys(restored).some(k => !options.some(row => key(row) === k) && Number(restored[k]) > 0))
        result.push('Un origen recuperado ya no está disponible. Descarta esa preparación y revisa el arrastre.');
      return result;
    };
    const updateSummary = () => {
      if (!summary) return;
      const selected = options.filter(row => Number(normalized(values[key(row)])) > 0).length;
      summary.textContent = `Arrastre inicial · ${selected} ${selected === 1 ? 'selección' : 'selecciones'} de ${options.length} pendientes`;
    };
    const render = () => {
      body.replaceChildren();
      const groups = new Map();
      for (const option of options) {
        const id = `${option.sourceWeekId}:${option.sourceLineId}`;
        if (!groups.has(id)) groups.set(id, []);
        groups.get(id).push(option);
      }
      for (const group of groups.values()) {
        const first = group[0], tr = document.createElement('tr');
        tr.append(node('th', first.sku), node('td', `${first.sourceStart} · renglón ${first.sequence}${first.provisional ? ' · Provisional' : ''}`), node('td', first.unit));
        areas.forEach((area, index) => {
          const td = document.createElement('td'), row = group.find(x => x.area === index); td.dataset.label = area;
          if (!row) { td.textContent = '—'; tr.append(td); return; }
          const input = document.createElement('input'); input.className = 'form-control production-opening-quantity'; input.inputMode = 'decimal';
          input.value = values[key(row)] || ''; input.readOnly = readOnly; input.placeholder = '0';
          input.setAttribute('aria-label', `${first.sku} · ${first.sourceStart} · ${area}`);
          const undo = node('button', 'Deshacer', 'btn btn-link'); undo.type = 'button'; undo.disabled = readOnly; undo.style.minHeight = '44px';
          input.addEventListener('keydown', event => {
            if (event.key !== 'Enter') return;
            event.preventDefault(); const inputs = [...body.querySelectorAll('input')];
            inputs[inputs.indexOf(input) + 1]?.focus();
          });
          input.addEventListener('input', () => { values[key(row)] = input.value; updateSummary(); notify(); });
          undo.addEventListener('click', () => { values[key(row)] = row.selected ? String(row.selected) : ''; input.value = values[key(row)]; updateSummary(); notify(); });
          td.append(input, node('small', `Disponible: ${row.available} ${row.unit}`, 'd-block text-body-secondary'), undo);
          if (row.needsReview) td.append(node('small', 'Origen actualizado: revisa esta selección.', 'd-block text-warning-emphasis'));
          tr.append(td);
        });
        body.append(tr);
      }
      for (const id of Object.keys(restored || {}).filter(id => !options.some(row => key(row) === id) && Number(restored[id]) > 0)) {
        const tr = document.createElement('tr'), td = node('td', `Origen recuperado no disponible: ${id} · Cantidad: ${restored[id]}. `);
        td.colSpan = 6;
        const remove = node('button', 'Quitar esta selección recuperada', 'btn btn-outline-danger'); remove.type = 'button'; remove.disabled = readOnly;
        remove.addEventListener('click', () => { delete restored[id]; delete values[id]; render(); notify(); });
        td.append(remove); tr.append(td); body.append(tr);
      }
      status.textContent = options.length ? 'Solo las cantidades seleccionadas entrarán al saldo inicial.' : 'No hay pendientes anteriores disponibles. Arrastre inicial: cero.';
      for (const row of options) {
        const previous = restoredSources[key(row)];
        if (previous && previous.fingerprint !== row.fingerprint && Number(values[key(row)]) > 0)
          status.textContent += ` ${row.sku} · ${areas[row.area]}: el origen cambió; disponible anterior ${previous.available} → actual ${row.available} ${row.unit}. Revisa la selección.`;
      }
      updateSummary();
    };
    const ready = fetch(root.dataset.url).then(response => { if (!response.ok) throw new Error(); return response.json(); })
      .then(data => { options = data; values = Object.fromEntries(options.map(row => [key(row), row.selected ? String(row.selected) : '']));
        if (restored) Object.assign(values, restored); loaded = true; render(); })
      .catch(() => { loaded = true; loadFailed = true; status.textContent = 'No se pudieron consultar los pendientes. Actualiza la página.'; if (panel) panel.open = true; });
    const prepare = async entries => {
      await ready;
      if (loadFailed) throw new Error('No se pudieron consultar los pendientes. Actualiza la página.');
      if (readOnly) throw new Error('Esta semana no permite preparar arrastres.');
      const response = await fetch(root.dataset.url);
      if (!response.ok) throw new Error('No se pudieron revalidar los pendientes. Reintenta.');
      const current = await response.json();
      const byKey = new Map(current.map(row => [key(row), row]));
      const pending = [];
      for (const entry of entries) {
        const row = byKey.get(entry.key);
        const original = options.find(option => key(option) === entry.key);
        const quantity = normalized(entry.quantity);
        if (!row || !original || row.selected !== original.selected || row.fingerprint !== entry.fingerprint ||
            !/^\d{1,14}(\.\d{1,4})?$/.test(quantity) ||
            Number(quantity) > row.available || (!row.allowsDecimals && !Number.isInteger(Number(quantity))))
          throw new Error('El pendiente de origen cambió. Prepara de nuevo el arrastre.');
        if (original?.selected > 0 || String(values[entry.key] ?? '').trim() !== '') continue;
        pending.push([entry.key, entry.quantity]);
      }
      pending.forEach(([id, quantity]) => { values[id] = String(quantity); });
      if (pending.length) { render(); notify(); if (panel) panel.open = true; }
      return pending.length;
    };
    const api = { ready, changes, errors, snapshot: () => ({ values: { ...values }, sources: Object.fromEntries(options.map(row => [key(row), { fingerprint: row.fingerprint, available: row.available }])) }),
      options: () => { if (loadFailed) throw new Error('No se pudieron consultar los pendientes. Actualiza la página.'); return options.map(row => ({ ...row, key: key(row),
        prepared: Number(normalized(values[key(row)])), protected: row.selected > 0 || String(values[key(row)] ?? '').trim() !== '' })); },
      prepare, reveal: () => { if (panel) panel.open = true; },
      restore: state => { restored = state?.values || state || {}; restoredSources = state?.sources || {}; Object.assign(values, restored); if (loaded) render(); if (panel && Object.values(restored).some(value => Number(normalized(value)) > 0)) panel.open = true; },
      describe: change => `${change.sku} · ${areas[change.area]} · Arrastre inicial: ${change.before} → ${change.quantity} ${change.unit}` };
    controllers.set(root, api);
    root.querySelector('[data-opening-refresh]')?.addEventListener('click', () => { notify(); location.reload(); });
    if (root.dataset.correction !== 'true') return;
    const storageKey = `warehouse-epi:opening:${root.dataset.userId}:${root.dataset.weekId}`;
    const reason = root.querySelector('[data-opening-reason]'), pin = root.querySelector('[data-opening-pin]');
    const preview = root.querySelector('[data-opening-preview]'), auth = root.querySelector('[data-opening-auth]');
    const reviewButton = root.querySelector('[data-opening-review]'), confirmButton = root.querySelector('[data-opening-confirm]');
    let pending = null, sending = false, uncertain = false, completed = false;
    const lock = () => {
      body.querySelectorAll('input, button').forEach(el => { el.disabled = sending || uncertain; });
      reason.disabled = sending || uncertain; reviewButton.disabled = sending || uncertain;
      const refresh = root.querySelector('[data-opening-refresh]'); if (refresh) refresh.disabled = sending || uncertain;
    };
    const finish = () => { completed = true; pending = null; localStorage.removeItem(storageKey); location.reload(); };
    const persist = () => { try { localStorage.setItem(storageKey, JSON.stringify({ values, sources: api.snapshot().sources, reason: reason.value, pending, version: root.dataset.weekVersion, updated: new Date().toISOString() })); }
      catch { status.textContent = 'La recuperación local no está disponible. No cierres la página.'; } };
    const invalidate = () => { if (uncertain) return; pending = null; auth.hidden = true; preview.hidden = true; persist(); };
    root.addEventListener('openingchange', invalidate); reason.addEventListener('input', invalidate);
    const post = async (url, payload) => {
      const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json',
        RequestVerificationToken: root.querySelector('input[name="__RequestVerificationToken"]').value }, body: JSON.stringify(payload) });
      if (!response.ok) throw new Error(); return response.json();
    };
    reviewButton.addEventListener('click', async () => {
      if (sending || uncertain) return;
      await ready;
      const issues = errors();
      if (issues.length) { status.textContent = issues.join(' '); return; }
      pending = { operationId: crypto.randomUUID(), weekId: root.dataset.weekId, expectedWeekVersion: Number(root.dataset.weekVersion),
        changes: changes(), reason: reason.value, fingerprint: '' };
      sending = true; lock();
      try {
        const result = await post(root.dataset.reviewUrl, pending);
        if (!result.canConfirm) { status.textContent = result.errors.join(' '); pending = null; return; }
        pending.fingerprint = result.fingerprint; preview.replaceChildren();
        pending.changes.forEach(change => preview.append(node('p', api.describe(change))));
        for (const after of result.after.products) {
          const before = result.before.products.find(x => x.productId === after.productId);
          const summary = ['cutting', 'sewing', 'readyToPack'].map((area, index) => `${areas[index]}: ${before?.[area]?.opening || 0} → ${after[area].opening}`).join(' · ');
          preview.append(node('p', `${after.sku} · Saldo inicial: ${summary}`, 'small'));
        }
        for (const after of result.afterClose?.products || []) {
          const before = result.beforeClose.products.find(x => x.productId === after.productId);
          const summary = ['cutting', 'sewing', 'readyToPack'].map((area, index) => `${areas[index]}: ${before?.[area]?.pending || 0} → ${after[area].pending}`).join(' · ');
          preview.append(node('p', `${after.sku} · Pendiente al cierre: ${summary}`, 'small'));
        }
        preview.hidden = false; auth.hidden = false; persist();
      } catch { status.textContent = 'No se pudo revisar. Se conservan los datos.'; }
      finally { sending = false; lock(); }
    });
    confirmButton.addEventListener('click', async () => {
      if (!pending || sending) return;
      sending = true; lock(); confirmButton.disabled = true; const secret = pin.value; pin.value = '';
      try {
        const result = await post(root.dataset.confirmUrl, { ...pending, pin: secret });
        if (result.success) { finish(); return; }
        status.textContent = (result.errors || ['No se guardó. Revisa nuevamente.']).join(' ');
        uncertain = false;
        if (result.status !== 5) { pending = null; auth.hidden = true; }
      } catch {
        uncertain = true; status.textContent = 'Respuesta pendiente. Reintenta la misma confirmación; no se duplicará el arrastre.';
        const url = new URL(root.dataset.operationUrl, location.origin); url.searchParams.set('operationId', pending.operationId);
        try { const result = await fetch(url).then(r => r.json()); if (result.saved) { finish(); return; } } catch { /* Keep the same operation. */ }
      } finally { sending = false; confirmButton.disabled = false; lock(); if (!completed) persist(); }
    });
    try {
      const old = JSON.parse(localStorage.getItem(storageKey) || 'null');
      if (old) ready.then(() => {
        const recovery = node('div', `Preparación local del ${new Date(old.updated).toLocaleString()}. `, 'alert alert-info');
        const restore = node('button', 'Recuperar', 'btn btn-primary me-2'), discard = node('button', 'Descartar', 'btn btn-outline-secondary');
        restore.type = discard.type = 'button';
        restore.addEventListener('click', async () => {
          api.restore({ values: old.values || {}, sources: old.sources }); reason.value = old.reason || ''; recovery.remove();
          if (old.pending) {
            pending = old.pending; uncertain = true; auth.hidden = false; lock();
            status.textContent = 'Comprobando la operación anterior. Reintenta la misma confirmación si sigue pendiente.';
            const url = new URL(root.dataset.operationUrl, location.origin); url.searchParams.set('operationId', pending.operationId);
            try { const result = await fetch(url).then(r => r.json()); if (result.saved) { finish(); return; } } catch { /* Same operation remains pending. */ }
            persist();
          } else { notify(); status.textContent += ' Preparación recuperada. Revisa los valores actuales antes de confirmar.'; }
        });
        discard.addEventListener('click', () => { localStorage.removeItem(storageKey); recovery.remove(); });
        recovery.append(restore, discard); root.append(recovery);
      });
    } catch { status.textContent = 'No se pudo recuperar la preparación local.'; }
    window.addEventListener('beforeunload', event => { if (!completed && (changes().length || uncertain)) { event.preventDefault(); event.returnValue = ''; } });
  });
  window.ProductionWeekOpenings = { get: root => controllers.get(root) };
})();
