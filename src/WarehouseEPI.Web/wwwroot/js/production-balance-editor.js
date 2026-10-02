(() => {
    'use strict';
    const form = document.querySelector('#balance-editor');
    if (!form) return;
    const cells = [...document.querySelectorAll('.balance-edit-cell')];
    const originalFields = [...document.querySelectorAll('[data-balance-field]')].map(el => [el, el.textContent, el.parentElement.hidden]);
    const originalPlans = [...document.querySelectorAll('[data-balance-plan]')].map(el => [el, el.textContent]);
    const originalStatuses = [...document.querySelectorAll('.production-weekly-status')].map(cell => [
        cell, cell.dataset.statusBand, cell.querySelector('[data-balance-status]')?.textContent,
        cell.querySelector('[data-balance-status-label]')?.textContent
    ]);
    const originalMetrics = [...document.querySelectorAll('[data-balance-metric]')].map(metric => [
        metric, metric.dataset.progressLevel, metric.querySelector('strong')?.textContent,
        metric.querySelector('small')?.textContent
    ]);
    const originalBreakdowns = [...document.querySelectorAll('[data-balance-breakdown]')].map(el => [el, el.textContent]);
    const originalWeekly = document.querySelector('#week-close')?.outerHTML;
    const filterValues = [...document.querySelectorAll('form[method="get"] input, form[method="get"] select, select[data-balance-week]')].map(el => [el, el.value, el.min, el.max]);
    const status = form.querySelector('[data-edit-status]');
    const errors = form.querySelector('[data-edit-errors]');
    const reviewButton = form.querySelector('[data-edit-review-button]');
    const confirm = form.querySelector('[data-edit-confirm]');
    const discard = form.querySelector('[data-edit-discard]');
    const review = form.querySelector('[data-edit-review]');
    const auth = form.querySelector('[data-edit-auth]');
    const pin = form.querySelector('#balance-edit-pin');
    const reason = form.querySelector('#balance-edit-reason');
    const reasonField = form.querySelector('[data-edit-reason-field]');
    let workspace = null;
    let previewRequest = null;
    const localRows = new Map();
    let serial = 0, timer, reviewed = null, retryPayload = null, saving = false, dirty = false;
    const valid = value => /^\d{1,14}(?:\.\d{1,4})?$/.test(value);
    const normalized = value => value.replace(/^0+(?=\d)/, '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
    const changes = () => cells.filter(el => !valid(el.value) || normalized(el.value) !== normalized(el.dataset.original));
    // Project only differences from server-calculated balances, using fixed-point quantities.
    // Planning and process applicability remain authoritative on the server.
    const quantity = value => {
        if (!/^-?\d+(?:\.\d{1,4})?$/.test(value)) return null;
        const negative = value.startsWith('-');
        const [whole, fraction = ''] = value.replace(/^-/, '').split('.');
        const result = BigInt(whole) * 10000n + BigInt(fraction.padEnd(4, '0'));
        return negative ? -result : result;
    };
    const formatQuantity = value => {
        const absolute = value < 0n ? -value : value;
        const fraction = String(absolute % 10000n).padStart(4, '0').replace(/0+$/, '');
        return `${value < 0n ? '-' : ''}${absolute / 10000n}${fraction ? '.' + fraction : ''}`;
    };
    const productionCell = cell => cell.dataset.editArea !== undefined && !cell.dataset.planLine && !cell.dataset.newPlan;
    const calculateQuantity = value => {
        const terms = String(value).trim().split(/([+-])/);
        if (terms.length < 3) return null;
        const number = term => String(term).trim().replace(',', '.');
        if (!valid(number(terms[0]))) return null;
        let total = quantity(number(terms[0]));
        for (let index = 1; index < terms.length; index += 2) {
            const amount = number(terms[index + 1]);
            if (!valid(amount)) return null;
            total += terms[index] === '+' ? quantity(amount) : -quantity(amount);
        }
        const result = formatQuantity(total);
        return valid(result) ? result : null;
    };
    const requestedQuantity = cell => productionCell(cell) ? calculateQuantity(cell.value) ?? cell.value : cell.value;
    const validCell = cell => valid(requestedQuantity(cell));
    function renderReview(result, submitted) {
        const products = new Map();
        const element = (tag, value, className) => {
            const el = document.createElement(tag);
            if (value !== undefined) el.textContent = value;
            if (className) el.className = className;
            return el;
        };
        function product(id, sku) {
            if (!products.has(id)) products.set(id, { id, sku, areas: new Map(), plans: [] });
            return products.get(id);
        }
        function transition(change) {
            const el = element('div', undefined, 'balance-review-change');
            el.append(element('span', `${formatQuantity(quantity(change.observed))} → `),
                element('strong', formatQuantity(quantity(change.requested))));
            const delta = quantity(change.requested) - quantity(change.observed);
            const signed = delta < 0n ? '−' + formatQuantity(-delta) : '+' + formatQuantity(delta);
            el.append(element('small', `${form.dataset.reviewDelta}: ${signed}`, 'd-block text-body-secondary'));
            return el;
        }
        for (const item of result.cells) {
            const change = submitted.cells.find(x => x.productId === item.cell.productId && x.area === item.cell.area && x.shift === item.cell.shift);
            if (!change) throw new Error('Reviewed capture is missing from submitted data');
            const group = product(change.productId, item.sku);
            if (!group.areas.has(change.area)) group.areas.set(change.area, new Map());
            group.areas.get(change.area).set(change.shift, change);
        }
        for (const item of result.plans || []) {
            const change = submitted.planChanges.find(x => x.lineId === item.change.lineId);
            const input = cells.find(x => x.dataset.planLine === item.change.lineId);
            if (!change || !input) throw new Error('Reviewed plan is missing from submitted data');
            product(input.closest('[data-balance-product]').dataset.balanceProduct, item.sku).plans.push({
                ...change, sequence: input.dataset.planSequence, label: form.dataset.reviewSchedule
            });
        }
        for (const item of result.newPlans || []) {
            const change = submitted.newPlans.find(x => x.operationId === item.change.operationId);
            if (!change) throw new Error('Reviewed new plan is missing from submitted data');
            product(change.productId, item.sku).plans.push({ ...change, observed: '0', label: form.dataset.newPlanLabel });
        }
        const count = result.cells.length + (result.plans?.length || 0) + (result.newPlans?.length || 0);
        review.querySelector('[data-review-summary]').textContent =
            `${products.size} ${products.size === 1 ? form.dataset.reviewProduct : form.dataset.reviewProducts} · ${count} ${count === 1 ? form.dataset.reviewChange : form.dataset.reviewChanges}`;
        const container = review.querySelector('[data-review-products]'); container.replaceChildren();
        let productIndex = 0;
        for (const group of [...products.values()].sort((a, b) => a.sku.localeCompare(b.sku) || a.id.localeCompare(b.id))) {
            const section = element('section', undefined, 'balance-review-product');
            section.setAttribute('data-review-product', group.id);
            const heading = element('h3', group.sku, 'h6 balance-review-sku');
            const headingId = `balance-review-product-${productIndex++}`;
            heading.setAttribute('id', headingId); section.setAttribute('aria-labelledby', headingId);
            section.append(heading);
            if (group.areas.size) {
                const table = element('table', undefined, 'table table-sm balance-review-table');
                const head = element('thead'); const headers = element('tr');
                for (const label of [form.dataset.reviewProcess, 'T1', 'T2']) {
                    const th = element('th', label); th.setAttribute('scope', 'col'); headers.append(th);
                }
                head.append(headers); table.append(head);
                const body = element('tbody');
                for (const [area, shifts] of [...group.areas].sort((a, b) => a[0] - b[0])) {
                    const row = element('tr'); const name = element('th', form.dataset[`reviewArea${area}`]);
                    name.setAttribute('scope', 'row'); row.append(name);
                    for (const shift of [1, 2]) {
                        const td = element('td');
                        td.append(shifts.has(shift) ? transition(shifts.get(shift)) : element('span', '—'));
                        row.append(td);
                    }
                    body.append(row);
                }
                table.append(body); section.append(table);
            }
            if (group.plans.length) {
                section.append(element('h4', form.dataset.reviewSchedule, 'h6 mt-3'));
                const list = element('dl', undefined, 'balance-review-plans');
                for (const plan of group.plans.sort((a, b) => Number(a.sequence || 0) - Number(b.sequence || 0))) {
                    const label = plan.sequence
                        ? form.dataset.reviewLine.replace('{0}', plan.sequence) : plan.label;
                    list.append(element('dt', label)); const value = element('dd'); value.append(transition(plan)); list.append(value);
                }
                section.append(list);
            }
            container.append(section);
        }
    }
    function rememberLocalRows() {
        for (const cell of cells) {
            const row = cell.closest('[data-balance-product]');
            if (!row?.querySelectorAll || localRows.has(row)) continue;
            localRows.set(row, {
                inputs: cells.filter(input => input.closest('[data-balance-product]') === row)
                    .map(input => [input, input.dataset.original]),
                fields: [...row.querySelectorAll('[data-balance-field]')].map(el => [el, quantity(el.textContent)])
            });
        }
    }
    function projectLocally() {
        for (const { inputs, fields } of localRows.values()) {
            if (inputs.some(([input, original]) => !valid(input.value) || input.dataset.original !== original ||
                ((input.dataset.planLine || input.dataset.newPlan) && normalized(input.value) !== normalized(original)))) continue;
            for (const [field, original] of fields) {
                if (original === null || !['pendingAfterShift1', 'netPending'].includes(field.dataset.balanceField)) continue;
                let delta = 0n;
                for (const [input, observed] of inputs) {
                    if (input.dataset.editArea !== field.dataset.area ||
                        (field.dataset.balanceField === 'pendingAfterShift1' && input.dataset.editShift !== '1')) continue;
                    delta += quantity(input.value) - quantity(observed);
                }
                field.textContent = formatQuantity(original - delta);
            }
        }
    }
    rememberLocalRows();
    const payload = () => ({ operationId: form.dataset.operation, weekId: form.dataset.week, date: form.dataset.date,
        cells: changes().filter(el => !el.dataset.planLine && !el.dataset.newPlan).map(el => ({ productId: el.closest('[data-balance-product]').dataset.balanceProduct,
            area: Number(el.dataset.editArea), shift: Number(el.dataset.editShift), observed: el.dataset.original, requested: requestedQuantity(el) })),
        planChanges: changes().filter(el => el.dataset.planLine).map(el => ({ lineId: el.dataset.planLine,
            observed: el.dataset.original, requested: el.value, expectedLineVersion: Number(el.dataset.lineVersion),
            expectedWeekVersion: Number(el.dataset.weekVersion) })),
        newPlans: changes().filter(el => el.dataset.newPlan).map(el => ({ operationId: el.dataset.newPlan,
            productId: el.closest('[data-balance-product]').dataset.balanceProduct, requested: el.value,
            expectedWeekVersion: Number(el.dataset.weekVersion) })),
        reason: reason.value, fingerprint: reviewed?.fingerprint || '', pin: '' });
    function replaceWeekly(html) {
        const current = document.querySelector('#week-close');
        if (!current || !html) return;
        const open = [...current.querySelectorAll('details[open]')].map(el => el.id);
        current.outerHTML = html;
        for (const id of open) document.getElementById(id)?.setAttribute('open', '');
    }
    function markWeeklyPending() {
        const weekly = document.querySelector('#week-close');
        if (!weekly) return;
        weekly.setAttribute('aria-busy', String(dirty));
        if (!dirty) return;
        let notice = weekly.querySelector('[data-weekly-preview-notice]');
        if (!notice) {
            notice = document.createElement('p'); notice.className = 'alert alert-warning py-2';
            notice.setAttribute('data-weekly-preview-notice', ''); weekly.prepend(notice);
        }
        notice.textContent = form.dataset.weeklyPending || form.dataset.pending;
    }
    function restoreProjection(restoreWeekly = true) {
        for (const [el, value, hidden] of originalFields) { el.textContent = value; el.parentElement.hidden = hidden; }
        for (const [el, value] of originalPlans) el.textContent = value;
        for (const [cell, band, value, label] of originalStatuses) {
            cell.dataset.statusBand = band;
            const number = cell.querySelector('[data-balance-status]');
            const description = cell.querySelector('[data-balance-status-label]');
            if (number) number.textContent = value;
            if (description) description.textContent = label;
        }
        for (const [metric, band, heading, details] of originalMetrics) {
            if (band) metric.dataset.progressLevel = band;
            else delete metric.dataset.progressLevel;
            const strong = metric.querySelector('strong');
            const small = metric.querySelector('small');
            if (strong) strong.textContent = heading;
            if (small) small.textContent = details;
        }
        for (const [el, value] of originalBreakdowns) el.textContent = value;
        if (restoreWeekly) replaceWeekly(originalWeekly);
    }
    function invalidate() {
        serial++; clearTimeout(timer); reviewed = null; pin.value = ''; confirm.hidden = true; auth.hidden = true; review.hidden = true;
        previewRequest?.abort(); previewRequest = null;
        dirty = changes().length > 0;
        reviewButton.disabled = !dirty || saving || changes().length > 100 || changes().some(el => !validCell(el)) || !!workspace?.blocked(); discard.disabled = !dirty || saving;
        status.textContent = dirty ? form.dataset.pending : '';
        const modifiedCells = new Set(changes());
        for (const cell of cells) {
            const modified = modifiedCells.has(cell);
            cell.classList.toggle('balance-edit-modified', modified);
            cell.setAttribute('aria-invalid', String(!validCell(cell))); cell.title = '';
            cell.nextElementSibling.hidden = !modified;
        }
        restoreProjection(!dirty);
        projectLocally();
        markWeeklyPending();
        const counter = form.querySelector('[data-edit-counter]');
        if (counter) counter.textContent = `${form.dataset.counter}: ${changes().length} / 100`;
        const actions = form.querySelector('[data-balance-actions]');
        if (actions) actions.hidden = form.dataset.editable !== 'true' && !dirty && !workspace?.hasDraft();
        workspace?.save();
    }
    async function post(url, body, signal) {
        const response = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: {
            'Content-Type': 'application/json', 'RequestVerificationToken': form.querySelector('[name="__RequestVerificationToken"]').value
        }, body: JSON.stringify(body), signal });
        if (!response.ok) throw new Error(form.dataset.error);
        return response.json();
    }
    async function preview(explicit) {
        if (explicit && !saving && !retryPayload) cells.forEach(calculateCell);
        if (!explicit && cells.some(cell => productionCell(cell) && /[+-]/.test(cell.value))) return;
        if (!dirty || saving || workspace?.blocked()) return;
        const generation = ++serial;
        previewRequest?.abort();
        const controller = new AbortController();
        previewRequest = controller;
        reviewed = null; confirm.hidden = true; pin.value = '';
        if (changes().length > 100 || changes().some(el => !valid(el.value))) { errors.textContent = form.dataset.invalid; return; }
        try {
            const submitted = payload();
            const result = await post(form.dataset.previewUrl, submitted, controller.signal);
            if (generation !== serial) return;
            errors.classList.remove('alert', 'alert-warning');
            errors.textContent = (result.errors || []).join(' ');
            if (!result.canConfirm) {
                for (const item of result.cells || []) {
                    const el = cells.find(x => x.closest('[data-balance-product]').dataset.balanceProduct === item.cell.productId &&
                        Number(x.dataset.editArea) === item.cell.area && Number(x.dataset.editShift) === item.cell.shift);
                    if (el) { if (workspace) workspace.conflict(el, String(item.current)); else el.dataset.original = String(item.current); el.title = (item.errors || []).join(' '); el.setAttribute('aria-invalid', String(!!item.errors?.length)); }
                }
                for (const item of result.plans || []) {
                    const el = cells.find(x => x.dataset.planLine === item.change.lineId);
                    if (el) {
                        if (workspace) workspace.conflict(el, String(item.current), item.currentLineVersion, item.currentWeekVersion);
                        else el.dataset.original = String(item.current);
                        if (!workspace) { el.dataset.lineVersion = String(item.currentLineVersion); el.dataset.weekVersion = String(item.currentWeekVersion); }
                        el.title = (item.errors || []).join(' '); el.setAttribute('aria-invalid', String(!!item.errors?.length));
                    }
                }
                for (const item of result.newPlans || []) {
                    const el = cells.find(x => x.dataset.newPlan === item.change.operationId);
                    if (el) {
                        if (workspace) workspace.conflict(el, "0", null, item.currentWeekVersion);
                        else el.dataset.weekVersion = String(item.currentWeekVersion);
                        el.title = (item.errors || []).join(' '); el.setAttribute('aria-invalid', String(!!item.errors?.length));
                    }
                }
                if (explicit) errors.focus();
                return;
            }
            for (const product of result.balance.products) {
                const row = document.querySelector(`[data-balance-product="${product.productId}"]`);
                if (!row) continue;
                const plan = row.querySelector('[data-balance-plan]');
                if (plan) plan.textContent = String(product.planned);
                const statusCell = row.querySelector('.production-weekly-status');
                const status = statusCell?.querySelector('[data-balance-status]') || row.querySelector('[data-balance-status]');
                if (status && product.statusRatio !== undefined) {
                    const displayedRatio = product.statusRatio >= .9995 && product.statusRatio < 1 ? .999 : product.statusRatio;
                    status.textContent = product.statusRatio === null ? (product.readyToPack?.applies === false ? form.dataset.notApplicable : form.dataset.noTarget) :
                        new Intl.NumberFormat(document.documentElement?.lang || 'es', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(displayedRatio);
                    if (statusCell) {
                        const ratio = product.statusRatio;
                        const base = product.readyToPack?.dailyCoverage?.beforeAdvance ??
                            Number(product.planned) + Number(product.readyToPack?.opening ?? 0);
                        const band = product.readyToPack?.applies === false ? 'none' : ratio === null || base <= 0 ? 'no-base' : ratio < .75 ? 'low' : ratio < 1 ? 'mid' : 'high';
                        statusCell.dataset.statusBand = band;
                        const label = statusCell.querySelector('[data-balance-status-label]');
                        if (label) label.textContent = ratio === null ? '' : band === 'no-base' ? form.dataset.statusNoBase :
                            form.dataset[`status${band[0].toUpperCase()}${band.slice(1)}`];
                    }
                }
                for (const el of row.querySelectorAll('[data-balance-field]')) {
                    const area = [product.cutting, product.sewing, product.readyToPack][Number(el.dataset.area)];
                    if (area.applies) {
                        const value = area[el.dataset.balanceField] ?? area.pending;
                        el.textContent = String(value);
                    }
                }
                for (const metric of row.querySelectorAll('[data-balance-metric]')) {
                    const area = [product.cutting, product.sewing, product.readyToPack][Number(metric.dataset.area)];
                    const value = metric.dataset.balanceMetric === 'daily' ? area?.dailyCoverage : area?.accumulatedCoverage;
                    if (!value) continue;
                    const ratio = value.ratio;
                    const band = ratio === null ? null : ratio < .75 ? 'low' : ratio < 1 ? 'mid' : 'high';
                    if (band) metric.dataset.progressLevel = band;
                    else delete metric.dataset.progressLevel;
                    const shown = ratio === null ? (value.coveredByAdvance ? form.dataset.coveredAdvance : form.dataset.noTarget) :
                        new Intl.NumberFormat(document.documentElement?.lang || 'es', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 })
                            .format(ratio >= .9995 && ratio < 1 ? .999 : ratio);
                    metric.querySelector('strong').textContent = `${metric.dataset.balanceMetric === 'daily' ? form.dataset.dailyLabel : form.dataset.accumulatedLabel}: ${shown}`;
                    const small = metric.querySelector('small');
                    if (small) small.textContent = metric.dataset.balanceMetric === 'daily'
                        ? `${value.covered} / ${value.beforeAdvance} · ${form.dataset.appliedAdvance} ${value.advanceApplied}`
                        : `${value.covered} / ${value.target}`;
                }
                for (const el of row.querySelectorAll('[data-balance-breakdown]')) {
                    const area = [product.cutting, product.sewing, product.readyToPack][Number(el.dataset.area)];
                    const value = el.dataset.balanceBreakdown === 'daily' ? area?.dailyCoverage : area?.accumulatedCoverage;
                    if (!value) continue;
                    el.textContent = el.dataset.balanceBreakdown === 'daily'
                        ? `${form.dataset.coveredLabel}: ${value.covered} · ${form.dataset.baseLabel}: ${value.beforeAdvance} · ${form.dataset.targetLabel}: ${value.target} · ${form.dataset.appliedAdvance}: ${value.advanceApplied}`
                        : `${form.dataset.coveredLabel}: ${value.covered} · ${form.dataset.targetLabel}: ${value.target}`;
                }
            }
            replaceWeekly(result.weeklyHtml);
            const weekly = document.querySelector('#week-close');
            if (weekly) {
                weekly.setAttribute('aria-busy', 'false');
                const badge = weekly.querySelector('[data-weekly-preview-notice]') || document.createElement('p');
                badge.className = 'alert alert-warning py-2'; badge.setAttribute('data-weekly-preview-notice', '');
                badge.textContent = form.dataset.previewLabel; weekly.prepend(badge);
            }
            status.textContent = form.dataset.previewLabel;
            if (explicit) {
                renderReview(result, submitted);
                reviewed = result;
                review.hidden = false; auth.hidden = false; confirm.hidden = false;
                reason.required = result.requiresReason;
                if (reasonField) reasonField.hidden = !result.requiresReason;
                const instructions = result.requiresReason ? form.dataset.admin : result.requiresAdmin ? form.dataset.planAdmin : form.dataset.review;
                status.textContent = `${form.dataset.previewLabel} · ${instructions}`;
                (result.requiresReason ? reason : pin).focus();
            }
        } catch (error) { if (generation === serial && error.name !== 'AbortError') errors.textContent = form.dataset.error; }
        finally { if (previewRequest === controller) previewRequest = null; }
    }
    function calculateCell(cell) {
        if (!productionCell(cell) || cell.disabled || saving || retryPayload) return false;
        const result = calculateQuantity(cell.value);
        if (result === null) return false;
        cell.value = result;
        workspace?.edited(cell);
        invalidate();
        return true;
    }
    function bindCell(cell) {
        let entered;
        if (productionCell(cell)) {
            // The decimal keyboard often omits + and - on tablets.
            cell.inputMode = 'text'; cell.spellcheck = false;
        }
        cell.addEventListener('focus', () => { entered = cell.value; if (!productionCell(cell)) cell.select(); });
        cell.addEventListener('input', () => { workspace?.edited(cell); invalidate(); timer = setTimeout(() => preview(false), 350); });
        cell.addEventListener('blur', () => { if (calculateCell(cell)) timer = setTimeout(() => preview(false), 350); });
        cell.addEventListener('keydown', event => {
            if (event.key === 'Enter' && !event.isComposing) {
                event.preventDefault();
                if (calculateCell(cell)) timer = setTimeout(() => preview(false), 350);
                const ordered = [...document.querySelectorAll('.balance-edit-cell')].filter(el => !el.disabled && el.getClientRects().length);
                const next = ordered[ordered.indexOf(cell) + 1];
                if (next) next.focus(); else reviewButton.focus();
            }
            if (event.key === 'Escape') { event.preventDefault(); cell.value = entered; invalidate(); preview(false); cell.blur(); }
        });
        cell.nextElementSibling.addEventListener('click', () => { if (saving || retryPayload) return; cell.value = cell.dataset.original; invalidate(); preview(false); });
    }
    cells.forEach(bindCell);
    function refreshCells() {
        for (const cell of document.querySelectorAll('.balance-edit-cell')) {
            if (cells.includes(cell)) continue;
            cells.push(cell); bindCell(cell);
        }
        for (const el of document.querySelectorAll('[data-balance-field]')) {
            if (!originalFields.some(x => x[0] === el)) originalFields.push([el, el.textContent, el.parentElement.hidden]);
        }
        for (const el of document.querySelectorAll('[data-balance-plan]')) {
            if (!originalPlans.some(x => x[0] === el)) originalPlans.push([el, el.textContent]);
        }
        for (const cell of document.querySelectorAll('.production-weekly-status')) {
            if (!originalStatuses.some(x => x[0] === cell)) originalStatuses.push([cell, cell.dataset.statusBand,
                cell.querySelector('[data-balance-status]')?.textContent, cell.querySelector('[data-balance-status-label]')?.textContent]);
        }
        for (const metric of document.querySelectorAll('[data-balance-metric]')) {
            if (!originalMetrics.some(x => x[0] === metric)) originalMetrics.push([metric, metric.dataset.progressLevel,
                metric.querySelector('strong')?.textContent, metric.querySelector('small')?.textContent]);
        }
        for (const el of document.querySelectorAll('[data-balance-breakdown]')) {
            if (!originalBreakdowns.some(x => x[0] === el)) originalBreakdowns.push([el, el.textContent]);
        }
        rememberLocalRows();
    }
    workspace = window.ProductionBalanceWorkspace?.(form, { cells, changes, payload, invalidate, post, refreshCells, valid, validCell });
    discard.addEventListener('click', () => { workspace?.discard(); for (const cell of cells) cell.value = cell.dataset.original; errors.textContent = ''; errors.classList.remove('alert', 'alert-warning'); invalidate(); });
    reviewButton.addEventListener('click', () => { clearTimeout(timer); preview(true); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!reviewed || saving || !form.reportValidity()) return;
        const body = retryPayload || payload(); body.pin = pin.value; pin.value = '';
        workspace?.begin();
        saving = true; serial++; confirm.disabled = true; reviewButton.disabled = true; discard.disabled = true;
        cells.forEach(el => { el.disabled = true; });
        try {
            const result = await post(form.dataset.confirmUrl, body); body.pin = ''; retryPayload = null;
            if (result.success) { workspace?.success(result); dirty = false; window.ProductionBalancePresentation?.prepareReload(); location.reload(); return; }
            workspace?.failed();
            errors.classList.toggle('alert', result.status === 'RoleNotAllowed');
            errors.classList.toggle('alert-warning', result.status === 'RoleNotAllowed');
            errors.textContent = (result.errors || [form.dataset.error]).join(' ');
        } catch (_) { body.pin = ''; retryPayload = body; errors.textContent = form.dataset.error; }
        finally { body.pin = ''; pin.value = ''; saving = false; confirm.disabled = false; cells.forEach(el => { el.disabled = !!retryPayload; }); reason.disabled = !!retryPayload; }
        if (!retryPayload) invalidate();
        errors.focus();
    });
    window.addEventListener('beforeunload', event => { if (dirty && !workspace?.save()) { event.preventDefault(); event.returnValue = ''; } });
    document.addEventListener('submit', event => {
        if (event.target !== form && workspace?.save()) { dirty = false; return; }
        if (event.target !== form && dirty && !window.confirm(workspace ? form.dataset.storageError : form.dataset.unsaved)) { event.preventDefault(); event.stopImmediatePropagation(); for (const [el, value, min, max] of filterValues) { el.value = value; if (min !== undefined) el.min = min; if (max !== undefined) el.max = max; } }
        else if (event.target !== form) dirty = false;
    }, true);
    document.addEventListener('click', event => {
        const link = event.target.closest('a[href]');
        if (link && dirty && !workspace?.save()) {
            if (!window.confirm(workspace ? form.dataset.storageError : form.dataset.unsaved)) { event.preventDefault(); event.stopImmediatePropagation(); }
            else { for (const cell of cells) cell.value = cell.dataset.original; invalidate(); }
        }
    }, true);
})();
