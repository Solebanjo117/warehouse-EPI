(() => {
  const previous = window.ProductionWeekOpenings;
  const controllers = new WeakMap();
  const say = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (_, i) => args[i] ?? ''));
  const key = row => `${row.productId}:${row.area}`;
  const normalize = value => String(value ?? '').trim().replace(',', '.');
  const scale = value => {
    const raw = normalize(value) || '0';
    if (!/^\d{1,14}(?:\.\d{1,4})?$/.test(raw)) return null;
    const [integer, fraction = ''] = raw.split('.');
    return BigInt(integer) * 10000n + BigInt(fraction.padEnd(4, '0'));
  };
  const format = value => {
    const fraction = String(value % 10000n).padStart(4, '0').replace(/0+$/, '');
    return `${value / 10000n}${fraction ? '.' + fraction : ''}`;
  };
  document.querySelectorAll('[data-opening-editor][data-totals="true"]').forEach(root => {
    const rows = new Map(), excluded = new Set(), aliases = new Map();
    let loaded = false, failed = false, generation = 0, missing = {};
    const readonly = root.dataset.readonly === 'true';
    const effective = row => excluded.has(key(row)) ? String(row.selected) : row.value;
    const changed = row => row.touched && !excluded.has(key(row)) &&
      (row.version === 0 || scale(effective(row)) !== scale(row.selected));
    const errors = (excludedProducts = new Set()) => !loaded ? [say('Espera a que se consulten los pendientes de arrastre.')] : failed
      ? [say('No se pudieron consultar los pendientes. Actualiza antes de guardar.')]
      : [...rows.values()].filter(row => row.touched && !excluded.has(key(row)) &&
        (scale(row.value) === null || !row.allowsDecimals && scale(row.value) % 10000n !== 0n))
        .map(row => say('{0} · {1}: cantidad no válida.', row.sku, say(['Corte', 'Costura', 'Ready to Pack'][row.area])))
        .concat(Object.keys(missing).length ? [say('Un origen recuperado ya no está disponible. Descarta esa preparación y revisa el arrastre.')] : []);
    const render = () => {
      const status = root.querySelector('[data-opening-status]');
      if (status) status.textContent = errors().join(' ');
    };
    const notify = () => { render(); root.dispatchEvent(new CustomEvent('openingchange', { bubbles: true })); };
    const register = product => {
      const productId = product.productId || product.id;
      if (!productId) return;
      [0, 1, 2].forEach(area => {
        const id = key({ productId, area });
        if (!rows.has(id)) rows.set(id, { productId, area, sku: product.sku, unit: product.unit,
          allowsDecimals: product.allowsDecimals !== false, selected: 0, version: 0, value: '0', touched: false, visible: false });
      });
    };
    const load = async url => {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) throw Error();
      return response.json();
    };
    const accept = data => data.forEach(item => {
      const old = rows.get(key(item));
      rows.set(key(item), { ...old, ...item, selected: item.quantity, value: old?.touched ? old.value : String(item.quantity),
        touched: old?.touched || false, visible: true });
    });
    const ready = load(root.dataset.url).then(data => { accept(data); loaded = true; render(); })
      .catch(() => { loaded = true; failed = true; render(); });
    const canonicalKey = id => aliases.get(id) || id;
    const setQuantity = (id, value) => {
      const row = rows.get(canonicalKey(id));
      if (!row || readonly) return;
      excluded.delete(key(row)); row.manual = true;
      row.value = String(value); row.touched = true; row.visible = true; notify();
    };
    const restore = async state => {
      await ready;
      const values = state?.values || state || {};
      (state?.products || []).forEach(register);
      excluded.clear(); missing = {};
      rows.forEach(row => { row.manual = false; });
      const legacyIds = Object.keys(values).filter(id => !rows.has(id) && id.split(':').length === 3 && normalize(values[id]) !== '');
      if (legacyIds.length) {
        try {
          const options = await load(root.dataset.legacyUrl);
          const combined = new Map();
          options.forEach(option => {
            const oldKey = `${option.sourceWeekId}:${option.sourceLineId}:${option.area}`;
            if (!Object.hasOwn(values, oldKey) || normalize(values[oldKey]) === '') return;
            register(option); const id = key(option); aliases.set(oldKey, id);
            const amount = scale(values[oldKey]);
            if (amount === null) combined.set(id, values[oldKey]);
            else if (typeof combined.get(id) !== 'string') combined.set(id, (combined.get(id) || 0n) + amount);
          });
          combined.forEach((value, id) => {
            const row = rows.get(id); row.value = typeof value === 'string' ? value : format(value); row.touched = true; row.visible = true;
          });
        } catch { /* Keep unresolvable selections visible for correction. */ }
      }
      Object.entries(values).forEach(([id, value]) => {
        if (aliases.has(id)) return;
        const row = rows.get(id);
        if (row) { row.value = String(value); row.touched = state?.touched ? state.touched.includes(id) : normalize(value) !== ''; row.visible ||= row.touched; }
        else if (normalize(value) !== '') missing[id] = value;
      });
      (state?.excluded || []).forEach(id => excluded.add(canonicalKey(id)));
      // Older preparations cannot distinguish an included edit from a copied default.
      // Preserve included values; keep explicitly excluded suggestions excluded.
      const manual = state?.manual ? new Set(state.manual.map(canonicalKey)) : null;
      rows.forEach(row => {
        row.manual = row.touched && (manual ? manual.has(key(row)) : !excluded.has(key(row)));
        if (row.manual) excluded.delete(key(row));
      });
      render();
    };
    const snapshot = () => ({ format: 2,
      values: Object.fromEntries([...rows].filter(([, row]) => row.touched).map(([id, row]) => [id, row.value])),
      manual: [...rows].filter(([, row]) => row.touched && row.manual).map(([id]) => id),
      touched: [...rows].filter(([, row]) => row.touched).map(([id]) => id), excluded: [...excluded],
      products: [...rows.values()].filter(row => row.visible).map(row => ({ productId: row.productId, sku: row.sku, unit: row.unit, allowsDecimals: row.allowsDecimals })) });
    const options = () => [...rows.values()].map(row => ({ ...row, key: key(row), quantity: effective(row),
      cachedQuantity: row.value, excluded: excluded.has(key(row)), prepared: Number(effective(row)),
      protected: row.touched || row.version > 0 || Number(row.selected) !== 0 }));
    const groups = () => {
      const result = new Map();
      options().filter(row => row.visible).forEach(row => {
        if (!result.has(row.productId)) result.set(row.productId, { productId: row.productId, sku: row.sku, unit: row.unit, entries: [] });
        result.get(row.productId).entries.push(row);
      });
      return [...result.values()];
    };
    const api = { totals: true, ready, register, errors, groups, options, snapshot, restore, canonicalKey,
      changes: () => [...rows.values()].filter(changed).map(row => ({ productId: row.productId, area: row.area,
        quantity: normalize(effective(row)) || '0', expectedVersion: row.version, sku: row.sku, unit: row.unit, before: row.selected })),
      setArea: (productId, area, value) => setQuantity(key({ productId, area }), value), setQuantity,
      missing: () => Object.keys(missing), removeMissing: id => { delete missing[id]; notify(); },
      setEnabled: (keys, enabled) => {
        keys.forEach(id => {
          const canonical = canonicalKey(id);
          if (enabled || rows.get(canonical)?.manual) excluded.delete(canonical);
          else excluded.add(canonical);
        }); render();
      },
      reset: keys => { keys.forEach(id => { const row = rows.get(canonicalKey(id)); if (row) { row.value = String(row.selected); row.touched = false; row.manual = false; } excluded.delete(canonicalKey(id)); }); notify(); },
      prepare: async entries => {
        await ready; if (readonly || failed) throw Error(say('No se pudieron consultar los pendientes. Actualiza antes de guardar.'));
        entries.forEach(entry => { register(entry); const row = rows.get(key(entry));
          if (row.touched || row.version > 0 || Number(row.selected) !== 0) return;
          Object.assign(row, { value: String(entry.quantity), touched: true, visible: true,
            sourceWeekId: entry.sourceWeekId, sourceStart: entry.sourceStart });
        }); render();
      },
      revalidate: async () => {
        await ready; const current = await load(root.dataset.url);
        const changed = current.some(item => rows.has(key(item)) && rows.get(key(item)).version !== item.version);
        // Preserve the observed version for optimistic concurrency; never adopt a changed server baseline silently.
        current.forEach(item => { if (!rows.has(key(item))) accept([item]); });
        failed = false; loaded = true; render(); return changed;
      },
      retarget: async url => {
        const currentGeneration = ++generation; root.dataset.url = url;
        const current = await load(url); if (currentGeneration !== generation) return;
        rows.forEach(row => { row.selected = 0; row.version = 0; }); accept(current); loaded = true; failed = false; render();
      } };
    controllers.set(root, api);
  });
  window.ProductionWeekOpenings = { get: root => controllers.get(root) || previous?.get(root) };
})();
