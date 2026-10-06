(() => {
    const form = document.querySelector('[data-overview-filter]');
    const status = form?.querySelector('[data-overview-status]');
    if (!form || !status) return;
    const original = status.textContent;
    form.addEventListener('submit', () => { status.textContent = status.dataset.loading; form.setAttribute('aria-busy', 'true'); });
    window.addEventListener('pageshow', () => { status.textContent = original; form.removeAttribute('aria-busy'); });
})();
