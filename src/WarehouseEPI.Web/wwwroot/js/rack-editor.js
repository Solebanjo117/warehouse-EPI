(() => {
  const form = document.querySelector('[data-rack-editor]');
  if (!form) return;

  const positions = [...form.querySelectorAll('[data-rack-position]')];
  const summary = form.querySelector('[data-rack-role-summary]');
  const refresh = () => {
    let wip = 0;
    let storage = 0;
    for (const position of positions) {
      const present = position.querySelector('[data-rack-present]').checked;
      const role = position.querySelector('[data-rack-role]');
      const wipInput = position.querySelector('[data-rack-wip]');
      role.disabled = !present;
      wipInput.checked = present && role.value === 'Wip';
      if (present) {
        if (wipInput.checked) wip++;
        else storage++;
      }
    }
    if (summary) {
      const state = wip && storage ? summary.dataset.mixedLabel : wip ? summary.dataset.wipLabel : summary.dataset.storageLabel;
      summary.textContent = `${state} · ${wip} WIP · ${storage} ${summary.dataset.storageLabel}`;
    }
  };
  form.addEventListener('change', (event) => {
    if (event.target.matches('[data-rack-present], [data-rack-role]')) refresh();
  });
  form.querySelector('[data-rack-all-storage]')?.addEventListener('click', () => {
    for (const position of positions) if (position.querySelector('[data-rack-present]').checked) position.querySelector('[data-rack-role]').value = 'Storage';
    refresh();
  });
  form.querySelector('[data-rack-all-wip]')?.addEventListener('click', () => {
    for (const position of positions) if (position.querySelector('[data-rack-present]').checked) position.querySelector('[data-rack-role]').value = 'Wip';
    refresh();
  });
  refresh();

  const pinInput = form.querySelector('[data-rack-pin]');
  const saveButton = form.querySelector('[data-rack-save]');
  if (!pinInput || !saveButton) return;

  pinInput.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || event.isComposing) return;

    event.preventDefault();
    form.requestSubmit(saveButton);
  });
})();
