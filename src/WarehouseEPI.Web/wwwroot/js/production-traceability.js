(() => {
  "use strict";
  document.querySelector("[data-batch-filter]")?.addEventListener("change", event => {
    event.currentTarget.closest("form")?.requestSubmit();
  });
  document.querySelector("[data-production-label-print]")?.addEventListener("click", () => window.print());
  const resultForm = document.querySelector("[data-batch-result-form]");
  if (resultForm) {
    const stage = resultForm.querySelector("[data-result-stage]");
    const input = resultForm.querySelector("[data-result-input]");
    const suggest = () => {
      const stageId = stage?.value || "";
      const inputQuantity = Number.parseFloat(input?.value || "0");
      resultForm.querySelectorAll("[data-result-material]").forEach(row => {
        const selected = row.querySelector("input[type=checkbox]");
        const quantity = row.querySelector("[data-result-material-quantity]");
        const applies = row.dataset.stageId === stageId;
        if (selected) selected.checked = applies;
        if (quantity) {
          const ratio = Number.parseFloat(quantity.dataset.ratio || "0");
          const pending = Number.parseFloat(quantity.dataset.pending || "0");
          quantity.value = applies && inputQuantity > 0 ? Math.min(pending, inputQuantity * ratio).toFixed(4).replace(/\.?0+$/, "") : "";
        }
      });
    };
    stage?.addEventListener("change", suggest);
    input?.addEventListener("input", suggest);
  }
})();
