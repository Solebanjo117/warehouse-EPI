(() => {
  const labels = JSON.parse(document.querySelector('[data-week-workspace]')?.dataset.progressLabels || '{}');
  const label = key => labels[key] || key;
  const days = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'];
  const areas = ['Corte', 'Costura', 'Ready to Pack'];
  // Presentation state belongs to this mounted workspace, never to the draft store.
  const disclosures = new WeakMap();
  const remember = root => {
    if (!disclosures.has(root)) disclosures.set(root, new Set());
    root.querySelectorAll('[data-schedule-disclosure]').forEach(node => {
      const state = disclosures.get(root), key = node.dataset.scheduleDisclosure;
      if (node.open) state.add(key); else state.delete(key);
    });
  };
  const restore = (root, details, key) => {
    details.dataset.scheduleDisclosure = key;
    details.open = disclosures.get(root)?.has(key) || false;
  };
  const snapshots = new WeakMap();
  const mountScroll = root => {
    const top = root?.querySelector('[data-workspace-scroll-top]');
    const viewport = root?.querySelector('[data-workspace-scroll]');
    const spacer = top?.querySelector('[data-workspace-scroll-width]');
    const table = viewport?.querySelector('table');
    if (!top || !viewport || !spacer || !table) return;
    const mirrored = new WeakMap();
    let previousMaximum = null;
    const move = (target, position) => {
      if (Math.abs(target.scrollLeft - position) <= 1) return;
      target.scrollLeft = position;
      mirrored.set(target, target.scrollLeft);
    };
    const synchronize = (source, target) => {
      const expected = mirrored.get(source);
      mirrored.delete(source);
      // Ignore our delayed scroll events without rewinding a newer native movement.
      if (expected !== undefined && Math.abs(source.scrollLeft - expected) <= 1) return;
      move(target, source.scrollLeft);
    };
    const refresh = () => {
      const maximum = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
      const atEnd = previousMaximum > 0 && viewport.scrollLeft >= previousMaximum - 1;
      // Identical usable widths keep both native scrollbar ranges equal.
      top.style.width = `${viewport.clientWidth}px`;
      spacer.style.width = `${viewport.scrollWidth}px`;
      top.hidden = maximum <= 1;
      if (atEnd && previousMaximum !== maximum) move(viewport, maximum);
      previousMaximum = maximum;
      move(top, viewport.scrollLeft);
    };
    top.addEventListener('scroll', () => synchronize(top, viewport), { passive: true });
    viewport.addEventListener('scroll', () => synchronize(viewport, top), { passive: true });
    viewport.addEventListener('focusin', event => {
      const table = event.target.closest('table');
      const scroll = event.target.closest('.table-responsive');
      if (!table || !scroll) return;
      requestAnimationFrame(() => {
        const field = event.target.getBoundingClientRect();
        const sku = table.querySelector('th')?.getBoundingClientRect();
        const frame = scroll.getBoundingClientRect();
        const left = sku && getComputedStyle(table.querySelector('th')).position === 'sticky' ? sku.right + 4 : frame.left;
        if (field.left < left) scroll.scrollLeft -= left - field.left;
        else if (field.right > frame.right) scroll.scrollLeft += field.right - frame.right;
      });
    });
    if (typeof ResizeObserver !== 'undefined') {
      const observer = new ResizeObserver(refresh);
      observer.observe(viewport);
      observer.observe(table);
    }
    window.addEventListener('resize', refresh);
    refresh();
  };
  const number = value => Number(value).toLocaleString(document.documentElement.lang || 'es', { maximumFractionDigits: 4 });
  const element = (tag, value = '', cls = '') => {
    const node = document.createElement(tag); node.textContent = label(value); node.className = cls; return node;
  };
  const progress = root => {
    if (!snapshots.has(root)) snapshots.set(root, new Map([...root.querySelectorAll('[data-progress]')]
      .map(node => { const value = JSON.parse(node.dataset.progress); return [value.productId, value]; })));
    return snapshots.get(root);
  };
  const displayPercent = ratio => ratio == null ? null : `${(ratio >= .9995 && ratio < 1 ? 99.9 : ratio * 100).toLocaleString(document.documentElement.lang || 'es', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
  const level = ratio => ratio == null ? null : ratio < .75 ? 'low' : ratio < 1 ? 'near' : 'complete';
  const indicators = (root, cell, day, unit, productId) => {
    if (!day) return;
    const list = element('div', '', 'production-schedule-progress');
    list.dataset.scheduleIndicators = '';
    list.append(element('div', 'Estado del plan', 'production-schedule-progress__heading'));
    const breakdown = element('details', '', 'production-schedule-progress__breakdown');
    restore(root, breakdown, `progress:${productId}:${cell.dataset.workspaceDayCell}`);
    breakdown.append(element('summary', 'Ver desglose'));
    day.areas.forEach(area => {
      const block = element('div', '', 'production-schedule-progress__area');
      const daily = element('div', '', 'production-schedule-progress__metric');
      const coverage = area.coverage || { required: 0, covered: 0, pending: 0, ratio: null };
      const ratio = area.applies ? coverage.ratio : null;
      if (level(ratio)) daily.dataset.progressLevel = level(ratio);
      const percent = ratio == null ? label('Sin meta nueva') : displayPercent(ratio);
      daily.append(element('strong', label(areas[area.area])), element('span', area.applies ? percent : label('No aplica')));
      block.append(daily);
      if (area.applies) {
        const produced = element('small', '', 'production-schedule-progress__produced');
        produced.append(element('span', `${label('Hecho ese día')}: `), element('span', `${number(area.produced)} ${unit}`));
        block.append(produced);
      }
      const detail = element('div', '', 'production-schedule-progress__coverage');
      detail.append(element('strong', label(areas[area.area])));
      if (area.applies) {
        if (ratio != null) {
          detail.append(element('span', `${label('Cubierto')}: ${number(coverage.covered)} ${unit}`, 'd-block'));
          detail.append(element('span', `${label('Meta')}: ${number(coverage.required)} ${unit}`, 'd-block'));
          detail.append(element('span', `${label('Pendiente de esta meta')}: ${number(coverage.pending)} ${unit}`, 'd-block'));
        } else detail.append(element('span', 'Sin meta nueva', 'd-block'));
      } else detail.append(element('span', 'No aplica', 'd-block'));
      breakdown.append(detail);
      list.append(block);
    });
    list.append(breakdown);
    cell.append(list);
  };
  const scaledTotal = value => {
    const raw = String(value ?? '').trim().replace(',', '.') || '0';
    if (!/^\d{1,14}(?:\.\d{1,4})?$/.test(raw)) return null;
    const [integer, fraction = ''] = raw.split('.');
    return BigInt(integer) * 10000n + BigInt(fraction.padEnd(4, '0'));
  };
  const sumTotal = values => {
    const amounts = values.map(scaledTotal);
    return amounts.some(value => value === null) ? null : amounts.reduce((sum, value) => sum + value, 0n);
  };
  const calculateTotals = (planned, openings, invalidPlan = false, allowsDecimals = true) => {
    const valid = values => allowsDecimals || values.every(value => { const amount = scaledTotal(value); return amount !== null && amount % 10000n === 0n; });
    const total = invalidPlan || !valid(planned) ? null : sumTotal(planned);
    return { total, areas: areas.map((_, area) => {
      const values = openings.filter(entry => entry.area === area).map(entry => entry.quantity);
      const initial = valid(values) ? sumTotal(values) : null;
      return total === null || initial === null ? null : total + initial;
    }) };
  };
  const displayTotal = (value, unit) => {
    if (value === null) return label('Por revisar');
    const fraction = String(value % 10000n).padStart(4, '0').replace(/0+$/, '');
    const decimal = (document.documentElement?.lang || 'es').startsWith('en') ? '.' : ',';
    return String(value / 10000n) + (fraction ? decimal + fraction : '') + ' ' + unit;
  };
  const carryTotalCell = product => {
    const cell = element('td', '', 'production-week-workspace__carry-totals');
    cell.dataset.scheduleCarryTotal = product.productId;
    cell.dataset.label = label('Total con arrastre');
    return cell;
  };
  const paintTotals = (cell, totals, unit) => {
    cell.replaceChildren(...areas.map((area, index) => {
      const line = element('div', '', 'production-week-workspace__area-total');
      line.dataset.totalArea = index;
      line.append(element('span', label(area)), element('strong', displayTotal(totals.areas[index], unit)));
      return line;
    }));
    cell.previousElementSibling.textContent = displayTotal(totals.total, unit);
  };
  const readOnlyOpenings = product => areas.map((area, index) => {
    const quantity = (product.openings || []).filter(value => value.area === index).reduce((sum, value) => sum + value.quantity, 0);
    const cell = element('td', '', 'production-week-workspace__carry-cell');
    cell.append(element('span', number(quantity), 'production-week-workspace__planned'));
    cell.dataset.workspaceOpeningArea = index; cell.dataset.label = `${label('Arrastre inicial')} · ${label(area)}`;
    cell.classList.toggle('production-week-workspace__carry-positive', quantity > 0); return cell;
  });
  const summaryRow = (root, product, grouped, editable, renderOpenings = readOnlyOpenings) => {
    const row = element('tr'); row.dataset.scheduleProduct = product.productId;
    const sku = element('th', product.sku); sku.scope = 'row'; sku.append(element('small', product.unit, 'd-block text-body-secondary')); row.append(sku);
    row.append(...renderOpenings(product));
    days.forEach((_, day) => {
      const cell = element('td'); cell.dataset.workspaceDayCell = day; cell.dataset.label = label(days[day]);
      const plan = element('div', `${number(product.days[day]?.planned || 0)} ${product.unit}`, 'production-week-workspace__planned');
      if (editable) { plan.dataset.schedulePlan = day; plan.dataset.productId = product.productId; }
      cell.append(plan); indicators(root, cell, product.days[day], product.unit, product.productId); row.append(cell);
    });
    const total = element('td', `${number(product.days.reduce((sum, day) => sum + day.planned, 0))} ${product.unit}`);
    if (editable) total.dataset.scheduleProductTotal = product.productId;
    total.dataset.label = label('Total'); total.className = 'production-week-workspace__total';
    row.append(total, carryTotalCell(product), element('td', grouped ? 'Detalle de pedidos debajo' : 'Sin programación nueva'));
    paintTotals(row.querySelector('[data-schedule-carry-total]'), calculateTotals(product.days.map(day => day.planned), product.openings || []), product.unit);
    return row;
  };
  const decorate = (root, rows, openingGroups = [], renderOpenings = readOnlyOpenings, renderOrigin = () => {}, renderDay = null, targetIds = []) => {
    const tbody = root.querySelector('[data-workspace-rows]');
    const saved = progress(root), groups = new Map();
    rows.filter(row => !row.rowDeletionSnapshot).forEach(row => {
      const id = row.productId || row.key;
      if (!groups.has(id)) groups.set(id, []);
      groups.get(id).push(row);
    });
    // Products with only carryover or extra production still have a useful weekly balance.
    saved.forEach((_, id) => { if (!groups.has(id)) groups.set(id, []); });
    openingGroups.forEach(group => { if (!groups.has(group.productId)) groups.set(group.productId, []); });
    groups.forEach((group, id) => {
      const prepared = openingGroups.find(value => value.productId === id) || group[0];
      const product = saved.get(id) || { productId: id, sku: prepared.sku, unit: prepared.unit, days: [] };
      const nodes = group.map(row => [...tbody.children].find(node => node.dataset.workspaceRow === row.key));
      if (nodes.length === 1) {
        const node = nodes[0];
        node.dataset.scheduleProduct = id;
        [...node.children].filter(cell => cell.dataset.workspaceDayCell !== undefined).forEach(cell => {
          indicators(root, cell, product.days[Number(cell.dataset.workspaceDayCell)], product.unit, id);
        });
        tbody.append(node); return;
      }
      const parent = summaryRow(root, product, nodes.length > 0, true, renderOpenings);
      if (renderDay) [...parent.children].filter(cell => cell.dataset.workspaceDayCell !== undefined).forEach(cell => {
        const day = Number(cell.dataset.workspaceDayCell);
        cell.replaceChildren(...renderDay(product, day));
        indicators(root, cell, product.days[day], product.unit, id);
      });
      renderOrigin(parent.children[0], product); tbody.append(parent);
    });
    update(root, rows);
  };
  const update = (root, rows, openingGroups = [], targets = {}, excludedProducts = new Set()) => {
    root.querySelectorAll('[data-schedule-carry-total]').forEach(cell => {
      const id = cell.dataset.scheduleCarryTotal;
      const productRows = rows.filter(row => row.productId === id && !row.rowDeletionSnapshot);
      const product = progress(root).get(id);
      const excluded = excludedProducts.has(id);
      const included = excluded ? [] : productRows.filter(row => row.included !== false);
      const quantities = rows.some(row => row.productId === id) ? included.flatMap(row => row.cells.filter(day => !day.removed && day.copyIncluded !== false).map(day => day.value))
        : (product?.days || []).map(day => day.planned);
      const group = openingGroups.find(group => group.productId === id);
      const openings = excluded ? [] : group?.entries || product?.openings || [];
      const invalidPlan = !excluded && Object.keys(targets[id] || {}).length > 0;
      paintTotals(cell, calculateTotals(quantities, openings, invalidPlan, productRows[0]?.allowsDecimals !== false),
        productRows[0]?.unit || group?.unit || product?.unit || '');
    });
  };
  const mountReadOnly = root => {
    const tbody = root.querySelector('[data-workspace-rows]'), saved = progress(root), filterRows = [];
    const lines = [...root.querySelectorAll('[data-line]')].map(node => JSON.parse(node.dataset.line));
    saved.forEach(product => {
      const own = lines.filter(line => line.ProductId === product.productId);
      const row = summaryRow(root, product, own.length > 0, false);
      const cell = row.lastElementChild; cell.replaceChildren();
      if (own.length) {
        const details = element('details'); details.append(element('summary', 'Pedidos y notas'));
        const references = [...new Set(own.flatMap(line => [line.OrderReference1, line.OrderReference2, line.OrderReference3]).filter(Boolean))];
        references.forEach((reference, index) => details.append(element('p', label('Pedido {0}').replace('{0}', index + 1) + ': ' + reference, 'small')));
        [...new Set(own.map(line => line.Notes).filter(Boolean))].forEach(note => details.append(element('p', note, 'small')));
        cell.append(details);
      }
      tbody.append(row);
      filterRows.push({ row, sku: product.sku.toUpperCase() });
    });
    const openingEditor = window.ProductionWeekOpenings?.get(root.querySelector('[data-opening-editor]'));
    openingEditor?.ready.then(() => {
      openingEditor.groups().forEach(group => {
        const sku = [...tbody.children].find(row => row.dataset.scheduleProduct === group.productId)?.firstElementChild;
        if (!sku) return;
        const origin = element('details'); origin.append(element('summary', 'Ver origen'));
        const date = value => String(value || '').slice(0, 10).split('-').reverse().join('/');
        group.entries.filter(entry => entry.sourceStart && Number(entry.quantity) > 0).forEach(entry => {
          const description = label(areas[entry.area]) + ' · ' + date(entry.sourceStart) + ' · ' + label('Renglón {0}').replace('{0}', entry.sequence) + ' · ' + number(entry.quantity) + ' ' + entry.unit;
          origin.append(element('p', description, 'small mb-1'));
          if (entry.originalStart && entry.originalStart !== entry.sourceStart)
            origin.append(element('p', label('origen inicial {0}').replace('{0}', date(entry.originalStart)), 'small mb-1'));
          [entry.orderReference1, entry.orderReference2, entry.orderReference3, entry.notes].filter(Boolean)
            .forEach(value => origin.append(element('p', value, 'small mb-1')));
        });
        if (origin.children.length > 1) sku.append(origin);
      });
    });
    const empty = root.querySelector('[data-workspace-empty]');
    const search = root.querySelector('[data-workspace-filter]');
    const status = root.querySelector('[data-workspace-filter-status]');
    const filter = () => {
      const query = (search?.value || '').trim().toUpperCase();
      let visible = 0;
      filterRows.forEach(({ row, sku }) => { row.hidden = !sku.includes(query); if (!row.hidden) visible++; });
      empty.hidden = visible > 0;
      empty.textContent = label(saved.size ? 'No se encontraron productos.' : 'Sin productos programados ni producción en esta semana.');
      if (status) status.textContent = query ? label('Mostrando {0} de {1} SKU.').replace('{0}', visible).replace('{1}', saved.size) : '';
    };
    search?.addEventListener('input', filter);
    search?.addEventListener('search', filter);
    search?.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); filter(); } });
    filter(); root.dataset.scheduleView = 'week';
  };
  window.ProductionScheduleProgress = { decorate, update, mountReadOnly, remember, restore, label, carryTotalCell, calculateTotals };
  mountScroll(document.querySelector('[data-week-workspace]'));
})();
