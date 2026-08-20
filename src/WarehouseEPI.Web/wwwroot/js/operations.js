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

  const preferredCameraStorageKey = "warehouseEpi.preferredCameraDeviceId";
  const cameraVideoConstraints = {
    width: { ideal: 1920 },
    height: { ideal: 1080 },
    frameRate: { ideal: 30 }
  };

  const readPreferredCameraDeviceId = () => {
    try { return window.localStorage.getItem(preferredCameraStorageKey); }
    catch { return null; }
  };

  const savePreferredCameraDeviceId = (deviceId) => {
    if (!deviceId) return;
    try { window.localStorage.setItem(preferredCameraStorageKey, deviceId); }
    catch { /* Storage can be unavailable in private or restricted browser modes. */ }
  };

  const openCameraStream = async (requestedDeviceId) => {
    const preferredDeviceId = requestedDeviceId || readPreferredCameraDeviceId();
    const candidates = [];
    if (preferredDeviceId) candidates.push({ ...cameraVideoConstraints, deviceId: { exact: preferredDeviceId } });
    candidates.push({ ...cameraVideoConstraints, facingMode: { exact: "environment" } });
    candidates.push({ ...cameraVideoConstraints, facingMode: { ideal: "environment" } });

    let lastError;
    for (const video of candidates) {
      try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video });
        savePreferredCameraDeviceId(stream.getVideoTracks?.()[0]?.getSettings?.().deviceId);
        return stream;
      } catch (error) {
        lastError = error;
        if (["NotAllowedError", "SecurityError", "NotReadableError"].includes(error?.name)) throw error;
      }
    }
    throw lastError;
  };

  const availableVideoDevices = async () => {
    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices.filter(device => device.kind === "videoinput" && device.deviceId);
  };

  const updateCameraSwitchButton = async (button, stream) => {
    if (!button || !navigator.mediaDevices?.enumerateDevices) return;
    try {
      const devices = await availableVideoDevices();
      button.classList.toggle("d-none", devices.length < 2);
      const currentDeviceId = stream.getVideoTracks?.()[0]?.getSettings?.().deviceId;
      const currentIndex = devices.findIndex(device => device.deviceId === currentDeviceId);
      button.title = `Cambiar cámara (${(currentIndex >= 0 ? currentIndex : 0) + 1} de ${devices.length})`;
    } catch {
      button.classList.add("d-none");
    }
  };

  const nextCameraDeviceId = async (stream) => {
    const devices = await availableVideoDevices();
    if (devices.length < 2) return null;
    const currentDeviceId = stream?.getVideoTracks?.()[0]?.getSettings?.().deviceId;
    const currentIndex = devices.findIndex(device => device.deviceId === currentDeviceId);
    return devices[(currentIndex + 1 + devices.length) % devices.length].deviceId;
  };

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
    const exitModePicker = operationShell.querySelector("[data-exit-mode-picker]");
    const isWipExit = () => operation === "exit"
      && exitModePicker?.querySelector('input:checked')?.value === "Wip";
    const selectedExitMode = () => exitModePicker?.querySelector('input:checked')?.value || "";
    const entryWorkstation = operationShell.hasAttribute("data-entry-workstation")
      || operationShell.hasAttribute("data-guided-workstation");
    const quantityInput = operationShell.querySelector("[data-quantity]");
    const unitLabel = operationShell.querySelector("[data-unit-label]");
    const balancePreview = operationShell.querySelector("[data-balance-preview]");
    const balanceText = operationShell.querySelector("[data-balance-text]");
    const negativeWarning = operationShell.querySelector("[data-negative-warning]");
    const versionInput = operationShell.querySelector("[data-balance-version]");
    const operationFeedback = operationShell.querySelector("[data-operation-feedback]");
    const selected = {};
    const lookups = {};
    let productLocations = null;
    const locationProducts = {};

    const hiddenFor = (kind) => operationShell.querySelector(`[data-selected-id="${kind}"]`);
    const primaryLocationKind = operation === "entry" ? "destination"
      : operation === "exit" || operation === "transfer" || operation === "wipissue" ? "source"
        : "location";
    const requiredKinds = () => operation === "entry" ? ["product", "destination"]
      : operation === "exit" ? ["product", "source", ...(isWipExit() ? ["destination"] : [])]
        : operation === "wipissue" ? ["product", "source", "destination"]
        : operation === "transfer" ? ["product", "source", "destination"]
          : ["product", "location"];

    const fieldName = (kind) => ({
      product: "Producto",
      source: "Ubicación origen",
      destination: "Ubicación destino",
      "exit-mode": "Tipo de salida",
      location: "Ubicación"
    })[kind];

    const setOperationFeedback = (message) => {
      if (!operationFeedback) return;
      operationFeedback.textContent = message;
      operationFeedback.classList.toggle("d-none", !message);
    };

    const number = (value) => {
      const parsed = Number.parseFloat(String(value || "0").replace(",", "."));
      return Number.isFinite(parsed) ? parsed : 0;
    };
    const format = (value) => new Intl.NumberFormat("es-MX", { maximumFractionDigits: 4 }).format(value);
    let editingEntryStep;
    const guidedKinds = Array.from(operationShell.querySelectorAll("[data-entry-step]"))
      .map(step => step.dataset.entryStep);
    const visibleGuidedKinds = () => guidedKinds.filter(kind => kind !== "destination" || operation !== "exit" || isWipExit());
    const notesInput = operationShell.querySelector("[data-operation-notes]");

    const refreshEntryState = () => {
      if (!entryWorkstation) return;

      const quantityComplete = quantityInput.value.trim() !== "" && quantityInput.checkValidity()
        && (operation === "adjustment" || number(quantityInput.value) > 0);
      const completed = Object.fromEntries(guidedKinds.map(kind => [kind,
        kind === "exit-mode" ? Boolean(selectedExitMode())
          : kind === "quantity" ? quantityComplete : kind === "notes" ? Boolean(notesInput?.value.trim()) : Boolean(selected[kind])
      ]));
      const kinds = visibleGuidedKinds();
      const completedCount = kinds.filter(kind => completed[kind]).length;
      const activeKind = editingEntryStep || kinds.find(kind => !completed[kind]);

      for (const kind of kinds) {
        const step = operationShell.querySelector(`[data-entry-step="${kind}"]`);
        if (!step) continue;
        const isActive = kind === activeKind;
        const isComplete = completed[kind] && !isActive;
        step.classList.toggle("is-active", isActive);
        step.classList.toggle("is-complete", isComplete);
        step.classList.toggle("is-pending", !isActive && !isComplete);
        if (isActive) step.setAttribute("aria-current", "step");
        else step.removeAttribute("aria-current");
        const status = step.querySelector("[data-entry-step-status]");
        status.textContent = isActive ? "En captura" : isComplete ? "Listo" : "Pendiente";
        step.querySelector("[data-edit-step]")?.classList.toggle("d-none", !isComplete);
      }

      const productRecord = lookups.product?.record;
      const productTitle = productRecord?.querySelector("[data-selected-title]")?.textContent?.trim();
      const productDetail = productRecord?.querySelector("[data-selected-detail]")?.textContent?.trim();
      const quantityText = quantityComplete
        ? `${format(number(quantityInput.value))} ${unitLabel.textContent.trim()}`
        : "Sin capturar";

      for (const kind of kinds) {
        const step = operationShell.querySelector(`[data-entry-step="${kind}"]`);
        const record = lookups[kind]?.record;
        const title = kind === "exit-mode" ? (selectedExitMode() === "Wip" ? "Surtir WIP" : selectedExitMode() === "General" ? "Salida general" : "Tipo pendiente")
          : kind === "quantity" ? quantityText : kind === "notes" ? (notesInput?.value.trim() || "Motivo pendiente")
          : record?.querySelector("[data-selected-title]")?.textContent?.trim() || `${fieldName(kind)} pendiente`;
        const detail = kind === "quantity" ? (quantityComplete ? balanceText.textContent : "")
          : kind === "notes" ? "" : record?.querySelector("[data-selected-detail]")?.textContent?.trim() || "";
        step?.querySelector("[data-entry-result-title]") && (step.querySelector("[data-entry-result-title]").textContent = title);
        step?.querySelector("[data-entry-result-detail]") && (step.querySelector("[data-entry-result-detail]").textContent = detail);
      }

      const progress = operationShell.querySelector("[data-entry-progress]");
      const progressText = `${completedCount} de ${kinds.length} listos`;
      if (progress.textContent !== progressText) progress.textContent = progressText;
      const setSummary = (selector, value) => { const element = operationShell.querySelector(selector); if (element) element.textContent = value; };
      setSummary("[data-entry-summary-product]", completed.product ? [productTitle, productDetail].filter(Boolean).join(" · ") : "Sin seleccionar");
      const locationKinds = ["source", "destination", "location"].filter(kind => selected[kind]);
      setSummary("[data-entry-summary-destination]", completed.destination ? lookups.destination?.input.value || "Sin seleccionar" : "Sin seleccionar");
      setSummary("[data-entry-summary-location]", locationKinds.map(kind => `${fieldName(kind)}: ${lookups[kind].input.value}`).join(" · ") || "Sin seleccionar");
      setSummary("[data-entry-summary-quantity]", quantityText);
      setSummary("[data-entry-summary-balance]", balanceText.textContent);

      const approvalsReady = Array.from(operationShell.querySelectorAll("[data-sharing-approval]"))
        .every(approval => approval.checked);
      const ready = completedCount === kinds.length && approvalsReady;
      const missing = kinds.length - completedCount;
      const state = operationShell.querySelector("[data-entry-summary-state]");
      state.textContent = ready ? "Lista para confirmar"
        : completedCount === kinds.length ? "Confirma el pallet compartido"
          : `Faltan ${missing} ${missing === 1 ? "paso" : "pasos"}`;
      operationShell.querySelector(".entry-summary-card")?.classList.toggle("is-ready", ready);
      operationShell.querySelector("[data-review-button]").disabled = !ready;
    };

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
      } else if ((operation === "exit" || operation === "wipissue") && selected.product && selected.source) {
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
      refreshEntryState();
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
        if (entryWorkstation && kind === "location" && item.description) {
          const description = document.createElement("small");
          description.className = "relationship-description";
          description.textContent = item.description;
          choice.append(name, description);
        } else {
          choice.append(name);
        }
        const detail = document.createElement("small");
        detail.textContent = relationshipMeta(item);
        choice.append(detail);
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

    const canSelectProductFrom = (kind) => !((operation === "transfer" || operation === "wipissue" || isWipExit()) && kind === "destination");

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
      if (operation !== "entry" && !selected[primaryLocationKind] && items.length === 1)
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
      if (lookupKind === "location") {
        const expectsWip = (operation === "wipissue" || isWipExit()) && kind === "destination";
        const isWipLocation = item.tracksInventory === false;
        if ((expectsWip && !isWipLocation) || (!expectsWip && isWipLocation)) {
          const message = expectsWip
            ? "Selecciona un rack WIP."
            : "Este rack WIP no controla saldo y no es válido para este campo.";
          lookup.input.setCustomValidity(message);
          lookup.input.reportValidity();
          setOperationFeedback(message);
          return;
        }
      }
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
        unitLabel.textContent = item.unitCode;
        quantityInput.step = item.allowsDecimals ? "0.0001" : "1";
      }
      lookup.record.classList.remove("d-none");
      lookup.results.replaceChildren();
      if (entryWorkstation) editingEntryStep = undefined;

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
      lookup.panel?.classList.add("d-none");
      lookup.panel?.replaceChildren();
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
      if (entryWorkstation) {
        const next = guidedKinds.find(kind => kind === "quantity"
          ? quantityInput.value.trim() === "" || !quantityInput.checkValidity()
          : kind === "notes" ? !notesInput?.value.trim() : !selected[kind]);
        if (next === "quantity") { quantityInput.focus(); quantityInput.select(); return; }
        if (next === "notes") { notesInput?.focus(); return; }
        if (next) {
          const relationshipPanel = next === primaryLocationKind && productLocations?.length
            ? lookups.product?.panel : lookups[next]?.panel;
          const relationshipButton = relationshipPanel?.querySelector("button.relationship-choice");
          (relationshipButton || lookups[next]?.input)?.focus();
          return;
        }
      }
      const missing = requiredKinds().find(kind => !selected[kind]);
      if (missing) {
        const relationshipPanel = entryWorkstation && missing === "destination"
          ? lookups.product?.panel : lookups[missing]?.panel;
        const relationshipButton = relationshipPanel?.querySelector("button.relationship-choice");
        (relationshipButton || lookups[missing]?.input)?.focus();
        return;
      }
      quantityInput.focus();
      quantityInput.select();
    };

    const setupLookup = (field) => {
      const kind = field.dataset.lookupField;
      const lookupKind = kind === "product" ? "product" : "location";
      const input = field.querySelector("[data-lookup-input]");
      const results = field.querySelector("[data-lookup-results]");
      const record = field.querySelector("[data-selected-record]");
      const hidden = hiddenFor(kind);
      let panel = field.querySelector("[data-relationship-panel]")
        || operationShell.querySelector(`[data-relationship-for="${kind}"]`);
      if (!panel && kind === "product") {
        const primaryLocationField = operationShell.querySelector(`[data-lookup-field="${primaryLocationKind}"]`);
        if (primaryLocationField) {
          panel = document.createElement("div");
          panel.className = "relationship-panel d-none entry-location-suggestions";
          panel.dataset.relationshipFor = "product";
          panel.setAttribute("aria-live", "polite");
          primaryLocationField.before(panel);
        }
      }
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
        const handler = lookupKind === "product" ? "Products"
          : (operation === "wipissue" || isWipExit()) && kind === "destination" ? "WipLocations" : "Locations";
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
        await resolveLookupCode(kind, input.value, true);
      });
    };

    const resolveLookupCode = async (kind, value, reportInvalidity) => {
      const lookup = lookups[kind];
      const code = value.trim();
      if (!code) return { selected: false, message: "" };
      const lookupKind = kind === "product" ? "product" : "location";
      const resolution = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "ResolveCode", code })}`);
      if (!resolution) {
        const message = "No fue posible validar el código. Intenta nuevamente.";
        lookup.input.setCustomValidity(message);
        if (reportInvalidity) lookup.input.reportValidity();
        setOperationFeedback(message);
        return { selected: false, message };
      }

      const product = resolution.product;
      const location = resolution.location;
      if (product && location) {
        const message = "El código coincide con un producto y una ubicación. Escanéalo en el campo correcto.";
        lookup.input.setCustomValidity(message);
        if (reportInvalidity) lookup.input.reportValidity();
        setOperationFeedback(message);
        return { selected: false, message };
      }

      const currentItem = lookupKind === "product" ? product : location;
      if (currentItem) {
        await applySelection(kind, currentItem, true);
        setOperationFeedback("");
        focusNextRequired();
        return { selected: true, message: "" };
      }

      const oppositeItem = lookupKind === "product" ? location : product;
      if (!oppositeItem) {
        const message = "No se encontró un registro operativo con ese código.";
        lookup.input.setCustomValidity(message);
        if (reportInvalidity) lookup.input.reportValidity();
        setOperationFeedback(message);
        return { selected: false, message };
      }

      const targetKind = lookupKind === "product"
        ? operation === "transfer"
          ? ["source", "destination"].find(candidate => !selected[candidate])
          : primaryLocationKind
        : "product";
      if (!targetKind) {
        const message = "Las ubicaciones de la transferencia ya están seleccionadas. Escanea el código en el campo correcto.";
        lookup.input.setCustomValidity(message);
        if (reportInvalidity) lookup.input.reportValidity();
        setOperationFeedback(message);
        return { selected: false, message };
      }

      clearSelection(kind);
      await applySelection(targetKind, oppositeItem, true);
      const message = lookupKind === "product"
        ? `Código de ubicación detectado. Se aplicó en ${fieldName(targetKind)}.`
        : "Código de producto detectado. Se aplicó en Producto.";
      setOperationFeedback(message);
      focusNextRequired();
      return { selected: true, message };
    };

    const scannerElement = operationShell.querySelector("[data-camera-scanner]");
    if (scannerElement) {
      const scannerModal = bootstrap.Modal.getOrCreateInstance(scannerElement);
      const scannerVideo = scannerElement.querySelector("[data-camera-video]");
      const scannerPreview = scannerElement.querySelector("[data-camera-preview]");
      const scannerStatus = scannerElement.querySelector("[data-camera-status]");
      const scannerPhoto = scannerElement.querySelector("[data-camera-photo]");
      const scannerSwitch = scannerElement.querySelector("[data-camera-switch]");
      let activeScannerLookup;
      let scannerControls;
      let resolvingCameraCode = false;
      let focusAfterScannerClose = false;
      const supportedCameraBarcodeFormats = [
        ZXingBrowser.BarcodeFormat.CODE_128,
        ZXingBrowser.BarcodeFormat.EAN_13,
        ZXingBrowser.BarcodeFormat.EAN_8,
        ZXingBrowser.BarcodeFormat.UPC_A,
        ZXingBrowser.BarcodeFormat.UPC_E
      ];
      const supportedNativeBarcodeFormats = ["code_128", "ean_13", "ean_8", "upc_a", "upc_e"];
      const zxingTryHarderHint = 3;

      const setScannerStatus = (message) => {
        scannerStatus.textContent = message;
      };

      const stopCamera = () => {
        scannerControls?.stop();
        scannerControls = undefined;
        const stream = scannerVideo.srcObject;
        if (stream && typeof stream.getTracks === "function")
          stream.getTracks().forEach(track => track.stop());
        scannerVideo.srcObject = null;
      };

      const describeCameraError = (error) => {
        if (error?.name === "NotAllowedError")
          return "No se concedió permiso para usar la cámara. Puedes escribir o usar el escáner físico.";
        if (error?.name === "NotFoundError")
          return "No se encontró una cámara disponible. Puedes escribir o usar el escáner físico.";
        if (error?.name === "NotReadableError")
          return "La cámara está ocupada por otra aplicación. Ciérrala e inténtalo nuevamente.";
        if (error?.name === "OverconstrainedError")
          return "La cámara no admite la configuración solicitada. Prueba con Tomar foto.";
        if (error === false)
          return "El navegador no pudo reproducir la vista previa. Cierra el modal e inténtalo nuevamente.";
        const detail = typeof error?.message === "string" && error.message.trim()
          ? ` ${error.message.trim()}`
          : "";
        return `No fue posible iniciar la cámara (${error?.name || "error desconocido"}).${detail} Prueba con Tomar foto, escribe o usa el escáner físico.`;
      };

      const isCodeNotDetectedError = (error) => {
        if (["NotFoundException", "ChecksumException", "FormatException"].includes(error?.name)) return true;

        // The bundled production build minifies ZXing exception class names (for example, to "e").
        // Its message still identifies the normal condition where the current video frame has no barcode.
        return /No MultiFormat Readers were able to detect the code/i.test(error?.message || "");
      };

      const resolveCameraCode = async (code) => {
        resolvingCameraCode = true;
        const lookup = lookups[activeScannerLookup];
        clearSelection(activeScannerLookup);
        lookup.input.value = code;
        setScannerStatus("Código detectado. Validando…");
        try {
          const resolution = await resolveLookupCode(activeScannerLookup, code, false);
          if (resolution.selected) {
            focusAfterScannerClose = true;
            stopCamera();
            scannerModal.hide();
            return;
          }

          setScannerStatus(resolution.message || "No se encontró un registro operativo con ese código. Intenta nuevamente.");
        } catch {
          setScannerStatus("No fue posible validar el código. Intenta nuevamente.");
        }
        resolvingCameraCode = false;
      };

      const startNativeBarcodeScanner = async () => {
        if (typeof window.BarcodeDetector !== "function") return false;

        try {
          const availableFormats = await window.BarcodeDetector.getSupportedFormats();
          const formats = supportedNativeBarcodeFormats.filter(format => availableFormats.includes(format));
          if (formats.length === 0) return false;

          const detector = new window.BarcodeDetector({ formats });
          let stopped = false;
          scannerControls = { stop: () => { stopped = true; } };

          const scanNextFrame = async () => {
            if (stopped || resolvingCameraCode) return;
            try {
              const [result] = await detector.detect(scannerVideo);
              if (result?.rawValue) {
                await resolveCameraCode(result.rawValue);
                return;
              }
            } catch {
              // A frame can be unavailable while the camera adjusts focus; retry the next one.
            }

            if (!stopped && !resolvingCameraCode)
              window.setTimeout(() => void scanNextFrame(), 100);
          };

          void scanNextFrame();
          return true;
        } catch {
          return false;
        }
      };

      const optimizeCameraForBarcodes = async (stream) => {
        const track = stream.getVideoTracks?.()[0];
        if (!track?.getCapabilities || !track.applyConstraints) return;

        const capabilities = track.getCapabilities();
        if (!capabilities.focusMode?.includes("continuous")) return;

        try {
          await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] });
        } catch {
          // Continuous focus is an optional camera capability; the default focus remains usable.
        }
      };

      const startCameraScanner = async (requestedDeviceId) => {
        if (!window.isSecureContext) {
          scannerPreview.classList.add("d-none");
          setScannerStatus("La cámara requiere HTTPS. Puedes escribir o usar el escáner físico.");
          return;
        }
        if (!navigator.mediaDevices?.getUserMedia
          || (!window.ZXingBrowser && typeof window.BarcodeDetector !== "function")) {
          scannerPreview.classList.add("d-none");
          setScannerStatus("Este navegador no permite usar la cámara. Puedes escribir o usar el escáner físico.");
          return;
        }

        scannerPreview.classList.remove("d-none");
        setScannerStatus("Solicitando la cámara trasera…");
        try {
          // Open the device first. This keeps the permission request tied to the user's button tap
          // and avoids relying on the decoder to create a second, hidden camera request on Android.
          const stream = await openCameraStream(requestedDeviceId);
          await optimizeCameraForBarcodes(stream);
          scannerVideo.srcObject = stream;
          await scannerVideo.play();
          await updateCameraSwitchButton(scannerSwitch, stream);
          setScannerStatus("Centra el código; para etiquetas largas, acércalo y espera a que enfoque.");

          if (await startNativeBarcodeScanner()) return;

          const reader = new ZXingBrowser.BrowserMultiFormatReader();
          reader.possibleFormats = supportedCameraBarcodeFormats;
          // DecodeHintType.TRY_HARDER is not exported by the browser bundle (its stable enum value is 3).
          // It samples more scan lines, which is important for dense, long Code 128 labels.
          reader.hints.set(zxingTryHarderHint, true);
          reader.reader.setHints(reader.hints);
          scannerControls = await reader.decodeFromStream(
            stream,
            scannerVideo,
            async (result, error, controls) => {
              if (!scannerControls) scannerControls = controls;
              if (result && !resolvingCameraCode) {
                await resolveCameraCode(result.getText());
                return;
              }

              if (error && !isCodeNotDetectedError(error)) {
                stopCamera();
                scannerPreview.classList.add("d-none");
                setScannerStatus(describeCameraError(error));
              }
            });
        } catch (error) {
          stopCamera();
          scannerPreview.classList.add("d-none");
          setScannerStatus(describeCameraError(error));
        }
      };

      scannerPhoto.addEventListener("change", async () => {
        const [photo] = scannerPhoto.files;
        if (!photo || resolvingCameraCode) return;
        if (!window.ZXingBrowser) {
          setScannerStatus("No fue posible leer la foto. Puedes escribir o usar el escáner físico.");
          return;
        }

        stopCamera();
          setScannerStatus("Leyendo el código de la foto…");
          const imageUrl = URL.createObjectURL(photo);
          try {
            const reader = new ZXingBrowser.BrowserMultiFormatReader();
          reader.possibleFormats = supportedCameraBarcodeFormats;
          const result = await reader.decodeFromImageUrl(imageUrl);
          await resolveCameraCode(result.getText());
        } catch {
          setScannerStatus("No se detectó un código de barras en la foto. Intenta nuevamente.");
        } finally {
          URL.revokeObjectURL(imageUrl);
          scannerPhoto.value = "";
        }
      });

      scannerSwitch?.addEventListener("click", async () => {
        scannerSwitch.disabled = true;
        setScannerStatus("Cambiando cámara…");
        try {
          const deviceId = await nextCameraDeviceId(scannerVideo.srcObject);
          if (!deviceId) return;
          stopCamera();
          await startCameraScanner(deviceId);
        } catch (error) {
          setScannerStatus(describeCameraError(error));
        } finally {
          scannerSwitch.disabled = false;
        }
      });

      operationShell.querySelectorAll("[data-camera-scan]").forEach(button => {
        button.addEventListener("click", () => {
          activeScannerLookup = button.closest("[data-lookup-field]").dataset.lookupField;
          resolvingCameraCode = false;
          focusAfterScannerClose = false;
          scannerPreview.classList.remove("d-none");
          setScannerStatus("Preparando cámara…");
          scannerModal.show();
          void startCameraScanner();
        });
      });

      scannerElement.addEventListener("hidden.bs.modal", () => {
        const shouldFocusNext = focusAfterScannerClose;
        stopCamera();
        scannerPhoto.value = "";
        activeScannerLookup = undefined;
        resolvingCameraCode = false;
        focusAfterScannerClose = false;
        if (shouldFocusNext) focusNextRequired();
      });
      window.addEventListener("pagehide", stopCamera, { once: true });
    }

    operationShell.querySelectorAll("[data-lookup-field]").forEach(setupLookup);
    exitModePicker?.querySelectorAll("input").forEach(control => {
      control.addEventListener("change", () => {
        const destinationStep = operationShell.querySelector("[data-wip-destination-step]");
        const isWip = isWipExit();
        destinationStep?.classList.toggle("d-none", !isWip);
        if (!isWip && lookups.destination) clearSelection("destination");
        refreshEntryState();
        focusNextRequired();
      });
    });
    for (const kind of Object.keys(lookups)) {
      if (!selected[kind]) continue;
      if (kind === "product") void loadProductLocations();
      else void loadLocationProducts(kind);
    }
    quantityInput.addEventListener("focus", () => quantityInput.select());
    quantityInput.addEventListener("input", refreshPreview);
    notesInput?.addEventListener("input", refreshEntryState);
    refreshPreview();

    if (entryWorkstation) {
      operationShell.querySelectorAll("[data-entry-step]").forEach(step => {
        const kind = step.dataset.entryStep;
        step.addEventListener("focusin", event => {
          if (!event.target.closest("[data-entry-step-body], .entry-step-body")) return;
          editingEntryStep = kind;
          refreshEntryState();
        });
        step.addEventListener("focusout", () => {
          window.setTimeout(() => {
            if (step.contains(document.activeElement)) return;
            if (editingEntryStep === kind) editingEntryStep = undefined;
            refreshEntryState();
          }, 0);
        });
      });
      operationShell.querySelectorAll("[data-edit-step]").forEach(button => {
        button.addEventListener("click", () => {
          const kind = button.dataset.editStep;
          editingEntryStep = kind;
          refreshEntryState();
          const target = kind === "quantity" ? quantityInput : kind === "notes" ? notesInput : lookups[kind]?.input;
          target?.focus();
          target?.select();
        });
      });
      operationShell.querySelectorAll("[data-sharing-approval]")
        .forEach(approval => approval.addEventListener("change", refreshEntryState));
      refreshEntryState();
    }

    const reviewButton = operationShell.querySelector("[data-review-button]");
    const modalElement = document.getElementById("confirm-operation");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    reviewButton.addEventListener("click", () => {
      const pinInput = operationShell.querySelector("[data-pin-input]");
      pinInput.required = false;
      if (operation === "exit" && !selectedExitMode()) {
        exitModePicker?.querySelector("input")?.setCustomValidity("Selecciona el tipo de salida.");
        exitModePicker?.querySelector("input")?.reportValidity();
        return;
      }
      for (const kind of requiredKinds()) {
        if (!hiddenFor(kind).value) {
          const input = operationShell.querySelector(`[data-lookup-field="${kind}"] [data-lookup-input]`);
          input.setCustomValidity("Selecciona un registro de la lista o escanea un código válido.");
          input.reportValidity();
          return;
        }
      }
      if (!form.reportValidity()) return;
      const productCode = operationShell.querySelector('[data-lookup-field="product"] [data-lookup-input]').value;
      const locations = requiredKinds().filter(kind => kind !== "product")
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

  const inventoryWorkspace = document.querySelector("[data-inventory-workspace]");
  if (inventoryWorkspace) {
    const lookupUrl = inventoryWorkspace.dataset.lookupUrl;
    const form = inventoryWorkspace.querySelector("[data-inventory-search-form]");
    const input = inventoryWorkspace.querySelector("[data-inventory-search-input]");
    const results = inventoryWorkspace.querySelector("[data-inventory-search-results]");
    const feedback = inventoryWorkspace.querySelector("[data-inventory-feedback]");
    let searchSequence = 0;

    const setFeedback = (message) => {
      feedback.textContent = message;
      feedback.classList.toggle("d-none", !message);
    };
    const navigate = (kind, id) => {
      const params = new URLSearchParams({ [kind === "product" ? "productId" : "locationId"]: id });
      window.location.assign(`${window.location.pathname}?${params}`);
    };
    const addGroup = (titleText, items, kind) => {
      if (!items?.length) return;
      const group = document.createElement("section");
      group.className = "inventory-suggestion-group";
      const title = document.createElement("strong");
      title.textContent = titleText;
      group.append(title);
      const list = document.createElement("div");
      list.className = "list-group";
      for (const item of items) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "list-group-item list-group-item-action";
        const name = document.createElement("strong");
        name.textContent = kind === "product" ? item.sku : item.code;
        const detail = document.createElement("small");
        detail.className = "d-block text-muted";
        const status = kind === "product" ? (!item.isActive ? " · Inactivo" : "")
          : (!item.isActive ? " · Inactiva" : item.isBlocked ? " · Bloqueada" : "");
        detail.textContent = `${kind === "product" ? describeProduct(item) : describeLocation(item)}${status}`;
        button.append(name, detail);
        button.addEventListener("click", () => navigate(kind, item.id));
        list.append(button);
      }
      group.append(list);
      results.append(group);
    };
    const renderSearchResults = (data) => {
      results.replaceChildren();
      addGroup("Productos", data?.products, "product");
      addGroup("Ubicaciones", data?.locations, "location");
    };
    const showAmbiguousChoices = (resolution) => {
      results.replaceChildren();
      const message = "El código coincide con un producto y una ubicación. Elige qué deseas consultar.";
      setFeedback(message);
      addGroup("Producto", [resolution.product], "product");
      addGroup("Ubicación", [resolution.location], "location");
      results.querySelector("button")?.focus();
    };
    const resolve = async (code) => {
      const value = code.trim();
      if (!value) return false;
      const resolution = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "ResolveInventoryCode", code: value })}`);
      if (!resolution) {
        const message = "No fue posible validar el código. Intenta nuevamente.";
        input.setCustomValidity(message);
        setFeedback(message);
        return false;
      }
      input.setCustomValidity("");
      if (resolution.product && resolution.location) {
        showAmbiguousChoices(resolution);
        return false;
      }
      if (resolution.product) { navigate("product", resolution.product.id); return true; }
      if (resolution.location) { navigate("location", resolution.location.id); return true; }
      const message = "No se encontró un producto ni una ubicación con ese código.";
      input.setCustomValidity(message);
      setFeedback(message);
      return false;
    };
    const search = debounce(async (sequence) => {
      const query = input.value.trim();
      if (!query) { results.replaceChildren(); return; }
      const data = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "InventorySearch", q: query })}`);
      if (sequence !== searchSequence) return;
      renderSearchResults(data);
    });
    input.addEventListener("input", () => {
      input.setCustomValidity("");
      setFeedback("");
      searchSequence++;
      search(searchSequence);
    });
    input.addEventListener("keydown", async (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      searchSequence++;
      results.replaceChildren();
      await resolve(input.value);
    });
    form.addEventListener("submit", () => results.replaceChildren());

    const modalElement = inventoryWorkspace.querySelector("[data-inventory-camera-modal]");
    const cameraButton = inventoryWorkspace.querySelector("[data-inventory-camera]");
    if (modalElement && cameraButton) {
      const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
      const video = modalElement.querySelector("[data-inventory-camera-video]");
      const preview = modalElement.querySelector("[data-inventory-camera-preview]");
      const status = modalElement.querySelector("[data-inventory-camera-status]");
      const photo = modalElement.querySelector("[data-inventory-camera-photo]");
      const cameraSwitch = modalElement.querySelector("[data-camera-switch]");
      let controls;
      let resolving = false;
      const stopCamera = () => {
        controls?.stop(); controls = undefined;
        const stream = video.srcObject;
        if (stream?.getTracks) stream.getTracks().forEach(track => track.stop());
        video.srcObject = null;
      };
      const scanCode = async (code) => {
        if (resolving) return;
        resolving = true;
        stopCamera();
        modal.hide();
        input.value = code;
        await resolve(code);
        resolving = false;
      };
      const startCamera = async (requestedDeviceId) => {
        if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia || !window.ZXingBrowser) {
          preview.classList.add("d-none");
          status.textContent = "La cámara requiere HTTPS y un navegador compatible. Puedes escribir o usar el lector físico.";
          return;
        }
        preview.classList.remove("d-none");
        status.textContent = "Solicitando cámara trasera…";
        try {
          const stream = await openCameraStream(requestedDeviceId);
          video.srcObject = stream;
          await video.play();
          await updateCameraSwitchButton(cameraSwitch, stream);
          status.textContent = "Centra el código y espera a que enfoque.";
          const reader = new ZXingBrowser.BrowserMultiFormatReader();
          controls = await reader.decodeFromStream(stream, video, async (result) => {
            if (result) await scanCode(result.getText());
          });
        } catch {
          stopCamera(); preview.classList.add("d-none");
          status.textContent = "No fue posible iniciar la cámara. Puedes tomar una foto, escribir o usar el lector físico.";
        }
      };
      cameraButton.addEventListener("click", () => {
        resolving = false; status.textContent = "Preparando cámara…"; modal.show(); void startCamera();
      });
      photo.addEventListener("change", async () => {
        const [file] = photo.files;
        if (!file || !window.ZXingBrowser || resolving) return;
        stopCamera(); status.textContent = "Leyendo la foto…";
        const url = URL.createObjectURL(file);
        try { const result = await new ZXingBrowser.BrowserMultiFormatReader().decodeFromImageUrl(url); await scanCode(result.getText()); }
        catch { status.textContent = "No se detectó un código en la foto. Intenta nuevamente."; }
        finally { URL.revokeObjectURL(url); photo.value = ""; }
      });
      cameraSwitch?.addEventListener("click", async () => {
        cameraSwitch.disabled = true;
        status.textContent = "Cambiando cámara…";
        try {
          const deviceId = await nextCameraDeviceId(video.srcObject);
          if (!deviceId) return;
          stopCamera();
          await startCamera(deviceId);
        } catch {
          status.textContent = "No fue posible cambiar de cámara. Intenta nuevamente.";
        } finally {
          cameraSwitch.disabled = false;
        }
      });
      modalElement.addEventListener("shown.bs.modal", () => document.body.classList.add("camera-active"));
      modalElement.addEventListener("hidden.bs.modal", () => { stopCamera(); photo.value = ""; document.body.classList.remove("camera-active"); if (!resolving) input.focus(); });
      window.addEventListener("pagehide", stopCamera, { once: true });
    }
  }
})();
