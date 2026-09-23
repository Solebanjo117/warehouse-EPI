(() => {
  'use strict';
  document.querySelector('[data-batch-filter]')?.addEventListener('change', event => event.currentTarget.closest('form')?.requestSubmit());
  document.querySelector('[data-production-label-print]')?.addEventListener('click', () => window.print());
  const form = document.querySelector('[data-batch-result-form]');
  if (!form) return;
  const stage = form.querySelector('[data-result-stage]');
  const input = form.querySelector('[data-result-input]');
  const mode = form.querySelector('[data-result-mode]');
  const rework = form.querySelector('[data-rework-case]');
  const rows = [...form.querySelectorAll('[data-result-material]')];
  const summary = form.querySelector('[data-consumption-summary]');
  const propose = form.querySelector('[data-propose-consumption]');
  const parse = window.ProductionCapture.parse;
  const number = value => Math.round(value * 10000) / 10000;
  const selected = row => row.querySelector('input[type="checkbox"]');
  const quantity = row => row.querySelector('[data-result-material-quantity]');
  const groups = () => { const result = new Map(); rows.filter(row=>row.dataset.stageId===stage.value).forEach(row=>{const group=result.get(row.dataset.productId)||[];group.push(row);result.set(row.dataset.productId,group);});return result; };
  const update = () => {
    propose.disabled = mode.value === 'true';
    const lines = [];
    for (const material of groups().values()) {
      const q = quantity(material[0]);
      const required = mode.value === 'true' ? 0 : number(parse(input.value) * Number(q.dataset.ratio));
      const proposed = material.some(row=>row.dataset.proposed!==undefined) ? number(material.reduce((sum,row)=>sum+Number(row.dataset.proposed||0),0)) : '—';
      const available = number(material.reduce((sum, row) => sum + Number(quantity(row).dataset.pending), 0));
      const captured = number(material.reduce((sum, row) => sum + (selected(row).checked ? parse(quantity(row).value) : 0), 0));
      lines.push(`${material[0].dataset.sku}: requerido ${Number.isFinite(required) ? required : '—'}, disponible ${available}, propuesto ${proposed}, capturado ${Number.isFinite(captured) ? captured : 'inválido'} ${material[0].dataset.unit}.`);
    }
    const incompatible = rows.some(row => selected(row).checked && row.dataset.stageId !== stage.value);
    if (incompatible) lines.push('Hay materiales seleccionados de otro proceso. Desmárcalos antes de confirmar; la captura se conserva.');
    summary.textContent = lines.join(' ');
    stage.setCustomValidity(incompatible ? 'Revisa materiales de otro proceso.' : '');
    const good = parse(form.elements.namedItem('BatchResult.GoodQuantity').value);
    const scrap = parse(form.elements.namedItem('BatchResult.ScrapQuantity').value);
    const pending = parse(form.elements.namedItem('BatchResult.ReworkQuantity').value);
    const total = number(good + scrap + pending);
    const balanced = Number.isFinite(total) && total === parse(input.value);
    form.querySelector('[data-result-balance]').textContent = `Procesada ${input.value} = buena + retrabajo + merma (${Number.isFinite(total) ? total : 'cantidad inválida'}). ${balanced ? 'Cantidades completas.' : 'Revisa la suma.'}`;
    input.setCustomValidity(balanced ? '' : 'Buena, retrabajo y merma deben sumar la cantidad procesada.');
  };
  propose.addEventListener('click', () => {
    if (mode.value === 'true' || !(parse(input.value) > 0)) return;
    if (rows.some(row => selected(row).checked || parse(quantity(row).value) > 0) && !window.confirm('¿Reemplazar la selección y cantidades por una nueva propuesta del proceso?')) return;
    for (const material of groups().values()) {
      let remaining = number(parse(input.value) * Number(quantity(material[0]).dataset.ratio));
      material.sort((a,b) => Number(selected(b).checked)-Number(selected(a).checked) || a.dataset.created.localeCompare(b.dataset.created) || a.dataset.issueId.localeCompare(b.dataset.issueId));
      for (const row of material) {
        const q = quantity(row);
        let amount = Math.min(remaining, Number(q.dataset.pending));
        amount = row.dataset.decimals === 'false' ? Math.floor(amount) : number(amount);
        q.value = String(amount); row.dataset.proposed=String(amount); selected(row).checked = amount > 0;
        remaining = number(remaining - amount);
      }
    }
    update();
    summary.textContent += ' Propuesta aplicada; revisa faltantes y diferencias con el requerido antes de confirmar.';
  });
  rework.addEventListener('change', () => {
    if (mode.value === 'true' && rework.selectedOptions[0]?.dataset.stageId) stage.value = rework.selectedOptions[0].dataset.stageId;
    update();
  });
  mode.addEventListener('change', () => {
    if(mode.value==='true' && rows.some(row=>selected(row).checked)){if(!window.confirm('Al atender retrabajo solo se registra material adicional. ¿Limpiar el consumo seleccionado?')){mode.value='false';return;}rows.forEach(row=>{selected(row).checked=false;quantity(row).value='0';});}
    if (mode.value === 'false') rework.value = '';
    update();
  });
  form.addEventListener('input', update);
  form.addEventListener('change', update);
  form.addEventListener('submit', event => { update(); if(!form.checkValidity()) {event.preventDefault();form.reportValidity();} });
  update();
})();
