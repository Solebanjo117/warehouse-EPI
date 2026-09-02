(() => {
  const draftLifetimeMs = 12 * 60 * 60 * 1000;

  // Icono del sprite de _Layout: los botones creados desde JS usan el mismo trazo que el marcado.
  const cameraIcon = () => {
    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("class", "app-icon");
    svg.setAttribute("aria-hidden", "true");
    const use = document.createElementNS(ns, "use");
    use.setAttribute("href", "#icon-camera");
    svg.append(use);
    return svg;
  };

  // La CSP no permite handlers en atributos: la impresión de la hoja ciega se enlaza aquí.
  document.querySelector("[data-cycle-print]")?.addEventListener("click", () => window.print());

  const campaignDetail = document.querySelector("[data-cycle-campaign-detail]");
  if (campaignDetail) {
    const campaignId = campaignDetail.dataset.cycleCampaign;
    const submittedLocation = campaignDetail.dataset.cycleSubmittedLocation;
    if (submittedLocation) {
      try { window.localStorage.removeItem(`warehouseEpi.cycleCount.${campaignId}.${submittedLocation}`); }
      catch { /* almacenamiento no disponible */ }
    }

    campaignDetail.querySelectorAll("[data-cycle-location-card]").forEach((card) => {
      const key = `warehouseEpi.cycleCount.${campaignId}.${card.dataset.cycleLocation}`;
      try {
        const draft = JSON.parse(window.localStorage.getItem(key) || "null");
        if (!draft?.fields || Date.now() - draft.savedAt > draftLifetimeMs) {
          if (draft) window.localStorage.removeItem(key);
          return;
        }
        const hasCapture = Object.entries(draft.fields).some(([name, value]) =>
          (name.endsWith(".Quantity") || name.endsWith(".Code")) && String(value).trim() !== "");
        const link = card.querySelector("[data-cycle-count-link]");
        if (hasCapture && link) {
          link.textContent = "Continuar captura";
          card.classList.add("has-cycle-draft");
        }
      } catch { /* un borrador ilegible no bloquea la campaña */ }
    });

    const filterShell = campaignDetail.querySelector("[data-cycle-filters]");
    const cards = [...campaignDetail.querySelectorAll("[data-cycle-location-card]")];
    const empty = campaignDetail.querySelector("[data-cycle-filter-empty]");
    const applyFilter = (filter) => {
      let visible = 0;
      cards.forEach((card) => {
        const show = filter === "all" || card.dataset.cycleFilterStatus === filter;
        card.hidden = !show;
        if (show) visible += 1;
      });
      campaignDetail.querySelectorAll("[data-cycle-rack]").forEach((rack) => {
        rack.hidden = !rack.querySelector("[data-cycle-location-card]:not([hidden])");
      });
      campaignDetail.querySelectorAll("[data-cycle-row]").forEach((row) => {
        row.hidden = !row.querySelector("[data-cycle-rack]:not([hidden])");
      });
      campaignDetail.querySelectorAll("[data-cycle-next-link]").forEach((link) => {
        link.hidden = filter !== "all" && filter !== "pending";
      });
      empty?.classList.toggle("d-none", visible !== 0);
      filterShell.querySelectorAll("[data-cycle-filter]").forEach((button) => {
        const active = button.dataset.cycleFilter === filter;
        button.setAttribute("aria-pressed", String(active));
        button.classList.toggle("btn-primary", active);
        button.classList.toggle("btn-outline-secondary", !active);
      });
    };
    filterShell?.classList.remove("d-none");
    filterShell?.addEventListener("click", (event) => {
      const button = event.target.closest("[data-cycle-filter]");
      if (button) applyFilter(button.dataset.cycleFilter);
    });
  }

  document.querySelectorAll("[data-cycle-review-decision]").forEach((section) => {
    const select = section.querySelector("[data-cycle-review-decision-select]");
    if (!select) return;
    const sync = () => {
      const approving = select.value === "Approve";
      section.querySelectorAll("[data-cycle-approval-field]").forEach((field) => {
        field.hidden = !approving;
        field.querySelectorAll("input, select, textarea").forEach((control) => { control.disabled = !approving; });
      });
    };
    select.addEventListener("change", sync);
    sync();
  });

  const preferredCameraStorageKey = "warehouseEpi.preferredCameraDeviceId";
  const cameraVideoConstraints = {
    width: { ideal: 1920 },
    height: { ideal: 1080 },
    frameRate: { ideal: 30 }
  };
  const supportedNativeBarcodeFormats = ["code_128", "ean_13", "ean_8", "upc_a", "upc_e"];
  const zxingTryHarderHint = 3;

  const readPreferredCameraDeviceId = () => {
    try { return window.localStorage.getItem(preferredCameraStorageKey); }
    catch { return null; }
  };

  const savePreferredCameraDeviceId = (deviceId) => {
    if (!deviceId) return;
    try { window.localStorage.setItem(preferredCameraStorageKey, deviceId); }
    catch { /* El almacenamiento puede no estar disponible en modo privado. */ }
  };

  const openCameraStream = async (requestedDeviceId) => {
    const preferredDeviceId = requestedDeviceId || readPreferredCameraDeviceId();
    const candidates = [];
    if (preferredDeviceId) candidates.push({ ...cameraVideoConstraints, deviceId: { exact: preferredDeviceId } });
    candidates.push({ ...cameraVideoConstraints, facingMode: { exact: "environment" } });
    candidates.push({ ...cameraVideoConstraints, facingMode: { ideal: "environment" } });

    let lastError;
    for (const video of candidates) {
      try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video });
        savePreferredCameraDeviceId(stream.getVideoTracks?.()[0]?.getSettings?.().deviceId);
        return stream;
      } catch (error) {
        lastError = error;
        if (["NotAllowedError", "SecurityError", "NotReadableError"].includes(error?.name)) throw error;
      }
    }
    throw lastError;
  };

  const availableVideoDevices = async () => {
    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices.filter(device => device.kind === "videoinput" && device.deviceId);
  };

  const nextCameraDeviceId = async (stream) => {
    const devices = await availableVideoDevices();
    if (devices.length < 2) return null;
    const currentDeviceId = stream?.getVideoTracks?.()[0]?.getSettings?.().deviceId;
    const currentIndex = devices.findIndex(device => device.deviceId === currentDeviceId);
    return devices[(currentIndex + 1 + devices.length) % devices.length].deviceId;
  };

  const scannerElement = document.querySelector("[data-cycle-camera-scanner]");
  let openCycleScanner = () => false;
  if (scannerElement && window.bootstrap) {
    const scannerModal = bootstrap.Modal.getOrCreateInstance(scannerElement);
    const scannerVideo = scannerElement.querySelector("[data-camera-video]");
    const scannerPreview = scannerElement.querySelector("[data-camera-preview]");
    const scannerStatus = scannerElement.querySelector("[data-camera-status]");
    const scannerPhoto = scannerElement.querySelector("[data-camera-photo]");
    const scannerSwitch = scannerElement.querySelector("[data-camera-switch]");
    let activeScanHandler;
    let scannerControls;
    let resolvingCameraCode = false;
    let focusAfterScannerClose;
    let triggerAfterScannerClose;
    let cameraSession = 0;

    const setScannerStatus = (message) => { scannerStatus.textContent = message; };

    const stopCamera = () => {
      cameraSession += 1;
      scannerControls?.stop();
      scannerControls = undefined;
      const stream = scannerVideo.srcObject;
      if (stream && typeof stream.getTracks === "function")
        stream.getTracks().forEach(track => track.stop());
      scannerVideo.srcObject = null;
    };

    const describeCameraError = (error) => {
      if (error?.name === "NotAllowedError" || error?.name === "SecurityError") return "No se concedió permiso para usar la cámara. Puedes tomar una foto, escribir o usar el lector físico.";
      if (error?.name === "NotFoundError") return "No se encontró una cámara disponible. Puedes tomar una foto, escribir o usar el lector físico.";
      if (error?.name === "NotReadableError") return "La cámara está ocupada por otra aplicación. Ciérrala e inténtalo nuevamente.";
      if (error?.name === "OverconstrainedError") return "La cámara no admite la configuración solicitada. Prueba con Tomar foto.";
      if (error === false) return "El navegador no pudo reproducir la vista previa. Cierra el lector e inténtalo nuevamente.";
      return "No fue posible iniciar la cámara. Prueba con Tomar foto, escribe o usa el lector físico.";
    };

    const isCodeNotDetectedError = (error) => {
      if (["NotFoundException", "ChecksumException", "FormatException"].includes(error?.name)) return true;
      return /No MultiFormat Readers were able to detect the code/i.test(error?.message || "");
    };

    const handleDetectedCode = async (code) => {
      if (resolvingCameraCode || !activeScanHandler) return;
      resolvingCameraCode = true;
      setScannerStatus("Código detectado. Validando…");
      try {
        const result = await activeScanHandler(code.trim());
        if (result === false) {
          setScannerStatus("El código no corresponde a esta captura. Intenta nuevamente.");
          resolvingCameraCode = false;
          return;
        }
        focusAfterScannerClose = result && typeof result.focus === "function" ? result : undefined;
        stopCamera();
        scannerModal.hide();
      } catch {
        setScannerStatus("No fue posible procesar el código. Intenta nuevamente.");
        resolvingCameraCode = false;
      }
    };

    const startNativeBarcodeScanner = async (session) => {
      if (typeof window.BarcodeDetector !== "function") return false;
      try {
        const availableFormats = await window.BarcodeDetector.getSupportedFormats();
        const formats = supportedNativeBarcodeFormats.filter(format => availableFormats.includes(format));
        if (formats.length === 0 || session !== cameraSession) return false;
        const detector = new window.BarcodeDetector({ formats });
        let stopped = false;
        scannerControls = { stop: () => { stopped = true; } };
        const scanNextFrame = async () => {
          if (stopped || resolvingCameraCode || session !== cameraSession) return;
          try {
            const [result] = await detector.detect(scannerVideo);
            if (result?.rawValue) {
              await handleDetectedCode(result.rawValue);
              if (!stopped && !resolvingCameraCode && session === cameraSession)
                window.setTimeout(() => void scanNextFrame(), 100);
              return;
            }
          } catch {
            // Una imagen puede no estar lista mientras la cámara enfoca; se prueba la siguiente.
          }
          if (!stopped && !resolvingCameraCode && session === cameraSession)
            window.setTimeout(() => void scanNextFrame(), 100);
        };
        void scanNextFrame();
        return true;
      } catch {
        return false;
      }
    };

    const optimizeCameraForBarcodes = async (stream) => {
      const track = stream.getVideoTracks?.()[0];
      if (!track?.getCapabilities || !track.applyConstraints) return;
      const capabilities = track.getCapabilities();
      if (!capabilities.focusMode?.includes("continuous")) return;
      try { await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] }); }
      catch { /* El enfoque continuo es opcional. */ }
    };

    const updateCameraSwitchButton = async (stream) => {
      if (!scannerSwitch || !navigator.mediaDevices?.enumerateDevices) return;
      try {
        const devices = await availableVideoDevices();
        scannerSwitch.classList.toggle("d-none", devices.length < 2);
        const currentDeviceId = stream.getVideoTracks?.()[0]?.getSettings?.().deviceId;
        const currentIndex = devices.findIndex(device => device.deviceId === currentDeviceId);
        scannerSwitch.title = `Cambiar cámara (${(currentIndex >= 0 ? currentIndex : 0) + 1} de ${devices.length})`;
      } catch {
        scannerSwitch.classList.add("d-none");
      }
    };

    const startCameraScanner = async (requestedDeviceId) => {
      const session = ++cameraSession;
      if (!window.isSecureContext) {
        scannerPreview.classList.add("d-none");
        setScannerStatus("La cámara requiere HTTPS. Puedes tomar una foto, escribir o usar el lector físico.");
        return;
      }
      if (!navigator.mediaDevices?.getUserMedia
        || (!window.ZXingBrowser && typeof window.BarcodeDetector !== "function")) {
        scannerPreview.classList.add("d-none");
        setScannerStatus("Este navegador no permite el lector en vivo. Puedes usar Tomar foto, escribir o usar el lector físico.");
        return;
      }

      scannerPreview.classList.remove("d-none");
      setScannerStatus("Solicitando la cámara trasera…");
      try {
        const stream = await openCameraStream(requestedDeviceId);
        if (session !== cameraSession) {
          stream.getTracks().forEach(track => track.stop());
          return;
        }
        await optimizeCameraForBarcodes(stream);
        scannerVideo.srcObject = stream;
        await scannerVideo.play();
        if (session !== cameraSession) return;
        await updateCameraSwitchButton(stream);
        setScannerStatus("Centra el código; la cámara permanecerá abierta hasta detectarlo o cancelar.");
        if (await startNativeBarcodeScanner(session)) return;

        const reader = new ZXingBrowser.BrowserMultiFormatReader();
        reader.possibleFormats = [
          ZXingBrowser.BarcodeFormat.CODE_128,
          ZXingBrowser.BarcodeFormat.EAN_13,
          ZXingBrowser.BarcodeFormat.EAN_8,
          ZXingBrowser.BarcodeFormat.UPC_A,
          ZXingBrowser.BarcodeFormat.UPC_E
        ];
        reader.hints.set(zxingTryHarderHint, true);
        reader.reader.setHints(reader.hints);
        scannerControls = await reader.decodeFromStream(stream, scannerVideo, async (result, error, controls) => {
          if (!scannerControls) scannerControls = controls;
          if (session !== cameraSession) return;
          if (result && !resolvingCameraCode) {
            await handleDetectedCode(result.getText());
            return;
          }
          if (error && !isCodeNotDetectedError(error)) {
            stopCamera();
            scannerPreview.classList.add("d-none");
            setScannerStatus(describeCameraError(error));
          }
        });
      } catch (error) {
        if (session !== cameraSession) return;
        stopCamera();
        scannerPreview.classList.add("d-none");
        setScannerStatus(describeCameraError(error));
      }
    };

    scannerPhoto.addEventListener("change", async () => {
      const [photo] = scannerPhoto.files;
      if (!photo || resolvingCameraCode) return;
      if (!window.ZXingBrowser) {
        setScannerStatus("No fue posible leer la foto. Puedes escribir o usar el lector físico.");
        return;
      }
      stopCamera();
      setScannerStatus("Leyendo el código de la foto…");
      const imageUrl = URL.createObjectURL(photo);
      try {
        const result = await new ZXingBrowser.BrowserMultiFormatReader().decodeFromImageUrl(imageUrl);
        await handleDetectedCode(result.getText());
      } catch {
        setScannerStatus("No se detectó un código de barras en la foto. Intenta nuevamente.");
      } finally {
        URL.revokeObjectURL(imageUrl);
        scannerPhoto.value = "";
      }
    });

    scannerSwitch?.addEventListener("click", async () => {
      scannerSwitch.disabled = true;
      setScannerStatus("Cambiando cámara…");
      try {
        const deviceId = await nextCameraDeviceId(scannerVideo.srcObject);
        if (!deviceId) return;
        stopCamera();
        await startCameraScanner(deviceId);
      } catch (error) {
        setScannerStatus(describeCameraError(error));
      } finally {
        scannerSwitch.disabled = false;
      }
    });

    openCycleScanner = (trigger, handler) => {
      activeScanHandler = handler;
      triggerAfterScannerClose = trigger;
      focusAfterScannerClose = undefined;
      resolvingCameraCode = false;
      scannerPreview.classList.remove("d-none");
      setScannerStatus("Preparando cámara…");
      scannerModal.show();
      void startCameraScanner();
      return true;
    };

    scannerElement.addEventListener("hidden.bs.modal", () => {
      const focusTarget = focusAfterScannerClose || triggerAfterScannerClose;
      stopCamera();
      scannerPhoto.value = "";
      activeScanHandler = undefined;
      resolvingCameraCode = false;
      focusAfterScannerClose = undefined;
      triggerAfterScannerClose = undefined;
      focusTarget?.focus();
    });
    window.addEventListener("pagehide", stopCamera, { once: true });
  }

  const locationButton = document.querySelector("[data-cycle-scan-location]");
  if (locationButton) {
    const form = locationButton.closest("form");
    const code = form.querySelector("[data-cycle-location-code]");
    locationButton.addEventListener("click", () => openCycleScanner(locationButton, value => {
      code.value = value;
      form.requestSubmit();
      return true;
    }));
  }
  const productButton = document.querySelector("[data-cycle-scan-product]");
  if (productButton) {
    const shell = productButton.closest("[data-cycle-count-capture]");
    productButton.addEventListener("click", () => openCycleScanner(productButton, value => {
      const row = [...shell.querySelectorAll("[data-cycle-product]")].find(item => item.dataset.cycleProduct.toUpperCase() === value.trim().toUpperCase());
      const target = row?.querySelector("[data-cycle-quantity]")
        || [...shell.querySelectorAll('input[name$=".Code"]')].find(input => !input.value.trim());
      if (!target) return false;
      if (!row) target.value = value;
      target.dispatchEvent(new Event("input", { bubbles: true }));
      return target;
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
  scanButton.className = "btn btn-outline-secondary d-inline-flex align-items-center gap-2";
  scanButton.append(cameraIcon(), "Escanear producto inesperado");
  scanButton.setAttribute("aria-describedby", "cycle-count-camera-status");

  const status = document.createElement("span");
  status.id = "cycle-count-camera-status";
  status.className = "small text-body-secondary";
  status.setAttribute("role", "status");
  status.setAttribute("aria-live", "polite");
  status.textContent = "También puedes usar un lector HID como teclado.";

  controls.append(addButton, scanButton, status);
  fieldset.insertBefore(controls, fieldset.querySelector(".row"));
  rows().forEach(addRemoveButton);
  renumber();

  scanButton.addEventListener("click", () => openCycleScanner(scanButton, value => {
    const target = codeInputs().find(input => !input.value.trim()) || codeInputs().at(-1);
    if (!target) return false;
    target.value = value;
    target.dispatchEvent(new Event("input", { bubbles: true }));
    target.dispatchEvent(new Event("change", { bubbles: true }));
    status.textContent = `Código ${target.value} capturado. Ingresa su cantidad física.`;
    return target;
  }));

  // ---- Borrador local -------------------------------------------------------------------
  // Las tablets son compartidas: el NIP nunca se guarda, el borrador caduca a las 12 horas y
  // sólo se restaura cuando el operador lo pide. El token de preparación tampoco se guarda:
  // el vigente es el que renderiza el servidor.
  const draftKey = `warehouseEpi.cycleCount.${capture.dataset.cycleCampaign}.${capture.dataset.cycleLocation}`;
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
  // El borrador sólo se elimina al volver a la campaña después de un POST exitoso.
  // Así sobrevive a errores de validación, red o concurrencia sin guardar el NIP.
})();
