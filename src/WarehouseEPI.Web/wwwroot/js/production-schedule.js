(() => {
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
