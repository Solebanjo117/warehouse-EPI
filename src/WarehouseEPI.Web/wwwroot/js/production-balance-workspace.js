(() => {
    'use strict';
    window.ProductionBalanceWorkspace = (form, api) => {
        if (!form.dataset.restoreUrl) return null;
        const key = `epi.balance.v1:${form.dataset.week}:${form.dataset.date}`;
        const status = form.querySelector('[data-workspace-status]');
        const conflicts = form.querySelector('[data-workspace-conflicts]');
        const search = form.querySelector('#balance-product-search');
        const results = form.querySelector('#balance-product-results');
        const searchStatus = form.querySelector('[data-search-status]');
        const reason = form.querySelector('#balance-edit-reason');
        const pending = new Map(), added = new Set();
        const normalized = value => String(value).replace(/^0+(?=\d)/, '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
        let restoring = true, hardBlocked = form.dataset.editable !== 'true', uncertain = false, last = { area: '0', shift: '1' };
        let originalDraft = null, planningReadOnly = false, generation = 0, searchTimer, active = -1, options = [];
        const id = cell => cell.dataset.planLine ? `plan:${cell.dataset.planLine}` : cell.dataset.newPlan ? `new:${cell.closest('[data-balance-product]').dataset.balanceProduct}` :
            `${cell.closest('[data-balance-product]').dataset.balanceProduct}:${cell.dataset.editArea}:${cell.dataset.editShift}`;
        const text = (tag, value, className) => { const el = document.createElement(tag); el.textContent = value; if (className) el.className = className; return el; };
        function describe() {
            const changedCells = new Set(api.changes());
            for (const cell of api.cells) {
                let hint = cell.parentElement.querySelector('.balance-cell-hint');
                if (!hint) { hint = text('small', '', 'balance-cell-hint'); cell.parentElement.append(hint); }
                const changed = changedCells.has(cell);
                hint.textContent = `${form.dataset.recorded}: ${cell.dataset.original}${changed ? ` · ${form.dataset.cellPending}` : ''}`;
                let error = cell.parentElement.querySelector('.balance-cell-error');
                if (!error) { error = text('small', '', 'balance-cell-error text-danger'); error.id = `balance-error-${api.cells.indexOf(cell)}`; cell.setAttribute('aria-describedby', error.id); cell.parentElement.append(error); }
                error.textContent = !(api.validCell ? api.validCell(cell) : api.valid(cell.value)) ? form.dataset.invalid : '';
            }
        }
        function save() {
            if (restoring) return true;
            describe();
            if (hardBlocked && originalDraft) return true; // Keep blocked/corrupt data intact until explicit discard.
            const edit = api.payload();
            // UI-only recovery data cannot authorize a later confirmation.
            delete edit.pin; delete edit.fingerprint;
            const products = [...new Set([...added, ...api.changes().map(x => x.closest('[data-balance-product]').dataset.balanceProduct)])];
            const draft = { version: 1, edit, products, added: [...added], last, uncertain, planningReadOnly,
                conflicts: [...pending].map(([cell, value]) => ({ key: id(cell), ...value })) };
            try {
                if (!products.length && !edit.planChanges.length && !edit.newPlans.length) sessionStorage.removeItem(key);
                else {
                    const encoded = JSON.stringify(draft);
                    sessionStorage.setItem(key, encoded);
                    if (sessionStorage.getItem(key) !== encoded) throw new Error('Storage verification failed');
                }
                return true;
            } catch (_) { status.textContent = form.dataset.storageWarning; return false; }
        }
        function conflict(cell, current, lineVersion, weekVersion, force = false) {
            if (!force && api.valid(cell.dataset.original) && normalized(cell.dataset.original) === normalized(current) &&
                (lineVersion == null || Number(cell.dataset.lineVersion) === lineVersion) &&
                (weekVersion == null || Number(cell.dataset.weekVersion) === weekVersion)) return;
            pending.set(cell, { current, lineVersion, weekVersion });
            drawConflicts();
        }
        function resolve(cell, useCurrent) {
            const change = pending.get(cell);
            if (!change) return;
            cell.dataset.original = change.current;
            if (change.lineVersion != null) cell.dataset.lineVersion = String(change.lineVersion);
            if (change.weekVersion != null) cell.dataset.weekVersion = String(change.weekVersion);
            if (useCurrent) cell.value = change.current;
            pending.delete(cell); drawConflicts(); api.invalidate(); window.ProductionBalancePresentation?.reveal(cell); cell.focus();
        }
        function drawConflicts() {
            conflicts.replaceChildren();
            for (const [cell, change] of pending) {
                const box = text('div', '', 'alert alert-warning');
                box.append(text('strong', cell.getAttribute('aria-label')),
                    text('p', form.dataset.conflict), text('p', `${form.dataset.originalLabel}: ${cell.dataset.original} · ${form.dataset.current}: ${change.current} · ${form.dataset.mine}: ${cell.value}`));
                for (const [label, current] of [[form.dataset.useCurrent, true], [form.dataset.useMine, false]]) {
                    const button = text('button', label, 'btn btn-outline-primary me-2'); button.type = 'button';
                    button.addEventListener('click', () => resolve(cell, current)); box.append(button);
                }
                conflicts.append(box);
            }
            form.querySelector('[data-edit-review-button]').disabled = true;
        }
        function appendRows(html, label) {
            const template = document.createElement('template'); template.innerHTML = `<table>${html}</table>`;
            for (const body of template.content.querySelectorAll('tbody')) {
                const row = body.querySelector('[data-balance-product]');
                if (!row || document.querySelector(`[data-balance-product="${row.dataset.balanceProduct}"]`)) continue;
                if (label) row.querySelector('th').append(text('small', label, 'd-block text-primary'));
                document.querySelector('#production-balance-table').append(body);
            }
            api.refreshCells();
        }
        function focusProduct(productId) {
            const row = document.querySelector(`[data-balance-product="${productId}"]`);
            const field = row?.querySelector(`[data-edit-area="${last.area}"][data-edit-shift="${last.shift}"]:not(:disabled)`) ||
                row?.querySelector('[data-edit-area]:not(:disabled)');
            window.ProductionBalancePresentation?.reveal(field); field?.focus();
        }
        async function selectProduct(productId) {
            resetLookup();
            if (hardBlocked || uncertain) return;
            const existing = document.querySelector(`[data-balance-product="${productId}"]`);
            if (!existing) {
                try {
                    const url = new URL(form.dataset.rowsUrl, location.href);
                    url.searchParams.set('weekId', form.dataset.week); url.searchParams.set('date', form.dataset.date); url.searchParams.set('productId', productId);
                    const response = await fetch(url, { credentials: 'same-origin' });
                    if (!response.ok) throw new Error();
                    const result = await response.json();
                    if (!result.editable) throw new Error();
                    appendRows(result.html, form.dataset.added); added.add(productId); save();
                } catch (_) { searchStatus.textContent = form.dataset.error; return; }
            }
            search.value = ''; focusProduct(productId);
        }
        function resetLookup() {
            generation++; clearTimeout(searchTimer); active = -1; options = [];
            results.replaceChildren(); results.hidden = true; searchStatus.textContent = '';
            search.setAttribute('aria-expanded', 'false'); search.removeAttribute('aria-activedescendant');
        }
        async function lookup(group, offset = 0) {
            const term = search.value.trim();
            if (!term) { resetLookup(); return; }
            const request = ++generation; active = -1;
            if (group == null) { results.replaceChildren(); options = []; }
            searchStatus.textContent = form.dataset.searching;
            try {
                const url = new URL(form.dataset.searchUrl, location.href);
                url.searchParams.set('date', form.dataset.date); url.searchParams.set('text', term);
                if (group != null) { url.searchParams.set('group', group); url.searchParams.set('offset', offset); }
                const response = await fetch(url, { credentials: 'same-origin' });
                if (!response.ok) throw new Error();
                const groups = await response.json(); if (request !== generation) return;
                for (const item of groups) {
                    if (!item.items.length) continue;
                    const label = [form.dataset.programmed, form.dataset.previous, form.dataset.catalog][item.group];
                    let section = results.querySelector(`[data-product-group="${item.group}"]`);
                    if (!section) {
                        section = document.createElement('div');
                        section.className = 'production-entry__suggestion-group';
                        section.dataset.productGroup = item.group;
                        section.setAttribute('role', 'group');
                        section.setAttribute('aria-label', label);
                        section.append(text('div', label, 'production-entry__suggestion-heading'));
                        results.append(section);
                    }
                    for (const product of item.items) {
                        const button = text('button', product.sku, 'list-group-item list-group-item-action text-start');
                        button.type = 'button'; button.setAttribute('role', 'option'); button.setAttribute('aria-selected', 'false');
                        button.id = `balance-option-${options.length}`;
                        button.addEventListener('pointerdown', event => event.preventDefault());
                        button.addEventListener('click', () => selectProduct(product.id)); options.push(button); section.append(button);
                    }
                    if (item.hasMore) {
                        const more = text('button', form.dataset.more, 'list-group-item list-group-item-action text-primary'); more.type = 'button';
                        more.addEventListener('click', () => { more.remove(); lookup(item.group, item.offset + item.items.length); }); section.append(more);
                    }
                }
                results.hidden = !options.length; search.setAttribute('aria-expanded', String(options.length > 0));
                searchStatus.textContent = options.length ? '' : form.dataset.noResults;
            } catch (_) { if (request === generation) searchStatus.textContent = form.dataset.error; }
        }
        search?.addEventListener('input', () => { resetLookup(); if (search.value.trim()) searchTimer = setTimeout(() => lookup(), 200); });
        search?.addEventListener('keydown', event => {
            if (event.key === 'Escape') { resetLookup(); return; }
            if (event.key === 'Enter') {
                event.preventDefault(); clearTimeout(searchTimer);
                if (!results.hidden && options.length) options[Math.max(active, 0)].click(); else lookup();
            }
            if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
                event.preventDefault(); if (!options.length) return;
                active = (active + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length;
                options.forEach((el, index) => el.setAttribute('aria-selected', String(index === active)));
                search.setAttribute('aria-activedescendant', options[active].id); options[active].scrollIntoView({ block: 'nearest' });
            }
        });
        const syncKeyboard = () => document.body.classList.toggle('balance-keyboard-active',
            !!window.visualViewport && window.visualViewport.height < window.innerHeight * .75 && document.activeElement.matches('input, textarea'));
        window.visualViewport?.addEventListener('resize', syncKeyboard);
        document.addEventListener('focusout', () => setTimeout(syncKeyboard, 0));
        document.addEventListener('focusin', event => {
            if (event.target.matches('.balance-edit-cell')) {
                last = { area: event.target.dataset.editArea ?? last.area, shift: event.target.dataset.editShift ?? last.shift, key: id(event.target) }; save();
            }
            syncKeyboard();
        });
        reason.addEventListener('input', () => api.invalidate());
        async function restore() {
            try {
                const receipt = sessionStorage.getItem(`${key}:receipt`);
                if (receipt) { status.textContent = `${form.dataset.registered} · ${JSON.parse(receipt).recordId}`; sessionStorage.removeItem(`${key}:receipt`); }
                const raw = sessionStorage.getItem(key);
                if (!raw) { restoring = false; describe(); return; }
                originalDraft = raw;
                const actions = form.querySelector('[data-balance-actions]');
                if (actions) actions.hidden = false;
                const draft = JSON.parse(raw);
                if (draft.version !== 1 || !draft.edit || !Array.isArray(draft.products) || draft.edit.weekId !== form.dataset.week || draft.edit.date !== form.dataset.date) throw new Error();
                const result = await api.post(form.dataset.restoreUrl, { edit: draft.edit, products: draft.products });
                if (result.registered) {
                    if (result.operationId === draft.edit.operationId) sessionStorage.removeItem(key);
                    status.textContent = `${form.dataset.registered} · ${result.recordId}`;
                    window.ProductionBalancePresentation?.returnToWork(api.cells.find(x => id(x) === draft.last?.key));
                    originalDraft = null; restoring = false; return;
                }
                if (result.conflict) throw new Error();
                planningReadOnly = !!draft.planningReadOnly;
                if (draft.edit.planChanges?.length || draft.edit.newPlans?.length) {
                    // Preserve retired planning separately; only production totals can be recovered for editing.
                    const planningKey = `${key}:planning:${draft.edit.operationId}`;
                    const planning = JSON.stringify({ planChanges: draft.edit.planChanges || [], newPlans: draft.edit.newPlans || [] });
                    sessionStorage.setItem(planningKey, planning);
                    if (sessionStorage.getItem(planningKey) !== planning) throw new Error('Storage verification failed');
                    if (!result.operationId || result.operationId === draft.edit.operationId) throw new Error('Recovery operation ID missing');
                    draft.edit.operationId = result.operationId;
                    planningReadOnly = true;
                }
                appendRows(result.html, form.dataset.outside);
                form.dataset.operation = draft.edit.operationId;
                for (const product of draft.added || []) added.add(product);
                last = draft.last || last; reason.value = draft.edit.reason || '';
                hardBlocked = result.blocked || result.unavailable.length > 0;
                for (const item of result.cells) {
                    const cell = api.cells.find(x => !x.dataset.planLine && !x.dataset.newPlan && id(x) === `${item.cell.productId}:${item.cell.area}:${item.cell.shift}`);
                    if (!cell || item.blocked) { hardBlocked = true; continue; }
                    cell.value = item.cell.requested ?? ''; cell.dataset.original = item.cell.observed;
                    if (item.conflict) conflict(cell, item.current, null, null, true);
                }
                for (const item of draft.conflicts || []) {
                    const cell = api.cells.find(x => id(x) === item.key);
                    if (cell && !pending.has(cell)) conflict(cell, item.current, item.lineVersion, item.weekVersion, true);
                }
                uncertain = false; restoring = false;
                status.textContent = hardBlocked ? form.dataset.blocked : form.dataset.recovered;
                if (planningReadOnly) status.textContent += ` ${form.dataset.planningReadOnly}`;
                api.invalidate();
                if (!hardBlocked && !pending.size && !document.activeElement.matches('input, textarea')) {
                    const field = api.cells.find(x => id(x) === last.key);
                    window.ProductionBalancePresentation?.reveal(field); field?.focus();
                }
            } catch (_) {
                restoring = false;
                if (originalDraft) { hardBlocked = true; status.textContent = form.dataset.blocked; }
                else status.textContent = form.dataset.storageWarning;
            }
            if (hardBlocked && originalDraft) {
                form.querySelector('[data-edit-discard]').disabled = false;
                api.cells.forEach(cell => { cell.disabled = true; });
                if (search) search.disabled = true;
            }
        }
        // Defer so the editor has assigned the workspace before recovery calls invalidate.
        Promise.resolve().then(restore);
        return {
            save, conflict, hasDraft: () => !!originalDraft, blocked: () => restoring || hardBlocked || uncertain || pending.size > 0,
            edited(cell) { if (pending.has(cell)) resolve(cell, false); },
            begin() { uncertain = true; save(); }, failed() { uncertain = false; save(); },
            success(result) { if (result.operationId !== form.dataset.operation) return; try { sessionStorage.removeItem(key); sessionStorage.setItem(`${key}:receipt`, JSON.stringify({ recordId: result.recordId })); } catch (_) { /* Server recovery remains idempotent. */ } },
            discard() {
                try { sessionStorage.removeItem(key); } catch (_) { status.textContent = form.dataset.storageWarning; }
                originalDraft = null; added.clear(); pending.clear(); conflicts.replaceChildren(); uncertain = false;
                hardBlocked = form.dataset.editable !== 'true'; status.textContent = '';
                api.cells.forEach(cell => { cell.disabled = hardBlocked; });
                if (search) search.disabled = hardBlocked;
            }
        };
    };
})();
