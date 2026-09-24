(() => {
  const root = document.querySelector("[data-production-daily]");
  if (!root) return;
  const fields = [...root.querySelectorAll("[data-product-field]")];

  const bind = (field) => {
    const input = field.querySelector("[data-product-input]");
    // A single picker may keep its hidden id outside the field; several pickers each own theirs.
    const id = field.querySelector("[data-product-id]") || (fields.length === 1 ? root.querySelector("[data-product-id]") : null);
    const results = field.querySelector("[data-product-results]");
    const skuOnly = field.dataset?.productValue === "sku";
    const exactScan = field.dataset?.productScanExact === "true";
    const scanError = exactScan ? field.querySelector("[data-product-scan-error]") : null;
    const quantity = exactScan ? field.closest("form")?.querySelector('[name="Line.Quantity"]') : null;
    if (!input || (!id && !skuOnly) || !results) return;

    let controller;
    let resolveController;
    let scanGeneration = 0;
    let keyboardSelection = false;
    let items = [];
    let active = -1;
    const close = () => {
      results.replaceChildren();
      input.setAttribute("aria-expanded", "false");
      items = [];
      active = -1;
    };
    const choose = (item) => {
      if (exactScan) { resolveController?.abort(); scanGeneration++; if (scanError) scanError.textContent = ""; }
      if (id) id.value = item.id;
      input.value = skuOnly ? item.sku : `${item.sku} · ${item.description || root.dataset.noDescription}`;
      input.dataset.selectedLabel = input.value;
      close();
      if (exactScan) { quantity?.focus(); quantity?.select(); }
      else root.querySelector(skuOnly ? 'button[formaction*="GroupFilter"]' : '#Capture_Quantity, #Line_Quantity, button[formaction*="GroupAdd"]')?.focus();
    };
    const highlight = () => {
      [...results.children].forEach((button, index) => button.classList.toggle("active", index === active));
    };
    const render = (rows) => {
      results.replaceChildren();
      items = rows;
      rows.forEach((item, index) => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "list-group-item list-group-item-action";
        button.setAttribute("role", "option");
        const sku = document.createElement("strong");
        sku.textContent = item.sku;
        button.append(sku);
        if (!skuOnly) {
          const description = document.createElement("span");
          description.className = "d-block small text-body-secondary";
          description.textContent = item.description || root.dataset.noDescription;
          button.append(description);
        }
        button.addEventListener("pointerdown", (event) => { event.preventDefault(); choose(items[index]); });
        results.append(button);
      });
      input.setAttribute("aria-expanded", rows.length ? "true" : "false");
      active = rows.length ? 0 : -1;
      highlight();
    };
    const search = async () => {
      const term = input.value.trim();
      if (term.length < 2) { close(); return; }
      controller?.abort();
      const searchController = new AbortController();
      controller = searchController;
      try {
        const response = await fetch(`${root.dataset.productUrl}&q=${encodeURIComponent(term)}`, {
          headers: { Accept: "application/json" }, signal: searchController.signal
        });
        if (!response.ok) throw new Error();
        const rows = (await response.json()).slice(0, 10);
        if (!searchController.signal.aborted && (!exactScan || document.activeElement === input)) render(rows);
      } catch (error) {
        if (error.name !== "AbortError") close();
      }
    };
    let timer;
    const resolveExact = async () => {
      controller?.abort();
      clearTimeout(timer);
      close();
      const code = input.value.trim();
      if (id) id.value = "";
      input.dataset.selectedLabel = "";
      if (!code) {
        if (scanError) scanError.textContent = "Escanea o escribe un SKU o código de barras.";
        input.focus();
        return;
      }
      resolveController?.abort();
      const request = new AbortController();
      resolveController = request;
      const generation = ++scanGeneration;
      input.setAttribute("aria-busy", "true");
      try {
        const url = new URL(root.dataset.productResolveUrl, window.location.href);
        url.searchParams.set("code", code);
        const response = await fetch(url.toString(), {
          headers: { Accept: "application/json" }, signal: request.signal
        });
        if (request.signal.aborted || generation !== scanGeneration) return;
        if (response.status === 404) {
          if (scanError) scanError.textContent = "No se encontró un SKU o código de barras activo.";
          input.focus();
          return;
        }
        if (!response.ok) throw new Error("lookup-failed");
        const product = await response.json();
        if (request.signal.aborted || generation !== scanGeneration) return;
        if (!product?.id || !product?.sku) throw new Error("lookup-failed");
        choose(product);
      } catch (error) {
        if (error.name !== "AbortError" && generation === scanGeneration) {
          if (scanError) scanError.textContent = "No se pudo consultar el producto. Reintenta el escaneo.";
          input.focus();
        }
      } finally {
        if (resolveController === request) input.setAttribute("aria-busy", "false");
      }
    };
    input.addEventListener("input", () => {
      if (exactScan) { resolveController?.abort(); scanGeneration++; keyboardSelection = false; if (scanError) scanError.textContent = ""; }
      if (id && input.value !== input.dataset.selectedLabel) id.value = "";
      clearTimeout(timer);
      timer = setTimeout(search, 180);
    });
    input.addEventListener("keydown", (event) => {
      if (event.isComposing) return;
      if (event.key === "ArrowDown" && items.length) { event.preventDefault(); keyboardSelection = true; active = (active + 1) % items.length; highlight(); }
      else if (event.key === "ArrowUp" && items.length) { event.preventDefault(); keyboardSelection = true; active = (active - 1 + items.length) % items.length; highlight(); }
      else if (event.key === "Enter" && exactScan) {
        event.preventDefault();
        if (keyboardSelection && active >= 0) choose(items[active]);
        else void resolveExact();
      }
      else if (event.key === "Enter" && active >= 0) { event.preventDefault(); choose(items[active]); }
      else if (event.key === "Escape") { keyboardSelection = false; close(); }
    });
    input.addEventListener("blur", () => setTimeout(close, 120));
  };

  fields.forEach(bind);
})();

