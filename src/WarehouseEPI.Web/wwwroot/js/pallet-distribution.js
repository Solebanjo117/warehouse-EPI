(() => {
  const root = document.querySelector('[data-pallet-distribution]');
  if (!root) return;
  const fields = root.querySelector('[data-distribution-fields]');
  const rows = root.querySelector('[data-distribution-rows]');
  const toggle = root.querySelector('[data-distribution-toggle]');
  const form = root.closest('form');
  const calculateTotal = root.dataset.calculateTotal === 'true';
  const quantityInput = form?.querySelector('[data-quantity]');
  const manualQuantity = form?.querySelector('[data-manual-quantity]');
  const maximum = 999999999999999999n;
  let currentValid = false;
  let total = root.dataset.total;
  let unit = root.dataset.unit;
  let decimals = root.dataset.decimals !== 'false';
  const enabled = () => !toggle || toggle.checked;
  const scaled = value => {
    const s = String(value).trim().replace(',', '.');
    if (s.length > 24 || !/^\d+(\.\d{1,4})?$/.test(s)) return null;
    const [a, b = ''] = s.split('.');
    return BigInt(a) * 10000n + BigInt(b.padEnd(4, '0'));
  };
  const display = n => `${n / 10000n}${n % 10000n ? '.' + String(n % 10000n).padStart(4, '0').replace(/0+$/, '') : ''}`;
  const inputs = () => [...rows.querySelectorAll('[data-part-quantity]')];
  const refresh = () => {
    fields.classList.toggle('d-none', !enabled());
    fields.disabled = !enabled();
    const values = inputs();
    const target = scaled(total);
    let sum = 0n;
    let valid = values.length >= 2 && values.length <= 100;
    values.forEach((input, i) => {
      input.name = `Distribution.Quantities[${i}]`;
      input.required = enabled();
      input.step = decimals ? '0.0001' : '1';
      input.min = decimals ? '0.0001' : '1';
      input.setCustomValidity('');
      const value = scaled(input.value);
      if (value === null || value <= 0n || value > maximum || (!decimals && value % 10000n !== 0n)) valid = false;
      if (value !== null) sum += value;
      const row = input.closest('[data-distribution-row]');
      row.querySelector('[data-part-label]').textContent = `${root.dataset.partLabel} ${i + 1}`;
      row.querySelector('[data-part-unit]').textContent = unit;
      row.querySelector('[data-remove-part]').disabled = values.length <= 2;
    });
    valid = valid && (calculateTotal ? sum > 0n && sum <= maximum : target !== null && target > 0n && sum === target);
    if (calculateTotal && quantityInput) {
      manualQuantity?.classList.toggle('d-none', enabled());
      quantityInput.readOnly = enabled();
      if (enabled()) quantityInput.value = display(sum);
    }
    root.querySelector('[data-add-part]').disabled = values.length >= 100;
    root.querySelector('[data-distribution-total]').textContent = `${calculateTotal ? display(sum) : total || '0'} ${unit}`;
    if (!calculateTotal) {
      root.querySelector('[data-distribution-sum]').textContent = `${display(sum)} ${unit}`;
      const difference = (target ?? 0n) - sum;
      root.querySelector('[data-distribution-difference]').textContent = `${difference < 0n ? '-' : ''}${display(difference < 0n ? -difference : difference)} ${unit}`;
    }
    root.querySelector('[data-distribution-error]').textContent = enabled() && !valid ? root.dataset.invalid : '';
    if (enabled() && !valid && values[0]) values[0].setCustomValidity(root.dataset.invalid);
    currentValid = !enabled() || valid;
    return currentValid;
  };
  const changed = () => {
    refresh();
    root.dispatchEvent(new CustomEvent('palletdistributionchange', { bubbles: true }));
  };
  root.addEventListener('input', changed);
  toggle?.addEventListener('change', () => {
    changed();
    (enabled() ? inputs()[0] : quantityInput)?.focus();
  });
  root.addEventListener('click', event => {
    const remove = event.target.closest('[data-remove-part]');
    if (remove && inputs().length > 2) {
      const index = [...rows.children].indexOf(remove.closest('[data-distribution-row]'));
      remove.closest('[data-distribution-row]').remove(); changed();
      inputs()[Math.min(index, inputs().length - 1)].focus();
    }
    if (event.target.closest('[data-add-part]') && inputs().length < 100) {
      const row = rows.firstElementChild.cloneNode(true);
      row.querySelector('input').value = '';
      rows.append(row); changed(); row.querySelector('input').focus();
    }
  });
  const summary = () => enabled() ? inputs().map((input, i) => `${root.dataset.partLabel} ${i + 1}: ${input.value} ${unit}`).join(' · ') : '';
  window.WarehousePalletDistribution = {
    update(context) {
      total = context.total; unit = context.unit; decimals = context.allowsDecimals;
      if (toggle) {
        if (!context.staging && toggle.checked) {
          toggle.checked = false;
          root.querySelector('[data-distribution-warning]').textContent = root.dataset.destinationWarning;
        }
        toggle.disabled = !context.staging;
      }
      refresh();
    }, summary,
    isValid: () => currentValid,
    quantityTarget: () => calculateTotal && enabled() ? inputs()[0] : null
  };
  form?.addEventListener('submit', event => { if (!refresh()) { event.preventDefault(); form.reportValidity(); } });
  const review = document.querySelector('[data-split-review]');
  if (review) {
    const modalElement = document.getElementById('split-confirm');
    const pin = modalElement.querySelector('[data-pin-input]');
    review.addEventListener('click', () => {
      pin.required = false;
      if (!refresh() || !form.reportValidity()) { form.reportValidity(); return; }
      modalElement.querySelector('[data-split-summary]').textContent = summary();
      pin.required = true;
      bootstrap.Modal.getOrCreateInstance(modalElement).show();
    });
    modalElement.addEventListener('shown.bs.modal', () => pin.focus());
    modalElement.addEventListener('hidden.bs.modal', () => { pin.value = ''; pin.required = false; });
  }
  refresh();
})();
