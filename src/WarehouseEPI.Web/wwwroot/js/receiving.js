(() => {
  const shell = document.querySelector("[data-receiving-builder]");
  if (!shell) return;
  const lines = shell.querySelector("[data-receiving-lines]");
  const template = document.querySelector("[data-receiving-line-template]");
  const lookupUrl = shell.dataset.lookupUrl;

  const renumber = () => {
    [...lines.querySelectorAll("[data-receiving-line]")].forEach((line, index) => {
      line.querySelectorAll("[name]").forEach(input => {
        input.name = input.name.replace(/Input\.Lines\[\d+\]/, `Input.Lines[${index}]`);
      });
    });
  };

  const choose = (line, kind, item) => {
    const input = line.querySelector(`[data-${kind}-lookup]`);
    const id = line.querySelector(`[data-${kind}-id]`);
    id.value = item.id;
    input.value = kind === "product" ? `${item.sku}${item.description ? ` · ${item.description}` : ""}` : item.code;
    input.setCustomValidity("");
    line.querySelector(`[data-${kind}-results]`).replaceChildren();
    if (kind === "product") {
      const description = line.querySelector("[data-product-description]");
      if (description) description.textContent = `${item.description || "Sin descripción"} · ${item.unitCode}`;
    }
  };

  const renderResults = (line, kind, items) => {
    const results = line.querySelector(`[data-${kind}-results]`);
    results.replaceChildren();
    items.forEach(item => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "list-group-item list-group-item-action";
      button.textContent = kind === "product" ? `${item.sku} · ${item.description || "Sin descripción"}` : `${item.code} · ${item.description || "Ubicación"}`;
      button.addEventListener("click", () => choose(line, kind, item));
      results.append(button);
    });
  };

  const exact = async (line, kind) => {
    const input = line.querySelector(`[data-${kind}-lookup]`);
    const code = input.value.trim().split(" · ")[0];
    if (!code) return;
    const handler = kind === "product" ? "ResolveProduct" : "ResolveLocation";
    const response = await fetch(`${lookupUrl}?handler=${handler}&code=${encodeURIComponent(code)}`, { headers: { Accept: "application/json" } });
    if (!response.ok) {
      line.querySelector(`[data-${kind}-id]`).value = "";
      input.setCustomValidity(kind === "product" ? "Selecciona un producto vigente." : "Selecciona una ubicación operativa.");
      input.reportValidity();
      return;
    }
    choose(line, kind, await response.json());
  };

  const setupLookup = (line, kind) => {
    const input = line.querySelector(`[data-${kind}-lookup]`);
    if (!input || input.dataset.receivingReady) return;
    input.dataset.receivingReady = "true";
    let timer;
    input.addEventListener("input", () => {
      line.querySelector(`[data-${kind}-id]`).value = "";
      input.setCustomValidity("");
      clearTimeout(timer);
      const term = input.value.trim();
      if (term.length < 2) { line.querySelector(`[data-${kind}-results]`).replaceChildren(); return; }
      timer = window.setTimeout(async () => {
        const handler = kind === "product" ? "Products" : "Locations";
        try {
          const response = await fetch(`${lookupUrl}?handler=${handler}&q=${encodeURIComponent(term)}`, { headers: { Accept: "application/json" } });
          if (response.ok) renderResults(line, kind, await response.json());
        } catch { input.setCustomValidity("No fue posible consultar. Intenta nuevamente."); }
      }, 180);
    });
    input.addEventListener("keydown", event => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      void exact(line, kind);
    });
    input.addEventListener("blur", () => {
      if (!line.querySelector(`[data-${kind}-id]`).value && input.value.trim()) void exact(line, kind);
    });
  };

  const setupLine = line => {
    setupLookup(line, "product");
    setupLookup(line, "location");
    line.querySelector("[data-remove-receiving-line]")?.addEventListener("click", () => {
      if (lines.querySelectorAll("[data-receiving-line]").length === 1) return;
      line.remove(); renumber();
    });
  };

  lines.querySelectorAll("[data-receiving-line]").forEach(setupLine);
  shell.querySelector("[data-add-receiving-line]")?.addEventListener("click", () => {
    const index = lines.querySelectorAll("[data-receiving-line]").length;
    const wrapper = document.createElement("div");
    wrapper.innerHTML = template.innerHTML.replaceAll("__index__", index);
    const line = wrapper.firstElementChild;
    lines.append(line); setupLine(line); line.querySelector("[data-product-lookup]")?.focus();
  });
  shell.querySelector("[data-receiving-form]")?.addEventListener("submit", event => {
    renumber();
    let invalid;
    lines.querySelectorAll("[data-receiving-line]").forEach(line => {
      ["product", ...(line.querySelector("[data-location-lookup]") ? ["location"] : [])].forEach(kind => {
        const input = line.querySelector(`[data-${kind}-lookup]`);
        if (!line.querySelector(`[data-${kind}-id]`).value) { input.setCustomValidity(`Selecciona ${kind === "product" ? "un producto" : "una ubicación"} de los resultados.`); invalid ||= input; }
      });
    });
    if (invalid) { event.preventDefault(); invalid.reportValidity(); invalid.focus(); return; }
    const button = event.submitter;
    if (button) { button.disabled = true; button.textContent = "Confirmando…"; }
  });
})();
