document.addEventListener('DOMContentLoaded', () => {
    document.querySelector('[data-production-print]')?.addEventListener('click', () => window.print());
});