(() => {
  // Irreversible actions ask first; the page provides the localized question.
  document.querySelectorAll("form[data-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      if (!window.confirm(form.dataset.confirm)) event.preventDefault();
    });
  });
})();

(() => {
  const form = document.querySelector("[data-capture-group]");
  if (!form) return;
  const errors = document.querySelector('[data-production-errors].validation-summary-errors');
  const review = form.querySelector('[data-group-review]');
  (errors || review)?.focus();
  const quantities = [...form.querySelectorAll("[data-group-quantity]")];
  const confirm = form.querySelector("[data-group-confirm]");
  const changed = () => {
    if (confirm) confirm.disabled = true;
    const pin = form.querySelector('input[type="password"]');
    if (pin) pin.value = "";
  };
  form.querySelectorAll('input:not([type="password"]):not([type="hidden"])').forEach(input => input.addEventListener("input", changed));
  quantities.forEach((input, index) => input.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      if (form.querySelector('[name="Group.Mode"]')?.value === 'quick')
        document.querySelector('#group-add-product')?.focus();
      else
        (quantities[index + 1] || form.querySelector('button[formaction*="GroupPreview"]'))?.focus();
    }
  }));
  document.querySelector("#group-search")?.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.defaultPrevented) {
      event.preventDefault();
      form.requestSubmit(document.querySelector('button[formaction*="GroupFilter"]'));
    }
  });
  const context = document.querySelector("[data-context-form]");
  const contextFields = [...document.querySelectorAll("[data-refresh-context]")];
  const originalValues = contextFields.map(input => input.value);
  const canLeave = () => ![...quantities, ...form.querySelectorAll('input[name$=".Notes"]')].some(input => input.value.trim() !== "" && input.value.trim() !== "0") || window.confirm(form.dataset.unsavedMessage);
  context?.addEventListener("submit", event => {
    if (!canLeave()) {
      event.preventDefault();
      contextFields.forEach((input, index) => { input.value = originalValues[index]; });
    }
  });
  contextFields.forEach(input => input.addEventListener("change", () => context.requestSubmit()));
  document.querySelectorAll("[data-capture-context-link]").forEach(link => link.addEventListener("click", event => {
    if (!canLeave()) event.preventDefault();
  }));
  form.addEventListener("submit", event => {
    if (form.dataset.submitting) { event.preventDefault(); return; }
    form.dataset.submitting = "true";
  });
})();

(() => {
  document.querySelectorAll('[data-balance-day]').forEach(button => button.addEventListener('click', () => {
    const form = button.closest('form');
    form.querySelector('[name="Through"]').value = button.dataset.balanceDay;
    form.requestSubmit();
  }));
  const week = document.querySelector('[data-balance-week]');
  week?.addEventListener('change', () => {
    const form = week.closest('form');
    form.querySelector('[name="Through"]').value = '';
    form.requestSubmit();
  });
})();
