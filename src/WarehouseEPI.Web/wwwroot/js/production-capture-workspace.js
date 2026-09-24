(() => {
  const form = document.querySelector('[data-capture-group]');
  if (!form) return;
  const modeInput = form.querySelector('[name="Group.Mode"]');
  const modes = [...form.querySelectorAll('[data-capture-mode]')];
  const rows = [...form.querySelectorAll('[data-product-row]')];
  const table = form.querySelector('.production-entry__rows');
  const actions = form.querySelector('[data-capture-actions]');
  const count = actions?.querySelector('[data-capture-count]');
  const units = actions?.querySelector('[data-capture-units]');
  const review = actions?.querySelector('[data-group-preview]');
  const search = form.querySelector('#group-add-product');
  const entry = form.closest?.('.production-entry');
  const quickGuidance = form.querySelector('[data-quick-guidance]');
  const listCount = form.querySelector('[data-list-count]');
  const quantity = row => row.querySelector('[data-group-quantity]');
  const hasContent = row => Boolean(quantity(row)?.value.trim() || row.querySelector('input[name$=".Notes"]')?.value.trim());
  const validAmount = value => {
    if (!/^\d+(?:\.\d{1,4})?$/.test(value)) return null;
    const [whole, fraction = ''] = value.split('.');
    return BigInt(whole) * 10000n + BigInt(fraction.padEnd(4, '0'));
  };
  const format = value => {
    const whole = value / 10000n;
    const fraction = (value % 10000n).toString().padStart(4, '0').replace(/0+$/, '');
    return fraction ? `${whole}.${fraction}` : whole.toString();
  };
  if (entry && typeof window !== 'undefined' && window.visualViewport) {
    const updateViewport = () => entry.classList.toggle('production-entry--keyboard',
      window.visualViewport.height < window.innerHeight - 120);
    window.visualViewport.addEventListener('resize', updateViewport);
    updateViewport();
  }

  const refresh = () => {
    if (quickGuidance) quickGuidance.hidden = modeInput?.value !== 'quick';
    if (listCount) listCount.hidden = modeInput?.value === 'quick';
    let selected = 0;
    const totals = new Map();
    rows.forEach(row => {
      row.hidden = modeInput?.value === 'quick' && !hasContent(row) && row.dataset.focusProduct !== 'true';
      const amount = validAmount(quantity(row)?.value.trim() || '');
      const registered = validAmount(row.querySelector('[data-registered]')?.dataset.registered || '0') ?? 0n;
      const result = row.querySelector('[data-result-total]');
      if (result) result.textContent = amount === null ? format(registered) : format(registered + amount);
      if (amount === null || amount <= 0n) return;
      selected++;
      const unit = row.dataset.unit || '';
      totals.set(unit, (totals.get(unit) || 0n) + amount);
    });
    if (table) table.hidden = modeInput?.value === 'quick' && rows.every(row => row.hidden);
    if (count) count.textContent = `${actions.dataset.countLabel}: ${selected}`;
    if (units) units.textContent = [...totals].map(([unit, value]) => `${format(value)} ${unit}`.trim()).join(' · ');
    if (review) review.disabled = selected === 0;
    modes.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.captureMode === modeInput?.value)));
  };

  modes.forEach(button => button.addEventListener('click', () => {
    if (!modeInput) return;
    const from = modeInput.value;
    modeInput.value = button.dataset.captureMode;
    if (from === 'quick' && modeInput.value === 'list') {
      const submit = form.querySelector('[data-group-mode-submit]');
      if (submit) { form.requestSubmit(submit); return; }
    }
    refresh();
    if (modeInput.value === 'quick') search?.focus();
  }));
  form.addEventListener('input', event => {
    if (event.target.matches('[data-group-quantity], input[name$=".Notes"]')) refresh();
  });
  refresh();
  if (document.querySelector('.production-entry__receipt') && !document.querySelector('[data-production-errors].validation-summary-errors'))
    search?.focus();
})();
