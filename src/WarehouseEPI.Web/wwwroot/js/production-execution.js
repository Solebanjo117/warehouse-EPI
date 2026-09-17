(() => {
    const form = document.querySelector('[data-execution-form]');
    if (!form) return;
    const action = form.querySelector('[data-execution-action]');
    const update = () => {
        form.querySelectorAll('[data-execution-panel]').forEach(panel => {
            panel.hidden = !panel.dataset.executionPanel.split(' ').includes(action.value);
            panel.querySelectorAll('input, select, button').forEach(input => { input.disabled = panel.hidden; });
        });
        const category = ['principal', 'definitive'].includes(action.value) ? 'Difference' : 'Adjustment';
        form.querySelectorAll('[data-reason-category]').forEach(option => {
            option.hidden = option.disabled = option.dataset.reasonCategory !== category;
            if (option.disabled && option.selected) option.parentElement.value = '';
        });
    };
    action.addEventListener('change', update);
    form.querySelector('[data-execution-scale]')?.addEventListener('click', () => {
        const amount = window.ProductionCapture.parse(form.querySelector('[data-execution-authorized]').value);
        if (!Number.isFinite(amount) || amount <= 0) return;
        form.querySelectorAll('[data-execution-ratio]').forEach(input => { input.value = (amount * Number(input.dataset.executionRatio)).toFixed(4); });
    });
    form.addEventListener('submit', event => {
        if (action.value !== 'retain') return;
        const lines = [...form.querySelectorAll('[data-retention-row]')].map(row => {
            const selected = row.querySelector('input[type="checkbox"]').checked;
            const quantity = row.querySelector('[inputmode="decimal"]').value;
            const retained = selected ? window.ProductionCapture.parse(quantity) : 0;
            const remaining = Math.max(0, Number(row.dataset.available) - retained);
            const caseLabel = row.querySelector('select').selectedOptions[0]?.textContent || '';
            return row.dataset.label + ': retener ' + retained + (selected ? ' para '+caseLabel : '') + '; '+remaining+' '+(row.dataset.kind === 'warehouse' ? 'se libera de almacén' : 'requiere devolución WIP');
        });
        if (!window.confirm('Revisar selección completa de reservas:\n' + lines.join('\n'))) event.preventDefault();
    });
    update();
})();
