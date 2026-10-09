(() => {
    'use strict';
    const text = window.warehouseText;
    let requestVersion = 0, controller;
    const fields = new Map();
    const cancelPrefill = () => { requestVersion += 1; controller?.abort(); };
    const prefill = async (source, sourceId) => {
        if (!source.prefillUrl) return;
        const target = fields.get(source.kind === 'product' ? 'location' : 'product');
        const version = requestVersion;
        controller = new AbortController();
        const signal = controller.signal;
        const url = new URL(source.prefillUrl, location.origin);
        url.searchParams.set(source.kind === 'product' ? 'productId' : 'locationId', sourceId);
        try {
            const response = await fetch(url, { signal, headers: { Accept: 'application/json' } });
            if (!response.ok) throw new Error('lookup');
            const item = await response.json();
            if (signal.aborted || version !== requestVersion || source.id.value !== sourceId || !item) return;
            // Setting the dependent field must not start a reverse lookup.
            target.lookup.setValue(item.code);
            target.id.value = item.id;
            target.automatic = item;
            if (target.kind === 'location') target.form.elements.namedItem('Relation').value = '';
            target.feedback.textContent = text(target.kind === 'product'
                ? 'Producto completado: {0}. Es el único con stock en esta ubicación.'
                : 'Ubicación completada: {0}. Es la única con stock de este producto.', item.code);
        } catch (error) {
            if (error.name !== 'AbortError' && version === requestVersion)
                target.feedback.textContent = text(target.kind === 'product'
                    ? 'No fue posible consultar el stock. Selecciona el producto manualmente.'
                    : 'No fue posible consultar el stock. Selecciona la ubicación manualmente.');
        }
    };
    for (const field of document.querySelectorAll('[data-incident-filter]')) {
        const form = field.closest('form');
        const source = {
            kind: field.dataset.incidentFilter, form,
            id: form.elements.namedItem(field.dataset.idInput),
            input: field.querySelector('[data-suggestion-input]'),
            feedback: field.querySelector('[data-suggestion-feedback]'),
            prefillUrl: field.dataset.singleProductUrl || field.dataset.singleLocationUrl
        };
        fields.set(source.kind, source);
        const clearContext = () => {
            cancelPrefill();
            source.id.value = '';
            source.automatic = null;
            if (source.kind === 'location') form.elements.namedItem('Relation').value = '';
            const target = fields.get(source.kind === 'product' ? 'location' : 'product');
            if (target?.automatic && target.id.value === target.automatic.id && target.input.value === target.automatic.code) {
                target.lookup.setValue('');
                target.id.value = '';
                target.automatic = null;
                if (target.kind === 'location') form.elements.namedItem('Relation').value = '';
            }
        };
        source.lookup = window.warehouseSuggestionLookup({
            form, input: source.input, feedback: source.feedback,
            results: field.querySelector('[data-suggestion-results]'),
            url: field.dataset.url,
            code: item => item.code,
            describe: item => `${item.description || text('Sin descripción')}${item.externalReference ? text(' · Ref. {0}', item.externalReference) : ''}${item.isActive ? '' : text(' · Inactivo')}${item.isBlocked ? text(' · Bloqueada') : ''}`,
            edited: clearContext,
            selected: item => { clearContext(); source.id.value = item.id; void prefill(source, item.id); },
            empty: text('No hay coincidencias. Puedes buscar con el texto escrito.'),
            found: count => text('Coincidencias: {0}. Usa flechas y Enter para elegir.', count)
        });
    }
})();
