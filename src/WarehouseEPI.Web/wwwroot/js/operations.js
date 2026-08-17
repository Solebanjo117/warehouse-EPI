(() => {
  const debounce = (callback, delay = 250) => {
    let timer;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => callback(...args), delay);
    };
  };

  const requestJson = async (url) => {
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (!response.ok) return null;
    return response.json();
  };

  const describeProduct = (item) => [item.description, item.externalReference, item.unitCode]
    .filter(Boolean).join(" · ");
  const describeLocation = (item) => item.description || "Ubicación operativa";

  const renderSuggestions = (container, items, kind, select) => {
    container.replaceChildren();
    for (const item of items || []) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "list-group-item list-group-item-action";
      const title = document.createElement("strong");
      title.textContent = kind === "product" ? item.sku : item.code;
      const detail = document.createElement("small");
      detail.className = "d-block text-muted";
      detail.textContent = kind === "product" ? describeProduct(item) : describeLocation(item);
      button.append(title, detail);
      button.addEventListener("click", () => select(item));
      container.append(button);
    }
  };

  const operationShell = document.querySelector("[data-operation]");
  if (operationShell) {
    const form = operationShell.querySelector("[data-operation-form]");
    const lookupUrl = operationShell.dataset.lookupUrl;
    const operation = operationShell.dataset.operation;
    const quantityInput = operationShell.querySelector("[data-quantity]");
    const unitLabel = operationShell.querySelector("[data-unit-label]");
    const balancePreview = operationShell.querySelector("[data-balance-preview]");
    const balanceText = operationShell.querySelector("[data-balance-text]");
    const negativeWarning = operationShell.querySelector("[data-negative-warning]");
    const versionInput = operationShell.querySelector("[data-balance-version]");
    const selected = {};
    const lookups = {};
    let productLocations = null;
    const locationProducts = {};

    const hiddenFor = (kind) => operationShell.querySelector(`[data-selected-id="${kind}"]`);
    const primaryLocationKind = operation === "entry" ? "destination"
      : operation === "exit" || operation === "transfer" ? "source"
        : "location";
    const requiredKinds = operation === "entry" ? ["product", "destination"]
      : operation === "exit" ? ["product", "source"]
        : operation === "transfer" ? ["product", "source", "destination"]
          : ["product", "location"];

    const number = (value) => {
      const parsed = Number.parseFloat(String(value || "0").replace(",", "."));
      return Number.isFinite(parsed) ? parsed : 0;
    };
    const format = (value) => new Intl.NumberFormat("es-MX", { maximumFractionDigits: 4 }).format(value);

    const refreshPreview = () => {
      const quantity = number(quantityInput.value);
      const source = number(balancePreview.dataset.source);
      const destination = number(balancePreview.dataset.destination);
      const location = number(balancePreview.dataset.location);
      let message = "Selecciona producto y ubicación";
      let isNegative = false;

      if (operation === "entry" && selected.product && selected.destination) {
        const result = destination + quantity;
        message = `Destino: ${format(destination)} → ${format(result)}`;
        isNegative = result < 0;
      } else if (operation === "exit" && selected.product && selected.source) {
        const result = source - quantity;
        message = `Origen: ${format(source)} → ${format(result)}`;
        isNegative = result < 0;
      } else if (operation === "transfer" && selected.product && selected.source && selected.destination) {
        const sourceResult = source - quantity;
        const destinationResult = destination + quantity;
        message = `Origen ${format(source)} → ${format(sourceResult)} · Destino ${format(destination)} → ${format(destinationResult)}`;
        isNegative = sourceResult < 0 || destinationResult < 0;
      } else if (operation === "adjustment" && selected.product && selected.location) {
        const delta = quantity - location;
        message = `Actual: ${format(location)} · Diferencia: ${delta > 0 ? "+" : ""}${format(delta)} · Final: ${format(quantity)}`;
        isNegative = quantity < 0;
      }

      balanceText.textContent = message;
      negativeWarning.classList.toggle("d-none", !isNegative);
    };

    const refreshBalance = async (kind) => {
      if (!selected.product || !selected[kind]) return;
      const params = new URLSearchParams({
        handler: "Balance",
        productId: selected.product.id,
        locationId: selected[kind].id
      });
      const balance = await requestJson(`${lookupUrl}?${params}`);
      if (!balance) return;
      balancePreview.dataset[kind] = balance.quantity;
      if (kind === "location" && versionInput) versionInput.value = balance.version;
      refreshPreview();
    };

    const relationshipMeta = (item) => {
      const quantity = format(number(item.quantity));
      if (item.hasActiveAssignment && item.hasNonZeroBalance) return `Asignación activa · saldo ${quantity}`;
      if (item.hasActiveAssignment) return "Asignación activa · saldo 0";
      return `Con saldo ${quantity}`;
    };

    const addRelationshipMessage = (panel, text, className = "text-muted") => {
      const message = document.createElement("p");
      message.className = `relationship-message ${className}`;
      message.textContent = text;
      panel.append(message);
    };

    const renderRelationshipChoices = (panel, titleText, items, kind, onSelect, selectedId) => {
      panel.replaceChildren();
      panel.classList.remove("d-none");
      const title = document.createElement("strong");
      title.className = "relationship-title";
      title.textContent = titleText;
      panel.append(title);
      const choices = document.createElement("div");
      choices.className = "relationship-choices";
      for (const item of items) {
        const choice = document.createElement(onSelect ? "button" : "div");
        if (onSelect) choice.type = "button";
        choice.className = `relationship-choice${item.id === selectedId ? " is-selected" : ""}`;
        const name = document.createElement("strong");
        name.textContent = kind === "product" ? item.sku : item.code;
        const detail = document.createElement("small");
        detail.textContent = relationshipMeta(item);
        choice.append(name, detail);
        if (onSelect) choice.addEventListener("click", () => onSelect(item));
        choices.append(choice);
      }
      panel.append(choices);
    };

    const renderProductRelationships = () => {
      const panel = lookups.product?.panel;
      if (!panel || productLocations === null) return;
      if (productLocations.length === 0) {
        panel.replaceChildren();
        panel.classList.remove("d-none");
        addRelationshipMessage(panel,
          "Sin ubicaciones asociadas ni saldo. Escanea una ubicación; se asociará al confirmar.");
        return;
      }

      const selectedLocation = selected[primaryLocationKind];
      renderRelationshipChoices(
        panel,
        productLocations.length === 1 ? "Ubicación relacionada" : "Ubicaciones relacionadas; elige una",
        productLocations,
        "location",
        (item) => void applySelection(primaryLocationKind, item, true),
        selectedLocation?.id);
      if (selectedLocation && !productLocations.some(item => item.id === selectedLocation.id))
        addRelationshipMessage(panel, "Esta pareja se asociará al confirmar con NIP.", "text-primary");
    };

    const canSelectProductFrom = (kind) => !(operation === "transfer" && kind === "destination");

    const renderLocationRelationships = (kind) => {
      const panel = lookups[kind]?.panel;
      const items = locationProducts[kind];
      if (!panel || items === undefined) return;
      if (items.length === 0) {
        panel.replaceChildren();
        panel.classList.remove("d-none");
        addRelationshipMessage(panel,
          "Sin productos asociados ni saldo. El producto se asociará al confirmar.");
        return;
      }

      const selectable = canSelectProductFrom(kind);
      renderRelationshipChoices(
        panel,
        items.length === 1 ? "Producto relacionado" : "Productos relacionados; elige uno",
        items,
        "product",
        selectable ? (item) => void applySelection("product", item, true) : null,
        selected.product?.id);
      if (selected.product && !items.some(item => item.id === selected.product.id))
        addRelationshipMessage(panel, "El producto seleccionado se asociará aquí al confirmar con NIP.", "text-primary");
      if (!selectable)
        addRelationshipMessage(panel, "El destino es informativo y no cambia el producto de la transferencia.");
    };

    const refreshRelationshipPanels = () => {
      renderProductRelationships();
      for (const kind of ["source", "destination", "location"])
        renderLocationRelationships(kind);
    };

    const loadProductLocations = async () => {
      if (!selected.product) return;
      const productId = selected.product.id;
      const params = new URLSearchParams({ handler: "ProductLocations", productId });
      const items = await requestJson(`${lookupUrl}?${params}`);
      if (selected.product?.id !== productId || !items) return;
      productLocations = items;
      renderProductRelationships();
      if (!selected[primaryLocationKind] && items.length === 1)
        await applySelection(primaryLocationKind, items[0], true);
    };

    const loadLocationProducts = async (kind) => {
      if (!selected[kind]) return;
      const locationId = selected[kind].id;
      const params = new URLSearchParams({ handler: "LocationProducts", locationId });
      const items = await requestJson(`${lookupUrl}?${params}`);
      if (selected[kind]?.id !== locationId || !items) return;
      locationProducts[kind] = items;
      renderLocationRelationships(kind);
      if (canSelectProductFrom(kind) && !selected.product && items.length === 1)
        await applySelection("product", items[0], true);
    };

    const applySelection = async (kind, item, loadRelationships) => {
      const lookup = lookups[kind];
      const lookupKind = kind === "product" ? "product" : "location";
      selected[kind] = item;
      lookup.hidden.value = item.id;
      lookup.input.value = lookupKind === "product" ? item.sku : item.code;
      lookup.input.setCustomValidity("");
      lookup.record.querySelector("[data-selected-title]").textContent = lookup.input.value;
      lookup.record.querySelector("[data-selected-detail]").textContent = lookupKind === "product"
        ? describeProduct(item) : describeLocation(item);
      if (lookupKind === "product") {
        lookup.record.dataset.unit = item.unitCode;
        lookup.record.dataset.allowsDecimals = item.allowsDecimals;
        unitLabel.textContent = item.unitCode;
        quantityInput.step = item.allowsDecimals ? "0.0001" : "1";
      }
      lookup.record.classList.remove("d-none");
      lookup.results.replaceChildren();

      if (lookupKind === "product") {
        for (const locationKind of ["source", "destination", "location"])
          void refreshBalance(locationKind);
        if (loadRelationships) await loadProductLocations();
      } else {
        void refreshBalance(kind);
        if (loadRelationships) await loadLocationProducts(kind);
      }
      refreshRelationshipPanels();
      refreshPreview();
    };

    const clearSelection = (kind) => {
      const lookup = lookups[kind];
      selected[kind] = null;
      lookup.hidden.value = "";
      lookup.record.classList.add("d-none");
      lookup.input.setCustomValidity("");
      lookup.panel.classList.add("d-none");
      lookup.panel.replaceChildren();
      if (kind === "product") {
        productLocations = null;
        unitLabel.textContent = "Unidad";
      } else {
        locationProducts[kind] = undefined;
        balancePreview.dataset[kind] = "0";
        if (kind === "location" && versionInput) versionInput.value = "0";
      }
      refreshRelationshipPanels();
      refreshPreview();
    };

    const focusNextRequired = () => {
      const missing = requiredKinds.find(kind => !selected[kind]);
      if (missing) {
        const relationshipButton = lookups[missing]?.panel.querySelector("button.relationship-choice");
        (relationshipButton || lookups[missing]?.input)?.focus();
        return;
      }
      quantityInput.focus();
    };

    const setupLookup = (field) => {
      const kind = field.dataset.lookupField;
      const lookupKind = kind === "product" ? "product" : "location";
      const input = field.querySelector("[data-lookup-input]");
      const results = field.querySelector("[data-lookup-results]");
      const record = field.querySelector("[data-selected-record]");
      const hidden = hiddenFor(kind);
      const panel = field.querySelector("[data-relationship-panel]");
      let searchSequence = 0;
      lookups[kind] = { field, input, results, record, hidden, panel };

      if (hidden.value) {
        selected[kind] = {
          id: hidden.value,
          sku: kind === "product" ? input.value : undefined,
          code: kind !== "product" ? input.value : undefined,
          unitCode: record.dataset.unit,
          allowsDecimals: record.dataset.allowsDecimals === "true",
          tracksLots: record.dataset.tracksLots === "true"
        };
      }

      const search = debounce(async (sequence) => {
        if (sequence !== searchSequence) return;
        const query = input.value.trim();
        if (!query) return results.replaceChildren();
        const handler = lookupKind === "product" ? "Products" : "Locations";
        const items = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, q: query })}`);
        if (sequence !== searchSequence) return;
        renderSuggestions(results, items, lookupKind, (item) => {
          searchSequence++;
          void applySelection(kind, item, true).then(focusNextRequired);
        });
      });

      input.addEventListener("input", () => {
        clearSelection(kind);
        searchSequence++;
        search(searchSequence);
      });
      input.addEventListener("keydown", async (event) => {
        if (event.key !== "Enter") return;
        event.preventDefault();
        searchSequence++;
        results.replaceChildren();
        await resolveLookupCode(kind, input.value, true);
      });
    };

    const resolveLookupCode = async (kind, value, reportInvalidity) => {
      const lookup = lookups[kind];
      const code = value.trim();
      if (!code) return false;
      const lookupKind = kind === "product" ? "product" : "location";
      const handler = lookupKind === "product" ? "ResolveProduct" : "ResolveLocation";
      const item = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, code })}`);
      if (!item) {
        lookup.input.setCustomValidity("No se encontró un registro operativo con ese código.");
        if (reportInvalidity) lookup.input.reportValidity();
        return false;
      }

      await applySelection(kind, item, true);
      focusNextRequired();
      return true;
    };

    const scannerElement = operationShell.querySelector("[data-camera-scanner]");
    if (scannerElement) {
      const scannerModal = bootstrap.Modal.getOrCreateInstance(scannerElement);
      const scannerVideo = scannerElement.querySelector("[data-camera-video]");
      const scannerPreview = scannerElement.querySelector("[data-camera-preview]");
      const scannerStatus = scannerElement.querySelector("[data-camera-status]");
      const scannerPhoto = scannerElement.querySelector("[data-camera-photo]");
      let activeScannerLookup;
      let scannerControls;
      let resolvingCameraCode = false;
      let focusAfterScannerClose = false;
      const supportedCameraBarcodeFormats = [
        ZXingBrowser.BarcodeFormat.CODE_128,
        ZXingBrowser.BarcodeFormat.EAN_13,
        ZXingBrowser.BarcodeFormat.EAN_8,
        ZXingBrowser.BarcodeFormat.UPC_A,
        ZXingBrowser.BarcodeFormat.UPC_E
      ];
      const supportedNativeBarcodeFormats = ["code_128", "ean_13", "ean_8", "upc_a", "upc_e"];
      const zxingTryHarderHint = 3;

      const setScannerStatus = (message) => {
        scannerStatus.textContent = message;
      };

      const stopCamera = () => {
        scannerControls?.stop();
        scannerControls = undefined;
        const stream = scannerVideo.srcObject;
        if (stream && typeof stream.getTracks === "function")
          stream.getTracks().forEach(track => track.stop());
        scannerVideo.srcObject = null;
      };

      const describeCameraError = (error) => {
        if (error?.name === "NotAllowedError")
          return "No se concedió permiso para usar la cámara. Puedes escribir o usar el escáner físico.";
        if (error?.name === "NotFoundError")
          return "No se encontró una cámara disponible. Puedes escribir o usar el escáner físico.";
        if (error?.name === "NotReadableError")
          return "La cámara está ocupada por otra aplicación. Ciérrala e inténtalo nuevamente.";
        if (error?.name === "OverconstrainedError")
          return "La cámara no admite la configuración solicitada. Prueba con Tomar foto.";
        if (error === false)
          return "El navegador no pudo reproducir la vista previa. Cierra el modal e inténtalo nuevamente.";
        const detail = typeof error?.message === "string" && error.message.trim()
          ? ` ${error.message.trim()}`
          : "";
        return `No fue posible iniciar la cámara (${error?.name || "error desconocido"}).${detail} Prueba con Tomar foto, escribe o usa el escáner físico.`;
      };

      const isCodeNotDetectedError = (error) => {
        if (["NotFoundException", "ChecksumException", "FormatException"].includes(error?.name)) return true;

        // The bundled production build minifies ZXing exception class names (for example, to "e").
        // Its message still identifies the normal condition where the current video frame has no barcode.
        return /No MultiFormat Readers were able to detect the code/i.test(error?.message || "");
      };

      const resolveCameraCode = async (code) => {
        resolvingCameraCode = true;
        const lookup = lookups[activeScannerLookup];
        clearSelection(activeScannerLookup);
        lookup.input.value = code;
        setScannerStatus("Código detectado. Validando…");
        try {
          const found = await resolveLookupCode(activeScannerLookup, code, false);
          if (found) {
            focusAfterScannerClose = true;
            stopCamera();
            scannerModal.hide();
            return;
          }

          setScannerStatus("No se encontró un registro operativo con ese código. Intenta nuevamente.");
        } catch {
          setScannerStatus("No fue posible validar el código. Intenta nuevamente.");
        }
        resolvingCameraCode = false;
      };

      const startNativeBarcodeScanner = async () => {
        if (typeof window.BarcodeDetector !== "function") return false;

        try {
          const availableFormats = await window.BarcodeDetector.getSupportedFormats();
          const formats = supportedNativeBarcodeFormats.filter(format => availableFormats.includes(format));
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
              // A frame can be unavailable while the camera adjusts focus; retry the next one.
            }

            if (!stopped && !resolvingCameraCode)
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

        try {
          await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] });
        } catch {
          // Continuous focus is an optional camera capability; the default focus remains usable.
        }
      };

      const startCameraScanner = async () => {
        if (!window.isSecureContext) {
          scannerPreview.classList.add("d-none");
          setScannerStatus("La cámara requiere HTTPS. Puedes escribir o usar el escáner físico.");
          return;
        }
        if (!navigator.mediaDevices?.getUserMedia
          || (!window.ZXingBrowser && typeof window.BarcodeDetector !== "function")) {
          scannerPreview.classList.add("d-none");
          setScannerStatus("Este navegador no permite usar la cámara. Puedes escribir o usar el escáner físico.");
          return;
        }

        scannerPreview.classList.remove("d-none");
        setScannerStatus("Solicitando la cámara trasera…");
        try {
          // Open the device first. This keeps the permission request tied to the user's button tap
          // and avoids relying on the decoder to create a second, hidden camera request on Android.
          const stream = await navigator.mediaDevices.getUserMedia({
            audio: false,
            video: {
              facingMode: { ideal: "environment" },
              width: { ideal: 1920 },
              height: { ideal: 1080 },
              frameRate: { ideal: 30 }
            }
          });
          await optimizeCameraForBarcodes(stream);
          scannerVideo.srcObject = stream;
          await scannerVideo.play();
          setScannerStatus("Centra el código; para etiquetas largas, acércalo y espera a que enfoque.");

          if (await startNativeBarcodeScanner()) return;

          const reader = new ZXingBrowser.BrowserMultiFormatReader();
          reader.possibleFormats = supportedCameraBarcodeFormats;
          // DecodeHintType.TRY_HARDER is not exported by the browser bundle (its stable enum value is 3).
          // It samples more scan lines, which is important for dense, long Code 128 labels.
          reader.hints.set(zxingTryHarderHint, true);
          reader.reader.setHints(reader.hints);
          scannerControls = await reader.decodeFromStream(
            stream,
            scannerVideo,
            async (result, error, controls) => {
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

      scannerPhoto.addEventListener("change", async () => {
        const [photo] = scannerPhoto.files;
        if (!photo || resolvingCameraCode) return;
        if (!window.ZXingBrowser) {
          setScannerStatus("No fue posible leer la foto. Puedes escribir o usar el escáner físico.");
          return;
        }

        stopCamera();
          setScannerStatus("Leyendo el código de la foto…");
          const imageUrl = URL.createObjectURL(photo);
          try {
            const reader = new ZXingBrowser.BrowserMultiFormatReader();
          reader.possibleFormats = supportedCameraBarcodeFormats;
          const result = await reader.decodeFromImageUrl(imageUrl);
          await resolveCameraCode(result.getText());
        } catch {
          setScannerStatus("No se detectó un código de barras en la foto. Intenta nuevamente.");
        } finally {
          URL.revokeObjectURL(imageUrl);
          scannerPhoto.value = "";
        }
      });

      operationShell.querySelectorAll("[data-camera-scan]").forEach(button => {
        button.addEventListener("click", () => {
          activeScannerLookup = button.closest("[data-lookup-field]").dataset.lookupField;
          resolvingCameraCode = false;
          focusAfterScannerClose = false;
          scannerPreview.classList.remove("d-none");
          setScannerStatus("Preparando cámara…");
          scannerModal.show();
          void startCameraScanner();
        });
      });

      scannerElement.addEventListener("hidden.bs.modal", () => {
        const shouldFocusNext = focusAfterScannerClose;
        stopCamera();
        scannerPhoto.value = "";
        activeScannerLookup = undefined;
        resolvingCameraCode = false;
        focusAfterScannerClose = false;
        if (shouldFocusNext) focusNextRequired();
      });
      window.addEventListener("pagehide", stopCamera, { once: true });
    }

    operationShell.querySelectorAll("[data-lookup-field]").forEach(setupLookup);
    for (const kind of Object.keys(lookups)) {
      if (!selected[kind]) continue;
      if (kind === "product") void loadProductLocations();
      else void loadLocationProducts(kind);
    }
    quantityInput.addEventListener("input", refreshPreview);
    refreshPreview();

    const reviewButton = operationShell.querySelector("[data-review-button]");
    const modalElement = document.getElementById("confirm-operation");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    reviewButton.addEventListener("click", () => {
      const pinInput = operationShell.querySelector("[data-pin-input]");
      pinInput.required = false;
      for (const kind of requiredKinds) {
        if (!hiddenFor(kind).value) {
          const input = operationShell.querySelector(`[data-lookup-field="${kind}"] [data-lookup-input]`);
          input.setCustomValidity("Selecciona un registro de la lista o escanea un código válido.");
          input.reportValidity();
          return;
        }
      }
      if (!form.reportValidity()) return;
      const productCode = operationShell.querySelector('[data-lookup-field="product"] [data-lookup-input]').value;
      const locations = requiredKinds.filter(kind => kind !== "product")
        .map(kind => operationShell.querySelector(`[data-lookup-field="${kind}"] [data-lookup-input]`).value)
        .join(" → ");
      const summary = operationShell.querySelector("[data-confirmation-summary]");
      summary.replaceChildren();
      const productLine = document.createElement("strong");
      productLine.textContent = productCode;
      const details = document.createElement("span");
      details.textContent = `${locations} · ${quantityInput.value} ${unitLabel.textContent}`;
      const balance = document.createElement("small");
      balance.textContent = balanceText.textContent;
      summary.append(productLine, details, balance);
      pinInput.required = true;
      modal.show();
      modalElement.addEventListener("shown.bs.modal", () => pinInput.focus(), { once: true });
    });

    form.addEventListener("submit", () => {
      const button = operationShell.querySelector("[data-submit-button]");
      button.disabled = true;
      button.textContent = "Confirmando…";
    });
  }

  const inventoryShell = document.querySelector("[data-inventory-query]");
  if (inventoryShell) {
    const lookupUrl = inventoryShell.dataset.lookupUrl;
    inventoryShell.querySelectorAll("[data-query-form]").forEach((form) => {
      const kind = form.dataset.queryForm;
      const input = form.querySelector("[data-query-input]");
      const hidden = form.querySelector("[data-query-selected-id]");
      const results = form.querySelector("[data-query-results]");
      const search = debounce(async () => {
        const query = input.value.trim();
        if (!query) return results.replaceChildren();
        const handler = kind === "product" ? "Products" : "Locations";
        const items = await requestJson(`${lookupUrl}?${new URLSearchParams({ handler, q: query })}`);
        renderSuggestions(results, items, kind, (item) => {
          hidden.value = item.id;
          input.value = kind === "product" ? item.sku : item.code;
          results.replaceChildren();
        });
      });
      input.addEventListener("input", () => {
        hidden.value = "";
        search();
      });
    });
  }
})();
