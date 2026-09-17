(() => {
  const form = document.querySelector("[data-location-search-form]");
  const input = form?.querySelector("[data-location-search-input]");
  const results = form?.querySelector("[data-location-search-results]");
  const feedback = form?.querySelector("[data-location-search-feedback]");
  const lookupUrl = form?.dataset.locationSearchUrl;
  if (!form || !input || !results || !feedback || !lookupUrl) return;

  let timer;
  let controller;
  let highlighted = -1;

  const options = () => Array.from(results.querySelectorAll("[role='option']"));
  const announce = (message) => { feedback.textContent = message; };
  const close = () => {
    controller?.abort();
    results.replaceChildren();
    highlighted = -1;
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
  };
  const highlight = (index) => {
    const items = options();
    if (!items.length) return;
    highlighted = (index + items.length) % items.length;
    items.forEach((item, itemIndex) => {
      const active = itemIndex === highlighted;
      item.classList.toggle("active", active);
      item.setAttribute("aria-selected", active ? "true" : "false");
    });
    input.setAttribute("aria-activedescendant", items[highlighted].id);
    items[highlighted].scrollIntoView({ block: "nearest" });
  };
  const select = (item) => {
    input.value = item.dataset.searchValue;
    close();
    form.requestSubmit();
  };
  const addOption = (item, kind, index) => {
    const option = document.createElement("button");
    option.type = "button";
    option.id = `location-search-option-${index}`;
    option.className = "list-group-item list-group-item-action";
    option.setAttribute("role", "option");
    option.setAttribute("aria-selected", "false");
    option.dataset.searchValue = kind === "product" ? item.sku : item.code;

    const title = document.createElement("strong");
    title.className = "d-block";
    title.textContent = option.dataset.searchValue;
    const detail = document.createElement("small");
    detail.className = "text-body-secondary";
    const type = kind === "product" ? "Producto" : "Ubicación";
    const description = item.description || (kind === "product" ? "Sin descripción" : "Ubicación física");
    const state = !item.isActive ? " · Inactivo" : kind === "location" && item.isBlocked ? " · Bloqueada" : "";
    detail.textContent = `${type} · ${description}${state}`;
    option.append(title, detail);
    option.addEventListener("mousedown", (event) => event.preventDefault());
    option.addEventListener("click", () => select(option));
    results.append(option);
  };
  const render = (data) => {
    results.replaceChildren();
    const matches = [
      ...(data?.products || []).map((item) => ({ item, kind: "product" })),
      ...(data?.locations || []).map((item) => ({ item, kind: "location" }))
    ];
    matches.forEach((match, index) => addOption(match.item, match.kind, index));
    if (!matches.length) {
      input.setAttribute("aria-expanded", "false");
      announce("No se encontraron productos ni ubicaciones.");
      return;
    }
    input.setAttribute("aria-expanded", "true");
    highlight(0);
    announce(`${matches.length} resultados disponibles. Usa flechas y Enter para seleccionar.`);
  };
  const search = async () => {
    const query = input.value.trim();
    controller?.abort();
    if (!query) {
      close();
      announce("Escribe para buscar y selecciona un producto o una ubicación.");
      return;
    }
    controller = new AbortController();
    announce("Buscando productos y ubicaciones…");
    try {
      const url = new URL(lookupUrl, window.location.href);
      url.searchParams.set("handler", "InventorySearch");
      url.searchParams.set("q", query);
      const response = await fetch(url, { headers: { Accept: "application/json" }, signal: controller.signal });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      render(await response.json());
    } catch (error) {
      if (error?.name === "AbortError") return;
      close();
      announce("No fue posible buscar en la red local. Puedes escribir el filtro y usar Buscar.");
    }
  };

  input.addEventListener("input", () => {
    window.clearTimeout(timer);
    timer = window.setTimeout(() => void search(), 250);
  });
  input.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown" && options().length) {
      event.preventDefault();
      highlight(highlighted + 1);
    } else if (event.key === "ArrowUp" && options().length) {
      event.preventDefault();
      highlight(highlighted - 1);
    } else if (event.key === "Enter" && highlighted >= 0) {
      event.preventDefault();
      options()[highlighted].click();
    } else if (event.key === "Escape") {
      close();
    }
  });
  input.addEventListener("blur", () => window.setTimeout(close, 150));

  window.WarehouseEpiHidCapture?.listen({
    root: document.body,
    onScan: (code) => {
      window.clearTimeout(timer);
      input.value = code;
      close();
      form.requestSubmit();
    }
  });
})();
