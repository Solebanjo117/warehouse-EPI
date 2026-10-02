(() => {
    'use strict';
    document.querySelector('[data-report-print]')?.addEventListener('click', () => window.print());
    const kind = document.querySelector('[data-report-kind]');
    const period = document.querySelector('[data-report-period]');
    const updateFilters = () => {
        if (!kind || !period) return;
        document.querySelector('[data-report-detail]').hidden = kind.value !== 'products';
        document.querySelector('[data-report-date]').hidden = kind.value !== 'products' || period.value === 'week';
    };
    kind?.addEventListener('change', updateFilters);
    period?.addEventListener('change', updateFilters);
    updateFilters();
    const topScroll = document.querySelector('[data-report-scroll-top]');
    const tableScroll = document.querySelector('[data-report-scroll]');
    if (topScroll && tableScroll) {
        const width = topScroll.querySelector('[data-report-scroll-width]');
        const mirroredPositions = new WeakMap();
        const mirrorPosition = (target, position) => {
            if (Math.abs(target.scrollLeft - position) <= 1) return;
            target.scrollLeft = position;
            mirroredPositions.set(target, target.scrollLeft);
        };
        const refreshScroll = () => {
            width.style.width = `${tableScroll.scrollWidth}px`;
            topScroll.hidden = tableScroll.scrollWidth <= tableScroll.clientWidth + 1;
            mirrorPosition(topScroll, tableScroll.scrollLeft);
        };
        const synchronize = (source, target) => {
            const mirrored = mirroredPositions.get(source);
            mirroredPositions.delete(source);
            // Scroll events are asynchronous: a mirror can arrive after the user
            // has moved the other bar again. Never echo that older position back.
            if (mirrored !== undefined && Math.abs(source.scrollLeft - mirrored) <= 1) return;
            mirrorPosition(target, source.scrollLeft);
        };
        topScroll.addEventListener('scroll', () => synchronize(topScroll, tableScroll), { passive: true });
        tableScroll.addEventListener('scroll', () => synchronize(tableScroll, topScroll), { passive: true });
        if (typeof ResizeObserver !== 'undefined') {
            const observer = new ResizeObserver(refreshScroll);
            observer.observe(tableScroll);
            observer.observe(tableScroll.querySelector('table'));
        }
        window.addEventListener('resize', refreshScroll);
        refreshScroll();
    }
})();
