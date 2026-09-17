(() => {
  "use strict";

  const form = document.querySelector("[data-process-editor]");
  const picker = form?.querySelector("[data-process-target-picker]");
  if (!form || !picker) return;

  const input = picker.querySelector("[data-process-target-search]");
  const results = picker.querySelector("[data-process-target-results]");
  const feedback = picker.querySelector("[data-process-target-feedback]");
  const selectedList = picker.querySelector("[data-process-target-list]");
  const emptyState = picker.querySelector("[data-process-target-empty]");
  const reason = form.querySelector("[data-process-rack-reason]");
  const lookupUrl = form.dataset.targetLookupUrl;
  if (!input || !results || !feedback || !selectedList || !emptyState || !reason || !lookupUrl) return;

  const originalGroupedTargetKeys = new Set([...form.querySelectorAll("[data-process-original-grouped-target]")].map(item => item.value));
  let controller;
  let timer = 0;
  let highlighted = -1;

  const selectedItems = () => [...selectedList.querySelectorAll("[data-process-target-item]")];
  const selectedKeys = () => new Set(selectedItems().map(item => item.dataset.targetKey));
  const selectedRows = () => new Set(selectedItems()
    .filter(item => item.dataset.targetType === "row")
    .map(item => item.dataset.targetKey.slice(2)));
  const rackRow = key => key.split(":")[1];
  const options = () => [...results.querySelectorAll("[role='option']")];
  const announce = message => { feedback.textContent = message; };

  const closeResults = () => {
    results.replaceChildren();
    highlighted = -1;
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
  };

  const setHighlight = index => {
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

  const updateReasonVisibility = () => {
    const currentGroupedTargetKeys = new Set(selectedItems()
      .filter(item => item.dataset.targetType === "row" || item.dataset.targetType === "rack")
      .map(item => item.dataset.targetKey));
    const changed = currentGroupedTargetKeys.size !== originalGroupedTargetKeys.size
      || [...currentGroupedTargetKeys].some(key => !originalGroupedTargetKeys.has(key));
    reason.dataset.groupedTargetsChanged = changed ? "true" : "false";
    reason.classList.remove("d-none");
    reason.setAttribute("aria-hidden", "false");
  };

  const createSelection = item => {
    const row = document.createElement("div");
    row.className = "border rounded p-3 d-flex flex-column flex-sm-row align-items-sm-center gap-2";
    row.dataset.processTargetItem = "";
    row.dataset.targetKey = item.key;
    row.dataset.targetType = item.type;

    const hidden = document.createElement("input");
    hidden.type = "hidden";
    hidden.name = "Input.Targets";
    hidden.value = item.key;

    const content = document.createElement("div");
    content.className = "flex-grow-1";
    const heading = document.createElement("div");
    heading.className = "d-flex flex-wrap align-items-center gap-2";
    const label = document.createElement("strong");
    label.textContent = item.label;
    const badge = document.createElement("span");
    badge.className = "badge text-bg-secondary";
    badge.textContent = item.type === "row" ? "Fila completa" : item.type === "rack" ? "Rack WIP" : "Área WIP";
    heading.append(label, badge);
    const description = document.createElement("small");
    description.className = "d-block text-body-secondary";
    description.textContent = item.type === "row"
      ? "Aplica a todos los racks de la fila, incluidos los de almacenamiento"
      : item.type === "rack" ? "Aplica a todas las posiciones del rack" : "Área de proceso WIP";
    content.append(heading, description);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "btn btn-outline-danger align-self-start align-self-sm-center";
    remove.dataset.processTargetRemove = "";
    remove.setAttribute("aria-label", `Quitar ${item.label}`);
    remove.textContent = "Quitar";
    row.append(hidden, content, remove);
    return row;
  };

  const addSelection = item => {
    if (selectedKeys().has(item.key)) {
      announce(`${item.label} ya está asociado.`);
      return;
    }
    if (item.type === "rack" && selectedRows().has(rackRow(item.key))) {
      announce(`${item.label} ya está incluido por la fila seleccionada.`);
      return;
    }
    let removedRacks = 0;
    if (item.type === "row") {
      const rowCode = item.key.slice(2);
      selectedItems().filter(selected => selected.dataset.targetType === "rack"
        && rackRow(selected.dataset.targetKey) === rowCode).forEach(selected => {
        selected.remove();
        removedRacks += 1;
      });
    }
    selectedList.append(createSelection(item));
    emptyState.classList.add("d-none");
    input.value = "";
    input.setCustomValidity("");
    closeResults();
    updateReasonVisibility();
    const removedMessage = removedRacks === 0 ? "" : ` Se retiraron ${removedRacks} racks individuales ya incluidos.`;
    announce(`${item.label} agregado.${removedMessage} Puedes buscar otro destino.`);
    input.focus();
  };

  const renderResults = items => {
    closeResults();
    const keys = selectedKeys();
    const rows = selectedRows();
    const available = items.filter(item => !keys.has(item.key)
      && !(item.type === "rack" && rows.has(rackRow(item.key))));
    if (!available.length) {
      announce("No se encontraron destinos WIP adicionales.");
      return;
    }

    available.forEach((item, index) => {
      const option = document.createElement("button");
      option.type = "button";
      option.id = `process-target-option-${index}`;
      option.className = "list-group-item list-group-item-action";
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", "false");
      const label = document.createElement("strong");
      label.className = "d-block";
      label.textContent = item.label;
      const detail = document.createElement("small");
      detail.className = "text-body-secondary";
      detail.textContent = item.description;
      option.append(label, detail);
      option.addEventListener("mousedown", event => event.preventDefault());
      option.addEventListener("click", () => addSelection(item));
      results.append(option);
    });

    input.setAttribute("aria-expanded", "true");
    setHighlight(0);
    announce(`${available.length} ${available.length === 1 ? "destino disponible" : "destinos disponibles"}. Usa flechas y Enter para elegir.`);
  };

  const search = async () => {
    const query = input.value.trim();
    controller?.abort();
    if (!query) {
      closeResults();
      announce("Escribe para buscar un área WIP, una fila completa o un rack WIP.");
      return;
    }

    controller = new AbortController();
    announce("Buscando destinos WIP…");
    try {
      const url = new URL(lookupUrl, window.location.href);
      url.searchParams.set("q", query);
      const response = await fetch(url, {
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

  input.addEventListener("input", () => {
    controller?.abort();
    window.clearTimeout(timer);
    input.setCustomValidity("");
    timer = window.setTimeout(() => void search(), 250);
  });
  input.addEventListener("focus", () => {
    if (input.value.trim() && results.childElementCount === 0) void search();
  });
  input.addEventListener("keydown", event => {
    const items = options();
    if (event.key === "ArrowDown" && items.length) {
      event.preventDefault();
      setHighlight(highlighted + 1);
    } else if (event.key === "ArrowUp" && items.length) {
      event.preventDefault();
      setHighlight(highlighted - 1);
    } else if (event.key === "Escape") {
      closeResults();
    } else if (event.key === "Enter" && highlighted >= 0) {
      event.preventDefault();
      items[highlighted].click();
    }
  });
  input.addEventListener("blur", () => window.setTimeout(closeResults, 150));

  selectedList.addEventListener("click", event => {
    const button = event.target.closest("[data-process-target-remove]");
    if (!button) return;
    const item = button.closest("[data-process-target-item]");
    const label = item.querySelector("strong")?.textContent || "Destino";
    item.remove();
    emptyState.classList.toggle("d-none", selectedItems().length > 0);
    updateReasonVisibility();
    announce(`${label} retirado.`);
    input.focus();
  });

  document.addEventListener("click", event => {
    if (!picker.contains(event.target)) closeResults();
  });
  form.addEventListener("submit", event => {
    if (!input.value.trim()) return;
    event.preventDefault();
    input.setCustomValidity("Selecciona un destino de los resultados o limpia la búsqueda.");
    announce("Selecciona un destino de los resultados antes de guardar.");
    input.reportValidity();
    input.focus();
  });

  updateReasonVisibility();

  const defaultPicker = form.querySelector("[data-wip-default-picker]");
  const defaultInput = defaultPicker?.querySelector("[data-wip-default-search]");
  const defaultKey = defaultPicker?.querySelector("[data-wip-default-key]");
  const defaultResults = defaultPicker?.querySelector("[data-wip-default-results]");
  const defaultFeedback = defaultPicker?.querySelector("[data-wip-default-feedback]");
  const defaultClear = defaultPicker?.querySelector("[data-wip-default-clear]");
  const defaultLookupUrl = form.dataset.defaultLookupUrl;
  let defaultTimer = 0;
  let defaultController;
  let defaultHighlighted = -1;
  const defaultOptions = () => [...defaultResults.querySelectorAll("[role='option']")];

  const closeDefaultResults = () => {
    defaultResults?.replaceChildren();
    defaultHighlighted = -1;
    defaultInput?.setAttribute("aria-expanded", "false");
    defaultInput?.removeAttribute("aria-activedescendant");
  };
  const highlightDefault = index => {
    const items = defaultOptions();
    if (!items.length) return;
    defaultHighlighted = (index + items.length) % items.length;
    items.forEach((item, itemIndex) => {
      const active = itemIndex === defaultHighlighted;
      item.classList.toggle("active", active);
      item.setAttribute("aria-selected", active ? "true" : "false");
    });
    defaultInput.setAttribute("aria-activedescendant", items[defaultHighlighted].id);
    items[defaultHighlighted].scrollIntoView({ block: "nearest" });
  };
  const chooseDefault = item => {
    defaultKey.value = item.key;
    defaultInput.value = item.label;
    defaultInput.setCustomValidity("");
    defaultFeedback.textContent = `${item.label} seleccionado como predeterminado.${item.warning ? ` ${item.warning}.` : ""}`;
    closeDefaultResults();
  };
  const searchDefaults = async () => {
    const query = defaultInput.value.trim();
    defaultController?.abort();
    if (!query) {
      closeDefaultResults();
      defaultFeedback.textContent = "Escribe para buscar un área, rack o posición WIP.";
      return;
    }
    defaultController = new AbortController();
    try {
      const url = new URL(defaultLookupUrl, window.location.href);
      url.searchParams.set("q", query);
      for (const item of selectedItems()) url.searchParams.append("targets", item.dataset.targetKey);
      const response = await fetch(url, { headers: { Accept: "application/json" }, signal: defaultController.signal });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const items = await response.json();
      closeDefaultResults();
      for (const [index, item] of items.entries()) {
        const option = document.createElement("button");
        option.type = "button";
        option.id = `process-default-wip-option-${index}`;
        option.className = "list-group-item list-group-item-action";
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", "false");
        const label = document.createElement("strong");
        label.className = "d-block";
        label.textContent = item.label;
        const detail = document.createElement("small");
        detail.className = "text-body-secondary";
        detail.textContent = `${item.type} · ${item.description}`;
        option.append(label, detail);
        option.addEventListener("mousedown", event => event.preventDefault());
        option.addEventListener("click", () => chooseDefault(item));
        defaultResults.append(option);
      }
      defaultInput.setAttribute("aria-expanded", items.length ? "true" : "false");
      if (items.length) highlightDefault(0);
      defaultFeedback.textContent = items.length ? `${items.length} destinos disponibles. Usa flechas y Enter para elegir.` : "No hay destinos compatibles con esa búsqueda.";
    } catch (error) {
      if (error?.name !== "AbortError") defaultFeedback.textContent = "No fue posible buscar en la red local. Intenta nuevamente.";
    }
  };
  defaultInput?.addEventListener("input", () => {
    defaultKey.value = "";
    window.clearTimeout(defaultTimer);
    defaultTimer = window.setTimeout(() => void searchDefaults(), 250);
  });
  defaultInput?.addEventListener("keydown", event => {
    const items = defaultOptions();
    if (event.key === "ArrowDown" && items.length) { event.preventDefault(); highlightDefault(defaultHighlighted + 1); }
    else if (event.key === "ArrowUp" && items.length) { event.preventDefault(); highlightDefault(defaultHighlighted - 1); }
    else if (event.key === "Enter" && defaultHighlighted >= 0) { event.preventDefault(); items[defaultHighlighted].click(); }
    else if (event.key === "Escape") closeDefaultResults();
  });
  defaultClear?.addEventListener("click", () => {
    defaultKey.value = "";
    defaultInput.value = "";
    closeDefaultResults();
    defaultFeedback.textContent = "El proceso no tendrá WIP predeterminado.";
    defaultInput.focus();
  });
})();
