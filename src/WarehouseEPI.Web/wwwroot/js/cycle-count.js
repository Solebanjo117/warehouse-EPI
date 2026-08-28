(() => {
  // La CSP no permite handlers en atributos: la impresión de la hoja ciega se enlaza aquí.
  document.querySelector("[data-cycle-print]")?.addEventListener("click", () => window.print());

  const decodePhoto = async (input, callback) => {
    const [photo] = input.files;
    if (!photo || !window.ZXingBrowser) return;
    const url = URL.createObjectURL(photo);
    try { const result = await new ZXingBrowser.BrowserMultiFormatReader().decodeFromImageUrl(url); callback(result.getText()); }
    catch { /* Keep the operator on the page; manual/HID capture remains available. */ }
    finally { URL.revokeObjectURL(url); input.value = ""; }
  };
  const locationButton = document.querySelector("[data-cycle-scan-location]");
  if (locationButton) {
    const form = locationButton.closest("form"); const code = form.querySelector("[data-cycle-location-code]"); const photo = form.querySelector("[data-cycle-location-photo]");
    locationButton.addEventListener("click", () => photo.click());
    photo.addEventListener("change", () => void decodePhoto(photo, value => { code.value = value; form.requestSubmit(); }));
  }
  const productButton = document.querySelector("[data-cycle-scan-product]");
  if (productButton) {
    const shell = productButton.closest("[data-cycle-count-capture]"); const photo = shell.querySelector("[data-cycle-scan-photo]");
    productButton.addEventListener("click", () => photo.click());
    photo.addEventListener("change", () => void decodePhoto(photo, value => {
      const row = [...shell.querySelectorAll("[data-cycle-product]")].find(item => item.dataset.cycleProduct.toUpperCase() === value.trim().toUpperCase());
      const target = row?.querySelector("[data-cycle-quantity]") || shell.querySelector('input[name$=".Code"]:not([value])');
      if (target) { if (!row) target.value = value; target.focus(); }
    }));
  }

  const capture = document.querySelector("[data-cycle-count-capture]");
  const fieldset = capture?.querySelector("[data-cycle-unexpected]");
  const form = capture?.querySelector("form");
  if (!capture || !fieldset || !form) return;

  const codeInputs = () => [...fieldset.querySelectorAll('input[name$=".Code"]')];
  const rows = () => [...fieldset.querySelectorAll("[data-cycle-unexpected-row]")];

  // ---- Líneas de producto inesperado: agregar y quitar sin recargar ---------------------
  // El enlace de modelo exige índices contiguos, así que cada cambio renumera las filas.
  const renumber = () => {
    const current = rows();
    current.forEach((row, index) => {
      row.querySelectorAll("input").forEach((input) => {
        const field = input.name.endsWith(".Quantity") ? "Quantity" : "Code";
        const id = `unexpected-${field === "Quantity" ? "quantity" : "code"}-${index}`;
        row.querySelector(`label[for="${input.id}"]`)?.setAttribute("for", id);
        input.name = `Input.UnexpectedEntries[${index}].${field}`;
        input.id = id;
      });
      const remove = row.querySelector("[data-cycle-remove-row]");
      if (remove) remove.disabled = current.length <= 1;
    });
  };

  const addRemoveButton = (row) => {
    if (row.querySelector("[data-cycle-remove-row]")) return;
    const holder = document.createElement("div");
    holder.className = "col-sm-2 d-grid";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn btn-outline-secondary";
    button.textContent = "Quitar";
    button.setAttribute("data-cycle-remove-row", "");
    button.addEventListener("click", () => {
      if (rows().length <= 1) return;
      row.remove();
      renumber();
      saveDraft();
    });
    holder.append(button);
    row.append(holder);
  };

  const controls = document.createElement("div");
  controls.className = "d-flex flex-wrap align-items-center gap-2 mb-3";

  const addButton = document.createElement("button");
  addButton.type = "button";
  addButton.className = "btn btn-outline-primary";
  addButton.textContent = "Agregar otra línea";
  addButton.setAttribute("data-cycle-add-row", "");
  addButton.addEventListener("click", () => {
    const template = rows().at(-1);
    const copy = template.cloneNode(true);
    copy.querySelectorAll("input").forEach((input) => { input.value = ""; });
    copy.querySelector("[data-cycle-remove-row]")?.parentElement?.remove();
    template.after(copy);
    addRemoveButton(copy);
    renumber();
    copy.querySelector("input").focus();
  });

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

  controls.append(addButton, scanButton, photoInput, status);
  fieldset.insertBefore(controls, fieldset.querySelector(".row"));
  rows().forEach(addRemoveButton);
  renumber();

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
      const target = codeInputs().find(input => !input.value.trim()) || codeInputs().at(-1);
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

  // ---- Borrador local -------------------------------------------------------------------
  // Las tablets son compartidas: el NIP nunca se guarda, el borrador caduca a las 12 horas y
  // sólo se restaura cuando el operador lo pide. El token de preparación tampoco se guarda:
  // el vigente es el que renderiza el servidor.
  const draftKey = `warehouseEpi.cycleCount.${capture.dataset.cycleCampaign}.${capture.dataset.cycleLocation}`;
  const draftLifetimeMs = 12 * 60 * 60 * 1000;
  const isDraftField = (element) => element.name
    && element.type !== "password"
    && element.name !== "__RequestVerificationToken"
    && element.name !== "Input.PreparationToken";

  const discardDraft = () => {
    try { window.localStorage.removeItem(draftKey); }
    catch { /* almacenamiento no disponible */ }
  };

  const readDraft = () => {
    try {
      const stored = JSON.parse(window.localStorage.getItem(draftKey) || "null");
      if (!stored || !stored.fields || Date.now() - stored.savedAt > draftLifetimeMs) { discardDraft(); return null; }
      return stored;
    } catch { discardDraft(); return null; }
  };

  let saveTimer = 0;
  function saveDraft() {
    window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => {
      const fields = {};
      form.querySelectorAll("input, select, textarea").forEach((element) => {
        if (!isDraftField(element)) return;
        fields[element.name] = element.type === "checkbox" ? element.checked : element.value;
      });
      const hasCapture = Object.entries(fields).some(([name, value]) =>
        (name.endsWith(".Quantity") || name.endsWith(".Code")) && String(value).trim() !== "");
      if (!hasCapture) return discardDraft();
      try { window.localStorage.setItem(draftKey, JSON.stringify({ savedAt: Date.now(), fields })); }
      catch { /* cuota o modo privado: la captura sigue en pantalla */ }
    }, 400);
  }

  const restoreDraft = (draft) => {
    const missingRows = Object.keys(draft.fields).filter(name => name.endsWith(".Code")).length - rows().length;
    for (let index = 0; index < missingRows; index += 1) addButton.click();
    form.querySelectorAll("input, select, textarea").forEach((element) => {
      if (!isDraftField(element) || !(element.name in draft.fields)) return;
      if (element.type === "checkbox") element.checked = draft.fields[element.name];
      else element.value = draft.fields[element.name];
    });
  };

  const draft = readDraft();
  const isCaptureIncomplete = [...form.querySelectorAll("[data-cycle-quantity]")].some(input => !input.value.trim());
  if (draft && isCaptureIncomplete) {
    const banner = document.createElement("div");
    banner.className = "alert alert-info d-flex flex-wrap align-items-center gap-2";
    banner.setAttribute("role", "status");
    const text = document.createElement("span");
    text.className = "me-auto";
    text.textContent = "Se encontró una captura sin enviar para esta ubicación.";
    const restoreButton = document.createElement("button");
    restoreButton.type = "button";
    restoreButton.className = "btn btn-sm btn-primary";
    restoreButton.textContent = "Restaurar";
    restoreButton.addEventListener("click", () => { restoreDraft(draft); banner.remove(); });
    const discardButton = document.createElement("button");
    discardButton.type = "button";
    discardButton.className = "btn btn-sm btn-outline-secondary";
    discardButton.textContent = "Descartar";
    discardButton.addEventListener("click", () => { discardDraft(); banner.remove(); });
    banner.append(text, restoreButton, discardButton);
    form.parentElement.insertBefore(banner, form);
  }

  form.addEventListener("input", saveDraft);
  form.addEventListener("submit", discardDraft);
})();
