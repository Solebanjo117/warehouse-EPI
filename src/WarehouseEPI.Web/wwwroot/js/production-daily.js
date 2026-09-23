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
    if (!input || (!id && !skuOnly) || !results) return;

    let controller;
    let items = [];
    let active = -1;
    const close = () => {
      results.replaceChildren();
      input.setAttribute("aria-expanded", "false");
      items = [];
      active = -1;
    };
    const choose = (item) => {
      if (id) id.value = item.id;
      input.value = skuOnly ? item.sku : `${item.sku} · ${item.description || root.dataset.noDescription}`;
      input.dataset.selectedLabel = input.value;
      close();
      root.querySelector(skuOnly ? 'button[formaction*="GroupFilter"]' : '#Capture_Quantity, #Line_Quantity, button[formaction*="GroupAdd"]')?.focus();
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
        const description = document.createElement("span");
        description.className = "d-block small text-body-secondary";
        description.textContent = item.description || root.dataset.noDescription;
        button.append(sku, description);
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
      controller = new AbortController();
      try {
        const response = await fetch(`${root.dataset.productUrl}&q=${encodeURIComponent(term)}`, {
          headers: { Accept: "application/json" }, signal: controller.signal
        });
        if (!response.ok) throw new Error();
        render((await response.json()).slice(0, 10));
      } catch (error) {
        if (error.name !== "AbortError") close();
      }
    };
    let timer;
    input.addEventListener("input", () => {
      if (id && input.value !== input.dataset.selectedLabel) id.value = "";
      clearTimeout(timer);
      timer = setTimeout(search, 180);
    });
    input.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" && items.length) { event.preventDefault(); active = (active + 1) % items.length; highlight(); }
      else if (event.key === "ArrowUp" && items.length) { event.preventDefault(); active = (active - 1 + items.length) % items.length; highlight(); }
      else if (event.key === "Enter" && active >= 0) { event.preventDefault(); choose(items[active]); }
      else if (event.key === "Escape") close();
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
  const date = document.querySelector('[data-balance-date]');
  date?.addEventListener('change', () => { if (date.value && date.checkValidity()) date.closest('form').requestSubmit(); });
  const week = document.querySelector('[data-balance-week]');
  week?.addEventListener('change', () => {
    const form = week.closest('form');
    form.querySelector('[name="Through"]').value = '';
    form.querySelector('[name="Through"]').removeAttribute('min');
    form.querySelector('[name="Through"]').removeAttribute('max');
    form.requestSubmit();
  });
})();
