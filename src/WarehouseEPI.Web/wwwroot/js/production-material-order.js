(() => {
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  "use strict";
  document.querySelectorAll("[data-material-operation-form]").forEach(form => {
    const mode = form.querySelector("[data-material-mode]");
    const destination = form.querySelector("[data-material-return-fields]");
    if (destination && !form.querySelector("[name='Material.ReturnEffect']")) {
      const label = document.createElement("label");
      const effect = document.createElement("select");
      const suffix = Math.random().toString(36).slice(2);
      label.className = "form-label mt-2";
      label.htmlFor = `material-return-effect-${suffix}`;
      label.textContent = "Efecto en surtimiento";
      effect.id = label.htmlFor;
      effect.name = "Material.ReturnEffect";
      effect.className = "form-select";
      effect.required = true;
      effect.innerHTML = '<option value="replenish">Solicitar reposición</option><option value="surplus">Devolver sobrante sin reabrir pendiente</option>';
      destination.append(label, effect);
    }
    const reference = form.querySelector("[name='Material.Reference']")?.closest("div");
    const updateFields = () => {
      destination?.classList.toggle("d-none", mode.value !== "return");
      reference?.classList.toggle("d-none", mode.value !== "supplier");
    };
    mode.addEventListener("change", updateFields);
    updateFields();
    form.addEventListener("submit", event => {
      const selected = [...form.querySelectorAll("input[type='checkbox'][name$='.Selected']:checked")];
      const lines = selected.map(checkbox => {
        const row = checkbox.closest('.row');
        return row.dataset.materialLabel + ': ' + row.querySelector("input[name$='.Quantity']").value + ' ' + row.dataset.unit;
      });
      if (!selected.length) {
        event.preventDefault();
        window.alert("Selecciona al menos una línea de material.");
        return;
      }
      const action = mode.options[mode.selectedIndex].text;
      if (!window.confirm(`${action}: ${selected.length} línea(s), ${lines.join("; ")}. ¿Confirmar operación?`))
        event.preventDefault();
    });
  });
})();

