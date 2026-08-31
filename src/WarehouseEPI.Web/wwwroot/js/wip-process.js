(() => {
  "use strict";

  const shell = document.querySelector("[data-wip-process]");
  if (!shell) return;

  const debounce = (callback, delay = 250) => {
    let timer;
    return (...args) => {
      clearTimeout(timer);
      timer = window.setTimeout(() => callback(...args), delay);
    };
  };

  const requestJson = async (url) => {
    try {
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      if (!response.ok) return { ok: false, status: response.status };
      return { ok: true, data: await response.json() };
    } catch {
      return { ok: false, status: 0 };
    }
  };

  const lookupUrl = shell.dataset.lookupUrl;
  const form = shell.querySelector("[data-wip-process-form]");
  const destination = shell.querySelector("[data-wip-destination]");
  const destinationInput = destination.querySelector("input");
  const referenceHint = shell.querySelector("[data-reference-required]");
  const productInput = shell.querySelector("[data-wip-product-search]");
  const productResults = shell.querySelector("[data-wip-product-results]");
  const productFeedback = shell.querySelector("[data-wip-product-feedback]");
  const quantityInput = shell.querySelector('[name="Input.Quantity"]');
  const referenceInput = shell.querySelector('[name="Input.Reference"]');

  const describeProduct = (item) => [item.description, item.externalReference, item.unitCode]
    .filter(Boolean).join(" · ");

  const setProductFeedback = (message, isError = false) => {
    productFeedback.textContent = message;
    productFeedback.classList.toggle("text-danger", isError);
  };

  const focusNext = (input) => {
    const kind = input?.dataset.wipLookup;
    const next = kind === "wip" ? productInput
      : kind === "product" ? quantityInput
        : kind === "destination" ? referenceInput : null;
    next?.focus();
    if (next === quantityInput) next.select();
  };

  const applyResolvedItem = (input, item) => {
    const kind = input.dataset.wipLookup;
    input.value = kind === "product" ? item.sku : item.code;
    input.setCustomValidity("");
    input.dispatchEvent(new Event("change", { bubbles: true }));
    if (kind === "product") {
      productResults.replaceChildren();
      setProductFeedback(`Producto seleccionado: ${item.sku}${item.description ? ` · ${item.description}` : ""}`);
    }
  };

  const invalidMessage = (kind, resolution) => {
    if (kind === "product") {
      return resolution?.location
        ? "El código corresponde a una ubicación, no a un producto."
        : "No se encontró un producto activo con ese código.";
    }
    if (kind === "wip") {
      return resolution?.location
        ? "La ubicación detectada no es un WIP disponible."
        : resolution?.product
          ? "El código corresponde a un producto, no a un WIP."
          : "No se encontró una ubicación WIP con ese código.";
    }
    return resolution?.location?.isWip
      ? "El regreso a bodega requiere una ubicación destino que no sea WIP."
      : resolution?.product
        ? "El código corresponde a un producto, no a una ubicación."
        : "No se encontró una ubicación destino con ese código.";
  };

  const resolveTargetCode = async (input, reportInvalidity = false) => {
    const code = input.value.trim();
    if (!code) return { selected: false, message: "Escanea o escribe un código." };

    const response = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "ResolveCode", code })}`);
    if (!response.ok) {
      const message = response.status === 0
        ? "No fue posible validar en la red local. Intenta nuevamente."
        : "No fue posible validar el código. Intenta nuevamente.";
      input.setCustomValidity(message);
      if (reportInvalidity) input.reportValidity();
      if (input === productInput) setProductFeedback(message, true);
      return { selected: false, message };
    }

    const resolution = response.data;
    const kind = input.dataset.wipLookup;
    const item = kind === "product" ? resolution.product
      : kind === "wip" && resolution.location?.isWip === true ? resolution.location
        : kind === "destination" && resolution.location && resolution.location.isWip !== true
          ? resolution.location : null;
    if (!item) {
      const message = invalidMessage(kind, resolution);
      input.setCustomValidity(message);
      if (reportInvalidity) input.reportValidity();
      if (input === productInput) setProductFeedback(message, true);
      return { selected: false, message };
    }

    applyResolvedItem(input, item);
    return { selected: true, message: "" };
  };

  let productSearchSequence = 0;
  const searchProducts = debounce(async (sequence) => {
    if (sequence !== productSearchSequence) return;
    const query = productInput.value.trim();
    if (!query) {
      productResults.replaceChildren();
      setProductFeedback("");
      return;
    }

    const response = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "Products", q: query })}`);
    if (sequence !== productSearchSequence) return;
    productResults.replaceChildren();
    if (!response.ok) {
      setProductFeedback("No fue posible buscar productos en la red local.", true);
      return;
    }

    const items = response.data || [];
    if (items.length === 0) {
      setProductFeedback("No se encontraron productos activos.");
      return;
    }

    setProductFeedback("Selecciona un producto de la lista.");
    for (const item of items) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "list-group-item list-group-item-action";
      const title = document.createElement("strong");
      title.textContent = item.sku;
      const detail = document.createElement("small");
      detail.className = "d-block text-muted";
      detail.textContent = describeProduct(item);
      button.append(title, detail);
      button.addEventListener("click", () => {
        productSearchSequence++;
        applyResolvedItem(productInput, item);
        focusNext(productInput);
      });
      productResults.append(button);
    }
  });

  productInput.addEventListener("input", () => {
    productInput.setCustomValidity("");
    productSearchSequence++;
    searchProducts(productSearchSequence);
  });

  shell.querySelectorAll("[data-wip-lookup]").forEach((input) => {
    input.addEventListener("input", () => input.setCustomValidity(""));
    input.addEventListener("keydown", async (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      if (input === productInput) {
        productSearchSequence++;
        productResults.replaceChildren();
      }
      const result = await resolveTargetCode(input, true);
      if (result.selected) focusNext(input);
    });
  });

  const preferredCameraStorageKey = "warehouseEpi.preferredCameraDeviceId";
  const cameraVideoConstraints = {
    width: { ideal: 1920 },
    height: { ideal: 1080 },
    frameRate: { ideal: 30 }
  };

  const readPreferredCameraDeviceId = () => {
    try { return window.localStorage.getItem(preferredCameraStorageKey); }
    catch { return null; }
  };

  const savePreferredCameraDeviceId = (deviceId) => {
    if (!deviceId) return;
    try { window.localStorage.setItem(preferredCameraStorageKey, deviceId); }
    catch { /* Storage can be unavailable in private or restricted browser modes. */ }
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

  const scannerElement = shell.querySelector("[data-camera-scanner]");
  const scannerModal = bootstrap.Modal.getOrCreateInstance(scannerElement);
  const scannerVideo = scannerElement.querySelector("[data-camera-video]");
  const scannerPreview = scannerElement.querySelector("[data-camera-preview]");
  const scannerStatus = scannerElement.querySelector("[data-camera-status]");
  const scannerPhoto = scannerElement.querySelector("[data-camera-photo]");
  const scannerSwitch = scannerElement.querySelector("[data-camera-switch]");
  let activeCameraTarget;
  let scannerControls;
  let resolvingCameraCode = false;
  let focusNextAfterClose = false;
  let cameraSession = 0;

  const setScannerStatus = (message) => { scannerStatus.textContent = message; };

  const stopCamera = () => {
    scannerControls?.stop();
    scannerControls = undefined;
    const stream = scannerVideo.srcObject;
    if (stream && typeof stream.getTracks === "function")
      stream.getTracks().forEach(track => track.stop());
    scannerVideo.srcObject = null;
  };

  const describeCameraError = (error) => {
    if (error?.name === "NotAllowedError") return "No se concedió permiso para usar la cámara. Puedes escribir o usar el lector físico.";
    if (error?.name === "NotFoundError") return "No se encontró una cámara disponible. Puedes escribir o usar el lector físico.";
    if (error?.name === "NotReadableError") return "La cámara está ocupada por otra aplicación. Ciérrala e inténtalo nuevamente.";
    if (error?.name === "OverconstrainedError") return "La cámara no admite la configuración solicitada. Prueba con Tomar foto.";
    return "No fue posible iniciar la cámara. Prueba con Tomar foto, escribe o usa el lector físico.";
  };

  const isCodeNotDetectedError = (error) => ["NotFoundException", "ChecksumException", "FormatException"].includes(error?.name)
    || /No MultiFormat Readers were able to detect the code/i.test(error?.message || "");

  const resolveCameraCode = async (code) => {
    if (!activeCameraTarget || resolvingCameraCode) return;
    resolvingCameraCode = true;
    activeCameraTarget.value = code;
    activeCameraTarget.setCustomValidity("");
    if (activeCameraTarget === productInput) {
      productSearchSequence++;
      productResults.replaceChildren();
    }
    setScannerStatus("Código detectado. Validando…");
    const result = await resolveTargetCode(activeCameraTarget, false);
    if (result.selected) {
      focusNextAfterClose = true;
      stopCamera();
      scannerModal.hide();
      return;
    }
    setScannerStatus(result.message || "No se encontró un registro operativo con ese código. Intenta nuevamente.");
    resolvingCameraCode = false;
  };

  const startNativeBarcodeScanner = async () => {
    if (typeof window.BarcodeDetector !== "function" || typeof window.BarcodeDetector.getSupportedFormats !== "function") return false;
    try {
      const availableFormats = await window.BarcodeDetector.getSupportedFormats();
      const formats = ["code_128", "ean_13", "ean_8", "upc_a", "upc_e"].filter(format => availableFormats.includes(format));
      if (formats.length === 0) return false;
      const detector = new window.BarcodeDetector({ formats });
      let stopped = false;
      scannerControls = { stop: () => { stopped = true; } };
      const scanNextFrame = async () => {
        if (stopped || resolvingCameraCode) return;
        try {
          const [result] = await detector.detect(scannerVideo);
          if (result?.rawValue) {
            await resolveCameraCode(result.rawValue);
            return;
          }
        } catch {
          // A frame without a readable code is normal while the camera focuses.
        }
        if (!stopped && !resolvingCameraCode) window.setTimeout(() => void scanNextFrame(), 100);
      };
      void scanNextFrame();
      return true;
    } catch {
      return false;
    }
  };

  const updateCameraSwitchButton = async (stream) => {
    if (!navigator.mediaDevices?.enumerateDevices) return;
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

  const optimizeCameraForBarcodes = async (stream) => {
    const track = stream.getVideoTracks?.()[0];
    if (!track?.getCapabilities || !track.applyConstraints) return;
    const capabilities = track.getCapabilities();
    if (!capabilities.focusMode?.includes("continuous")) return;
    try { await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] }); }
    catch { /* Continuous focus is optional. */ }
  };

  const startCameraScanner = async (requestedDeviceId, session = cameraSession) => {
    if (!window.isSecureContext) {
      scannerPreview.classList.add("d-none");
      setScannerStatus("La cámara requiere HTTPS. Puedes escribir o usar el lector físico.");
      return;
    }
    if (!navigator.mediaDevices?.getUserMedia
      || (!window.ZXingBrowser && typeof window.BarcodeDetector !== "function")) {
      scannerPreview.classList.add("d-none");
      setScannerStatus("Este navegador no permite usar la cámara. Puedes escribir o usar el lector físico.");
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
      if (session !== cameraSession) {
        stream.getTracks().forEach(track => track.stop());
        return;
      }
      scannerVideo.srcObject = stream;
      await scannerVideo.play();
      if (session !== cameraSession) {
        stream.getTracks().forEach(track => track.stop());
        scannerVideo.srcObject = null;
        return;
      }
      await updateCameraSwitchButton(stream);
      setScannerStatus("Centra el código; para etiquetas largas, acércalo y espera a que enfoque.");
      if (await startNativeBarcodeScanner()) return;
      if (!window.ZXingBrowser) throw new Error("No barcode reader available");

      const reader = new ZXingBrowser.BrowserMultiFormatReader();
      reader.possibleFormats = [
        ZXingBrowser.BarcodeFormat.CODE_128,
        ZXingBrowser.BarcodeFormat.EAN_13,
        ZXingBrowser.BarcodeFormat.EAN_8,
        ZXingBrowser.BarcodeFormat.UPC_A,
        ZXingBrowser.BarcodeFormat.UPC_E
      ];
      reader.hints.set(3, true);
      reader.reader.setHints(reader.hints);
      scannerControls = await reader.decodeFromStream(stream, scannerVideo, async (result, error, controls) => {
        if (!scannerControls) scannerControls = controls;
        if (result && !resolvingCameraCode) {
          await resolveCameraCode(result.getText());
          return;
        }
        if (error && !isCodeNotDetectedError(error)) {
          stopCamera();
          scannerPreview.classList.add("d-none");
          setScannerStatus(describeCameraError(error));
        }
      });
    } catch (error) {
      stopCamera();
      scannerPreview.classList.add("d-none");
      setScannerStatus(describeCameraError(error));
    }
  };

  shell.querySelectorAll("[data-wip-camera]").forEach((button) => {
    button.addEventListener("click", () => {
      activeCameraTarget = document.getElementById(button.dataset.wipCamera);
      resolvingCameraCode = false;
      focusNextAfterClose = false;
      scannerPreview.classList.remove("d-none");
      setScannerStatus("Preparando cámara…");
      scannerModal.show();
      const session = ++cameraSession;
      void startCameraScanner(undefined, session);
    });
  });

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
      const reader = new ZXingBrowser.BrowserMultiFormatReader();
      const result = await reader.decodeFromImageUrl(imageUrl);
      await resolveCameraCode(result.getText());
    } catch {
      setScannerStatus("No se detectó un código de barras en la foto. Intenta nuevamente.");
    } finally {
      URL.revokeObjectURL(imageUrl);
      scannerPhoto.value = "";
    }
  });

  scannerSwitch.addEventListener("click", async () => {
    scannerSwitch.disabled = true;
    setScannerStatus("Cambiando cámara…");
    try {
      const currentSession = cameraSession;
      const deviceId = await nextCameraDeviceId(scannerVideo.srcObject);
      if (!deviceId || currentSession !== cameraSession) return;
      stopCamera();
      const session = ++cameraSession;
      await startCameraScanner(deviceId, session);
    } catch (error) {
      setScannerStatus(describeCameraError(error));
    } finally {
      scannerSwitch.disabled = false;
    }
  });

  scannerElement.addEventListener("shown.bs.modal", () => document.body.classList.add("camera-active"));
  scannerElement.addEventListener("hidden.bs.modal", () => {
    const target = activeCameraTarget;
    const shouldFocusNext = focusNextAfterClose;
    cameraSession++;
    stopCamera();
    scannerPhoto.value = "";
    document.body.classList.remove("camera-active");
    activeCameraTarget = undefined;
    resolvingCameraCode = false;
    focusNextAfterClose = false;
    if (shouldFocusNext) focusNext(target);
    else target?.focus();
  });
  window.addEventListener("pagehide", () => {
    cameraSession++;
    stopCamera();
  }, { once: true });

  const refresh = () => {
    const action = shell.querySelector('input[name="Input.Action"]:checked')?.value;
    const returning = action === "WarehouseReturn";
    destination.classList.toggle("d-none", !returning);
    destinationInput.required = returning;
    if (!returning) destinationInput.setCustomValidity("");
    referenceHint.textContent = action === "SupplierReturn" ? "(obligatoria)" : "(opcional)";
  };

  shell.querySelectorAll('input[name="Input.Action"]').forEach((item) => item.addEventListener("change", refresh));
  form.addEventListener("submit", (event) => {
    if (event.submitter?.matches("[data-wip-confirm]")) return;
    event.preventDefault();
    shell.querySelector("[data-wip-review]").click();
  });

  shell.querySelector("[data-wip-review]")?.addEventListener("click", () => {
    if (!form.reportValidity()) return;
    const action = shell.querySelector('input[name="Input.Action"]:checked')?.nextElementSibling?.querySelector("strong")?.textContent ?? "WIP";
    const wip = shell.querySelector('[name="Input.WipCode"]')?.value || "Sin WIP";
    const product = shell.querySelector('[name="Input.ProductCode"]')?.value || "Sin producto";
    const quantity = quantityInput?.value || "0";
    const target = destinationInput?.value;
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
