(() => {
  "use strict";

  const root = document.querySelector("[data-product-recipe-catalog]");
  if (!root) return;

  const endpoint = root.dataset.recipeSummaryUrl;
  const productDetailsUrl = root.dataset.productDetailsUrl;
  const cache = new Map();
  const number = new Intl.NumberFormat("es-MX", { maximumFractionDigits: 4 });
  let openButton = null;

  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  const panelFor = button => document.getElementById(button.getAttribute("aria-controls"));
  const triggerFor = button => button.closest("[data-product-recipe-trigger]");

  const setExpanded = (button, expanded) => {
    button.setAttribute("aria-expanded", expanded ? "true" : "false");
    triggerFor(button)?.classList.toggle("is-recipe-expanded", expanded);
  };

  const close = (button, returnFocus = false) => {
    const panel = panelFor(button);
    if (panel) panel.hidden = true;
    setExpanded(button, false);
    if (openButton === button) openButton = null;
    if (returnFocus) button.focus();
  };

  const statusClass = status => status === "Configurado" ? "is-ok" :
    status === "Requiere destino" || status === "Requiere etapa" ? "is-warning" : "is-danger";

  const renderSummary = (panel, summary) => {
    panel.replaceChildren();
    panel.removeAttribute("aria-busy");
    if (!summary.hasRecipe) {
      panel.append(element("p", "mb-0 text-body-secondary", "Sin receta activa."));
      return;
    }

    const header = element("header", "product-recipe-header");
    const titleGroup = element("div");
    titleGroup.append(element("h3", "h6 mb-1", `Receta v${summary.version}`));
    const route = summary.route ? `Ruta: ${summary.route}` : "Sin ruta activa";
    titleGroup.append(element("p", "mb-0 text-body-secondary", `${route} · Produce ${number.format(summary.baseQuantity)} ${summary.productUnit}`));
    const status = element("div", "d-flex flex-wrap gap-2");
    status.append(element("span", `pc-chip ${summary.isComplete ? "is-ok" : "is-warning"}`, summary.isComplete ? "Lista lista para producción" : "Lista incompleta"));
    status.append(element("span", "pc-chip is-static", `${summary.lines.length} ${summary.lines.length === 1 ? "material" : "materiales"}`));
    header.append(titleGroup, status);
    panel.append(header);

    const lines = element("div", "product-recipe-lines");
    for (const line of summary.lines) {
      const article = element("article", "product-recipe-line");
      const material = element("div", "product-recipe-material");
      const materialLink = element("a", "product-catalog-sku", line.materialSku);
      materialLink.href = productDetailsUrl.replace("00000000-0000-0000-0000-000000000000", encodeURIComponent(line.materialProductId));
      materialLink.setAttribute("aria-label", `Ver ficha de ${line.materialSku}`);
      material.append(materialLink);
      material.append(element("span", "text-body-secondary", line.materialDescription || "Sin descripción"));
      const facts = element("dl", "product-recipe-facts mb-0");
      const addFact = (label, value) => {
        const group = element("div");
        group.append(element("dt", "", label), element("dd", "", value));
        facts.append(group);
      };
      addFact("Cantidad", `${number.format(line.quantity)} ${line.unit}`);
      addFact("Proceso", `${line.sequence === 2147483647 ? "" : `${line.sequence}. `}${line.process}`);
      addFact("Destino WIP", line.target);
      addFact("Origen", line.resolutionSource);
      const state = element("div", "product-recipe-state");
      state.append(element("span", `pc-chip ${statusClass(line.status)}`, line.status));
      if (line.warning) state.append(element("small", "text-body-secondary", line.warning));
      article.append(material, facts, state);
      lines.append(article);
    }
    panel.append(lines);
  };

  const renderError = (button, panel) => {
    panel.replaceChildren();
    panel.removeAttribute("aria-busy");
    const alert = element("div", "alert alert-warning mb-0");
    alert.setAttribute("role", "alert");
    alert.append(element("p", "mb-2", "No fue posible cargar la receta desde la red local."));
    const retry = element("button", "btn btn-outline-warning", "Reintentar");
    retry.type = "button";
    retry.addEventListener("click", () => void load(button, panel, true));
    alert.append(retry);
    panel.append(alert);
  };

  const load = async (button, panel, refresh = false) => {
    // Preserve the desktop row and its spanning cell; mobile uses the panel itself.
    panel = panel.querySelector("[data-product-recipe-content]") || panel;
    const productId = button.dataset.productId;
    if (!refresh && cache.has(productId)) {
      renderSummary(panel, cache.get(productId));
      return;
    }
    panel.replaceChildren(element("p", "mb-0 text-body-secondary", "Cargando receta…"));
    panel.setAttribute("aria-busy", "true");
    try {
      const url = new URL(endpoint, window.location.href);
      url.searchParams.set("productId", productId);
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const summary = await response.json();
      cache.set(productId, summary);
      renderSummary(panel, summary);
    } catch {
      renderError(button, panel);
    }
  };

  const toggle = button => {
    const panel = panelFor(button);
    if (!panel) return;
    if (button.getAttribute("aria-expanded") === "true") {
      close(button);
      return;
    }
    if (openButton) close(openButton);
    openButton = button;
    panel.hidden = false;
    setExpanded(button, true);
    void load(button, panel);
  };

  for (const button of root.querySelectorAll("[data-product-recipe-toggle]")) {
    button.addEventListener("click", () => toggle(button));
  }

  root.addEventListener("click", event => {
    if (event.target.closest("a, button, input, select, textarea, label, [data-product-recipe-panel]")) return;
    const trigger = event.target.closest('[data-product-recipe-trigger="true"]');
    if (!trigger || !root.contains(trigger)) return;
    const button = trigger.querySelector("[data-product-recipe-toggle]");
    if (button) toggle(button);
  });

  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && openButton) close(openButton, true);
  });
})();
