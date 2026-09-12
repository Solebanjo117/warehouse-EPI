(() => {
  "use strict";

  const form = document.querySelector("[data-production-recipe]");
  if (!form) return;

  const lines = [...form.querySelectorAll("[data-production-recipe-line]")];

  const clearValidity = (line) => {
    line.querySelector("[data-cycle-plan-search]")?.setCustomValidity("");
    line.querySelector("[data-production-recipe-stage]")?.setCustomValidity("");
    line.querySelector("[data-production-recipe-quantity]")?.setCustomValidity("");
  };

  lines.forEach((line) => {
    line.addEventListener("input", () => clearValidity(line));
    line.addEventListener("change", () => clearValidity(line));
  });

  form.addEventListener("submit", (event) => {
    for (const line of lines) {
      const materialId = line.querySelector("[data-cycle-plan-id]");
      const material = line.querySelector("[data-cycle-plan-search]");
      const stage = line.querySelector("[data-production-recipe-stage]");
      const quantity = line.querySelector("[data-production-recipe-quantity]");
      const amount = Number(quantity.value.trim().replace(",", "."));
      const hasContent = material.value.trim() || stage.value || quantity.value.trim();
      clearValidity(line);
      if (!hasContent) continue;

      let invalid;
      if (!materialId.value) {
        material.setCustomValidity("Selecciona el material de los resultados.");
        invalid = material;
      } else if (!stage.value) {
        stage.setCustomValidity("Selecciona el proceso donde se incorpora el material.");
        invalid = stage;
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
})();
