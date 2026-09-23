(() => {
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  "use strict";
  const shell = document.querySelector("[data-production-planning]");
  if (!shell) return;
  const lookupUrl = shell.dataset.targetLookupUrl;
  for (const [rowIndex, row] of [...shell.querySelectorAll("[data-planning-row]")].entries()) {
    const search = row.querySelector("[data-planning-target-search]");
    const key = row.querySelector("[data-planning-target-key]");
    const results = row.querySelector("[data-planning-target-results]");
    const feedback = row.querySelector("[data-planning-target-feedback]");
    const stageId = row.dataset.stageId;
    if (!search || !key || !results || !feedback || !stageId || !lookupUrl) continue;
    let controller;
    let timer = 0;
    let highlighted = -1;
    const options = () => [...results.querySelectorAll("[role='option']")];
    const close = () => { results.replaceChildren(); highlighted = -1; search.setAttribute("aria-expanded", "false"); search.removeAttribute("aria-activedescendant"); };
    const highlight = index => { const items = options(); if (!items.length) return; highlighted = (index + items.length) % items.length; items.forEach((item, i) => { const active = i === highlighted; item.classList.toggle("active", active); item.setAttribute("aria-selected", active ? "true" : "false"); }); search.setAttribute("aria-activedescendant", items[highlighted].id); items[highlighted].scrollIntoView({ block: "nearest" }); };
    const select = item => { key.value = item.key; search.value = item.label; search.setCustomValidity(""); close(); feedback.textContent = `${item.label} seleccionado para esta orden. Guarda para confirmar con NIP.`; };
    const render = items => { close(); for (const [index, item] of items.entries()) { const option = document.createElement("button"); option.type = "button"; option.id = `planning-option-${rowIndex}-${index}`; option.className = "list-group-item list-group-item-action"; option.setAttribute("role", "option"); option.setAttribute("aria-selected", "false"); const title = document.createElement("strong"); title.className = "d-block"; title.textContent = item.label; const detail = document.createElement("small"); detail.className = "text-body-secondary"; detail.textContent = `${item.type} · ${item.description}`; option.append(title, detail); option.addEventListener("mousedown", event => event.preventDefault()); option.addEventListener("click", () => select(item)); results.append(option); } if (items.length) { search.setAttribute("aria-expanded", "true"); highlight(0); feedback.textContent = `${items.length} destinos disponibles. Usa flechas y Enter para elegir.`; } else feedback.textContent = "No hay destinos compatibles con esa búsqueda."; };
    const runSearch = async () => { const query = search.value.trim(); controller?.abort(); if (!query) { close(); feedback.textContent = "Escribe para buscar un destino compatible."; return; } controller = new AbortController(); feedback.textContent = "Buscando destinos WIP…"; try { const url = new URL(lookupUrl, window.location.href); url.searchParams.set("stageId", stageId); url.searchParams.set("q", query); const response = await fetch(url, { headers: { Accept: "application/json" }, signal: controller.signal }); if (!response.ok) throw new Error(`HTTP ${response.status}`); render(await response.json()); } catch (error) { if (error?.name !== "AbortError") feedback.textContent = "No fue posible buscar en la red local. Intenta nuevamente."; } };
    search.addEventListener("input", () => { key.value = ""; window.clearTimeout(timer); timer = window.setTimeout(() => void runSearch(), 250); });
    search.addEventListener("keydown", event => { if (event.key === "ArrowDown" && options().length) { event.preventDefault(); highlight(highlighted + 1); } else if (event.key === "ArrowUp" && options().length) { event.preventDefault(); highlight(highlighted - 1); } else if (event.key === "Enter" && highlighted >= 0) { event.preventDefault(); options()[highlighted].click(); } else if (event.key === "Escape") close(); });
    search.addEventListener("blur", () => window.setTimeout(close, 150));
  }
})();
