(() => {
    'use strict';
    const form = document.querySelector('#balance-editor');
    if (!form) return;
    const cells = [...document.querySelectorAll('.balance-edit-cell')];
    const originalFields = [...document.querySelectorAll('[data-balance-field]')].map(el => [el, el.textContent, el.parentElement.hidden]);
    const filterValues = [...document.querySelectorAll('form[method="get"] input, form[method="get"] select')].map(el => [el, el.value, el.min, el.max]);
    const status = form.querySelector('[data-edit-status]');
    const errors = form.querySelector('[data-edit-errors]');
    const reviewButton = form.querySelector('[data-edit-review-button]');
    const confirm = form.querySelector('[data-edit-confirm]');
    const discard = form.querySelector('[data-edit-discard]');
    const review = form.querySelector('[data-edit-review]');
    const auth = form.querySelector('[data-edit-auth]');
    const pin = form.querySelector('#balance-edit-pin');
    const reason = form.querySelector('#balance-edit-reason');
    let serial = 0, timer, reviewed = null, retryPayload = null, saving = false, dirty = false;
    const valid = value => /^\d{1,14}(?:\.\d{1,4})?$/.test(value);
    const changes = () => cells.filter(el => !valid(el.value) || Number(el.value) !== Number(el.dataset.original));
    const payload = () => ({ operationId: form.dataset.operation, weekId: form.dataset.week, date: form.dataset.date,
        cells: changes().map(el => ({ productId: el.closest('[data-balance-product]').dataset.balanceProduct,
            area: Number(el.dataset.editArea), shift: Number(el.dataset.editShift), observed: el.dataset.original, requested: el.value })),
        reason: reason.value, fingerprint: reviewed?.fingerprint || '', pin: '' });
    function invalidate() {
        serial++; clearTimeout(timer); reviewed = null; pin.value = ''; confirm.hidden = true; auth.hidden = true; review.hidden = true;
        dirty = changes().length > 0;
        reviewButton.disabled = !dirty || saving; discard.disabled = !dirty || saving;
        status.textContent = dirty ? form.dataset.pending : '';
        for (const cell of cells) {
            const modified = changes().includes(cell);
            cell.classList.toggle('balance-edit-modified', modified);
            cell.setAttribute('aria-invalid', String(!valid(cell.value))); cell.title = '';
            cell.nextElementSibling.hidden = !modified;
        }
        if (!dirty) for (const [el, value, hidden] of originalFields) { el.textContent = value; el.parentElement.hidden = hidden; }
    }
    async function post(url, body) {
        const response = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: {
            'Content-Type': 'application/json', 'RequestVerificationToken': form.querySelector('[name="__RequestVerificationToken"]').value
        }, body: JSON.stringify(body) });
        if (!response.ok) throw new Error(form.dataset.error);
        return response.json();
    }
    async function preview(explicit) {
        if (!dirty || saving) return;
        const generation = ++serial;
        reviewed = null; confirm.hidden = true; pin.value = '';
        if (changes().length > 100 || changes().some(el => !valid(el.value))) { errors.textContent = form.dataset.invalid; return; }
        try {
            const result = await post(form.dataset.previewUrl, payload());
            if (generation !== serial) return;
            errors.textContent = (result.errors || []).join(' ');
            if (!result.canConfirm) {
                for (const item of result.cells || []) {
                    const el = cells.find(x => x.closest('[data-balance-product]').dataset.balanceProduct === item.cell.productId &&
                        Number(x.dataset.editArea) === item.cell.area && Number(x.dataset.editShift) === item.cell.shift);
                    if (el) { el.dataset.original = String(item.current); el.title = (item.errors || []).join(' '); el.setAttribute('aria-invalid', String(!!item.errors?.length)); }
                }
                if (explicit) errors.focus();
                return;
            }
            for (const product of result.balance.products) {
                const row = document.querySelector(`[data-balance-product="${product.productId}"]`);
                if (!row) continue;
                for (const el of row.querySelectorAll('[data-balance-field]')) {
                    const area = [product.cutting, product.sewing, product.readyToPack][Number(el.dataset.area)];
                    if (area.applies) {
                        const value = area[el.dataset.balanceField] ?? area.pending;
                        el.textContent = String(value);
                        if (["extra", "toReconcile"].includes(el.dataset.balanceField)) el.parentElement.hidden = value <= 0;
                    }
                }
            }
            if (explicit) {
                reviewed = result;
                const tbody = review.querySelector('tbody'); tbody.replaceChildren();
                for (const item of result.cells) {
                    const tr = document.createElement('tr');
                    const input = cells.find(x => x.closest('[data-balance-product]').dataset.balanceProduct === item.cell.productId &&
                        Number(x.dataset.editArea) === item.cell.area && Number(x.dataset.editShift) === item.cell.shift);
                    for (const value of [item.sku, input?.getAttribute('aria-label'), item.current, item.cell.requested, item.cell.requested - item.current]) {
                        const td = document.createElement('td'); td.textContent = String(value); tr.append(td);
                    }
                    tbody.append(tr);
                }
                review.hidden = false; auth.hidden = false; confirm.hidden = false;
                reason.required = result.requiresAdmin;
                status.textContent = result.requiresAdmin ? form.dataset.admin : form.dataset.review;
                (result.requiresAdmin ? reason : pin).focus();
            }
        } catch (_) { if (generation === serial) errors.textContent = form.dataset.error; }
    }
    for (const cell of cells) {
        let entered;
        cell.addEventListener('focus', () => { entered = cell.value; cell.select(); });
        cell.addEventListener('input', () => { invalidate(); timer = setTimeout(() => preview(false), 350); });
        cell.addEventListener('keydown', event => {
            if (event.key === 'Enter') { event.preventDefault(); cell.blur(); }
            if (event.key === 'Escape') { event.preventDefault(); cell.value = entered; invalidate(); preview(false); cell.blur(); }
        });
        cell.nextElementSibling.addEventListener('click', () => { if (saving || retryPayload) return; cell.value = cell.dataset.original; invalidate(); preview(false); });
    }
    discard.addEventListener('click', () => { for (const cell of cells) cell.value = cell.dataset.original; errors.textContent = ''; invalidate(); });
    reviewButton.addEventListener('click', () => { clearTimeout(timer); preview(true); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!reviewed || saving || !form.reportValidity()) return;
        const body = retryPayload || payload(); body.pin = pin.value; pin.value = '';
        saving = true; serial++; confirm.disabled = true; reviewButton.disabled = true; discard.disabled = true;
        cells.forEach(el => { el.disabled = true; });
        try {
            const result = await post(form.dataset.confirmUrl, body); body.pin = ''; retryPayload = null;
            if (result.success) { dirty = false; location.reload(); return; }
            errors.textContent = (result.errors || [form.dataset.error]).join(' ');
        } catch (_) { body.pin = ''; retryPayload = body; errors.textContent = form.dataset.error; }
        finally { body.pin = ''; pin.value = ''; saving = false; confirm.disabled = false; cells.forEach(el => { el.disabled = !!retryPayload; }); reason.disabled = !!retryPayload; }
        if (!retryPayload) invalidate();
        errors.focus();
    });
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    document.addEventListener('submit', event => {
        if (event.target !== form && dirty && !window.confirm(form.dataset.unsaved)) { event.preventDefault(); event.stopImmediatePropagation(); for (const [el, value, min, max] of filterValues) { el.value = value; if (min !== undefined) el.min = min; if (max !== undefined) el.max = max; } }
        else if (event.target !== form) dirty = false;
    }, true);
    document.addEventListener('click', event => {
        const link = event.target.closest('a[href]');
        if (link && dirty) {
            if (!window.confirm(form.dataset.unsaved)) { event.preventDefault(); event.stopImmediatePropagation(); }
            else { for (const cell of cells) cell.value = cell.dataset.original; invalidate(); }
        }
    }, true);
})();
