(() => {
    'use strict';
    const root = document.querySelector('[data-balance-root]');
    if (!root) return;
    const actions = root.querySelector('[data-balance-actions]');
    const editor = root.querySelector('#balance-editor');
    let lastField;
    const reveal = input => {
        if (!input) return;
        for (let details = input.closest('details'); details; details = details.parentElement.closest('details')) details.open = true;
    };
    const returnToWork = field => {
        const restore = () => {
            const target = field && root.contains(field) ? field : root.querySelector('#balance-product-search') ||
                root.querySelector('.balance-edit-cell') || root.querySelector('.production-weekly-sku') || root.querySelector('[data-balance-week]');
            if (!target) return;
            reveal(target);
            if (target.matches('th')) target.tabIndex = -1;
            target.focus({ preventScroll: true });
            const viewport = target.closest('[data-balance-scroll]');
            if (window.innerWidth >= 992 && viewport) {
                viewport.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
                target.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
                const top = root.querySelector('[data-balance-scroll-top]');
                const leading = top && !top.hidden ? top : viewport;
                const minimum = (root.querySelector('[data-balance-context]')?.getBoundingClientRect().bottom || 0) + 8;
                if (leading.getBoundingClientRect().top < minimum) window.scrollBy({ top: leading.getBoundingClientRect().top - minimum, behavior: 'instant' });
            } else target.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
        };
        // Wait until native reload restoration and layout have settled.
        const ready = () => requestAnimationFrame(() => requestAnimationFrame(restore));
        if (document.readyState === 'complete') ready();
        else window.addEventListener('load', ready, { once: true });
    };
    const prepareReload = () => {
        const field = lastField || root.querySelector('.balance-edit-cell');
        if (!editor || !field) return;
        try {
            // Only a UI anchor in this history entry; never draft quantities or secrets.
            history.replaceState({ ...history.state, epiBalanceReturn: {
                week: editor.dataset.week, date: editor.dataset.date, fieldId: field.id,
                productId: field.closest('[data-balance-product]')?.dataset.balanceProduct,
                restoration: history.scrollRestoration
            } }, '', location.href);
            history.scrollRestoration = 'manual';
        } catch (_) { window.scrollTo({ top: 0, behavior: 'instant' }); }
    };
    root.addEventListener('focusin', event => {
        if (event.target.matches('.balance-edit-cell')) lastField = event.target;
    });
    const pending = history.state?.epiBalanceReturn;
    if (pending) {
        if (editor?.dataset.week === pending.week && editor.dataset.date === pending.date) {
            const row = [...root.querySelectorAll('[data-balance-product]')].find(el => el.dataset.balanceProduct === pending.productId);
            returnToWork(document.getElementById(pending.fieldId) || row?.querySelector('.balance-edit-cell') || row?.querySelector('.production-weekly-sku'));
        }
        const clean = () => {
            try { const state = { ...history.state }; delete state.epiBalanceReturn; history.replaceState(state, '', location.href); } catch (_) { /* Optional UI anchor. */ }
            history.scrollRestoration = pending.restoration || 'auto';
        };
        if (document.readyState === 'complete') requestAnimationFrame(() => requestAnimationFrame(clean));
        else window.addEventListener('load', () => requestAnimationFrame(() => requestAnimationFrame(clean)), { once: true });
    }
    window.ProductionBalancePresentation = { reveal, returnToWork, prepareReload };
    const measure = () => root.style.setProperty('--balance-actions-height', `${actions && !actions.hidden ? actions.getBoundingClientRect().height : 0}px`);
    if (actions && typeof ResizeObserver !== 'undefined') new ResizeObserver(measure).observe(actions);
    window.addEventListener('resize', measure);
    measure();
})();
