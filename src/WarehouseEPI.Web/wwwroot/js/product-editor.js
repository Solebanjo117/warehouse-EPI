(() => {
  "use strict";

  const editor = document.querySelector("[data-product-editor]");
  if (!editor) return;

  const forms = [...editor.querySelectorAll("form[data-product-dirty-form]")];
  const snapshots = new Map();
  const dirty = new Set();
  const serialize = form => [...new FormData(form).entries()]
    .filter(([name]) => !name.endsWith(".Pin") && name !== "__RequestVerificationToken")
    .map(([name, value]) => `${name}=${value}`).join("&");
  forms.forEach(form => snapshots.set(form, serialize(form)));

  const refreshDirty = form => {
    if (serialize(form) === snapshots.get(form)) dirty.delete(form); else dirty.add(form);
    const panel = form.closest("[data-product-panel]");
    const tab = panel && editor.querySelector(`[data-product-tab='${panel.dataset.productPanel}']`);
    const panelDirty = panel && [...dirty].some(item => item.closest("[data-product-panel]") === panel);
    const indicator = tab?.querySelector("[data-product-dirty-indicator]");
    if (indicator) indicator.textContent = panelDirty ? "Cambios pendientes" : indicator.dataset.cleanLabel;
    tab?.classList.toggle("has-pending-changes", panelDirty);
  };
  forms.forEach(form => {
    form.addEventListener("input", () => refreshDirty(form));
    form.addEventListener("change", () => refreshDirty(form));
    form.addEventListener("submit", event => {
      const otherDirty = [...dirty].filter(item => item !== form);
      if (otherDirty.length && !window.confirm(`Hay cambios sin guardar en: ${otherDirty.map(item => item.dataset.productDirtyForm).join(", ")}. ¿Deseas descartarlos y continuar?`)) event.preventDefault();
      else if (!event.defaultPrevented) dirty.delete(form);
    });
  });

  const confirmNavigation = event => {
    if (!dirty.size) return;
    if (!window.confirm(`Hay cambios sin guardar en: ${[...dirty].map(form => form.dataset.productDirtyForm).join(", ")}. ¿Deseas descartarlos y continuar?`)) event.preventDefault();
  };
  editor.querySelectorAll("a:not([data-product-tab]), form[data-product-navigation-form]").forEach(element => {
    element.addEventListener(element.tagName === "FORM" ? "submit" : "click", confirmNavigation);
  });
  window.addEventListener("beforeunload", event => { if (dirty.size) { event.preventDefault(); event.returnValue = ""; } });

  const tabs = [...editor.querySelectorAll("[data-product-tab]")];
  const panels = [...editor.querySelectorAll("[data-product-panel]")];
  const activate = (name, updateHash = true) => {
    tabs.forEach(tab => { const active = tab.dataset.productTab === name; tab.classList.toggle("is-active", active); tab.setAttribute("aria-selected", active ? "true" : "false"); tab.tabIndex = active ? 0 : -1; });
    panels.forEach(panel => { const active = panel.dataset.productPanel === name; panel.classList.toggle("is-active", active); panel.setAttribute("aria-hidden", active ? "false" : "true"); });
    if (updateHash) history.replaceState(null, "", tabs.find(tab => tab.dataset.productTab === name)?.getAttribute("href") || window.location.pathname);
  };
  if (tabs.length) {
    editor.classList.add("product-editor-tabs-ready");
    tabs.forEach(tab => tab.addEventListener("click", event => { event.preventDefault(); activate(tab.dataset.productTab); }));
    const errorPanel = panels.find(panel => panel.dataset.productErrors === "True" || panel.dataset.productErrors === "true");
    const hashMap = { "product-production": "production", "product-production-materials": "production", "product-production-route": "production", "product-wip-defaults": "wip", "product-locations": "locations" };
    activate(errorPanel?.dataset.productPanel || hashMap[location.hash.slice(1)] || editor.dataset.productDefaultPanel || "general", false);
  }

  const subtabs = [...editor.querySelectorAll("[data-product-subtab]")];
  const subpanels = [...editor.querySelectorAll("[data-product-subpanel]")];
  const activateSubtab = name => {
    subtabs.forEach(tab => { const active = tab.dataset.productSubtab === name; tab.classList.toggle("is-active", active); tab.setAttribute("aria-selected", active ? "true" : "false"); });
    subpanels.forEach(panel => { const active = panel.dataset.productSubpanel === name; panel.classList.toggle("is-active", active); panel.hidden = !active; });
  };
  if (subtabs.length && subpanels.length) {
    subtabs.forEach(tab => tab.addEventListener("click", () => activateSubtab(tab.dataset.productSubtab)));
    const errorSubpanel = subpanels.find(panel => panel.dataset.productErrors === "True" || panel.dataset.productErrors === "true");
    activateSubtab(errorSubpanel?.dataset.productSubpanel || (location.hash === "#product-production-route" ? "route" : editor.dataset.productDefaultProductionPanel || "materials"));
  }

  editor.querySelectorAll("[data-product-autogrow]").forEach(textarea => {
    const resize = () => { textarea.style.height = "auto"; textarea.style.height = `${Math.min(textarea.scrollHeight, 10 * parseFloat(getComputedStyle(textarea).lineHeight))}px`; };
    textarea.addEventListener("input", resize); resize();
  });

  const invalidInput = editor.querySelector(".input-validation-error");
  const invalidMessage = editor.querySelector(".field-validation-error[data-valmsg-for]");
  const namedInput = invalidMessage?.dataset.valmsgFor
    ? invalidMessage.closest("form")?.elements.namedItem(invalidMessage.dataset.valmsgFor)
    : null;
  (invalidInput || namedInput)?.focus?.();
})();
