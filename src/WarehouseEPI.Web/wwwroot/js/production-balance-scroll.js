(() => {
    'use strict';
    const top = document.querySelector('[data-balance-scroll-top]');
    const viewport = document.querySelector('[data-balance-scroll]');
    const table = viewport?.querySelector('table');
    const spacer = top?.querySelector('[data-balance-scroll-width]');
    if (!top || !viewport || !table || !spacer) return;

    const mirrored = new WeakMap();
    let previousMaximum = null;
    const move = (target, position) => {
        if (Math.abs(target.scrollLeft - position) <= 1) return;
        target.scrollLeft = position;
        mirrored.set(target, target.scrollLeft);
    };
    const synchronize = (source, target) => {
        const expected = mirrored.get(source);
        mirrored.delete(source);
        // Ignore delayed events from our own writes: they must not rewind
        // a newer native drag, keyboard scroll or touch gesture on the other bar.
        if (expected !== undefined && Math.abs(source.scrollLeft - expected) <= 1) return;
        move(target, source.scrollLeft);
    };
    const refresh = () => {
        // Match the usable width, excluding the table's vertical scrollbar.
        // Otherwise the two horizontal bars have different maximum positions.
        const maximum = viewport.scrollWidth - viewport.clientWidth;
        const wasAtEnd = previousMaximum > 0 && viewport.scrollLeft >= previousMaximum - 1;
        top.style.width = `${viewport.clientWidth}px`;
        spacer.style.width = `${viewport.scrollWidth}px`;
        top.hidden = viewport.scrollWidth <= viewport.clientWidth + 1;
        if (wasAtEnd && previousMaximum !== maximum) move(viewport, maximum);
        previousMaximum = maximum;
        move(top, viewport.scrollLeft);
    };
    top.addEventListener('scroll', () => synchronize(top, viewport), { passive: true });
    viewport.addEventListener('scroll', () => synchronize(viewport, top), { passive: true });
    viewport.addEventListener('focusin', event => {
        if (window.innerWidth < 992 || !event.target.matches('.balance-edit-cell')) return;
        const sku = event.target.closest('[data-balance-product]')?.querySelector('.production-weekly-sku');
        if (!sku) return;
        const field = event.target.getBoundingClientRect(), bounds = viewport.getBoundingClientRect();
        const left = bounds.left + sku.getBoundingClientRect().width + 8;
        if (field.left < left) move(viewport, viewport.scrollLeft - (left - field.left));
        else if (field.right > bounds.right - 8) move(viewport, viewport.scrollLeft + field.right - bounds.right + 8);
        move(top, viewport.scrollLeft);
    });
    if (typeof ResizeObserver !== 'undefined') {
        const observer = new ResizeObserver(refresh);
        observer.observe(viewport);
        observer.observe(table);
    }
    window.addEventListener('resize', refresh);
    refresh();
})();
