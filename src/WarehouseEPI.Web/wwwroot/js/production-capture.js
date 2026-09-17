(() => {
  'use strict';
  const parse = value => /^\d+(?:\.\d{1,4})?$/.test(value) && Number.isFinite(Number(value)) ? Number(value) : NaN;
  window.ProductionCapture = { parse };
  document.querySelectorAll('form[method="post"]').forEach(form => {
    const quantities = [...form.querySelectorAll('[inputmode="decimal"]')];
    const validate = input => {
      const parsed = parse(input.value);
      const decimals = input.dataset.decimals ?? input.closest('[data-decimals]')?.dataset.decimals;
      const valid = Number.isFinite(parsed) && (decimals !== 'false' || Number.isInteger(parsed));
      input.setCustomValidity(valid ? '' : 'Usa solo punto decimal, hasta cuatro decimales y cantidades enteras si la unidad no admite fracciones.');
      return valid;
    };
    quantities.forEach(input => input.addEventListener('input', () => validate(input)));
    form.addEventListener('submit', event => {
      const invalid = quantities.find(input => !input.disabled && !validate(input));
      if (invalid) { event.preventDefault(); event.stopImmediatePropagation(); invalid.reportValidity(); }
    }, true);
    form.querySelector('[name="Warehouse.DestinationLocationId"]')?.addEventListener('change', () => {
      form.querySelector('[data-sharing-review]')?.remove();
    });
  });
  const context = document.querySelector('[data-production-capture]');
  if (!context?.dataset.failedHandler) return;
  const forms = [...document.querySelectorAll('form[method="post"]')];
  const form = forms.find(item => new URL(item.action, location.href).searchParams.get('handler') === context.dataset.failedHandler && (!context.dataset.failedStage || item.elements.namedItem('Material.StageId')?.value === context.dataset.failedStage)) || forms[0];
  if (!form) return;
  let parent = form.parentElement;
  while (parent) {
    if (parent.tagName === 'DETAILS') parent.open = true;
    if (parent.classList.contains('collapse')) parent.classList.add('show');
    parent = parent.parentElement;
  }
  const error = document.querySelector('.validation-summary-errors');
  if (error) { form.prepend(error); error.tabIndex = -1; error.focus(); }
  const versions = [...form.querySelectorAll('input[name$="ExpectedVersion"],input[name="Input.Version"]')];
  if (versions.some(input => input.value !== context.dataset.currentVersion)) {
    const review = document.createElement('button');
    review.type = 'button'; review.className = 'btn btn-warning mb-3';
    review.textContent = 'Revisar datos actualizados';
    const note = document.createElement('p');
    note.textContent = 'La orden cambió. La captura se conserva; compara las cantidades con los saldos actuales mostrados antes de confirmar esta revisión.';
    form.prepend(note, review);
    const submits = [...form.querySelectorAll('button[type="submit"]')];
    submits.forEach(button => button.disabled = true);
    review.addEventListener('click', () => {
      versions.forEach(input => input.value = context.dataset.currentVersion);
      submits.forEach(button => button.disabled = false);
      review.remove(); note.textContent = 'Datos actualizados revisados. Comprueba la captura e introduce nuevamente el NIP.';
    });
  }
})();
