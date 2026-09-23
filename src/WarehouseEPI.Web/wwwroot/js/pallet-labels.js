(() => {
  "use strict";

  const shell = document.querySelector("[data-pallet-finder]");
  if (!shell) return;

  const form = shell.querySelector("[data-pallet-finder-form]");
  const related = shell.querySelector("[data-pallet-related]");
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
      if (kind === "product") field.selectedId.value = "";
      else selectedLocationId = "";
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
      if (!first) return;
      event.preventDefault();
      first.click();
    });
  }

  document.addEventListener("click", (event) => {
    if (!shell.contains(event.target)) clearResults();
  });
})();
