(() => {
  const translate = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  const controllers = new WeakMap();
  const areas = [translate("Corte"), translate("Costura"), translate("Ready to Pack")];
  const key = row => `${row.sourceWeekId}:${row.sourceLineId}:${row.area}`;
  const normalized = value => String(value || '0').trim().replace(',', '.');
  const node = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value; if (cls) el.className = cls; return el; };
  const displayDate = value => {
    const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value || ''));
    return match ? `${match[3]}/${match[2]}/${match[1]}` : String(value || '');
  };
  document.querySelectorAll('[data-opening-editor]').forEach(root => {
    if (root.dataset.totals === 'true') return;
    const body = root.querySelector('[data-opening-rows]'), status = root.querySelector('[data-opening-status]');
    const panel = root.querySelector('[data-opening-panel]'), summary = root.querySelector('[data-opening-summary]');
    let options = [], values = {}, restored = null, restoredSources = {}, loaded = false, loadFailed = false, targetGeneration = 0;
    const excluded = new Set();
    const effective = row => excluded.has(key(row)) ? String(row.selected || 0) : normalized(values[key(row)]);
    const readOnly = root.dataset.readonly === 'true';
    const notify = () => root.dispatchEvent(new CustomEvent('openingchange', { bubbles: true }));
    const changes = () => options.filter(row => Number(effective(row)) !== row.selected || row.needsReview && !excluded.has(key(row)))
      .map(row => ({ sourceWeekId: row.sourceWeekId, sourceLineId: row.sourceLineId, area: row.area,
        quantity: effective(row), expectedFingerprint: row.fingerprint, sku: row.sku, before: row.selected, unit: row.unit }));
    const errors = () => {
      if (!loaded) return [translate("Espera a que se consulten los pendientes de arrastre.")];
      if (loadFailed) return [translate("No se pudieron consultar los pendientes. Actualiza antes de guardar.")];
      const result = [];
      for (const row of options) {
        const value = effective(row);
        if (!/^\d{1,14}(\.\d{1,4})?$/.test(value) || Number(value) > row.available || (!row.allowsDecimals && !Number.isInteger(Number(value))))
          result.push(translate("{0} · {1}: usa una cantidad entre 0 y {2} {3}.", row.sku, areas[row.area], row.available, row.unit));
      }
      if (restored && Object.keys(restored).some(k => !options.some(row => key(row) === k) && Number(restored[k]) > 0))
        result.push(translate("Un origen recuperado ya no está disponible. Descarta esa preparación y revisa el arrastre."));
      return result;
    };
    const updateSummary = () => {
      if (!summary) return;
      const selected = options.filter(row => Number(normalized(values[key(row)])) > 0).length;
      if (root.dataset.integrated === 'true') root.hidden = !selected && !changes().length && !errors().length;
      summary.textContent = root.dataset.integrated === 'true' ? translate("Ver arrastre incorporado ({0})", selected)
        : translate(selected === 1 ? "Arrastre inicial · {0} selección de {1} pendientes" : "Arrastre inicial · {0} selecciones de {1} pendientes", selected, options.length);
    };
    const render = () => {
      if (root.dataset.integrated === 'true') {
        status.textContent = errors().join(' ');
        root.hidden = true;
        return;
      }
      body.replaceChildren();
      const groups = new Map();
      for (const option of options) {
        const id = `${option.sourceWeekId}:${option.sourceLineId}`;
        if (!groups.has(id)) groups.set(id, []);
        groups.get(id).push(option);
      }
      const groupList = [...groups.values()];
      const counts = new Map(), positions = new Map();
      groupList.forEach(group => {
        const id = `${group[0].sourceWeekId}:${group[0].sku}`;
        counts.set(id, (counts.get(id) || 0) + 1);
      });
      for (const group of groupList) {
        const first = group[0], tr = document.createElement('tr');
        const id = `${first.sourceWeekId}:${first.sku}`;
        const position = (positions.get(id) || 0) + 1; positions.set(id, position);
        const source = translate("Desde semana {0}", displayDate(first.sourceStart));
        const original = first.originalStart && first.originalStart !== first.sourceStart
          ? translate(" · origen inicial {0}", displayDate(first.originalStart)) : '';
        const distinction = counts.get(id) > 1 ? translate(" · pendiente {0} de {1}", position, counts.get(id)) : '';
        const origin = `${source}${original}${distinction}${first.provisional ? translate(" · Provisional") : ''}`;
        tr.append(node('th', first.sku), node('td', origin), node('td', first.unit));
        areas.forEach((area, index) => {
          const td = document.createElement('td'), row = group.find(x => x.area === index); td.dataset.label = area;
          if (!row) { td.textContent = '—'; tr.append(td); return; }
          const input = document.createElement('input'); input.className = 'form-control production-opening-quantity'; input.inputMode = 'decimal';
          input.value = values[key(row)] || ''; input.readOnly = readOnly; input.placeholder = '0';
          input.setAttribute('aria-label', `${first.sku} · ${origin} · ${area}`);
          const undo = node('button', translate("Deshacer"), 'btn btn-link'); undo.type = 'button'; undo.disabled = readOnly; undo.style.minHeight = '44px';
          input.addEventListener('keydown', event => {
            if (event.key !== 'Enter') return;
            event.preventDefault(); const inputs = [...body.querySelectorAll('input')];
            inputs[inputs.indexOf(input) + 1]?.focus();
          });
          input.addEventListener('input', () => { values[key(row)] = input.value; updateSummary(); notify(); });
          undo.addEventListener('click', () => { values[key(row)] = row.selected ? String(row.selected) : ''; input.value = values[key(row)]; updateSummary(); notify(); });
          td.append(input, node('small', translate("Disponible: {0} {1}", row.available, row.unit), 'd-block text-body-secondary'), undo);
          if (row.needsReview) td.append(node('small', translate("Origen actualizado: revisa esta selección."), 'd-block text-warning-emphasis'));
          tr.append(td);
        });
        body.append(tr);
      }
      for (const id of Object.keys(restored || {}).filter(id => !options.some(row => key(row) === id) && Number(restored[id]) > 0)) {
        const tr = document.createElement('tr'), td = node('td', root.dataset.integrated === 'true'
          ? translate("Origen recuperado no disponible · Cantidad: {0}. ", restored[id])
          : translate("Origen recuperado no disponible: {0} · Cantidad: {1}. ", id, restored[id]));
        td.colSpan = 6;
        const remove = node('button', translate("Quitar esta selección recuperada"), 'btn btn-outline-danger'); remove.type = 'button'; remove.disabled = readOnly;
        remove.addEventListener('click', () => { delete restored[id]; delete values[id]; render(); notify(); });
        td.append(remove); tr.append(td); body.append(tr);
      }
      status.textContent = options.length ? translate("Solo las cantidades seleccionadas entrarán al saldo inicial.") : translate("No hay pendientes anteriores disponibles. Arrastre inicial: cero.");
      for (const row of options) {
        const previous = restoredSources[key(row)];
        if (previous && previous.fingerprint !== row.fingerprint && Number(values[key(row)]) > 0)
          status.textContent += translate(" {0} · {1}: el origen cambió; disponible anterior {2} → actual {3} {4}. Revisa la selección.", row.sku, areas[row.area], previous.available, row.available, row.unit);
      }
      updateSummary();
    };
    const ready = fetch(root.dataset.url).then(response => { if (!response.ok) throw new Error(); return response.json(); })
      .then(data => { options = data; values = Object.fromEntries(options.map(row => [key(row), row.selected ? String(row.selected) : '']));
        if (restored) Object.assign(values, restored); loaded = true; render(); })
      .catch(() => { loaded = true; loadFailed = true; root.hidden = false; status.textContent = translate("No se pudieron consultar los pendientes. Actualiza la página."); if (panel) panel.open = true; });
    const prepare = async entries => {
      await ready;
      const generation = targetGeneration, targetUrl = root.dataset.url;
      if (loadFailed) throw new Error(translate("No se pudieron consultar los pendientes. Actualiza la página."));
      if (readOnly) throw new Error(translate("Esta semana no permite preparar arrastres."));
      const response = await fetch(targetUrl);
      if (!response.ok) throw new Error(translate("No se pudieron revalidar los pendientes. Reintenta."));
      const current = await response.json();
      if (generation !== targetGeneration || targetUrl !== root.dataset.url) throw new Error(translate("El pendiente de origen cambió. Prepara de nuevo el arrastre."));
      const byKey = new Map(current.map(row => [key(row), row]));
      const pending = [];
      for (const entry of entries) {
        const row = byKey.get(entry.key);
        const original = options.find(option => key(option) === entry.key);
        const quantity = normalized(entry.quantity);
        if (!row || !original || row.selected !== original.selected || row.fingerprint !== entry.fingerprint ||
            !/^\d{1,14}(\.\d{1,4})?$/.test(quantity) ||
            Number(quantity) > row.available || (!row.allowsDecimals && !Number.isInteger(Number(quantity))))
          throw new Error(translate("El pendiente de origen cambió. Prepara de nuevo el arrastre."));
        if (original?.selected > 0 || String(values[entry.key] ?? '').trim() !== '') continue;
        pending.push([entry.key, entry.quantity]);
      }
      pending.forEach(([id, quantity]) => { values[id] = String(quantity); });
      if (pending.length) { render(); notify(); if (panel && root.dataset.integrated !== 'true') panel.open = true; }
      return pending.length;
    };
    const scale = value => {
      const raw = normalized(value);
      if (!/^\d{1,14}(\.\d{1,4})?$/.test(raw)) return null;
      const [integer, fraction = ''] = raw.split('.');
      return BigInt(integer) * 10000n + BigInt(fraction.padEnd(4, '0'));
    };
    const unscale = value => {
      const fraction = String(value % 10000n).padStart(4, '0').replace(/0+$/, '');
      return `${value / 10000n}${fraction ? '.' + fraction : ''}`;
    };
    const groups = () => {
      const result = new Map();
      options.filter(row => row.selected > 0 || Object.hasOwn(values, key(row)) && String(values[key(row)]).trim() !== '').forEach(row => {
        // Untouched zero options do not add thousands of products to the planning table.
        if (!result.has(row.productId)) result.set(row.productId, { productId: row.productId, sku: row.sku, unit: row.unit, entries: [] });
        result.get(row.productId).entries.push({ ...row, key: key(row), quantity: effective(row), cachedQuantity: normalized(values[key(row)]), excluded: excluded.has(key(row)) });
      });
      return [...result.values()];
    };
    const setQuantity = (id, value) => {
      if (readOnly || !options.some(row => key(row) === id) || excluded.has(id)) return;
      values[id] = String(value); render(); notify();
    };
    const setArea = (productId, area, value) => {
      const entries = groups().find(group => group.productId === productId)?.entries.filter(row => row.area === area && !row.excluded) || [];
      if (!entries.length || readOnly) return;
      const amount = scale(value), available = entries.reduce((sum, row) => sum + (scale(row.available) || 0n), 0n);
      // Retain invalid input for correction rather than silently clipping it.
      if (amount === null || amount > available || entries.some(row => !row.allowsDecimals) && amount % 10000n !== 0n) {
        entries.forEach((row, index) => { values[row.key] = index ? '0' : String(value); });
      } else {
        let remaining = amount;
        entries.forEach(row => {
          const maximum = scale(row.available) || 0n, assigned = remaining < maximum ? remaining : maximum;
          values[row.key] = unscale(assigned); remaining -= assigned;
        });
      }
      render(); notify();
    };
    const revalidate = async () => {
      await ready;
      const generation = targetGeneration, targetUrl = root.dataset.url;
      const response = await fetch(targetUrl);
      if (!response.ok) throw new Error(translate('No se pudieron revalidar los pendientes. Reintenta.'));
      const current = await response.json();
      if (generation !== targetGeneration || targetUrl !== root.dataset.url) return true;
      let changed = false;
      const previous = new Map(options.map(row => [key(row), row]));
      for (const row of current) {
        const old = previous.get(key(row));
        if (old && (old.fingerprint !== row.fingerprint || old.selected !== row.selected) && Number(effective(old)) > 0) changed = true;
        if (!Object.hasOwn(values, key(row))) values[key(row)] = row.selected ? String(row.selected) : '';
      }
      for (const old of options) if (!current.some(row => key(row) === key(old)) && Number(effective(old)) > 0) {
        restored ||= {}; restored[key(old)] = values[key(old)]; changed = true;
      }
      options = current; loaded = true; loadFailed = false; render();
      return changed;
    };
    const retarget = async url => {
      const generation = ++targetGeneration;
      await ready;
      if (generation !== targetGeneration) return;
      const previous = { ...values };
      restored = { ...restored, ...previous };
      restoredSources = Object.fromEntries(options.map(row => [key(row), { fingerprint: row.fingerprint, available: row.available }]));
      root.dataset.url = url; loaded = false;
      try {
        const response = await fetch(url, { cache: 'no-store' });
        if (!response.ok) throw Error();
        const current = await response.json();
        if (generation !== targetGeneration) return;
        options = current; values = { ...Object.fromEntries(current.map(row => [key(row), ''])), ...previous };
        loaded = true; loadFailed = false; render();
      } catch (error) {
        if (generation !== targetGeneration) return;
        loaded = true; loadFailed = true; throw error;
      }
    };
    const api = { ready, changes, errors, groups, setQuantity, setArea, revalidate, retarget,
      reset: keys => { keys.forEach(id => { const row = options.find(option => key(option) === id); values[id] = row?.selected ? String(row.selected) : ''; excluded.delete(id); }); render(); notify(); },
      missing: () => Object.keys(restored || {}).filter(id => !options.some(row => key(row) === id) && Number(restored[id]) > 0),
      removeMissing: id => { if (restored) delete restored[id]; delete values[id]; render(); notify(); },
      setEnabled: (keys, enabled) => { keys.forEach(id => { if (enabled) excluded.delete(id); else excluded.add(id); }); render(); notify(); },
      snapshot: () => ({ values: { ...values }, excluded: [...excluded], sources: Object.fromEntries(options.map(row => [key(row), { fingerprint: row.fingerprint, available: row.available }])) }),
      options: () => { if (loadFailed) throw new Error(translate("No se pudieron consultar los pendientes. Actualiza la página.")); return options.map(row => ({ ...row, key: key(row),
        prepared: Number(effective(row)), protected: row.selected > 0 || String(values[key(row)] ?? '').trim() !== '' })); },
      prepare, reveal: () => { if (panel) panel.open = true; },
      restore: state => { restored = state?.values || state || {}; restoredSources = state?.sources || {}; excluded.clear(); (state?.excluded || []).forEach(id => excluded.add(id)); Object.assign(values, restored); if (loaded) render(); if (panel && Object.values(restored).some(value => Number(normalized(value)) > 0)) panel.open = true; },
      describe: change => translate("{0} · {1} · Arrastre inicial: {2} → {3} {4}", change.sku, areas[change.area], change.before, change.quantity, change.unit) };
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
      catch { status.textContent = translate("La recuperación local no está disponible. No cierres la página."); } };
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
          preview.append(node('p', translate("{0} · Saldo inicial: {1}", after.sku, summary), 'small'));
        }
        for (const after of result.afterClose?.products || []) {
          const before = result.beforeClose.products.find(x => x.productId === after.productId);
          const summary = ['cutting', 'sewing', 'readyToPack'].map((area, index) => `${areas[index]}: ${before?.[area]?.pending || 0} → ${after[area].pending}`).join(' · ');
          preview.append(node('p', translate("{0} · Pendiente al cierre: {1}", after.sku, summary), 'small'));
        }
        preview.hidden = false; auth.hidden = false; persist();
      } catch { status.textContent = translate("No se pudo revisar. Se conservan los datos."); }
      finally { sending = false; lock(); }
    });
    confirmButton.addEventListener('click', async () => {
      if (!pending || sending) return;
      sending = true; lock(); confirmButton.disabled = true; const secret = pin.value; pin.value = '';
      try {
        const result = await post(root.dataset.confirmUrl, { ...pending, pin: secret });
        if (result.success) { finish(); return; }
        status.textContent = (result.errors || [translate("No se guardó. Revisa nuevamente.")]).join(' ');
        uncertain = false;
        if (result.status !== 5) { pending = null; auth.hidden = true; }
      } catch {
        uncertain = true; status.textContent = translate("Respuesta pendiente. Reintenta la misma confirmación; no se duplicará el arrastre.");
        const url = new URL(root.dataset.operationUrl, location.origin); url.searchParams.set('operationId', pending.operationId);
        try { const result = await fetch(url).then(r => r.json()); if (result.saved) { finish(); return; } } catch { /* Keep the same operation. */ }
      } finally { sending = false; confirmButton.disabled = false; lock(); if (!completed) persist(); }
    });
    try {
      const old = JSON.parse(localStorage.getItem(storageKey) || 'null');
      if (old) ready.then(() => {
        const recovery = node('div', translate("Preparación local del {0}. ", new Date(old.updated).toLocaleString()), 'alert alert-info');
        const restore = node('button', translate("Recuperar"), 'btn btn-primary me-2'), discard = node('button', translate("Descartar"), 'btn btn-outline-secondary');
        restore.type = discard.type = 'button';
        restore.addEventListener('click', async () => {
          api.restore({ values: old.values || {}, sources: old.sources }); reason.value = old.reason || ''; recovery.remove();
          if (old.pending) {
            pending = old.pending; uncertain = true; auth.hidden = false; lock();
            status.textContent = translate("Comprobando la operación anterior. Reintenta la misma confirmación si sigue pendiente.");
            const url = new URL(root.dataset.operationUrl, location.origin); url.searchParams.set('operationId', pending.operationId);
            try { const result = await fetch(url).then(r => r.json()); if (result.saved) { finish(); return; } } catch { /* Same operation remains pending. */ }
            persist();
          } else { notify(); status.textContent += translate(" Preparación recuperada. Revisa los valores actuales antes de confirmar."); }
        });
        discard.addEventListener('click', () => { localStorage.removeItem(storageKey); recovery.remove(); });
        recovery.append(restore, discard); root.append(recovery);
      });
    } catch { status.textContent = translate("No se pudo recuperar la preparación local."); }
    window.addEventListener('beforeunload', event => { if (!completed && (changes().length || uncertain)) { event.preventDefault(); event.returnValue = ''; } });
  });
  window.ProductionWeekOpenings = { get: root => controllers.get(root) };
})();
