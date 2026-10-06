(() => {
  const form = document.querySelector('[data-rack-editor]');
  if (!form) return;

  const positions = [...form.querySelectorAll('[data-rack-position]')];
  const summary = form.querySelector('[data-rack-role-summary]');
  const columnsInput = form.querySelector('[data-rack-columns-input]');
  const levelsInput = form.querySelector('[data-rack-levels-input]');
  const keypad = form.querySelector('[data-rack-keypad]');
  const formatSummary = form.querySelector('[data-rack-format-summary]');
  const formatError = form.querySelector('[data-rack-format-error]');
  let columns = Number(columnsInput?.value);
  let levels = Number(levelsInput?.value);
  const refresh = () => {
    let wip = 0;
    let storage = 0;
    for (const position of positions) {
      const present = position.querySelector('[data-rack-present]').checked;
      position.classList.toggle('present', present);
      position.classList.toggle('absent', !present);
      const role = position.querySelector('[data-rack-role]');
      const wipInput = position.querySelector('[data-rack-wip]');
      role.disabled = !present;
      wipInput.checked = present && role.value === 'Wip';
      if (present) {
        if (wipInput.checked) wip++;
        else storage++;
      }
    }
    const wipPositions = form.querySelector('[data-rack-wip-positions]');
    if (wipPositions) wipPositions.textContent = positions
      .filter(position => position.querySelector('[data-rack-wip]').checked)
      .map(position => position.querySelector('label span').textContent).join(', ');
    if (summary) {
      const state = wip && storage ? summary.dataset.mixedLabel : wip ? summary.dataset.wipLabel : summary.dataset.storageLabel;
      summary.textContent = `${state} · ${wip} WIP · ${storage} ${summary.dataset.storageLabel}`;
    }
  };
  const changeFormat = () => {
    const nextColumns = Number(columnsInput.value);
    const nextLevels = Number(levelsInput.value);
    const capacity = nextColumns * nextLevels;
    if (positions.some(position => Number(position.dataset.pallet) > capacity &&
        position.querySelector('[data-rack-present]').disabled &&
        position.querySelector('[data-rack-present]').checked)) {
      columnsInput.value = String(columns);
      levelsInput.value = String(levels);
      formatError.textContent = formatError.dataset.protectedMessage;
      formatError.hidden = false;
      return;
    }
    columns = nextColumns;
    levels = nextLevels;
    formatError.hidden = true;
    keypad.dataset.rackColumns = String(columns);
    formatSummary.textContent = `${columns} × ${levels}`;
    for (let level = levels - 1; level >= 0; level--) {
      for (let column = 1; column <= columns; column++) {
        const position = positions.find(item => Number(item.dataset.pallet) === level * columns + column);
        keypad.append(position);
      }
    }
    for (const position of positions) {
      const included = Number(position.dataset.pallet) <= capacity;
      position.hidden = !included;
      position.querySelector('[data-rack-present]').checked = included;
    }
    refresh();
  };
  columnsInput?.addEventListener('change', changeFormat);
  levelsInput?.addEventListener('change', changeFormat);
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
