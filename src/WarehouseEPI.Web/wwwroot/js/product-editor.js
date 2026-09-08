(() => {
  "use strict";

  const form = document.querySelector("[data-product-editor-form]");
  const field = form?.querySelector("[data-product-entry-location]");
  if (!form || !field) return;

  const input = field.querySelector("[data-product-entry-location-search]");
  const id = field.querySelector("[data-product-entry-location-id]");
  const results = field.querySelector("[data-product-entry-location-results]");
  const selected = field.querySelector("[data-product-entry-location-selected]");
  const selectedCode = field.querySelector("[data-product-entry-location-code]");
  const selectedDescription = field.querySelector("[data-product-entry-location-description]");
  const selectedStatus = field.querySelector("[data-product-entry-location-status]");
  const feedback = field.querySelector("[data-product-entry-location-feedback]");
  const clearButton = field.querySelector("[data-product-entry-location-clear]");
  const lookupUrl = form.dataset.locationLookupUrl;
  let controller;
  let timer = 0;
  let highlighted = -1;

  const announce = (message) => { feedback.textContent = message; };

  const closeResults = () => {
    results.replaceChildren();
    highlighted = -1;
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
  };

  const clearSelection = (clearSearch = false) => {
    id.value = "";
    selected.classList.add("d-none");
    selectedCode.textContent = "";
    selectedDescription.textContent = "";
    selectedStatus.textContent = "";
    if (clearSearch) input.value = "";
    clearButton.disabled = !input.value.trim();
  };

  const selectLocation = (location) => {
    id.value = location.id;
    input.value = location.code;
    input.setCustomValidity("");
    selectedCode.textContent = location.code;
    selectedDescription.textContent = location.description || "Sin descripción";
    selectedStatus.textContent = "Ubicación principal seleccionada";
    selected.classList.remove("d-none");
    clearButton.disabled = false;
    closeResults();
    announce(`Ubicación principal seleccionada: ${location.code}.`);
  };

  const setHighlight = (index) => {
    const options = [...results.querySelectorAll("[role='option']")];
    if (!options.length) return;
    highlighted = (index + options.length) % options.length;
    options.forEach((option, optionIndex) => {
      const active = optionIndex === highlighted;
      option.classList.toggle("active", active);
      option.setAttribute("aria-selected", active ? "true" : "false");
    });
    input.setAttribute("aria-activedescendant", options[highlighted].id);
    options[highlighted].scrollIntoView({ block: "nearest" });
  };

  const renderResults = (locations) => {
    closeResults();
    if (!locations.length) {
      announce("No se encontraron ubicaciones físicas disponibles.");
      return;
    }

    locations.forEach((location, index) => {
      const option = document.createElement("button");
      option.type = "button";
      option.id = `product-entry-location-option-${index}`;
      option.className = "list-group-item list-group-item-action";
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", "false");

      const code = document.createElement("strong");
      code.className = "d-block";
      code.textContent = location.code;
      const description = document.createElement("span");
      description.className = "small text-body-secondary";
      description.textContent = location.description || "Sin descripción";
      option.append(code, description);
      option.addEventListener("mousedown", event => event.preventDefault());
      option.addEventListener("click", () => {
        selectLocation(location);
        input.focus();
      });
      results.append(option);
    });

    input.setAttribute("aria-expanded", "true");
    announce(`${locations.length} ${locations.length === 1 ? "ubicación disponible" : "ubicaciones disponibles"}.`);
  };

  const search = async () => {
    const query = input.value.trim();
    controller?.abort();
    if (!query) {
      closeResults();
      announce("Sin ubicación principal. Escribe para buscar una ubicación física disponible.");
      return;
    }

    controller = new AbortController();
    announce("Buscando ubicaciones…");
    try {
      const parameters = new URLSearchParams({ handler: "Locations", q: query });
      const response = await fetch(`${lookupUrl}?${parameters}`, {
        headers: { Accept: "application/json" },
        signal: controller.signal
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      renderResults(await response.json());
    } catch (error) {
      if (error?.name === "AbortError") return;
      closeResults();
      announce("No fue posible buscar en la red local. Intenta nuevamente.");
    }
  };

  const resolveLocation = async () => {
    const code = input.value.trim();
    if (!code) return;

    controller?.abort();
    window.clearTimeout(timer);
    closeResults();
    controller = new AbortController();
    announce("Validando ubicación…");
    try {
      const parameters = new URLSearchParams({ handler: "ResolveLocation", code });
      const response = await fetch(`${lookupUrl}?${parameters}`, {
        headers: { Accept: "application/json" },
        signal: controller.signal
      });
      if (response.status === 404) {
        announce("No se encontró una ubicación exacta. Selecciona una de los resultados.");
        void search();
        return;
      }
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      selectLocation(await response.json());
      input.focus();
    } catch (error) {
      if (error?.name === "AbortError") return;
      closeResults();
      announce("No fue posible buscar en la red local. Intenta nuevamente.");
    }
  };

  input.addEventListener("input", () => {
    controller?.abort();
    window.clearTimeout(timer);
    clearSelection();
    input.setCustomValidity("");
    clearButton.disabled = !input.value.trim();
    timer = window.setTimeout(() => void search(), 250);
  });

  input.addEventListener("focus", () => {
    if (!id.value && input.value.trim() && results.childElementCount === 0) void search();
  });

  input.addEventListener("keydown", (event) => {
    const options = [...results.querySelectorAll("[role='option']")];
    if (event.key === "ArrowDown" && options.length) {
      event.preventDefault();
      setHighlight(highlighted + 1);
    } else if (event.key === "ArrowUp" && options.length) {
      event.preventDefault();
      setHighlight(highlighted - 1);
    } else if (event.key === "Escape") {
      closeResults();
    } else if (event.key === "Enter" && highlighted >= 0) {
      event.preventDefault();
      options[highlighted].click();
    } else if (event.key === "Enter") {
      event.preventDefault();
      void resolveLocation();
    }
  });

  input.addEventListener("blur", () => window.setTimeout(closeResults, 150));

  clearButton.addEventListener("click", () => {
    controller?.abort();
    window.clearTimeout(timer);
    clearSelection(true);
    closeResults();
    input.setCustomValidity("");
    announce("Sin ubicación principal.");
    input.focus();
  });

  document.addEventListener("click", (event) => {
    if (!field.contains(event.target)) closeResults();
  });

  form.addEventListener("submit", (event) => {
    if (!input.value.trim()) {
      id.value = "";
      input.setCustomValidity("");
      return;
    }
    if (id.value) return;
    event.preventDefault();
    input.setCustomValidity("Selecciona una ubicación de los resultados o deja el campo vacío.");
    announce("Selecciona una ubicación de los resultados antes de guardar.");
    input.reportValidity();
    input.focus();
  });
})();
