(() => {
  const root = document.querySelector('[data-week-picker]');
  if (!root) return;
  const say = window.warehouseText || (key => key);
  const options = root.querySelector('[data-week-picker-options]');
  const summary = root.querySelector('[data-week-picker-summary]');
  const status = root.querySelector('[data-week-picker-status]');
  const existing = root.querySelector('[data-week-picker-existing]');
  const retry = root.querySelector('[data-week-picker-retry]');
  const previous = root.querySelector('[data-week-picker-previous]');
  const next = root.querySelector('[data-week-picker-next]');
  let loading = false, submitting = false, generation = 0, controller = null, failed = null;
  const selected = () => options.querySelector('input:checked:not(:disabled)');
  const update = () => {
    const frozen = window.ProductionWeekWorkspace?.canChangeTarget?.() === false;
    options.querySelectorAll('[data-week-picker-option]').forEach(input => {
      input.disabled = loading || frozen || input.closest('.production-week-picker__row--existing') !== null;
    });
    const available = root.dataset.configurationReady === 'true' && !!selected() && !loading;
    options.querySelectorAll('[data-week-picker-option]').forEach(input => {
      input.closest('label').classList.toggle('is-selected', input.checked && !input.disabled);
    });
    root.setAttribute('aria-busy', String(loading));
    return window.ProductionWeekWorkspace?.setTarget(selected()?.value || '', available);
  };
  const announceSelection = () => {
    summary.textContent = selected()?.dataset.label || say('Selecciona una semana.');
    existing.hidden = true;
  };
  const node = (tag, content, className) => {
    const element = document.createElement(tag);
    if (content) element.textContent = content;
    if (className) element.className = className;
    return element;
  };
  const render = (month, targetDate) => {
    root.dataset.month = month.month;
    root.dataset.previousMonth = month.previousMonth || '';
    root.dataset.nextMonth = month.nextMonth || '';
    root.querySelector('[data-week-picker-month]').textContent = month.label;
    previous.disabled = !month.previousMonth;
    next.disabled = !month.nextMonth;
    options.replaceChildren();
    for (const option of month.options) {
      const row = node('div', '', 'production-week-picker__row');
      const label = node('label', '', 'production-week-picker__choice');
      const input = node('input', '', 'form-check-input');
      input.type = 'radio'; input.name = 'NewWeek.WeekStart'; input.setAttribute('form', 'week-workspace-form'); input.value = option.weekStart;
      input.required = true; input.disabled = !!option.viewUrl;
      input.checked = option.weekStart === targetDate && !input.disabled;
      input.dataset.weekPickerOption = ''; input.dataset.label = option.label;
      const text = node('span', option.label);
      label.append(input, text); row.append(label);
      if (option.viewUrl) {
        row.classList.add('production-week-picker__row--existing');
        text.append(node('span', say('Ya creada'), 'badge text-bg-secondary ms-1'));
        const link = node('a', say('Ver semana'), 'btn btn-outline-secondary');
        link.href = option.viewUrl; link.setAttribute('aria-label', `${say('Ver semana')}: ${option.label}`);
        row.append(link);
      }
      options.append(row);
    }
    announceSelection();
    const target = month.options.find(option => option.weekStart === targetDate);
    status.textContent = target?.viewUrl ? say('Esta semana ya está creada.') : '';
    if (target?.viewUrl) { existing.href = target.viewUrl; existing.hidden = false; }
  };
  const load = async (month, targetDate = null) => {
    if (!month || submitting || window.ProductionWeekWorkspace?.canChangeTarget?.() === false) return;
    const request = ++generation;
    controller?.abort(); controller = new AbortController();
    loading = true; retry.hidden = true;
    status.textContent = say('Consultando semanas…'); update();
    try {
      const url = new URL(root.dataset.optionsUrl, location.href);
      url.searchParams.set('month', month);
      const response = await fetch(url, { signal: controller.signal, cache: 'no-store' });
      if (!response.ok) throw Error('request');
      const result = await response.json();
      if (request !== generation) return;
      render(result, targetDate); failed = null;
    } catch (error) {
      if (request !== generation) return;
      failed = { month, targetDate }; retry.hidden = false;
      status.textContent = say('No se pudieron consultar las semanas. Se conserva el mes anterior. Reintenta.');
    } finally {
      if (request === generation) { loading = false; await update(); }
    }
  };
  options.addEventListener('change', event => {
    if (!event.target.matches('[data-week-picker-option]') || loading || submitting) return;
    status.textContent = ''; failed = null; retry.hidden = true;
    announceSelection(); update();
  });
  previous.addEventListener('click', () => load(root.dataset.previousMonth));
  next.addEventListener('click', () => load(root.dataset.nextMonth));
  root.querySelector('[data-week-picker-current]').addEventListener('click', () =>
    load(root.dataset.currentWeek.slice(0, 7), root.dataset.currentWeek));
  root.querySelector('[data-week-picker-upcoming]').addEventListener('click', () =>
    load(root.dataset.nextWeek.slice(0, 7), root.dataset.nextWeek));
  retry.addEventListener('click', () => { if (failed) load(failed.month, failed.targetDate); });
  window.ProductionWeekPicker = { selectDate: date => load(date.slice(0, 7), date) };
  window.addEventListener('schedulepreparationchange', () => {
    const frozen = window.ProductionWeekWorkspace?.canChangeTarget?.() === false;
    root.querySelectorAll('button, input').forEach(control => {
      if (control.matches('input')) control.disabled = loading || frozen || control.closest('.production-week-picker__row--existing') !== null;
      else if (!frozen) control.disabled = control === previous ? !root.dataset.previousMonth : control === next ? !root.dataset.nextMonth : false;
      else control.disabled = true;
    });
  });
  window.addEventListener('pageshow', () => { submitting = false; update(); });
  root.querySelector('[data-week-picker-navigation]').hidden = false;
  root.querySelector('[data-week-picker-shortcuts]').hidden = false;
  update();
})();
