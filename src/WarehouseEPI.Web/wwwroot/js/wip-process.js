(() => {
  "use strict";

  const shell = document.querySelector("[data-wip-process]");
  if (!shell) return;
  const destination = shell.querySelector("[data-wip-destination]");
  const destinationInput = destination.querySelector("input");
  const referenceHint = shell.querySelector("[data-reference-required]");
  const cameraFile = shell.querySelector("[data-wip-camera-file]");
  let cameraTarget = null;

  const refresh = () => {
    const action = shell.querySelector('input[name="Input.Action"]:checked')?.value;
    const returning = action === "WarehouseReturn";
    destination.classList.toggle("d-none", !returning);
    destinationInput.required = returning;
    referenceHint.textContent = action === "SupplierReturn" ? "(obligatoria)" : "(opcional)";
  };

  shell.querySelectorAll('input[name="Input.Action"]').forEach((item) => item.addEventListener("change", refresh));
  shell.querySelector("[data-wip-process-form]").addEventListener("submit", (event) => {
    if (event.submitter?.matches("[data-wip-confirm]")) return;
    event.preventDefault();
    shell.querySelector("[data-wip-review]").click();
  });
  shell.querySelectorAll("[data-wip-camera]").forEach((button) => button.addEventListener("click", () => {
    cameraTarget = document.getElementById(button.dataset.wipCamera);
    cameraFile.value = "";
    cameraFile.click();
  }));
  cameraFile.addEventListener("change", async () => {
    if (!cameraTarget || !cameraFile.files?.[0]) return;
    if (!("BarcodeDetector" in window)) {
      window.alert("Este navegador no admite lectura de códigos desde cámara. Usa el lector HID o escribe el código.");
      return;
    }
    try {
      const bitmap = await createImageBitmap(cameraFile.files[0]);
      const codes = await new BarcodeDetector().detect(bitmap);
      bitmap.close();
      if (!codes.length) throw new Error("No se detectó un código.");
      cameraTarget.value = codes[0].rawValue;
      cameraTarget.dispatchEvent(new Event("change", { bubbles: true }));
      cameraTarget.focus();
    } catch (error) {
      window.alert(error.message || "No fue posible leer el código.");
    }
  });

  shell.querySelector("[data-wip-review]")?.addEventListener("click", () => {
    const form = shell.querySelector("[data-wip-process-form]");
    if (!form.reportValidity()) return;
    const action = shell.querySelector('input[name="Input.Action"]:checked')?.nextElementSibling?.querySelector("strong")?.textContent ?? "WIP";
    const wip = shell.querySelector('[name="Input.WipCode"]')?.value || "Sin WIP";
    const product = shell.querySelector('[name="Input.ProductCode"]')?.value || "Sin producto";
    const quantity = shell.querySelector('[name="Input.Quantity"]')?.value || "0";
    const target = shell.querySelector('[name="Input.DestinationCode"]')?.value;
    shell.querySelector("[data-wip-review-action]").textContent = action;
    shell.querySelector("[data-wip-review-position]").textContent = `${wip} · ${product}`;
    shell.querySelector("[data-wip-review-quantity]").textContent = quantity;
    const targetRow = shell.querySelector("[data-wip-review-target-row]");
    targetRow.classList.toggle("d-none", !(target && action === "Regreso a bodega"));
    shell.querySelector("[data-wip-review-target]").textContent = target || "";
    const current = Number(shell.dataset.sourceBalance);
    const requested = Number(quantity.replace(",", "."));
    shell.querySelector("[data-wip-negative-review]").textContent = Number.isFinite(current) && Number.isFinite(requested) && requested > current
      ? "Advertencia: el saldo WIP resultante será negativo."
      : "";
    bootstrap.Modal.getOrCreateInstance(document.getElementById("confirm-wip-process")).show();
  });
  refresh();
})();
