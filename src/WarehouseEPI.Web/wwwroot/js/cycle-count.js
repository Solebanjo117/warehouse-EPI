(() => {
  const unexpectedInputs = [...document.querySelectorAll('input[name$=".Code"]')];
  if (unexpectedInputs.length === 0) return;

  const fieldset = unexpectedInputs[0].closest("fieldset");
  const controls = document.createElement("div");
  controls.className = "d-flex flex-wrap align-items-center gap-2 mb-3";

  const scanButton = document.createElement("button");
  scanButton.type = "button";
  scanButton.className = "btn btn-outline-secondary";
  scanButton.textContent = "📷 Escanear producto inesperado";
  scanButton.setAttribute("aria-describedby", "cycle-count-camera-status");

  const photoInput = document.createElement("input");
  photoInput.type = "file";
  photoInput.accept = "image/*";
  photoInput.setAttribute("capture", "environment");
  photoInput.className = "visually-hidden";

  const status = document.createElement("span");
  status.id = "cycle-count-camera-status";
  status.className = "small text-body-secondary";
  status.setAttribute("role", "status");
  status.setAttribute("aria-live", "polite");
  status.textContent = "También puedes usar un lector HID como teclado.";

  controls.append(scanButton, photoInput, status);
  fieldset.insertBefore(controls, fieldset.querySelector(".row"));
  scanButton.addEventListener("click", () => photoInput.click());
  photoInput.addEventListener("change", async () => {
    const [photo] = photoInput.files;
    if (!photo) return;
    if (!window.ZXingBrowser) {
      status.textContent = "La lectura por cámara no está disponible; escribe o usa el lector HID.";
      return;
    }

    status.textContent = "Leyendo código…";
    const imageUrl = URL.createObjectURL(photo);
    try {
      const reader = new ZXingBrowser.BrowserMultiFormatReader();
      const result = await reader.decodeFromImageUrl(imageUrl);
      const target = unexpectedInputs.find(input => !input.value.trim()) || unexpectedInputs.at(-1);
      target.value = result.getText();
      target.dispatchEvent(new Event("change", { bubbles: true }));
      target.focus();
      status.textContent = `Código ${target.value} capturado. Ingresa su cantidad física.`;
    } catch {
      status.textContent = "No se detectó un código. Intenta otra foto o usa el lector HID.";
    } finally {
      URL.revokeObjectURL(imageUrl);
      photoInput.value = "";
    }
  });
})();
