(() => {
    'use strict';
    const form = document.querySelector('[data-supplier-sheet]');
    if (!form) return;
    const text = window.warehouseText;
    const input = form.querySelector('#supplier-product');
    const list = form.querySelector('[data-supplier-selection]');
    const feedback = form.querySelector('#supplier-feedback');
    const status = document.querySelector('[data-sheet-status]');
    const allProducts = form.querySelector('[data-all-products]');
    let revision = 0;
    const update = () => {
        const count = list.children.length;
        form.querySelector('[data-generate-sheet]').disabled = !allProducts.checked && (count === 0 || count > 100);
        form.querySelector('[data-manual-products]').hidden = allProducts.checked;
        form.querySelector('[data-supplier-empty]').hidden = count > 0;
    };
    const invalidate = () => {
        document.querySelector('[data-sheet-preview]')?.remove();
        status.textContent = text('La selección cambió. Genera la hoja nuevamente para imprimir.');
        update();
    };
    const add = product => {
        if (allProducts.checked) return;
        revision++;
        if (Array.from(list.children).some(row => row.dataset.productId === product.id)) {
            feedback.textContent = text('El producto {0} ya está incluido.', product.sku);
            input.focus();
            return;
        }
        if (list.children.length >= 100) {
            feedback.textContent = text('La hoja admite hasta 100 productos. Quita uno antes de agregar otro.');
            return;
        }
        const row = document.createElement('li');
        row.className = 'list-group-item d-flex align-items-center gap-3';
        row.dataset.productId = product.id;
        const id = document.createElement('input');
        id.type = 'hidden'; id.name = 'ProductIds'; id.value = product.id;
        const detail = document.createElement('div');
        detail.className = 'flex-grow-1 supplier-selected-text';
        const sku = document.createElement('strong'); sku.textContent = product.sku;
        const description = document.createElement('span');
        description.className = 'd-block'; description.textContent = product.description || '';
        detail.append(sku, description);
        const remove = document.createElement('button');
        remove.type = 'button'; remove.className = 'btn btn-outline-danger';
        remove.dataset.removeProduct = ''; remove.textContent = text('Quitar');
        remove.setAttribute('aria-label', text('Quitar {0}', product.sku));
        row.append(id, detail, remove); list.append(row);
        lookup.setValue(''); invalidate();
        feedback.textContent = text('Producto {0} agregado.', product.sku);
        input.focus();
    };
    form.querySelector('[data-supplier-name]').addEventListener('input', () => {
        document.querySelector('[data-sheet-preview]')?.remove();
        status.textContent = text('El proveedor cambió. Genera la hoja nuevamente para imprimir.');
    });
    const lookup = window.warehouseSuggestionLookup({
        form, input, results: form.querySelector('#supplier-results'), feedback,
        url: form.dataset.productsUrl, code: item => item.sku,
        describe: item => item.description || '', selected: add, edited: () => { revision++; },
        empty: text('No hay coincidencias. Prueba otra búsqueda.'),
        found: count => text('Coincidencias: {0}. Usa flechas y Enter para elegir.', count)
    });
    allProducts.addEventListener('change', () => {
        revision++;
        lookup.setValue(input.value);
        invalidate();
    });
    input.addEventListener('keydown', async event => {
        if (event.key === 'Escape') revision++;
        if (event.key !== 'Enter' || event.defaultPrevented) return;
        event.preventDefault();
        const code = input.value.trim();
        if (!code) return;
        const current = ++revision;
        lookup.setValue(input.value);
        feedback.textContent = text('Buscando…');
        try {
            const url = new URL(form.dataset.resolveUrl, location.origin);
            url.searchParams.set('code', code);
            const response = await fetch(url, { headers: { Accept: 'application/json' } });
            if (current !== revision) return;
            if (response.status === 404) {
                feedback.textContent = text('No se encontró un código exacto. Escribe para buscar y elige una sugerencia.');
                return;
            }
            if (!response.ok) throw new Error('lookup failed');
            const product = await response.json();
            if (current === revision) add(product);
        } catch {
            if (current === revision) feedback.textContent = text('No fue posible buscar en la red local. Conservamos el texto para que puedas consultar o reintentar.');
        }
    });
    list.addEventListener('click', event => {
        const button = event.target.closest('[data-remove-product]');
        if (!button) return;
        revision++;
        button.closest('li').remove(); invalidate(); input.focus();
    });
    form.addEventListener('submit', () => { revision++; });
    let changingLanguage = false;
    document.querySelectorAll('.app-language-control').forEach(languageForm => {
        languageForm.addEventListener('submit', async event => {
            event.preventDefault();
            if (changingLanguage || !event.submitter) return;
            changingLanguage = true;
            revision++;
            lookup.setValue(input.value);
            const data = new FormData(languageForm);
            data.set('language', event.submitter.value);
            try {
                // Keep the current DOM intact until the language cookie is successfully updated.
                const response = await fetch(languageForm.action, { method: 'POST', body: data });
                if (!response.ok) throw new Error('language change failed');
                const preview = document.createElement('input');
                preview.type = 'hidden'; preview.name = 'preview';
                preview.value = String(!!document.querySelector('[data-sheet-preview]'));
                form.append(preview);
                form.action = form.dataset.restoreUrl;
                // Restore drafts too, even when optional input currently exceeds its limit.
                HTMLFormElement.prototype.submit.call(form);
            } catch {
                changingLanguage = false;
                status.textContent = text('No fue posible cambiar el idioma. Conservamos la hoja; vuelve a intentarlo.');
            }
        });
    });
    document.querySelector('[data-print-sheet]')?.addEventListener('click', () => window.print());
    update();
})();
