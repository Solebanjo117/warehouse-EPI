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

    const remaining = campaignDetail.querySelector("[data-cycle-session-remaining]");
    if (remaining) {
      const expiresAt = Date.parse(remaining.dataset.cycleSessionExpires || "");
      const refreshRemaining = () => {
        const minutes = Math.max(0, Math.ceil((expiresAt - Date.now()) / 60000));
        remaining.textContent = minutes === 1 ? "1 minuto" : `${minutes} minutos`;
      };
      refreshRemaining();
      window.setInterval(refreshRemaining, 60000);
    }
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

  // ---- Planes recurrentes: búsqueda seleccionable, HID y cámara ------------------------
  document.querySelectorAll("[data-cycle-plan]").forEach((planForm, formIndex) => {
    const lookupUrl = planForm.dataset.lookupUrl;
    const fields = [];

    const requestJson = async (url, signal) => {
      const response = await fetch(url, { headers: { Accept: "application/json" }, signal });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    };

    const closeResults = (field) => {
      field.results.replaceChildren();
      field.highlighted = -1;
      field.input.setAttribute("aria-expanded", "false");
      field.input.removeAttribute("aria-activedescendant");
    };

    const announce = (field, message) => { field.feedback.textContent = message; };

    const clearSelection = (field, clearInput = false) => {
      field.id.value = "";
      field.selected.classList.add("d-none");
      field.selected.querySelector("[data-cycle-plan-selected-title]").textContent = "";
      field.selected.querySelector("[data-cycle-plan-selected-detail]").textContent = "";
      field.selected.querySelector("[data-cycle-plan-selected-meta]").textContent = "";
      if (clearInput) field.input.value = "";
      if (field.clear) field.clear.disabled = !field.input.value.trim();
    };

    const resultValues = (field, item) => field.type === "product"
      ? { id: item.id, title: item.sku, detail: item.description || "Sin descripción", meta: item.unitCode }
      : { id: item.id, title: item.code, detail: item.description || "Sin descripción", meta: "Ubicación" };

    const itemIsAllowed = (field, item) => !field.allowedIds
      || field.allowedIds.has(String(item.id).toLowerCase());

    const selectItem = (field, item, message) => {
      const values = resultValues(field, item);
      window.clearTimeout(field.timer);
      field.controller?.abort();
      field.id.value = values.id;
      field.input.value = values.title;
      field.input.setCustomValidity("");
      field.selected.querySelector("[data-cycle-plan-selected-title]").textContent = values.title;
      field.selected.querySelector("[data-cycle-plan-selected-detail]").textContent = values.detail;
      field.selected.querySelector("[data-cycle-plan-selected-meta]").textContent = values.meta;
      field.selected.classList.remove("d-none");
      if (field.clear) field.clear.disabled = false;
      closeResults(field);
      announce(field, message || `${field.label} seleccionado.`);
    };

    const setHighlight = (field, index) => {
      const options = [...field.results.querySelectorAll("[role='option']")];
      if (options.length === 0) return;
      field.highlighted = (index + options.length) % options.length;
      options.forEach((option, optionIndex) => {
        const active = optionIndex === field.highlighted;
        option.classList.toggle("active", active);
        option.setAttribute("aria-selected", String(active));
      });
      const active = options[field.highlighted];
      field.input.setAttribute("aria-activedescendant", active.id);
      active.scrollIntoView({ block: "nearest" });
    };

    const renderResults = (field, items) => {
      closeResults(field);
      items = items.filter(item => itemIsAllowed(field, item));
      if (items.length === 0) {
        announce(field, `No se encontraron ${field.type === "product" ? "productos" : "ubicaciones"}.`);
        return;
      }

      items.forEach((item, index) => {
        const values = resultValues(field, item);
        const option = document.createElement("button");
        option.type = "button";
        option.id = `cycle-plan-${formIndex}-${field.key}-option-${index}`;
        option.className = "list-group-item list-group-item-action";
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", "false");

        const title = document.createElement("strong");
        title.className = "d-block";
        title.textContent = values.title;
        const detail = document.createElement("span");
        detail.className = "small text-body-secondary";
        detail.textContent = field.type === "product" && item.externalReference
          ? `${values.detail} · Ref. ${item.externalReference}`
          : values.detail;
        option.append(title, detail);
        option.addEventListener("mousedown", event => event.preventDefault());
        option.addEventListener("click", () => {
          selectItem(field, item);
          field.input.focus();
        });
        field.results.append(option);
      });

      field.input.setAttribute("aria-expanded", "true");
      announce(field, `${items.length} ${items.length === 1 ? "resultado disponible" : "resultados disponibles"}.`);
    };

    const search = async (field) => {
      const query = field.input.value.trim();
      field.controller?.abort();
      if (!query) {
        closeResults(field);
        announce(field, "Escribe para buscar o usa un lector HID.");
        return;
      }

      field.controller = new AbortController();
      try {
        const handler = field.type === "product" ? "Products" : "Locations";
        const items = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, q: query })}`, field.controller.signal);
        renderResults(field, items);
      } catch (error) {
        if (error?.name === "AbortError") return;
        closeResults(field);
        announce(field, "No fue posible buscar en la red local. Intenta nuevamente.");
      }
    };

    const resolveCode = async (sourceField, code) => {
      if (!code.trim()) return false;
      announce(sourceField, "Validando código…");
      try {
        const resolution = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler: "ResolveCode", code })}`);
        const expected = resolution?.[sourceField.type];
        if (expected && itemIsAllowed(sourceField, expected)) {
          selectItem(sourceField, expected, `${sourceField.label} seleccionado por código.`);
          return sourceField.input;
        }
        if (expected) {
          announce(sourceField, `${sourceField.label} no está disponible para esta operación.`);
          return false;
        }

        const otherType = sourceField.type === "product" ? "location" : "product";
        const other = resolution?.[otherType];
        const target = fields.find(field => field.type === otherType);
        if (other && target) {
          selectItem(target, other, `${target.label} seleccionado por código.`);
          announce(sourceField, `El código corresponde a ${target.label.toLowerCase()}; se colocó en el campo correcto.`);
          return target.input;
        }

        announce(sourceField, `El código no corresponde a ${sourceField.label.toLowerCase()} activo.`);
        return false;
      } catch {
        announce(sourceField, "No fue posible validar el código en la red local. Intenta nuevamente.");
        return false;
      }
    };

    planForm.querySelectorAll("[data-cycle-plan-field]").forEach((element, fieldIndex) => {
      const type = element.dataset.cyclePlanField;
      const field = {
        type,
        key: element.dataset.cyclePlanKey || `${type}-${fieldIndex}`,
        label: element.dataset.cyclePlanLabel || (type === "product" ? "SKU" : "Ubicación"),
        optional: element.hasAttribute("data-cycle-plan-optional"),
        allowedIds: element.dataset.cyclePlanAllowedIds
          ? new Set(element.dataset.cyclePlanAllowedIds.split(",").map(value => value.trim().toLowerCase()).filter(Boolean))
          : undefined,
        element,
        input: element.querySelector("[data-cycle-plan-search]"),
        id: element.querySelector("[data-cycle-plan-id]"),
        results: element.querySelector("[data-cycle-plan-results]"),
        selected: element.querySelector("[data-cycle-plan-selected]"),
        feedback: element.querySelector("[data-cycle-plan-feedback]"),
        camera: element.querySelector("[data-cycle-plan-camera]"),
        clear: element.querySelector("[data-cycle-plan-clear]"),
        highlighted: -1,
        timer: 0,
        controller: undefined
      };
      fields.push(field);

      field.input.addEventListener("input", () => {
        field.controller?.abort();
        clearSelection(field);
        field.input.setCustomValidity("");
        if (field.clear) field.clear.disabled = !field.input.value.trim();
        window.clearTimeout(field.timer);
        field.timer = window.setTimeout(() => void search(field), 250);
      });
      field.input.addEventListener("focus", () => {
        if (!field.id.value && field.input.value.trim() && field.results.childElementCount === 0)
          void search(field);
      });
      field.input.addEventListener("keydown", (event) => {
        const options = [...field.results.querySelectorAll("[role='option']")];
        if (event.key === "ArrowDown" && options.length) {
          event.preventDefault();
          setHighlight(field, field.highlighted + 1);
        } else if (event.key === "ArrowUp" && options.length) {
          event.preventDefault();
          setHighlight(field, field.highlighted - 1);
        } else if (event.key === "Escape") {
          closeResults(field);
        } else if (event.key === "Enter") {
          event.preventDefault();
          if (field.highlighted >= 0) options[field.highlighted].click();
          else void resolveCode(field, field.input.value);
        }
      });
      field.input.addEventListener("blur", () => window.setTimeout(() => closeResults(field), 150));
      field.camera?.addEventListener("click", () => {
        if (!openCycleScanner(field.camera, value => resolveCode(field, value))) {
          announce(field, "No fue posible abrir el lector. Escribe el código o usa un lector HID.");
          field.input.focus();
        }
      });
      field.clear?.addEventListener("click", () => {
        field.controller?.abort();
        window.clearTimeout(field.timer);
        clearSelection(field, true);
        closeResults(field);
        field.input.setCustomValidity("");
        announce(field, `Sin ${field.label.toLowerCase()}.`);
        field.input.focus();
      });
    });

    document.addEventListener("click", (event) => {
      fields.forEach(field => {
        if (!field.element.contains(event.target)) closeResults(field);
      });
    });

    planForm.addEventListener("submit", (event) => {
      const missing = fields.find(field => !field.id.value && (!field.optional || field.input.value.trim()));
      if (!missing) return;
      event.preventDefault();
      missing.input.setCustomValidity(`Selecciona ${missing.label.toLowerCase()} de los resultados.`);
      announce(missing, `Selecciona ${missing.label.toLowerCase()} de los resultados antes de guardar.`);
      missing.input.reportValidity();
      missing.input.focus();
    });
  });

  const locationButton = document.querySelector("[data-cycle-scan-location]");
  if (locationButton) {
    const form = locationButton.closest("form");
    const code = form.querySelector("[data-cycle-location-code]");
    locationButton.addEventListener("click", () => openCycleScanner(locationButton, value => {
      code.value = value;
      form.requestSubmit();
      return true;
    }));
    if (campaignDetail?.dataset.cycleScanNext === "true") code.focus();
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
    form.dispatchEvent(new Event("change", { bubbles: true }));
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

  // ---- Estación guiada: progreso, ceros visibles, Enter y confirmación -----------------
  const steps = () => [...form.querySelectorAll("[data-cycle-product]")];
  const quantityInputs = () => [...form.querySelectorAll("[data-cycle-quantity]")];
  const emptyToggle = form.querySelector("[data-cycle-empty-location]");
  const progress = capture.querySelector("[data-cycle-progress]");
  const reviewButton = form.querySelector("[data-cycle-review]");

  const applyEmptyLocation = () => {
    const empty = Boolean(emptyToggle?.checked);
    quantityInputs().forEach((input) => {
      if (empty) {
        if (input.dataset.cyclePrevious === undefined) input.dataset.cyclePrevious = input.value;
        input.value = "0";
        input.readOnly = true;
      } else {
        if (input.dataset.cyclePrevious !== undefined) {
          input.value = input.dataset.cyclePrevious;
          delete input.dataset.cyclePrevious;
        }
        input.readOnly = false;
      }
    });
    steps().forEach(step => step.classList.toggle("is-empty-location", empty));
  };

  const countedTotal = () => quantityInputs().filter(input => input.value.trim() !== "").length;
  const refreshCaptureState = () => {
    const empty = Boolean(emptyToggle?.checked);
    steps().forEach((step) => {
      const input = step.querySelector("[data-cycle-quantity]");
      const filled = Boolean(input && input.value.trim() !== "");
      step.classList.toggle("is-counted", filled);
      const stepStatus = step.querySelector("[data-cycle-step-status]");
      if (stepStatus) stepStatus.textContent = empty ? "Vacío" : filled ? "Contado" : "Pendiente";
    });
    if (progress) progress.textContent = `${countedTotal()} de ${steps().length} contados`;
  };

  emptyToggle?.addEventListener("change", () => { applyEmptyLocation(); refreshCaptureState(); saveDraft(); });
  form.addEventListener("input", refreshCaptureState);
  form.addEventListener("change", () => { applyEmptyLocation(); refreshCaptureState(); });

  quantityInputs().forEach((input) => input.addEventListener("keydown", (event) => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    input.dispatchEvent(new Event("input", { bubbles: true }));
    const quantities = quantityInputs();
    const current = quantities.indexOf(input);
    const next = quantities.slice(current + 1).find(candidate => !candidate.value.trim() && !candidate.readOnly)
      || quantities.find(candidate => !candidate.value.trim() && !candidate.readOnly);
    (next || reviewButton)?.focus();
  }));

  fieldset.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" || !(event.target instanceof HTMLInputElement)) return;
    const row = event.target.closest("[data-cycle-unexpected-row]");
    if (!row) return;
    const code = row.querySelector('input[name$=".Code"]');
    const quantity = row.querySelector('input[name$=".Quantity"]');
    if (event.target === code && code.value.trim()) {
      event.preventDefault();
      quantity?.focus();
      return;
    }
    if (event.target === quantity) {
      event.preventDefault();
      const nextCode = rows().map(item => item.querySelector('input[name$=".Code"]')).find(candidate => candidate && !candidate.value.trim());
      (nextCode || reviewButton)?.focus();
    }
  });

  const pinInput = form.querySelector("[data-cycle-pin]");
  const modalElement = document.getElementById("confirm-cycle-count");
  if (reviewButton && modalElement && window.bootstrap) {
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    reviewButton.addEventListener("click", () => {
      if (pinInput) pinInput.required = false;
      if (!form.reportValidity()) { if (pinInput) pinInput.required = true; return; }
      if (pinInput) pinInput.required = true;
      const summary = form.querySelector("[data-cycle-confirmation]");
      if (summary) {
        const unexpected = codeInputs().filter(input => input.value.trim() !== "").length;
        summary.textContent = emptyToggle?.checked
          ? `Ubicación vacía · ${unexpected} producto(s) inesperado(s)`
          : `${countedTotal()} de ${steps().length} productos contados · ${unexpected} inesperado(s)`;
      }
      if (pinInput) modalElement.addEventListener("shown.bs.modal", () => pinInput.focus(), { once: true });
      modal.show();
    });
    form.addEventListener("submit", () => {
      const submit = form.querySelector("[data-cycle-submit]");
      if (submit) { submit.disabled = true; submit.textContent = "Registrando…"; }
    });
  }

  applyEmptyLocation();
  refreshCaptureState();
  capture.querySelector("[data-cycle-errors]")?.focus();
})();
