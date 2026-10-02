(() => {
  const form = document.querySelector('[data-capture-group]');
  if (!form) return;
  const modeInput = form.querySelector('[name="Group.Mode"]');
  const modes = [...form.querySelectorAll('[data-capture-mode]')];
  const rows = [...form.querySelectorAll('[data-product-row]')];
  const matrix = form.dataset?.matrix === 'true';
  const cells = matrix ? [...form.querySelectorAll('[data-capture-cell]')] : rows;
  const productRow = cell => matrix ? cell.closest('[data-product-row]') : cell;
  const focusArea = form.querySelector('[name="Group.FocusArea"]');
  const scanDestination = form.querySelector('[data-scan-destination]');
  const scanArea = form.querySelector('[data-scan-area]');
  const table = form.querySelector('.production-entry__rows');
  const actions = form.querySelector('[data-capture-actions]');
  const count = actions?.querySelector('[data-capture-count]');
  const units = actions?.querySelector('[data-capture-units]');
  const review = actions?.querySelector('[data-group-preview]');
  const search = form.querySelector('#group-add-product');
  const entry = form.closest?.('.production-entry');
  const quickGuidance = form.querySelector('[data-quick-guidance]');
  const listCount = form.querySelector('[data-list-count]');
  const onlyQuantity = form.querySelector('[data-only-quantity]');
  const filterEmpty = form.querySelector('[data-capture-filter-empty]');
  const editor = form.querySelector('[data-capture-editor]');
  const reviewPanel = form.querySelector('[data-group-review]');
  const touched = new Set();
  const undo = new Map();
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
    const selectedProducts = new Set();
    let selected = 0;
    let invalid = false;
    const totals = new Map();
    cells.forEach(row => {
      if (!matrix) row.hidden = modeInput?.value === 'quick' && !hasContent(row) && row.dataset.focusProduct !== 'true';
      const input = quantity(row);
      const value = input?.value || '';
      const amount = validAmount(value);
      const tooLarge = amount !== null && amount > 999999999999999999n;
      const inactive = row.dataset.inactive === 'true' && amount !== null && amount > 0n;
      const bad = value.trim() !== '' && (amount === null || tooLarge || inactive);
      invalid ||= bad;
      if (input?.dataset) input.dataset.invalid = String(bad);
      if (!matrix && onlyQuantity?.checked && (!value.trim() || amount === 0n) && row.dataset.focusProduct !== 'true' && !undo.has(row)) row.hidden = true;
      if (!matrix && undo.has(row)) row.hidden = false;
      const error = row.querySelector('[data-quantity-error]');
      if (touched.has(input) && error) {
        error.textContent = bad ? (inactive ? form.dataset.inactiveError : tooLarge ? form.dataset.rangeError : form.dataset.quantityError) : '';
        input.setAttribute('aria-invalid', String(bad));
        input.classList.toggle('is-invalid', bad);
      }
      const registered = validAmount(row.querySelector('[data-registered]')?.dataset.registered || '0') ?? 0n;
      const result = row.querySelector('[data-result-total]');
      if (result) result.textContent = bad ? '—' : format(registered + (amount ?? 0n));
      if (bad || amount === null || amount <= 0n) return;
      selectedProducts.add(productRow(row));
      selected = selectedProducts.size;
      const unit = (matrix ? `${row.dataset.areaLabel} · ` : '') + (row.dataset.unit || '');
      totals.set(unit, (totals.get(unit) || 0n) + amount);
    });
    if (matrix) {
      rows.forEach(row => {
        const parts = [...row.querySelectorAll('[data-capture-cell]')];
        const hasQuantity = parts.some(cell => { const value = quantity(cell)?.value || ''; return value.trim() && validAmount(value) !== 0n; });
        const keep = row.dataset.focusProduct === 'true' || parts.some(cell => undo.has(cell));
        row.hidden = !keep && ((modeInput?.value === 'quick' && !parts.some(hasContent)) || (onlyQuantity?.checked && !hasQuantity));
      });
      if (scanDestination) scanDestination.hidden = modeInput?.value !== 'quick';
      if (scanArea) scanArea.textContent = cells.find(cell => cell.dataset.area === (focusArea?.value || '0'))?.dataset.areaLabel || '';
    }
    if (table) table.hidden = modeInput?.value === 'quick' && rows.every(row => row.hidden);
    if (filterEmpty) filterEmpty.hidden = !onlyQuantity?.checked || rows.some(row => !row.hidden);
    if (count) count.textContent = `${actions.dataset.countLabel}: ${selected}`;
    if (units) units.textContent = [...totals].map(([unit, value]) => `${format(value)} ${unit}`.trim()).join(' · ');
    if (review) review.disabled = selected === 0 || invalid || form.dataset?.restoreConflict === 'true';
    modes.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.captureMode === modeInput?.value)));
  };

  const invalidateReview = () => {
    if (editor) editor.hidden = false;
    if (reviewPanel) reviewPanel.hidden = true;
    const confirm = form.querySelector('[data-group-confirm]');
    if (confirm) confirm.disabled = true;
    const pin = form.querySelector('[name="Group.Pin"]');
    if (pin) pin.value = '';
    const fingerprint = form.querySelector('[name="Group.Fingerprint"]');
    if (fingerprint) fingerprint.value = '';
  };
  if (editor && reviewPanel) editor.hidden = true;
  form.querySelector('[data-back-to-capture]')?.addEventListener('click', () => {
    invalidateReview();
    (rows.find(row => quantity(row)?.value.trim())?.querySelector('[data-group-quantity]') || search)?.focus();
  });
  onlyQuantity?.addEventListener('change', () => {
    rows.forEach(row => { delete row.dataset.focusProduct; });
    refresh();
  });
  cells.forEach(row => {
    const input = quantity(row);
    const notes = row.querySelector('input[name$=".Notes"]');
    const undoButton = row.querySelector('[data-undo-row]');
    const showValidation = () => { touched.add(input); refresh(); };
    input?.addEventListener?.('focus', () => {
      if (matrix && focusArea) { focusArea.value = row.dataset.area; refresh(); }
    });
    input?.addEventListener?.('blur', showValidation);
    input?.addEventListener?.('keydown', event => {
      if (event.key === 'Enter') showValidation();
    }, true);
    const changed = () => {
      touched.add(input);
      invalidateReview();
      refresh();
      input.dispatchEvent(new Event('input', { bubbles: true }));
    };
    row.querySelector('[data-clear-row]')?.addEventListener('click', () => {
      undo.set(row, { quantity: input.value, notes: notes?.value || '' });
      input.value = '';
      if (notes) notes.value = '';
      if (undoButton) undoButton.hidden = false;
      changed();
      undoButton?.focus();
    });
    undoButton?.addEventListener('click', () => {
      const previous = undo.get(row);
      if (!previous) return;
      input.value = previous.quantity;
      if (notes) notes.value = previous.notes;
      undo.delete(row);
      undoButton.hidden = true;
      changed();
      input.focus();
    });
  });
  form.addEventListener('submit', event => {
    if (event.submitter?.matches('[data-group-preview], [data-group-confirm]')) {
      cells.forEach(row => touched.add(quantity(row)));
      refresh();
      const firstInvalid = cells.find(row => quantity(row)?.dataset?.invalid === 'true');
      if (firstInvalid || review?.disabled) {
        event.preventDefault();
        firstInvalid?.querySelector('[data-group-quantity]')?.focus();
        return;
      }
    }
    if (event.defaultPrevented || form.dataset?.submitting) return;
    if (event.submitter) {
      event.submitter.dataset.originalText = event.submitter.textContent;
      event.submitter.textContent = event.submitter.matches('[data-group-confirm]') ? form.dataset.registering : form.dataset.working;
      event.submitter.setAttribute('aria-disabled', 'true');
    }
  }, true);

  modes.forEach(button => button.addEventListener('click', () => {
    if (!modeInput) return;
    const from = modeInput.value;
    modeInput.value = button.dataset.captureMode;
    if (from === 'quick' && modeInput.value === 'list') {
      const submit = form.querySelector('[data-group-mode-submit]');
      if (submit) { form.requestSubmit(submit); return; }
    }
    refresh();
    if (matrix) modeInput.dispatchEvent(new Event('change', { bubbles: true }));
    if (modeInput.value === 'quick') search?.focus();
  }));
  form.addEventListener('input', event => {
    if (event.target.matches('[data-group-quantity], input[name$=".Notes"]')) refresh();
  });
  refresh();
  if (matrix) {
    const added = rows.find(row => row.dataset.focusProduct === 'true');
    added?.querySelector(`[data-capture-cell][data-area="${focusArea?.value || '0'}"] [data-group-quantity]`)?.focus();
  }
  if (typeof window !== 'undefined') {
    window.addEventListener?.('pageshow', event => {
      delete form.dataset.submitting;
      form.querySelectorAll('[data-original-text]').forEach(button => {
        button.textContent = button.dataset.originalText;
        button.removeAttribute('aria-disabled');
      });
      if (event.persisted) invalidateReview();
    });
    document.querySelector('[data-change-capture-date]')?.addEventListener('click', () => {
      const context = document.querySelector('[data-capture-context]');
      if (context) context.open = true;
      document.querySelector('#group-week')?.focus();
    });
    document.querySelector('#group-date')?.addEventListener('change', event => {
      const label = document.querySelector('[data-readable-date]');
      if (label && event.target.value) {
        const [year, month, day] = event.target.value.split('-').map(Number);
        label.textContent = new Intl.DateTimeFormat(document.documentElement.lang || 'es', { day: '2-digit', month: 'short', year: 'numeric' }).format(new Date(year, month - 1, day));
      }
    });
  }
  if (document.querySelector('.production-entry__receipt') && !document.querySelector('[data-production-errors].validation-summary-errors'))
    search?.focus();
})();
