(() => {
  "use strict";

  const form = document.querySelector("[data-production-recipe]");
  if (!form) return;

  const linesContainer = form.querySelector("[data-production-recipe-lines]");
  const addButton = form.querySelector("[data-production-recipe-add]");
  const status = form.querySelector("[data-production-recipe-status]");
  if (!linesContainer || !addButton || !status) return;

  const lines = () => [...linesContainer.querySelectorAll("[data-production-recipe-line]")];

  const clearValidity = (line) => {
    line.querySelector("[data-cycle-plan-search]")?.setCustomValidity("");
    line.querySelector("[data-production-recipe-stage]")?.setCustomValidity("");
    line.querySelector("[data-production-recipe-quantity]")?.setCustomValidity("");
  };

  const replaceIndex = (value, index) => value
    .replace(/Recipe\.Lines\[\d+\]/g, `Recipe.Lines[${index}]`)
    .replace(/Recipe_Lines_\d+__/g, `Recipe_Lines_${index}__`)
    .replace(/recipe-material-search-\d+/g, `recipe-material-search-${index}`)
    .replace(/recipe-material-results-\d+/g, `recipe-material-results-${index}`)
    .replace(/recipe-material-feedback-\d+/g, `recipe-material-feedback-${index}`);

  const renumber = () => {
    lines().forEach((line, index) => {
      line.querySelectorAll("[name], [id], [for], [aria-controls], [aria-describedby]").forEach((element) => {
        ["name", "id", "for", "aria-controls", "aria-describedby"].forEach((attribute) => {
          const value = element.getAttribute(attribute);
          if (value) element.setAttribute(attribute, replaceIndex(value, index));
        });
      });

      const position = index + 1;
      const lookup = line.querySelector("[data-cycle-plan-field]");
      if (lookup) lookup.dataset.cyclePlanKey = `material-${index}`;
      line.querySelector("[data-cycle-plan-search]")?.setAttribute("aria-label", `Material ${position}`);
      line.querySelector("[data-cycle-plan-camera]")?.setAttribute("aria-label", `Escanear material ${position} con cámara`);
      line.querySelector("[data-production-recipe-quantity]")?.setAttribute("aria-label", `Cantidad del material ${position}`);
      line.querySelector("[data-production-recipe-stage]")?.setAttribute("aria-label", `Etapa del material ${position}`);
      line.querySelector("[data-production-recipe-remove]")?.setAttribute("aria-label", `Quitar material ${position}`);
    });
  };

  const clearLine = (line) => {
    const materialId = line.querySelector("[data-cycle-plan-id]");
    const material = line.querySelector("[data-cycle-plan-search]");
    const quantity = line.querySelector("[data-production-recipe-quantity]");
    const stage = line.querySelector("[data-production-recipe-stage]");
    if (materialId) materialId.value = "";
    if (material) {
      material.value = "";
      material.setAttribute("aria-expanded", "false");
      material.removeAttribute("aria-activedescendant");
    }
    if (quantity) quantity.value = "";
    if (stage) stage.value = "";
    line.querySelector("[data-cycle-plan-results]")?.replaceChildren();
    const selected = line.querySelector("[data-cycle-plan-selected]");
    selected?.classList.add("d-none");
    selected?.querySelectorAll("[data-cycle-plan-selected-title], [data-cycle-plan-selected-detail], [data-cycle-plan-selected-meta]")
      .forEach((element) => { element.textContent = ""; });
    line.querySelector("[data-production-recipe-inactive]")?.remove();
    const feedback = line.querySelector("[data-cycle-plan-feedback]");
    if (feedback) feedback.textContent = "Escribe para buscar o usa un lector HID.";
    clearValidity(line);
  };

  const refreshLookupFields = (scope) => {
    form.dispatchEvent(new CustomEvent("cycle-plan:refresh", { detail: { scope } }));
  };

  addButton.addEventListener("click", () => {
    const source = lines()[0];
    if (!source) return;
    const line = source.cloneNode(true);
    line.querySelector("[data-cycle-plan-field]")?.removeAttribute("data-cycle-plan-initialized");
    clearLine(line);
    linesContainer.append(line);
    renumber();
    refreshLookupFields(line);
    status.textContent = `Material ${lines().length} agregado.`;
    linesContainer.dispatchEvent(new Event("change", { bubbles: true }));
    line.querySelector("[data-cycle-plan-search]")?.focus();
  });

  linesContainer.addEventListener("click", (event) => {
    const removeButton = event.target.closest("[data-production-recipe-remove]");
    if (!removeButton) return;
    const line = removeButton.closest("[data-production-recipe-line]");
    const currentLines = lines();
    const removedIndex = currentLines.indexOf(line);

    if (currentLines.length === 1) {
      clearLine(line);
      line.querySelector("[data-cycle-plan-search]")?.dispatchEvent(new Event("input", { bubbles: true }));
      status.textContent = "Material limpiado.";
      line.querySelector("[data-cycle-plan-search]")?.focus();
      return;
    }

    line.remove();
    renumber();
    refreshLookupFields(linesContainer);
    linesContainer.dispatchEvent(new Event("change", { bubbles: true }));
    status.textContent = "Material quitado.";
    const remaining = lines();
    remaining[Math.min(removedIndex, remaining.length - 1)]
      ?.querySelector("[data-cycle-plan-search]")?.focus();
  });

  form.addEventListener("input", (event) => {
    const line = event.target.closest("[data-production-recipe-line]");
    if (line) clearValidity(line);
  });
  form.addEventListener("change", (event) => {
    const line = event.target.closest("[data-production-recipe-line]");
    if (line) clearValidity(line);
  });

  form.addEventListener("submit", (event) => {
    renumber();
    for (const line of lines()) {
      const materialId = line.querySelector("[data-cycle-plan-id]");
      const material = line.querySelector("[data-cycle-plan-search]");
      const stage = line.querySelector("[data-production-recipe-stage]");
      const quantity = line.querySelector("[data-production-recipe-quantity]");
      const amount = Number(quantity.value.trim().replace(",", "."));
      const hasContent = material.value.trim() || stage?.value || quantity.value.trim();
      clearValidity(line);
      if (!hasContent) continue;

      let invalid;
      if (!materialId.value) {
        material.setCustomValidity("Selecciona el material de los resultados.");
        invalid = material;
      } else if (!(amount > 0)) {
        quantity.setCustomValidity("Indica una cantidad mayor que cero.");
        invalid = quantity;
      }

      if (invalid) {
        event.preventDefault();
        invalid.reportValidity();
        invalid.focus();
        return;
      }
    }
  });

  renumber();
})();
