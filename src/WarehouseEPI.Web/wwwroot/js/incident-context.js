(() => {
    'use strict';
    const form = document.querySelector('[data-incident-context-picker]');
    if (!form) return;
    const text = window.warehouseText;
    const product = form.querySelector('#new-incident-product');
    const locationInput = form.querySelector('#new-incident-location');
    const productId = form.elements.namedItem('ProductId');
    const locationId = form.elements.namedItem('LocationId');
    const next = form.querySelector('[data-context-continue]');
    const locationsUrl = new URL(form.dataset.locationsUrl, window.location.origin);
    const update = () => { next.disabled = !productId.value || !locationId.value; };
    const options = (input, resultsId, feedbackId, url, code) => ({
        form, input, url, code,
        results: form.querySelector(resultsId), feedback: form.querySelector(feedbackId),
        describe: item => `${item.description || text('Sin descripción')}${item.isActive === false ? text(' · Inactivo') : ''}${item.isBlocked ? text(' · Bloqueada') : ''}`,
        empty: text('No hay coincidencias. Prueba otra búsqueda.'),
        found: count => text('Coincidencias: {0}. Usa flechas y Enter para elegir.', count)
    });
    const locationLookup = window.warehouseSuggestionLookup({
        ...options(locationInput, '#new-incident-locations', '#new-incident-location-feedback', locationsUrl, item => item.code),
        edited: () => { locationId.value = ''; update(); },
        selected: item => { locationId.value = item.id; update(); next.focus(); }
    });
    const clearLocation = () => {
        locationLookup.setValue(''); locationId.value = ''; locationInput.disabled = true; update();
    };
    window.warehouseSuggestionLookup({
        ...options(product, '#new-incident-products', '#new-incident-product-feedback', form.dataset.productsUrl, item => item.sku),
        edited: () => { productId.value = ''; clearLocation(); },
        selected: item => {
            productId.value = item.id; clearLocation();
            locationsUrl.searchParams.set('productId', item.id);
            locationInput.disabled = false; locationInput.focus();
        }
    });
    form.addEventListener('submit', event => {
        if (!productId.value || !locationId.value) {
            event.preventDefault(); (!productId.value ? product : locationInput).focus();
        }
    });
})();
