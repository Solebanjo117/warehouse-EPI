(() => {
  "use strict";
  document.querySelectorAll("[data-material-operation-form]").forEach(form => {
    const mode = form.querySelector("[data-material-mode]");
    const destination = form.querySelector("[data-material-return-fields]");
    const reference = form.querySelector("[name='Material.Reference']")?.closest("div");
    const updateFields = () => {
      destination?.classList.toggle("d-none", mode.value !== "return");
      reference?.classList.toggle("d-none", mode.value !== "supplier");
    };
    mode.addEventListener("change", updateFields);
    updateFields();
    form.addEventListener("submit", event => {
      const selected = [...form.querySelectorAll("input[type='checkbox'][name$='.Selected']:checked")];
      const total = selected.reduce((sum, checkbox) => {
        const row = checkbox.closest(".row");
        return sum + Number(row?.querySelector("input[name$='.Quantity']")?.value || 0);
      }, 0);
      if (!selected.length) {
        event.preventDefault();
        window.alert("Selecciona al menos una línea de material.");
        return;
      }
      const action = mode.options[mode.selectedIndex].text;
      if (!window.confirm(`${action}: ${selected.length} línea(s), cantidad total ${total}. ¿Confirmar operación?`))
        event.preventDefault();
    });
  });
})();

