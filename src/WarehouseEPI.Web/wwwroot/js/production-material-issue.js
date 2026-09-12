(() => {
  "use strict";
  const root = document.querySelector("[data-guided-workstation]");
  const section = root?.querySelector("[data-production-material-link]");
  if (!root || !section) return;
  const destination = root.querySelector("[data-selected-id='destination']");
  const orderId = root.querySelector("[data-production-order-id]");
  const stageId = root.querySelector("[data-production-stage-id]");
  const orderVersion = root.querySelector("[data-production-order-version]");
  const search = root.querySelector("[data-production-target-search]");
  const results = root.querySelector("[data-production-target-results]");
  const feedback = root.querySelector("[data-production-target-feedback]");
  const orderMode = root.querySelector("#wip-link-order");
  const generalMode = root.querySelector("#wip-link-general");
  const targetField = root.querySelector("[data-production-target-field]");
  const wipMode = root.querySelector("#exit-mode-wip");
  let timer = 0;
  let controller;
  let preferredOrderId = orderId.value;
  let preferredStageId = stageId.value;

  const clearTarget = () => {
    orderId.value = "";
    stageId.value = "";
    orderVersion.value = "";
    search.value = "";
    results.replaceChildren();
    search.setAttribute("aria-expanded", "false");
  };
  const updateMode = () => {
    section.classList.toggle("d-none", !wipMode.checked);
    targetField.classList.toggle("d-none", generalMode.checked);
    if (generalMode.checked) clearTarget();
  };
  const render = items => {
    results.replaceChildren();
    items.forEach(item => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "list-group-item list-group-item-action";
      button.setAttribute("role", "option");
      const title = document.createElement("strong");
      title.className = "d-block";
      title.textContent = item.label;
      const detail = document.createElement("small");
      detail.className = "text-body-secondary";
      detail.textContent = item.description;
      button.append(title, detail);
      button.addEventListener("click", () => {
        orderId.value = item.workOrderId;
        stageId.value = item.workOrderStageId;
        orderVersion.value = item.workOrderVersion;
        search.value = item.label;
        results.replaceChildren();
        search.setAttribute("aria-expanded", "false");
        feedback.textContent = `${item.label} seleccionado.`;
        preferredOrderId = item.workOrderId;
        preferredStageId = item.workOrderStageId;
      });
      results.append(button);
    });
    const preferredIndex = items.findIndex(item =>
      item.workOrderId === preferredOrderId && item.workOrderStageId === preferredStageId);
    if (preferredIndex >= 0) results.children[preferredIndex].click();
    else if (items.length === 1) results.firstElementChild.click();
    else {
      search.setAttribute("aria-expanded", items.length ? "true" : "false");
      feedback.textContent = items.length ? `${items.length} opciones disponibles.` : "No hay órdenes compatibles con este destino.";
    }
  };
  const load = async () => {
    controller?.abort();
    if (!destination.value || generalMode.checked) {
      results.replaceChildren();
      feedback.textContent = "Selecciona primero una ubicación WIP.";
      return;
    }
    controller = new AbortController();
    const url = new URL(root.dataset.productionTargetUrl, window.location.href);
    url.searchParams.set("destinationId", destination.value);
    if (search.value.trim()) url.searchParams.set("q", search.value.trim());
    feedback.textContent = "Buscando órdenes y procesos…";
    try {
      const response = await fetch(url, { headers: { Accept: "application/json" }, signal: controller.signal });
      if (!response.ok) throw new Error();
      render(await response.json());
    } catch (error) {
      if (error.name !== "AbortError") feedback.textContent = "No fue posible consultar las órdenes.";
    }
  };
  search.addEventListener("input", () => {
    orderId.value = "";
    stageId.value = "";
    orderVersion.value = "";
    clearTimeout(timer);
    timer = setTimeout(load, 250);
  });
  search.addEventListener("keydown", event => {
    const choices = [...results.children];
    if (event.key === "Enter" && choices.length) { event.preventDefault(); choices[0].click(); }
    if (event.key === "Escape") { results.replaceChildren(); search.setAttribute("aria-expanded", "false"); }
  });
  destination.addEventListener("change", () => {
    search.value = "";
    results.replaceChildren();
    void load();
  });
  orderMode.addEventListener("change", () => { updateMode(); void load(); });
  generalMode.addEventListener("change", updateMode);
  wipMode.addEventListener("change", updateMode);
  root.querySelector("#exit-mode-general")?.addEventListener("change", updateMode);
  updateMode();
  if (wipMode.checked && orderMode.checked && destination.value) void load();
})();
