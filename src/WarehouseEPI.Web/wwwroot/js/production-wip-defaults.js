(() => {
  "use strict";

  document.querySelectorAll("[data-material-wip-editor]").forEach(editor => {
    const lookupUrl = editor.dataset.wipLookupUrl;
    const rows = editor.querySelector("[data-material-wip-rows]");
    const template = editor.querySelector("[data-material-wip-template]");
    const empty = editor.querySelector("[data-material-wip-empty]");
    const count = editor.querySelector("[data-material-wip-count]");
    let instanceSequence = 0;

    const reindex = () => {
      [...rows.querySelectorAll("[data-material-wip-row]")].forEach((row, index) => {
        const stage = row.querySelector("[data-material-wip-stage]");
        const key = row.querySelector("[data-material-wip-key]");
        const label = row.querySelector("[data-material-wip-label]");
        const search = row.querySelector("[data-material-wip-search]");
        const results = row.querySelector("[data-material-wip-results]");
        const stageId = `material-wip-stage-${index}`;
        const searchId = `material-wip-target-${index}`;
        const resultsId = `material-wip-results-${index}`;
        stage.name = `Wip.Rules[${index}].StageId`;
        stage.id = stageId;
        key.name = `Wip.Rules[${index}].TargetKey`;
        label.name = `Wip.Rules[${index}].TargetLabel`;
        search.id = searchId;
        search.setAttribute("aria-controls", resultsId);
        results.id = resultsId;
        row.querySelector("[data-wip-label-for='stage']")?.setAttribute("for", stageId);
        row.querySelector("[data-wip-label-for='search']")?.setAttribute("for", searchId);
        row.querySelector("[data-material-wip-remove]").setAttribute("aria-label", `Quitar destino WIP ${index + 1}`);
      });
      const total = rows.querySelectorAll("[data-material-wip-row]").length;
      empty?.classList.toggle("d-none", total !== 0);
      if (count) count.textContent = `${total} ${total === 1 ? "destino" : "destinos"}`;
    };

    const initialize = row => {
      if (row.dataset.materialWipInitialized === "true") return;
      row.dataset.materialWipInitialized = "true";
      const stage = row.querySelector("[data-material-wip-stage]");
      const key = row.querySelector("[data-material-wip-key]");
      const labelValue = row.querySelector("[data-material-wip-label]");
      const search = row.querySelector("[data-material-wip-search]");
      const results = row.querySelector("[data-material-wip-results]");
      const feedback = row.querySelector("[data-material-wip-feedback]");
      let timer = 0;
      let controller;
      let highlighted = -1;
      const instanceId = instanceSequence++;
      const options = () => [...results.querySelectorAll("[role='option']")];
      const close = () => { results.replaceChildren(); highlighted = -1; search.setAttribute("aria-expanded", "false"); search.removeAttribute("aria-activedescendant"); };
      const highlight = index => {
        const items = options();
        if (!items.length) return;
        highlighted = (index + items.length) % items.length;
        items.forEach((item, itemIndex) => { const active = itemIndex === highlighted; item.classList.toggle("active", active); item.setAttribute("aria-selected", active ? "true" : "false"); });
        search.setAttribute("aria-activedescendant", items[highlighted].id);
        items[highlighted].scrollIntoView({ block: "nearest" });
      };
      const select = item => { key.value = item.key; labelValue.value = item.label; search.value = item.label; search.setCustomValidity(""); close(); feedback.textContent = `${item.label} seleccionado.${item.warning ? ` ${item.warning}.` : ""}`; search.dispatchEvent(new Event("change", { bubbles: true })); };
      const render = items => {
        close();
        items.forEach((item, index) => {
          const option = document.createElement("button"); option.type = "button"; option.id = `material-wip-option-${instanceId}-${index}`; option.className = "list-group-item list-group-item-action"; option.setAttribute("role", "option"); option.setAttribute("aria-selected", "false");
          const title = document.createElement("strong"); title.className = "d-block"; title.textContent = item.label;
          const detail = document.createElement("small"); detail.className = "text-body-secondary"; detail.textContent = `${item.type} · ${item.description}`;
          option.append(title, detail); option.addEventListener("mousedown", event => event.preventDefault()); option.addEventListener("click", () => select(item)); results.append(option);
        });
        if (items.length) { search.setAttribute("aria-expanded", "true"); highlight(0); feedback.textContent = `${items.length} destinos disponibles. Usa flechas y Enter para elegir.`; }
        else feedback.textContent = "No hay destinos compatibles con esa búsqueda.";
      };
      const runSearch = async () => {
        const query = search.value.trim(); controller?.abort();
        if (!stage.value) { close(); feedback.textContent = "Selecciona primero el proceso."; return; }
        if (!query) { close(); feedback.textContent = "Escribe para buscar un área, rack o posición WIP."; return; }
        controller = new AbortController(); feedback.textContent = "Buscando destinos WIP…";
        try { const url = new URL(lookupUrl, window.location.href); url.searchParams.set("stageId", stage.value); url.searchParams.set("q", query); const response = await fetch(url, { headers: { Accept: "application/json" }, signal: controller.signal }); if (!response.ok) throw new Error(`HTTP ${response.status}`); render(await response.json()); }
        catch (error) { if (error?.name !== "AbortError") feedback.textContent = "No fue posible buscar en la red local. Intenta nuevamente."; }
      };
      stage.addEventListener("change", () => { controller?.abort(); key.value = ""; labelValue.value = ""; search.value = ""; close(); feedback.textContent = stage.value ? "Escribe para buscar un destino compatible." : "Selecciona primero el proceso."; });
      search.addEventListener("input", () => { key.value = ""; labelValue.value = ""; window.clearTimeout(timer); timer = window.setTimeout(() => void runSearch(), 250); });
      search.addEventListener("keydown", event => { if (event.key === "ArrowDown" && options().length) { event.preventDefault(); highlight(highlighted + 1); } else if (event.key === "ArrowUp" && options().length) { event.preventDefault(); highlight(highlighted - 1); } else if (event.key === "Enter" && highlighted >= 0) { event.preventDefault(); options()[highlighted].click(); } else if (event.key === "Escape") close(); });
      search.addEventListener("blur", () => window.setTimeout(close, 150));
      row.querySelector("[data-material-wip-remove]").addEventListener("click", () => { controller?.abort(); row.remove(); reindex(); rows.dispatchEvent(new Event("change", { bubbles: true })); });
    };

    [...rows.querySelectorAll("[data-material-wip-row]")].forEach(initialize);
    editor.querySelector("[data-material-wip-add]")?.addEventListener("click", () => {
      const row = template.content.firstElementChild.cloneNode(true); rows.append(row); reindex(); initialize(row, rows.children.length - 1); row.querySelector("[data-material-wip-stage]").focus(); rows.dispatchEvent(new Event("change", { bubbles: true }));
    });
    reindex();
    editor.closest("form")?.addEventListener("submit", event => {
      for (const row of rows.querySelectorAll("[data-material-wip-row]")) {
        const stage = row.querySelector("[data-material-wip-stage]"); const key = row.querySelector("[data-material-wip-key]"); const search = row.querySelector("[data-material-wip-search]");
        if ((stage.value && !key.value) || (!stage.value && key.value)) { event.preventDefault(); search.setCustomValidity("Selecciona proceso y destino, o quita la fila."); search.reportValidity(); search.focus(); return; }
      }
    });
  });
})();
