(() => {
    'use strict';
    const picker = document.querySelector('[data-incident-picker]');
    if (picker) {
        const input = picker.querySelector('[data-product-search]');
        const results = picker.querySelector('[data-product-results]');
        let controller, timer;
        const search = async () => {
            controller?.abort(); controller = new AbortController();
            const signal = controller.signal;
            const url = new URL(picker.dataset.url, location.origin); url.searchParams.set('q', input.value);
            try {
                const response = await fetch(url, { signal });
                if (!response.ok) throw new Error('lookup');
                const products = await response.json();
                if (signal.aborted) return;
                results.replaceChildren();
                for (const product of products) {
                    const link = document.createElement('a');
                    const target = new URL(picker.dataset.createUrl, location.origin); target.searchParams.set('productId', product.id);
                    link.href = target; link.className = 'list-group-item list-group-item-action py-3';
                    link.textContent = `${product.sku} · ${product.description || ''}`; results.append(link);
                }
                if (!products.length) results.textContent = picker.dataset.empty;
            } catch (error) { if (error.name !== 'AbortError') results.textContent = picker.dataset.error; }
        };
        input.addEventListener('input', () => { clearTimeout(timer); controller?.abort(); timer = setTimeout(search, 180); });
        input.addEventListener('keydown', event => {
            if (event.key === 'ArrowDown') { event.preventDefault(); results.querySelector('a')?.focus(); }
            if (event.key === 'Enter') { event.preventDefault(); results.querySelector('a')?.click(); }
        });
        results.addEventListener('keydown', event => {
            if (event.key === 'Escape') input.focus();
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault(); (event.key === 'ArrowDown' ? event.target.nextElementSibling : event.target.previousElementSibling)?.focus();
            }
        });
        search();
    }
    const form = document.querySelector('[data-incident-form]');
    if (!form) return;
    const input = form.querySelector('[data-photos]'), camera = form.querySelector('[data-photo-camera]');
    const preview = form.querySelector('[data-photo-preview]');
    const modalElement = form.querySelector('.modal'), pin = form.querySelector('[data-incident-pin]');
    const review = form.querySelector('[data-incident-review]');
    const summary = form.querySelector('[data-incident-summary]');
    let photosReady = Promise.resolve();
    const refreshPhotos = () => {
        preview.replaceChildren();
        const readers = [];
        input.setCustomValidity(input.files.length > 5 || [...input.files].some(file => file.size > 5 * 1024 * 1024 || !['image/png', 'image/jpeg'].includes(file.type)) ? preview.dataset.invalid : '');
        if (!input.validity.valid) { preview.textContent = preview.dataset.invalid; return; }
        for (const [index, file] of [...input.files].entries()) {
            const box = document.createElement('div'); box.className = 'col-5 col-md-3';
            const image = document.createElement('img');
            image.alt = file.name; image.className = 'img-fluid rounded';
            readers.push(new Promise(resolve => {
                const reader = new FileReader();
                reader.addEventListener('load', () => { image.src = reader.result; resolve(); });
                reader.addEventListener('error', () => { input.setCustomValidity(preview.dataset.invalid); resolve(); });
                reader.readAsDataURL(file);
            }));
            const remove = document.createElement('button'); remove.type = 'button'; remove.className = 'btn btn-outline-secondary btn-lg w-100 mt-1';
            remove.textContent = preview.dataset.remove; remove.setAttribute('aria-label', `${preview.dataset.remove}: ${file.name}`);
            remove.addEventListener('click', () => {
                const files = new DataTransfer(); [...input.files].filter((_, i) => i !== index).forEach(f => files.items.add(f));
                input.files = files.files; refreshPhotos(); input.focus();
            }); box.append(image, remove); preview.append(box);
        }
        photosReady = Promise.all(readers);
    };
    input.addEventListener('change', refreshPhotos);
    form.querySelector('[data-open-camera]').addEventListener('click', () => camera.click());
    camera.addEventListener('change', () => {
        const files = new DataTransfer(); [...input.files, ...camera.files].forEach(file => files.items.add(file));
        input.files = files.files; camera.value = ''; refreshPhotos(); input.reportValidity();
    });
    const kind = form.querySelector('[name="Kind"]');
    const difference = form.querySelector('[data-difference-field]');
    if (kind && difference) {
        const update = () => { difference.hidden = kind.value !== 'QuantityDifference'; };
        kind.addEventListener('change', update); update();
    }
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    review.addEventListener('click', async () => {
        pin.required = false; pin.value = '';
        await photosReady;
        const action = form.querySelector('[name="Action"]')?.value;
        const comment = form.querySelector('[name="Comment"]');
        if (comment) comment.required = ['Resolve', 'Void', 'Reopen'].includes(action) ||
            action === 'Comment' && !input.files.length && !form.querySelector('[name="CorrectionId"]')?.value;
        if (!form.reportValidity()) return;
        summary.replaceChildren();
        const context = document.querySelector('[data-incident-context]');
        if (context) { const text = document.createElement('p'); text.textContent = context.innerText.replace(/\s+/g, ' '); summary.append(text); }
        for (const field of form.querySelectorAll('[data-review-label]')) {
            if (field.closest('[hidden]')) continue;
            const line = document.createElement('p');
            line.textContent = `${field.dataset.reviewLabel}: ${field.tagName === 'SELECT' ? field.selectedOptions[0]?.textContent || '' : field.value}`;
            summary.append(line);
        }
        for (const image of preview.querySelectorAll('img')) {
            const copy = image.cloneNode(); copy.className = 'img-fluid rounded mb-2'; summary.append(copy);
        }
        modal.show();
    });
    modalElement.addEventListener('shown.bs.modal', () => { pin.required = true; pin.focus(); });
    modalElement.addEventListener('hidden.bs.modal', () => { pin.value = ''; pin.required = false; review.focus(); });
    form.addEventListener('submit', event => {
        if (!modalElement.classList.contains('show')) { event.preventDefault(); review.click(); }
    });
    addEventListener('pagehide', () => { pin.value = ''; });
    document.querySelector('[role="alert"]')?.focus();
})();
