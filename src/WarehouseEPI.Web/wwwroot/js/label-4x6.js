(() => {
  "use strict";

  document.querySelectorAll("[data-label-print]").forEach(button =>
    button.addEventListener("click", () => window.print()));

  const barcodeDialog = document.querySelector("[data-label-barcode-dialog]");
  const barcodeCanvas = barcodeDialog?.querySelector("[data-label-barcode-canvas]");
  const barcodeValue = barcodeDialog?.querySelector("[data-label-barcode-value]");
  let barcodeTrigger;

  const closeBarcodeDialog = () => {
    if (barcodeDialog?.open) barcodeDialog.close();
  };

  document.querySelectorAll("[data-label-barcode-zoom]").forEach(button =>
    button.addEventListener("click", () => {
      const svg = button.parentElement?.querySelector("svg");
      if (!barcodeDialog || !barcodeCanvas || !barcodeValue || !svg) return;
      const modules = Number(button.dataset.barcodeModules) || 120;
      barcodeCanvas.style.setProperty("--barcode-screen-width", `${Math.max(360, modules * 3)}px`);
      barcodeCanvas.replaceChildren(svg.cloneNode(true));
      barcodeValue.textContent = button.dataset.barcodeValue || "";
      barcodeTrigger = button;
      barcodeDialog.showModal();
      barcodeDialog.querySelector("[data-label-barcode-close]")?.focus();
    }));

  barcodeDialog?.querySelectorAll("[data-label-barcode-close]").forEach(button =>
    button.addEventListener("click", closeBarcodeDialog));
  barcodeDialog?.addEventListener("close", () => {
    barcodeCanvas?.replaceChildren();
    barcodeTrigger?.focus();
    barcodeTrigger = undefined;
  });

  const workspace = document.querySelector("[data-label-workspace]");
  if (!workspace) return;

  // Use local calendar components: UTC conversion can select the previous/next day.
  if (workspace.dataset.labelDeviceDate === 'true') {
    const today = new Date();
    const localDate = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
    workspace.querySelectorAll('input[type="date"]').forEach(input => {
      if (['Input.Values[input.manufacturingDate]', 'Input.Values[receivingMfgDate]', 'Input.Values[mfd]'].includes(input.name)) {
        input.value = localDate;
      }
    });
  }

  const seriesField = workspace.querySelector('[data-label-series-field]');
  const seriesEnd = workspace.querySelector('[data-label-series-end]');
  const endContainer = workspace.querySelector('[data-label-series-end-container]');
  const endHome = document.createComment('series-end-position');
  endContainer?.before(endHome);
  let seriesGroup;
  let startColumn;
  const updateSeries = () => {
    if (seriesGroup) {
      seriesGroup.before(startColumn);
      endHome.after(endContainer);
      seriesGroup.remove();
      seriesGroup = undefined;
    }
    const enabled = Boolean(seriesField?.value);
    const container = workspace.querySelector('[data-label-series-end-container]');
    if (container) container.hidden = !enabled;
    if (seriesEnd) { seriesEnd.disabled = !enabled; seriesEnd.required = enabled; }
    const name = workspace.querySelector('[data-label-series-name]');
    if (name) name.textContent = enabled ? `· ${seriesField.selectedOptions[0].textContent}` : '';
    const normal = workspace.querySelector('[data-label-copies-normal]');
    const series = workspace.querySelector('[data-label-copies-series]');
    if (normal) normal.hidden = enabled;
    if (series) series.hidden = !enabled;
    if (enabled && endContainer) {
      const start = Array.from(workspace.querySelectorAll('input[name]'))
        .find(input => input.name === `Input.Values[${seriesField.value}]`);
      startColumn = start?.closest('.col-sm-6');
      if (startColumn) {
        seriesGroup = document.createElement('div');
        seriesGroup.className = 'col-12';
        const row = document.createElement('div');
        row.className = 'row g-3';
        startColumn.before(seriesGroup);
        seriesGroup.append(row);
        row.append(startColumn, endContainer);
      }
    }
  };
  seriesField?.addEventListener('change', updateSeries);
  updateSeries();

  const search = workspace.querySelector("[data-label-product-search]");
  const productId = workspace.querySelector("[data-label-product-id]");
  const results = workspace.querySelector("[data-label-product-results]");
  const selected = workspace.querySelector("[data-label-selected-product]");
  const selectedSku = workspace.querySelector("[data-label-selected-sku]");
  const selectedDescription = workspace.querySelector("[data-label-selected-description]");
  const selectedUnit = workspace.querySelector("[data-label-selected-unit]");
  const unitLabel = workspace.querySelector("[data-label-unit]");
  const quantity = workspace.querySelector("[data-label-quantity]");
  const lookupUrl = workspace.dataset.lookupUrl;
  let request;
  let timer;

  workspace.querySelector("[data-template-picker]")?.addEventListener("change", event => event.currentTarget.form.submit());
  const clearResults = () => results.replaceChildren();

  const choose = product => {
    productId.value = product.id;
    search.value = product.sku;
    selectedSku.textContent = product.sku;
    selectedDescription.textContent = product.description || "Sin descripción";
    selectedUnit.textContent = product.unitCode;
    if (unitLabel) unitLabel.textContent = product.unitCode;
    selected.dataset.allowsDecimals = product.allowsDecimals ? "true" : "false";
    selected.classList.remove("d-none");
    if (quantity) quantity.step = product.allowsDecimals ? "any" : "1";
    clearResults();
    (quantity || workspace.querySelector("[name='Input.Copies']"))?.focus();
  };

  const clearSelection = () => {
    productId.value = "";
    selected.classList.add("d-none");
    if (unitLabel) unitLabel.textContent = "Unidad";
  };

  const resultButton = product => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "list-group-item list-group-item-action";
    const title = document.createElement("strong");
    title.textContent = product.sku;
    const detail = document.createElement("span");
    detail.textContent = product.description || "Sin descripción";
    const unit = document.createElement("small");
    unit.textContent = product.unitCode;
    button.append(title, detail, unit);
    button.addEventListener("click", () => choose(product));
    return button;
  };

  const fetchJson = async params => {
    request?.abort();
    request = new AbortController();
    const response = await fetch(`${lookupUrl}?${params}`, {
      headers: { Accept: "application/json" },
      signal: request.signal
    });
    if (!response.ok) return null;
    return response.json();
  };

  const resolveExact = async () => {
    const code = search.value.trim();
    if (!code) return;
    try {
      const product = await fetchJson(new URLSearchParams({ handler: "ResolveProduct", code }));
      if (product) choose(product);
      else {
        clearResults();
        const message = document.createElement("div");
        message.className = "list-group-item text-body-secondary";
        message.textContent = "No se encontró un producto activo con ese código.";
        results.append(message);
      }
    } catch (error) {
      if (error.name !== "AbortError") clearResults();
    }
  };

  const searchProducts = async () => {
    const term = search.value.trim();
    if (term.length < 2) {
      clearResults();
      return;
    }
    try {
      const products = await fetchJson(new URLSearchParams({ handler: "Products", q: term }));
      clearResults();
      products?.forEach(product => results.append(resultButton(product)));
      if (products?.length === 0) {
        const message = document.createElement("div");
        message.className = "list-group-item text-body-secondary";
        message.textContent = "Sin coincidencias activas.";
        results.append(message);
      }
    } catch (error) {
      if (error.name !== "AbortError") clearResults();
    }
  };

  search.addEventListener("input", () => {
    if (search.value.trim() !== selectedSku.textContent?.trim()) clearSelection();
    clearTimeout(timer);
    timer = setTimeout(searchProducts, 220);
  });
  search.addEventListener("keydown", event => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    clearTimeout(timer);
    resolveExact();
  });

  if (quantity && !selected.classList.contains("d-none"))
    quantity.step = selected.dataset.allowsDecimals === "true" ? "any" : "1";
})();
