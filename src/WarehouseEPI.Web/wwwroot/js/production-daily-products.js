(() => {
  const field = document.querySelector('[data-daily-product-field]');
  if (!field) return;
  const form = field.closest('form');
  const input = field.querySelector('[data-product-input]');
  const id = field.querySelector('[data-product-id]');
  const results = field.querySelector('[data-product-results]');
  const add = form.querySelector('[data-daily-product-add]');
  const labels = [field.dataset.planned, field.dataset.pending, field.dataset.other];
  let controller, timer, generation = 0, active = -1, resolving = false;
  let groups = [];
  let options = [];
  const close = () => {
    controller?.abort();
    clearTimeout(timer);
    generation++;
    results.replaceChildren();
    options = [];
    active = -1;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    input.removeAttribute('aria-busy');
  };
  const select = item => {
    id.value = item.id;
    input.value = item.sku;
    close();
    // Selecting an existing row is navigation, never another insertion or submission.
    const row = [...form.querySelectorAll('[data-product-row]')].find(row => row.dataset.productRow === item.id);
    if (row) {
      row.hidden = false;
      row.dataset.focusProduct = 'true';
      const table = row.closest?.('.production-entry__rows');
      if (table) table.hidden = false;
      row.querySelector('[data-group-quantity]')?.focus();
    }
    else form.requestSubmit(add);
  };
  const highlight = () => {
    options.forEach((option, index) => {
      option.classList.toggle('active', index === active);
      if (option.getAttribute('role') === 'option') option.setAttribute('aria-selected', String(index === active));
    });
    if (options[active]) {
      input.setAttribute('aria-activedescendant', options[active].id);
      options[active].scrollIntoView({ block: 'nearest' });
    }
  };
  const button = (label, action) => {
    const node = document.createElement('button');
    node.type = 'button';
    node.className = 'list-group-item list-group-item-action';
    node.id = `daily-product-option-${options.length}`;
    node.textContent = label;
    node.addEventListener('pointerdown', event => event.preventDefault());
    node.addEventListener('click', action);
    options.push(node);
    return node;
  };
  const render = () => {
    results.replaceChildren();
    options = [];
    active = -1;
    input.removeAttribute('aria-activedescendant');
    groups.forEach(group => {
      if (!group.items.length) return;
      const section = document.createElement('div');
      section.setAttribute('role', 'group');
      section.setAttribute('aria-label', labels[group.group]);
      section.className = 'production-entry__suggestion-group';
      const heading = document.createElement('div');
      heading.className = 'production-entry__suggestion-heading';
      heading.textContent = labels[group.group];
      section.append(heading);
      group.items.forEach(item => {
        const option = button('', () => select(item));
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', 'false');
        const sku = document.createElement('strong');
        sku.textContent = item.sku;
        const description = document.createElement('span');
        description.className = 'd-block small text-body-secondary';
        description.textContent = item.description || '';
        option.append(sku, description);
        section.append(option);
      });
      if (group.hasMore) section.append(button(field.dataset.more, () => search(group.group, group.items.length)));
      results.append(section);
    });
    if (!options.length) message(field.dataset.empty);
    input.setAttribute('aria-expanded', 'true');
  };
  const message = text => {
    const node = document.createElement('div');
    node.className = 'list-group-item';
    node.setAttribute('role', 'status');
    node.textContent = text;
    results.append(node);
  };
  const search = async (group, offset = 0) => {
    controller?.abort();
    controller = new AbortController();
    const current = ++generation;
    const url = new URL(field.dataset.url, window.location.origin);
    url.searchParams.set('date', form.querySelector('[name="Group.Date"]').value);
    url.searchParams.set('area', form.querySelector('[name="Group.Area"]').value);
    url.searchParams.set('q', input.value.trim());
    if (group !== undefined) { url.searchParams.set('category', group); url.searchParams.set('offset', offset); }
    input.setAttribute('aria-busy', 'true');
    try {
      const response = await fetch(url, { headers: { Accept: 'application/json' }, signal: controller.signal });
      if (!response.ok) throw new Error();
      const loaded = await response.json();
      if (current !== generation) return;
      if (group === undefined) groups = loaded;
      else groups = groups.map(previous => previous.group !== group ? previous : {
        ...loaded[0], items: [...previous.items, ...(loaded[0]?.items || [])]
      });
      render();
    } catch (error) {
      if (current !== generation || error.name === 'AbortError') return;
      results.replaceChildren(); options = []; active = -1;
      message(field.dataset.error);
      input.setAttribute('aria-expanded', 'true');
    } finally {
      if (current === generation) input.removeAttribute('aria-busy');
    }
  };
  input.addEventListener('focus', () => search());
  input.addEventListener('input', () => {
    id.value = '';
    close();
    timer = setTimeout(() => search(), 180);
  });
  input.addEventListener('keydown', async event => {
    if (event.key === 'Escape') { close(); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!options.length) { search(); return; }
      active = (active + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length;
      highlight();
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (form.querySelector('[name="Group.Mode"]')?.value === 'quick' && field.dataset.resolveUrl) {
        if (resolving) return;
        resolving = true;
        const code = input.value.trim();
        try {
          const url = new URL(field.dataset.resolveUrl, window.location.origin);
          url.searchParams.set('code', code);
          const response = await fetch(url, { headers: { Accept: 'application/json' } });
          if (input.value.trim() !== code) return;
          if (response.ok) {
            const exact = await response.json();
            if (input.value.trim() !== code) return;
            if (exact?.id) { select(exact); return; }
          }
        } catch { /* Keep the typed code and let the normal search show the error. */ }
        finally { resolving = false; }
        search();
        return;
      }
      if (options.length) options[active < 0 ? 0 : active].click();
      else search();
    }
  });
  field.addEventListener('focusout', event => { if (!field.contains(event.relatedTarget)) close(); });
  document.querySelector('[data-focus-product="true"] [data-group-quantity]')?.focus();
})();
