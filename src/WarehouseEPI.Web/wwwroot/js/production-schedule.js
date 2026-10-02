(() => {
  const sameQuantity = (left, right) => {
    const a = String(left ?? '').trim().replace(',', '.');
    const b = String(right ?? '').trim().replace(',', '.');
    if (a === '' || b === '') return a === b;
    const pattern = /^\d{1,14}(?:\.\d{1,4})?$/;
    if (!pattern.test(a) || !pattern.test(b)) return false;
    const scale = value => {
      const [integer, fraction = ''] = value.split('.');
      return BigInt(integer) * 10000n + BigInt(fraction.padEnd(4, '0'));
    };
    return scale(a) === scale(b);
  };
  // One definition for live editing, stored recovery, navigation and opening.
  const inspect = state => {
    if (!state) return { pending: false, blocksOpening: false, status: 'saved' };
    const localChanges = Object.values(state.skuDayTargets || {}).some(targets => Object.keys(targets).length > 0) || (state.rows || []).some(row =>
      !row.originalDetails && !row.rowDeletionSnapshot ||
      row.originalDetails && JSON.stringify(row.details) !== JSON.stringify(row.originalDetails) ||
      (row.cells || []).some(cell => cell.removed ? !!cell.lineId :
        cell.lineId ? !sameQuantity(cell.value, cell.original) : String(cell.value ?? '').trim() !== '')) ||
      (state.openingChangeCount ?? Object.values(state.openings?.values || state.openings || {}).filter(value => String(value).trim() !== '').length) > 0;
    const prepared = !!(state.deferredPreparation || state.preview?.length || state.openingPreview?.length);
    const excludedProducts = new Set(state.excludedProducts || []);
    const actionableRows = (state.rows || []).some(row => !excludedProducts.has(row.productId) && row.included !== false && (row.error || !row.productId && row.sku ||
      row.originalDetails && JSON.stringify(row.details) !== JSON.stringify(row.originalDetails) ||
      (row.cells || []).some(cell => cell.copyIncluded !== false && (cell.removed ? !!cell.lineId :
        cell.lineId ? !sameQuantity(cell.value, cell.original) : String(cell.value ?? '').trim() !== '' && !sameQuantity(cell.value, '0')))));
    const excluded = state.openings?.excluded || [];
    const openingChanges = state.openingChangeCount ?? Object.entries(state.openings?.values || state.openings || {})
      .filter(([key, value]) => !excluded.includes(key) && String(value).trim() !== '').length;
    const changed = !!(actionableRows || openingChanges ||
      Object.entries(state.skuDayTargets || {}).some(([id, targets]) => !excludedProducts.has(id) && Object.keys(targets).length));
    const unmergedPreview = !!(state.preview?.length || state.openingPreview?.length);
    const status = state.saving ? 'saving' : state.pendingSent ? 'uncertain' :
      state.recoveryPending || state.needsReconcile ? 'recovery' : unmergedPreview ? 'prepared' : changed ? 'changed' : 'saved';
    const blocksOpening = status !== 'saved';
    // Empty/excluded suggestions remain recoverable and clearable without claiming unsaved plan changes.
    return { pending: blocksOpening || localChanges || prepared, blocksOpening, status, changed, prepared };
  };
  window.ProductionSchedulePreparation = { inspect };
  const context = document.querySelector('[data-schedule-context]');
  if (context) {
    const storageKey = `warehouse-epi:schedule:${context.dataset.userId}:${context.dataset.weekId}`;
    const current = () => {
      if (window.ProductionWeekWorkspace) return window.ProductionWeekWorkspace.state();
      try { return inspect(JSON.parse(localStorage.getItem(storageKey) || 'null')); }
      catch { return { pending: true, blocksOpening: true, status: 'recovery' }; }
    };
    const status = context.querySelector('[data-schedule-preparation-status]');
    const publish = document.querySelector('[data-schedule-publish]');
    const confirm = publish?.querySelector('[data-schedule-publish-confirm]');
    const pin = publish?.querySelector('[name="Publish.Pin"]');
    const warning = document.querySelector('[data-schedule-pending-warning]');
    const update = () => {
      const state = current();
      if (status) {
        status.textContent = context.dataset[`state${state.status[0].toUpperCase()}${state.status.slice(1)}`];
        status.dataset.preparationState = state.status;
      }
      if (confirm) confirm.disabled = publish.dataset.serverBlocked === 'true' || state.blocksOpening || publish.dataset.submitting === 'true';
      if (pin) pin.disabled = publish.dataset.serverBlocked === 'true' || state.blocksOpening;
      if (warning) warning.hidden = !state.blocksOpening;
    };
    window.addEventListener('schedulepreparationchange', update);
    window.addEventListener('storage', event => { if (event.key === storageKey) update(); });
    document.querySelector('[data-schedule-open-review]')?.addEventListener('click', async event => {
      event.preventDefault();
      if (window.ProductionWeekWorkspace) await window.ProductionWeekWorkspace.openReview();
      else location.assign(current().blocksOpening ? context.dataset.programUrl : context.dataset.reviewUrl);
    });
    publish?.addEventListener('submit', event => {
      if (publish.dataset.serverBlocked === 'true' || current().blocksOpening || publish.dataset.submitting === 'true') {
        event.preventDefault(); update(); return;
      }
      publish.dataset.submitting = 'true'; confirm.disabled = true;
    });
    window.addEventListener('pageshow', () => {
      if (publish) delete publish.dataset.submitting;
      update();
      if (pin && !pin.disabled) {
        pin.scrollIntoView({ block: 'center', behavior: 'instant' });
        pin.focus({ preventScroll: true });
      }
    });
    update();
  }
  const modal = document.querySelector('[data-schedule-status-modal]');
  if (modal) {
    const trigger = document.querySelector('[data-schedule-status-trigger]');
    modal.addEventListener('hidden.bs.modal', () => {
      trigger?.focus();
      const route = new URL(location.href);
      if (['close', 'reopen'].includes(route.searchParams.get('ActionPanel'))) {
        route.searchParams.delete('ActionPanel'); history.replaceState(null, '', route);
      }
    });
    if (modal.dataset.autoShow === 'true' && window.bootstrap)
      window.bootstrap.Modal.getOrCreateInstance(modal).show(trigger);
  }
  document.querySelectorAll('[data-schedule-confirm]').forEach(form => {
    form.addEventListener('submit', event => {
      if (form.dataset.submitting === 'true') { event.preventDefault(); return; }
      form.dataset.submitting = 'true';
      form.querySelectorAll('button').forEach(button => { button.disabled = true; });
    });
    window.addEventListener('pageshow', () => {
      delete form.dataset.submitting;
      form.querySelectorAll('button').forEach(button => { button.disabled = false; });
    });
  });
  document.querySelector('[data-schedule-week-select]')?.addEventListener('change', event => {
    const select = event.currentTarget;
    if (select.value) select.form?.requestSubmit();
  });
  const editor = document.querySelector('[data-schedule-editor]');
  if (!editor) return;
  let changed = false;
  let submitting = false;
  const staged = document.querySelector('[data-schedule-staged]');
  const route = new URLSearchParams(window.location.search);
  if ((route.get('AddLine') === 'true' || route.get('handler') === 'StageLine') &&
      !document.querySelector('.validation-summary-errors')) {
    editor.querySelector('[data-product-input]')?.focus();
  }
  editor.addEventListener('input', event => {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement) {
      changed = true;
      if (event.target.id === 'Line_OriginalAnnotation1') editor.querySelector('#Line_OriginalAnnotation1Kind').value = 'Text';
      if (event.target.id === 'Line_OriginalAnnotation2') editor.querySelector('#Line_OriginalAnnotation2Kind').value = 'Text';
    }
  });
  const confirm = document.querySelector('form[action*="SaveBatch"]');
  const showUnadded = () => {
    confirm?.querySelector('[data-schedule-unadded]')?.classList.remove('d-none');
    editor.querySelector('button[type="submit"], button:not([type])')?.focus();
  };
  confirm?.addEventListener('submit', event => {
    if (!changed) return;
    event.preventDefault();
    showUnadded();
  });
  document.querySelectorAll('button[form="remove-staged-line"]').forEach(button => {
    button.addEventListener('click', event => {
      if (!changed) return;
      event.preventDefault();
      showUnadded();
    });
  });
  document.querySelectorAll('[data-schedule-editor], [data-schedule-batch-action]').forEach(form => {
    form.addEventListener('submit', event => {
      if (event.defaultPrevented) return;
      changed = false;
      submitting = true;
    });
  });
  document.querySelector('[data-schedule-discard]')?.addEventListener('click', () => { submitting = true; });
  window.addEventListener('beforeunload', event => {
    if (submitting || (!changed && !staged)) return;
    event.preventDefault();
    event.returnValue = '';
  });
})();
