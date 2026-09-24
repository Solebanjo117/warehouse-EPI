(() => {
  "use strict";

  const shell = document.querySelector("[data-pallet-finder]");
  if (!shell) return;

  const form = shell.querySelector("[data-pallet-finder-form]");
  const related = shell.querySelector("[data-pallet-related]");
  const scanStatus = shell.querySelector("[data-pallet-scan-status]");
  const fields = Object.fromEntries(Array.from(shell.querySelectorAll("[data-pallet-lookup]"))
    .map((field) => [field.dataset.palletLookup, {
      field,
      input: field.querySelector("[data-pallet-lookup-input]"),
      results: field.querySelector("[data-pallet-lookup-results]"),
      selectedId: field.querySelector("[data-pallet-selected-id]")
    }]));
  if (!form || !fields.product || !fields.location) return;

  let selectedLocationId = shell.dataset.selectedLocationId || "";
  const requests = { product: null, location: null, related: null };
  const timers = { product: 0, location: 0 };
  const scanIntentKey = "warehouse-pallet-scan-preview";
  let scanSequence = 0;
  const setScanStatus = (message) => {
    if (!scanStatus) return;
    scanStatus.textContent = message;
    scanStatus.hidden = !message;
  };
  const optionUrl = (kind, query = "") => {
    const url = new URL(kind === "product" ? shell.dataset.productOptionsUrl : shell.dataset.locationOptionsUrl,
      window.location.href);
    if (query) url.searchParams.set("q", query);
    if (kind === "product" && selectedLocationId) url.searchParams.set("locationId", selectedLocationId);
    if (kind === "location" && fields.product.selectedId?.value)
      url.searchParams.set("productId", fields.product.selectedId.value);
    return url;
  };

  const fetchOptions = async (kind, query, requestKey = kind) => {
    requests[requestKey]?.abort();
    const controller = new AbortController();
    requests[requestKey] = controller;
    try {
      const response = await fetch(optionUrl(kind, query), {
        headers: { Accept: "application/json" },
        signal: controller.signal
      });
      return response.ok ? await response.json() : [];
    } catch (error) {
      if (error.name !== "AbortError") return [];
      return null;
    }
  };

  const describe = (kind, item) => {
    const quantity = Number(item.quantity || 0).toLocaleString(undefined, { maximumFractionDigits: 4 });
    return kind === "product"
      ? [item.description, `${quantity} ${item.unitCode}`].filter(Boolean).join(" · ")
      : [item.description, `${shell.dataset.stockLabel || "Stock"} ${quantity}`].filter(Boolean).join(" · ");
  };

  const clearResults = () => {
    for (const field of Object.values(fields)) field.results.replaceChildren();
  };

  const resetForScan = () => {
    for (const kind of ["product", "location"]) {
      window.clearTimeout(timers[kind]);
      requests[kind]?.abort();
      fields[kind].input.value = "";
    }
    requests.related?.abort();
    fields.product.selectedId.value = "";
    selectedLocationId = "";
    clearResults();
    related.replaceChildren();
    const previousProducts = document.querySelector("[data-pallet-products]");
    if (previousProducts) previousProducts.hidden = true;
    setScanStatus("");
  };

  const requestScanJson = async (url) => {
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Scan lookup failed");
    return response.json();
  };

  const navigateScan = (kind, item, other = null) => {
    const url = new URL(form.action || window.location.href, window.location.href);
    url.search = "";
    url.hash = "";
    const product = kind === "product" ? item : other;
    const location = kind === "location" ? item : other;
    if (product) url.searchParams.set("productId", product.id);
    if (location) url.searchParams.set("location", location.code);
    if (other) {
      const token = window.crypto?.randomUUID?.() || `${Date.now()}-${Math.random()}`;
      try {
        window.sessionStorage.setItem(scanIntentKey, JSON.stringify({ token,
          productId: product.id, locationId: location.id }));
        url.searchParams.set("scanToken", token);
      } catch { /* The selected pair remains available for manual printing. */ }
    }
    window.location.assign(url.href);
  };

  const processScan = async (resolution) => {
    const sequence = ++scanSequence;
    resetForScan();
    if (resolution.product && resolution.location) {
      setScanStatus(shell.dataset.scanAmbiguous);
      return;
    }
    const kind = resolution.product ? "product" : "location";
    const item = resolution.product || resolution.location;
    if (!item) {
      setScanStatus(shell.dataset.scanUnknown);
      return;
    }
    fields[kind].input.value = kind === "product" ? item.sku : item.code;
    if (kind === "product") fields.product.selectedId.value = item.id;
    else selectedLocationId = item.id;
    const otherKind = kind === "product" ? "location" : "product";
    try {
      const options = await requestScanJson(optionUrl(otherKind));
      if (sequence !== scanSequence) return;
      if (!options.length) {
        setScanStatus(kind === "product" ? shell.dataset.scanNoLocations : shell.dataset.scanNoProducts);
        return;
      }
      navigateScan(kind, item, options.length === 1 ? options[0] : null);
    } catch {
      if (sequence === scanSequence) setScanStatus(shell.dataset.scanError);
    }
  };

  const resolveScan = async (code, fallback = null) => {
    const sequence = ++scanSequence;
    try {
      const url = new URL(shell.dataset.resolveCodeUrl, window.location.href);
      url.searchParams.set("code", code);
      const resolution = await requestScanJson(url);
      if (sequence !== scanSequence) return;
      if (!resolution.product && !resolution.location && fallback) {
        fallback.click();
        return;
      }
      await processScan(resolution);
    } catch {
      if (sequence === scanSequence) setScanStatus(shell.dataset.scanError);
    }
  };

  const consumeScanPreview = () => {
    const url = new URL(window.location.href);
    const token = url.searchParams.get("scanToken");
    if (!token) return;
    url.searchParams.delete("scanToken");
    window.history.replaceState(null, "", url.href);
    let intent;
    try {
      intent = JSON.parse(window.sessionStorage.getItem(scanIntentKey) || "null");
      window.sessionStorage.removeItem(scanIntentKey);
    } catch { return; }
    if (!intent || intent.token !== token || intent.locationId !== selectedLocationId ||
        intent.productId !== fields.product.selectedId.value) return;
    const row = document.querySelector("[data-pallet-product-id]");
    if (row?.dataset.palletProductId !== intent.productId) return;
    const action = row.querySelector(".pallet-print-action");
    if (action?.tagName === "FORM") action.requestSubmit();
    else if (action?.tagName === "A") action.click();
  };

  consumeScanPreview();
  window.WarehouseEpiHidCapture?.listen({
    root: document.body,
    allowSpaces: true,
    onScan: (code) => resolveScan(code.trim())
  });

  const submitPair = () => {
    clearResults();
    form.requestSubmit();
  };

  const select = async (kind, item, submit = false) => {
    const field = fields[kind];
    field.input.value = kind === "product" ? item.sku : item.code;
    if (kind === "product") field.selectedId.value = item.id;
    else selectedLocationId = item.id;
    field.results.replaceChildren();
    if (submit || (fields.product.selectedId.value && selectedLocationId)) {
      submitPair();
      return;
    }
    const other = kind === "product" ? "location" : "product";
    const items = await fetchOptions(other, "", "related");
    if (!items) return;
    renderRelated(other, items);
    fields[other].input.focus();
  };

  const createChoice = (kind, item, onSelect) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "list-group-item list-group-item-action";
    const title = document.createElement("strong");
    title.textContent = kind === "product" ? item.sku : item.code;
    const detail = document.createElement("small");
    detail.className = "d-block text-body-secondary";
    detail.textContent = describe(kind, item);
    button.append(title, detail);
    button.addEventListener("click", () => onSelect(item));
    return button;
  };

  const renderResults = (kind, items) => {
    const container = fields[kind].results;
    container.replaceChildren(...(items || []).map((item) => createChoice(kind, item,
      (choice) => void select(kind, choice))));
  };

  const renderRelated = (kind, items) => {
    related.replaceChildren();
    const title = document.createElement("strong");
    title.textContent = kind === "product"
      ? shell.dataset.productStockTitle
      : shell.dataset.locationStockTitle;
    const choices = document.createElement("div");
    choices.className = "relationship-choices mt-2";
    for (const item of items || []) {
      const choice = createChoice(kind, item, (selected) => void select(kind, selected, true));
      choice.className = "relationship-choice";
      choices.append(choice);
    }
    related.append(title, choices);
  };

  for (const kind of ["product", "location"]) {
    const field = fields[kind];
    field.input.addEventListener("input", () => {
      scanSequence++;
      setScanStatus("");
      if (kind === "product") field.selectedId.value = "";
      else {
        selectedLocationId = "";
        fields.product.input.value = "";
        fields.product.selectedId.value = "";
        fields.product.results.replaceChildren();
        window.clearTimeout(timers.product);
        requests.product?.abort();
        requests.related?.abort();
        related.replaceChildren();
      }
      window.clearTimeout(timers[kind]);
      const query = field.input.value.trim();
      if (!query) {
        field.results.replaceChildren();
        return;
      }
      timers[kind] = window.setTimeout(async () => {
        const items = await fetchOptions(kind, query);
        if (items) renderResults(kind, items);
      }, 180);
    });
    field.input.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        field.results.replaceChildren();
        return;
      }
      if (event.key !== "Enter") return;
      const first = field.results.querySelector("button");
      const code = field.input.value.trim();
      if (!code && !first) return;
      event.preventDefault();
      if (code) void resolveScan(code, first);
      else first.click();
    });
  }

  document.addEventListener("click", (event) => {
    if (!shell.contains(event.target)) clearResults();
  });
})();
