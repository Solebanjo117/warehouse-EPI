(() => {
  const debounce = (callback, delay = 250) => {
    let timer;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => callback(...args), delay);
    };
  };

  const requestJson = async (url) => {
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (!response.ok) return null;
    return response.json();
  };

  const describeProduct = (item) => [item.description, item.externalReference, item.unitCode]
    .filter(Boolean).join(" · ");
  const describeLocation = (item) => item.description || "Ubicación operativa";

  const renderSuggestions = (container, items, kind, select) => {
    container.replaceChildren();
    for (const item of items || []) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "list-group-item list-group-item-action";
      const title = document.createElement("strong");
      title.textContent = kind === "product" ? item.sku : item.code;
      const detail = document.createElement("small");
      detail.className = "d-block text-muted";
      detail.textContent = kind === "product" ? describeProduct(item) : describeLocation(item);
      button.append(title, detail);
      button.addEventListener("click", () => select(item));
      container.append(button);
    }
  };

  const operationShell = document.querySelector("[data-operation]");
  if (operationShell) {
    const form = operationShell.querySelector("[data-operation-form]");
    const lookupUrl = operationShell.dataset.lookupUrl;
    const operation = operationShell.dataset.operation;
    const quantityInput = operationShell.querySelector("[data-quantity]");
    const unitLabel = operationShell.querySelector("[data-unit-label]");
    const balancePreview = operationShell.querySelector("[data-balance-preview]");
    const balanceText = operationShell.querySelector("[data-balance-text]");
    const negativeWarning = operationShell.querySelector("[data-negative-warning]");
    const versionInput = operationShell.querySelector("[data-balance-version]");
    const selected = {};
    const lookups = {};
    let productLocations = null;
    const locationProducts = {};

    const hiddenFor = (kind) => operationShell.querySelector(`[data-selected-id="${kind}"]`);
    const primaryLocationKind = operation === "entry" ? "destination"
      : operation === "exit" || operation === "transfer" ? "source"
        : "location";
    const requiredKinds = operation === "entry" ? ["product", "destination"]
      : operation === "exit" ? ["product", "source"]
        : operation === "transfer" ? ["product", "source", "destination"]
          : ["product", "location"];

    const number = (value) => {
      const parsed = Number.parseFloat(String(value || "0").replace(",", "."));
      return Number.isFinite(parsed) ? parsed : 0;
    };
    const format = (value) => new Intl.NumberFormat("es-MX", { maximumFractionDigits: 4 }).format(value);

    const refreshPreview = () => {
      const quantity = number(quantityInput.value);
      const source = number(balancePreview.dataset.source);
      const destination = number(balancePreview.dataset.destination);
      const location = number(balancePreview.dataset.location);
      let message = "Selecciona producto y ubicación";
      let isNegative = false;

      if (operation === "entry" && selected.product && selected.destination) {
        const result = destination + quantity;
        message = `Destino: ${format(destination)} → ${format(result)}`;
        isNegative = result < 0;
      } else if (operation === "exit" && selected.product && selected.source) {
        const result = source - quantity;
        message = `Origen: ${format(source)} → ${format(result)}`;
        isNegative = result < 0;
      } else if (operation === "transfer" && selected.product && selected.source && selected.destination) {
        const sourceResult = source - quantity;
        const destinationResult = destination + quantity;
        message = `Origen ${format(source)} → ${format(sourceResult)} · Destino ${format(destination)} → ${format(destinationResult)}`;
        isNegative = sourceResult < 0 || destinationResult < 0;
      } else if (operation === "adjustment" && selected.product && selected.location) {
        const delta = quantity - location;
        message = `Actual: ${format(location)} · Diferencia: ${delta > 0 ? "+" : ""}${format(delta)} · Final: ${format(quantity)}`;
        isNegative = quantity < 0;
      }

      balanceText.textContent = message;
      negativeWarning.classList.toggle("d-none", !isNegative);
    };

    const refreshBalance = async (kind) => {
      if (!selected.product || !selected[kind]) return;
      const params = new URLSearchParams({
        handler: "Balance",
        productId: selected.product.id,
        locationId: selected[kind].id
      });
      const balance = await requestJson(`${lookupUrl}?${params}`);
      if (!balance) return;
      balancePreview.dataset[kind] = balance.quantity;
      if (kind === "location" && versionInput) versionInput.value = balance.version;
      refreshPreview();
    };

    const relationshipMeta = (item) => {
      const quantity = format(number(item.quantity));
      if (item.hasActiveAssignment && item.hasNonZeroBalance) return `Asignación activa · saldo ${quantity}`;
      if (item.hasActiveAssignment) return "Asignación activa · saldo 0";
      return `Con saldo ${quantity}`;
    };

    const addRelationshipMessage = (panel, text, className = "text-muted") => {
      const message = document.createElement("p");
      message.className = `relationship-message ${className}`;
      message.textContent = text;
      panel.append(message);
    };

    const renderRelationshipChoices = (panel, titleText, items, kind, onSelect, selectedId) => {
      panel.replaceChildren();
      panel.classList.remove("d-none");
      const title = document.createElement("strong");
      title.className = "relationship-title";
      title.textContent = titleText;
      panel.append(title);
      const choices = document.createElement("div");
      choices.className = "relationship-choices";
      for (const item of items) {
        const choice = document.createElement(onSelect ? "button" : "div");
        if (onSelect) choice.type = "button";
        choice.className = `relationship-choice${item.id === selectedId ? " is-selected" : ""}`;
        const name = document.createElement("strong");
        name.textContent = kind === "product" ? item.sku : item.code;
        const detail = document.createElement("small");
        detail.textContent = relationshipMeta(item);
        choice.append(name, detail);
        if (onSelect) choice.addEventListener("click", () => onSelect(item));
        choices.append(choice);
      }
      panel.append(choices);
    };

    const renderProductRelationships = () => {
      const panel = lookups.product?.panel;
      if (!panel || productLocations === null) return;
      if (productLocations.length === 0) {
        panel.replaceChildren();
        panel.classList.remove("d-none");
        addRelationshipMessage(panel,
          "Sin ubicaciones asociadas ni saldo. Escanea una ubicación; se asociará al confirmar.");
        return;
      }

      const selectedLocation = selected[primaryLocationKind];
      renderRelationshipChoices(
        panel,
        productLocations.length === 1 ? "Ubicación relacionada" : "Ubicaciones relacionadas; elige una",
        productLocations,
        "location",
        (item) => void applySelection(primaryLocationKind, item, true),
        selectedLocation?.id);
      if (selectedLocation && !productLocations.some(item => item.id === selectedLocation.id))
        addRelationshipMessage(panel, "Esta pareja se asociará al confirmar con NIP.", "text-primary");
    };

    const canSelectProductFrom = (kind) => !(operation === "transfer" && kind === "destination");

    const renderLocationRelationships = (kind) => {
      const panel = lookups[kind]?.panel;
      const items = locationProducts[kind];
      if (!panel || items === undefined) return;
      if (items.length === 0) {
        panel.replaceChildren();
        panel.classList.remove("d-none");
        addRelationshipMessage(panel,
          "Sin productos asociados ni saldo. El producto se asociará al confirmar.");
        return;
      }

      const selectable = canSelectProductFrom(kind);
      renderRelationshipChoices(
        panel,
        items.length === 1 ? "Producto relacionado" : "Productos relacionados; elige uno",
        items,
        "product",
        selectable ? (item) => void applySelection("product", item, true) : null,
        selected.product?.id);
      if (selected.product && !items.some(item => item.id === selected.product.id))
        addRelationshipMessage(panel, "El producto seleccionado se asociará aquí al confirmar con NIP.", "text-primary");
      if (!selectable)
        addRelationshipMessage(panel, "El destino es informativo y no cambia el producto de la transferencia.");
    };

    const refreshRelationshipPanels = () => {
      renderProductRelationships();
      for (const kind of ["source", "destination", "location"])
        renderLocationRelationships(kind);
    };

    const loadProductLocations = async () => {
      if (!selected.product) return;
      const productId = selected.product.id;
      const params = new URLSearchParams({ handler: "ProductLocations", productId });
      const items = await requestJson(`${lookupUrl}?${params}`);
      if (selected.product?.id !== productId || !items) return;
      productLocations = items;
      renderProductRelationships();
      if (!selected[primaryLocationKind] && items.length === 1)
        await applySelection(primaryLocationKind, items[0], true);
    };

    const loadLocationProducts = async (kind) => {
      if (!selected[kind]) return;
      const locationId = selected[kind].id;
      const params = new URLSearchParams({ handler: "LocationProducts", locationId });
      const items = await requestJson(`${lookupUrl}?${params}`);
      if (selected[kind]?.id !== locationId || !items) return;
      locationProducts[kind] = items;
      renderLocationRelationships(kind);
      if (canSelectProductFrom(kind) && !selected.product && items.length === 1)
        await applySelection("product", items[0], true);
    };

    const applySelection = async (kind, item, loadRelationships) => {
      const lookup = lookups[kind];
      const lookupKind = kind === "product" ? "product" : "location";
      selected[kind] = item;
      lookup.hidden.value = item.id;
      lookup.input.value = lookupKind === "product" ? item.sku : item.code;
      lookup.input.setCustomValidity("");
      lookup.record.querySelector("[data-selected-title]").textContent = lookup.input.value;
      lookup.record.querySelector("[data-selected-detail]").textContent = lookupKind === "product"
        ? describeProduct(item) : describeLocation(item);
      if (lookupKind === "product") {
        lookup.record.dataset.unit = item.unitCode;
        lookup.record.dataset.allowsDecimals = item.allowsDecimals;
        lookup.record.dataset.tracksLots = item.tracksLots;
        unitLabel.textContent = item.unitCode;
        quantityInput.step = item.allowsDecimals ? "0.0001" : "1";
        if (item.tracksLots)
          lookup.input.setCustomValidity("Este producto controla lotes y estará disponible en la fase 9.");
      }
      lookup.record.classList.remove("d-none");
      lookup.results.replaceChildren();

      if (lookupKind === "product") {
        for (const locationKind of ["source", "destination", "location"])
          void refreshBalance(locationKind);
        if (loadRelationships) await loadProductLocations();
      } else {
        void refreshBalance(kind);
        if (loadRelationships) await loadLocationProducts(kind);
      }
      refreshRelationshipPanels();
      refreshPreview();
    };

    const clearSelection = (kind) => {
      const lookup = lookups[kind];
      selected[kind] = null;
      lookup.hidden.value = "";
      lookup.record.classList.add("d-none");
      lookup.input.setCustomValidity("");
      lookup.panel.classList.add("d-none");
      lookup.panel.replaceChildren();
      if (kind === "product") {
        productLocations = null;
        unitLabel.textContent = "Unidad";
      } else {
        locationProducts[kind] = undefined;
        balancePreview.dataset[kind] = "0";
        if (kind === "location" && versionInput) versionInput.value = "0";
      }
      refreshRelationshipPanels();
      refreshPreview();
    };

    const focusNextRequired = () => {
      const missing = requiredKinds.find(kind => !selected[kind]);
      if (missing) {
        const relationshipButton = lookups[missing]?.panel.querySelector("button.relationship-choice");
        (relationshipButton || lookups[missing]?.input)?.focus();
        return;
      }
      quantityInput.focus();
    };

    const setupLookup = (field) => {
      const kind = field.dataset.lookupField;
      const lookupKind = kind === "product" ? "product" : "location";
      const input = field.querySelector("[data-lookup-input]");
      const results = field.querySelector("[data-lookup-results]");
      const record = field.querySelector("[data-selected-record]");
      const hidden = hiddenFor(kind);
      const panel = field.querySelector("[data-relationship-panel]");
      let searchSequence = 0;
      lookups[kind] = { field, input, results, record, hidden, panel };

      if (hidden.value) {
        selected[kind] = {
          id: hidden.value,
          sku: kind === "product" ? input.value : undefined,
          code: kind !== "product" ? input.value : undefined,
          unitCode: record.dataset.unit,
          allowsDecimals: record.dataset.allowsDecimals === "true",
          tracksLots: record.dataset.tracksLots === "true"
        };
      }

      const search = debounce(async (sequence) => {
        if (sequence !== searchSequence) return;
        const query = input.value.trim();
        if (!query) return results.replaceChildren();
        const handler = lookupKind === "product" ? "Products" : "Locations";
        const items = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, q: query })}`);
        if (sequence !== searchSequence) return;
        renderSuggestions(results, items, lookupKind, (item) => {
          searchSequence++;
          void applySelection(kind, item, true).then(focusNextRequired);
        });
      });

      input.addEventListener("input", () => {
        clearSelection(kind);
        searchSequence++;
        search(searchSequence);
      });
      input.addEventListener("keydown", async (event) => {
        if (event.key !== "Enter") return;
        event.preventDefault();
        searchSequence++;
        results.replaceChildren();
        const code = input.value.trim();
        if (!code) return;
        const handler = lookupKind === "product" ? "ResolveProduct" : "ResolveLocation";
        const item = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, code })}`);
        if (item) {
          await applySelection(kind, item, true);
          focusNextRequired();
        } else {
          input.setCustomValidity("No se encontró un registro operativo con ese código.");
          input.reportValidity();
        }
      });
    };

    operationShell.querySelectorAll("[data-lookup-field]").forEach(setupLookup);
    for (const kind of Object.keys(lookups)) {
      if (!selected[kind]) continue;
      if (kind === "product") void loadProductLocations();
      else void loadLocationProducts(kind);
    }
    quantityInput.addEventListener("input", refreshPreview);
    refreshPreview();

    const reviewButton = operationShell.querySelector("[data-review-button]");
    const modalElement = document.getElementById("confirm-operation");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    reviewButton.addEventListener("click", () => {
      const pinInput = operationShell.querySelector("[data-pin-input]");
      pinInput.required = false;
      for (const kind of requiredKinds) {
        if (!hiddenFor(kind).value) {
          const input = operationShell.querySelector(`[data-lookup-field="${kind}"] [data-lookup-input]`);
          input.setCustomValidity("Selecciona un registro de la lista o escanea un código válido.");
          input.reportValidity();
          return;
        }
      }
      if (!form.reportValidity()) return;
      const productCode = operationShell.querySelector('[data-lookup-field="product"] [data-lookup-input]').value;
      const locations = requiredKinds.filter(kind => kind !== "product")
        .map(kind => operationShell.querySelector(`[data-lookup-field="${kind}"] [data-lookup-input]`).value)
        .join(" → ");
      const summary = operationShell.querySelector("[data-confirmation-summary]");
      summary.replaceChildren();
      const productLine = document.createElement("strong");
      productLine.textContent = productCode;
      const details = document.createElement("span");
      details.textContent = `${locations} · ${quantityInput.value} ${unitLabel.textContent}`;
      const balance = document.createElement("small");
      balance.textContent = balanceText.textContent;
      summary.append(productLine, details, balance);
      pinInput.required = true;
      modal.show();
      modalElement.addEventListener("shown.bs.modal", () => pinInput.focus(), { once: true });
    });

    form.addEventListener("submit", () => {
      const button = operationShell.querySelector("[data-submit-button]");
      button.disabled = true;
      button.textContent = "Confirmando…";
    });
  }

  const inventoryShell = document.querySelector("[data-inventory-query]");
  if (inventoryShell) {
    const lookupUrl = inventoryShell.dataset.lookupUrl;
    inventoryShell.querySelectorAll("[data-query-form]").forEach((form) => {
      const kind = form.dataset.queryForm;
      const input = form.querySelector("[data-query-input]");
      const hidden = form.querySelector("[data-query-selected-id]");
      const results = form.querySelector("[data-query-results]");
      const search = debounce(async () => {
        const query = input.value.trim();
        if (!query) return results.replaceChildren();
        const handler = kind === "product" ? "Products" : "Locations";
        const items = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, q: query })}`);
        renderSuggestions(results, items, kind, (item) => {
          hidden.value = item.id;
          input.value = kind === "product" ? item.sku : item.code;
          results.replaceChildren();
        });
      });
      input.addEventListener("input", () => {
        hidden.value = "";
        search();
      });
    });
  }
})();
