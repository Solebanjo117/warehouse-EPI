(() => {
  "use strict";
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));

  const form = document.querySelector("[data-kardex-search-form]");
  if (!form) return;

  const input = form.querySelector("[data-kardex-product-input]");
  const results = form.querySelector("[data-kardex-product-results]");
  const feedback = form.querySelector("[data-kardex-search-feedback]");
  const productsUrl = form.dataset.productsUrl;
  if (!input || !results || !feedback || !productsUrl) return;

  let timer;
  let sequence = 0;
  let activeIndex = -1;

  const buttons = () => Array.from(results.querySelectorAll("button[data-product-suggestion]"));
  const setFeedback = (message) => { feedback.textContent = message; };
  const close = () => {
    results.replaceChildren();
    activeIndex = -1;
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
  };
  const activate = (index) => {
    const items = buttons();
    if (!items.length) return;
    activeIndex = (index + items.length) % items.length;
    items.forEach((item, itemIndex) => item.classList.toggle("active", itemIndex === activeIndex));
    input.setAttribute("aria-activedescendant", items[activeIndex].id);
    items[activeIndex].scrollIntoView({ block: "nearest" });
  };
  const select = (item) => {
    input.value = item.dataset.sku;
    close();
    form.requestSubmit();
  };
  const render = (items) => {
    close();
    if (!items.length) {
      setFeedback(text("No se encontraron productos. Puedes consultar el texto escrito para confirmar."));
      return;
    }
    const fragment = document.createDocumentFragment();
    items.forEach((item, index) => {
      const button = document.createElement("button");
      button.type = "button";
      button.id = `kardex-product-${index}`;
      button.className = "list-group-item list-group-item-action";
      button.dataset.productSuggestion = "";
      button.dataset.sku = item.sku;
      button.setAttribute("role", "option");
      const title = document.createElement("strong");
      title.textContent = item.sku;
      const detail = document.createElement("small");
      detail.className = "d-block text-muted";
      const description = item.description || text("Sin descripción");
      const reference = item.externalReference ? text(" · Ref. {0}", item.externalReference) : "";
      const status = item.isActive ? "" : text(" · Inactivo");
      detail.textContent = `${description}${reference}${status}`;
      button.append(title, detail);
      button.addEventListener("mousedown", (event) => event.preventDefault());
      button.addEventListener("click", () => select(button));
      fragment.append(button);
    });
    results.append(fragment);
    input.setAttribute("aria-expanded", "true");
    setFeedback(text("{0} {1}. Usa flechas y Enter para elegir.", items.length, items.length === 1 ? text("producto encontrado") : text("productos encontrados")));
  };
  const search = async (requestSequence) => {
    const query = input.value.trim();
    if (!query) { close(); setFeedback(""); return; }
    try {
      const url = `${productsUrl}&${new URLSearchParams({ q: query })}`;
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error("lookup failed");
      const items = await response.json();
      if (requestSequence === sequence) render(items);
    } catch {
      if (requestSequence !== sequence) return;
      close();
      setFeedback(text("No fue posible buscar en la red local. Conservamos el texto para que puedas consultar o reintentar."));
    }
  };

  input.setAttribute("aria-expanded", "false");
  input.addEventListener("input", () => {
    sequence += 1;
    clearTimeout(timer);
    setFeedback(text("Buscando…"));
    const current = sequence;
    timer = setTimeout(() => void search(current), 250);
  });
  input.addEventListener("keydown", (event) => {
    const items = buttons();
    if (event.key === "ArrowDown" && items.length) { event.preventDefault(); activate(activeIndex + 1); }
    else if (event.key === "ArrowUp" && items.length) { event.preventDefault(); activate(activeIndex - 1); }
    else if (event.key === "Escape") { event.preventDefault(); close(); setFeedback(""); }
    else if (event.key === "Enter" && activeIndex >= 0 && items[activeIndex]) {
      event.preventDefault(); select(items[activeIndex]);
    }
  });
  input.addEventListener("blur", () => window.setTimeout(close, 100));
})();
