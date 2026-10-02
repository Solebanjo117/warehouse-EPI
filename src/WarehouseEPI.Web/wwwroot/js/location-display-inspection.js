(() => {
  window.WarehouseDisplayInspection = {
    create(root, pause) {
      const panel = root.querySelector("[data-display-search-panel]");
      if (!panel) return null;
      const texts = JSON.parse(root.dataset.uiTexts || "{}");
      const translate = (key, ...args) => (texts[key] || key).replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match);
      const input = panel.querySelector("[data-display-search-input]");
      const options = panel.querySelector("[data-display-product-options]");
      const selectedProduct = panel.querySelector("[data-display-selected-product]");
      const locationList = panel.querySelector("[data-display-product-locations]");
      const message = panel.querySelector("[data-display-search-status]");
      const openButton = root.querySelector("[data-display-search-open]");
      const holder = root.querySelector("[data-display-slides]");
      const inspection = root.querySelector("[data-display-inspection]");
      const stage = root.querySelector(".display-stage");
      const originalSplit = stage.classList.contains("display-stage-split");
      const status = root.querySelector("[data-display-status]");
      const updated = root.querySelector("[data-display-updated]");
      const live = root.querySelector("[data-display-live]");
      const map = root.querySelector("[data-display-map-svg]");
      const caption = root.querySelector("[data-display-map-caption]");
      const elements = [...(map?.querySelectorAll("[data-display-map-select]") || [])];
      const requests = new Map();
      const failures = new Set();
      const number = new Intl.NumberFormat(document.documentElement.lang || "es", { maximumFractionDigits: 4 });
      let active = false, epoch = 0, product = null, target = null, locationData = null, lastLocations = "", lastInspection = "";
      let searchTimer = null, activeOption = -1;
      const node = (tag, value, className) => {
        const element = document.createElement(tag);
        if (value != null) element.textContent = value;
        if (className) element.className = className;
        return element;
      };
      function begin() {
        if (!active) {
          active = true;
          root.dataset.displayConsulting = "true";
          pause();
        }
        consultationStatus();
      }
      function consultationStatus() {
        status.textContent = failures.size ? translate("No se pudo actualizar · se muestra la última consulta") : translate("Consulta pausada");
      }
      function abort(kind) {
        requests.get(kind)?.controller.abort();
        requests.delete(kind);
      }
      async function request(kind, parameters, read) {
        abort(kind);
        const entry = { controller: new AbortController(), epoch };
        requests.set(kind, entry);
        const timeout = window.setTimeout(() => entry.controller.abort(), 8000);
        try {
          const url = new URL("/Locations/Display", window.location.href);
          for (const [key, value] of Object.entries(parameters)) url.searchParams.set(key, value);
          const response = await fetch(url, { cache: "no-store", signal: entry.controller.signal });
          if (!response.ok) throw new Error(response.status === 404 ? "missing" : "network");
          const value = await read(response);
          if (!active || epoch !== entry.epoch || requests.get(kind) !== entry) return undefined;
          failures.delete(kind);
          return value;
        } catch (error) {
          if (active && epoch === entry.epoch && requests.get(kind) === entry) {
            const key = error.message === "missing" ? "La ubicación ya no está disponible. Selecciona otra ubicación."
              : "No se pudo consultar. Vuelve a intentarlo.";
            message.textContent = translate(key);
            failures.add(kind);
            consultationStatus();
          }
        } finally {
          window.clearTimeout(timeout);
          if (requests.get(kind) === entry) requests.delete(kind);
        }
      }
      const matches = (element, location) => location.kind === "Rack"
        ? element.dataset.mapRow === location.rowCode && element.dataset.mapRackNumber === String(location.rackNumber)
        : element.dataset.mapLocation === location.locationId;
      function highlight() {
        const productLegend = root.querySelector("[data-display-product-legend]");
        const assignmentLegend = root.querySelector("[data-display-assignment-legend]");
        if (productLegend) productLegend.hidden = !product;
        if (assignmentLegend) assignmentLegend.hidden = !(locationData?.locations || []).some(location => !location.hasNonZeroBalance);
        for (const element of elements) {
          const locations = (locationData?.locations || []).filter(location => matches(element, location));
          element.classList.toggle("is-product-match", locations.length > 0);
          element.classList.toggle("is-assigned-match", locations.length > 0 && locations.every(location => !location.hasNonZeroBalance));
          const selected = target && (target.locationId
            ? locationData?.locations?.find(location => location.locationId === target.locationId) ||
              (target.rowCode ? { kind: "Rack", rowCode: target.rowCode, rackNumber: target.rackNumber } : null)
            : { kind: "Rack", rowCode: target.rowCode, rackNumber: target.rackNumber });
          const chosen = !!selected && matches(element, selected) || !!target?.locationId && element.dataset.mapLocation === target.locationId;
          element.classList.toggle("is-current", !!chosen);
          element.setAttribute("aria-pressed", String(!!chosen));
        }
        if (target?.rowCode) map?.querySelectorAll("[data-map-row]").forEach(element =>
          element.classList.toggle("is-row", element.dataset.mapRow === target.rowCode));
        if (map && product) map.setAttribute("aria-label", translate("Ubicaciones de {0}", product.sku));
        else if (map && target?.rowCode) map.setAttribute("aria-label", translate("Croquis del almacén con la fila {0} resaltada", target.rowCode));
        inspection.querySelectorAll("[data-display-location]").forEach(element => {
          const selected = !!target?.locationId && element.dataset.displayLocation === target.locationId;
          element.classList.toggle("is-selected-location", selected);
          if (selected) element.setAttribute("aria-label", translate("Ubicación seleccionada"));
        });
        inspection.querySelectorAll("[data-display-product]").forEach(element =>
          element.classList.toggle("is-selected-product", element.dataset.displayProduct === product?.id));
      }
      function open() {
        begin();
        panel.hidden = false;
        openButton.setAttribute("aria-expanded", "true");
        if (!message.textContent) message.textContent = translate("Escribe o escanea para ver sugerencias.");
        input.focus();
      }
      function close() {
        cancelSuggestions();
        panel.hidden = true;
        openButton.setAttribute("aria-expanded", "false");
        openButton.focus();
      }
      async function inspect(parameters, focus = true) {
        const moveFocus = focus && document.activeElement?.matches(":focus-visible");
        if (focus) {
          failures.clear();
          begin();
          target = parameters;
          message.textContent = translate("Consultando ubicación…");
          live.textContent = message.textContent;
          highlight();
        }
        const html = await request("inspect", { handler: "Inspect", ...parameters }, response => response.text());
        if (html === undefined) return;
        const content = new DOMParser().parseFromString(html, "text/html").querySelector("[data-display-inspection-content]");
        if (!content) { status.textContent = translate("No se pudo consultar. Vuelve a intentarlo."); return; }
        const restoreFocus = inspection.contains(document.activeElement);
        if (lastInspection !== content.innerHTML) {
          lastInspection = content.innerHTML;
          inspection.replaceChildren(content);
        }
        inspection.firstElementChild.dataset.inspectionUpdated = content.dataset.inspectionUpdated;
        if (target?.locationId && content.dataset.inspectionRow) target = { ...target,
          rowCode: content.dataset.inspectionRow, rackNumber: content.dataset.inspectionRack };
        holder.hidden = true;
        inspection.hidden = false;
        root.dataset.displayInspecting = "true";
        stage.classList.toggle("display-stage-split", !!map);
        const heading = inspection.querySelector("[data-display-inspection-heading]");
        if (moveFocus || restoreFocus) heading?.focus();
        if (focus) {
          live.textContent = heading?.textContent || "";
        }
        if (caption) caption.textContent = heading?.textContent || "";
        updated.textContent = content.dataset.inspectionUpdated;
        consultationStatus();
        if (focus) message.textContent = "";
        highlight();
      }
      function renderLocations(data) {
        locationData = data;
        const signature = JSON.stringify({ product: data.product, locations: data.locations });
        if (signature !== lastLocations) {
          const focused = document.activeElement?.dataset.displayLocationResult;
          lastLocations = signature;
          locationList.replaceChildren(node("h3", translate("Ubicaciones de {0}", data.product.sku), "h5 mt-3"));
          if (!data.product.isActive) locationList.append(node("p", translate("Producto inactivo")));
          if (!data.locations.length) locationList.append(node("p", translate("Sin ubicaciones con saldo ni asignaciones activas.")));
          for (const withStock of [true, false]) {
            const rows = data.locations.filter(location => location.hasNonZeroBalance === withStock);
            if (!rows.length) continue;
            locationList.append(node("h4", withStock ? translate("Con saldo") : translate("Asignado sin saldo"), "h6 mt-3"));
            const list = node("div", null, "display-location-results");
            for (const location of rows) {
              const button = node("button", null, "btn btn-outline-secondary display-location-result");
              button.type = "button";
              button.dataset.displayLocationResult = location.locationId;
              button.append(node("strong", location.code), node("span", `${number.format(location.quantity)} ${location.unit}`));
              if (location.description) button.append(node("small", location.description));
              if (location.quantity < 0) button.append(node("small", translate("Saldo negativo"), "text-danger"));
              if (!location.isActive) button.append(node("small", translate("Inactiva")));
              if (location.isBlocked) button.append(node("small", translate("Bloqueada")));
              if (location.isWip) button.append(node("small", "WIP"));
              if (!elements.some(element => matches(element, location))) button.append(node("small", translate("Ubicación no visible en el croquis")));
              button.addEventListener("click", () => void inspect({ locationId: location.locationId }));
              list.append(button);
            }
            locationList.append(list);
          }
          if (focused) [...locationList.querySelectorAll("[data-display-location-result]")].find(element => element.dataset.displayLocationResult === focused)?.focus();
        }
        if (!target) updated.textContent = data.updatedAt;
        highlight();
      }
      async function loadLocations(id, initial = false) {
        const data = await request("locations", { handler: "ProductLocations", productId: id }, response => response.json());
        if (data === undefined) return;
        renderLocations(data);
        consultationStatus();
        if (initial || !target) message.textContent = data.updatedAt;
      }
      async function selectProduct(item) {
        cancelSuggestions();
        failures.clear();
        abort("locations");
        begin();
        product = item;
        input.value = item.sku;
        selectedProduct.replaceChildren(node("strong", item.sku), node("span", item.description || translate("Sin descripción")), node("small", item.unitCode));
        selectedProduct.hidden = false;
        locationData = null;
        lastLocations = "";
        locationList.replaceChildren();
        highlight();
        message.textContent = translate("Cargando ubicaciones…");
        await loadLocations(item.id, true);
      }
      function closeSuggestions() {
        options.replaceChildren();
        activeOption = -1;
        input.setAttribute("aria-expanded", "false");
        input.removeAttribute("aria-activedescendant");
      }
      function cancelSuggestions() {
        window.clearTimeout(searchTimer);
        searchTimer = null;
        abort("products");
        closeSuggestions();
      }
      function activateOption(index) {
        const buttons = [...options.children];
        if (!buttons.length) return;
        activeOption = (index + buttons.length) % buttons.length;
        buttons.forEach((button, position) => {
          button.classList.toggle("active", position === activeOption);
          button.setAttribute("aria-selected", String(position === activeOption));
        });
        input.setAttribute("aria-activedescendant", buttons[activeOption].id);
        buttons[activeOption].scrollIntoView({ block: "nearest" });
      }
      async function searchProducts(selectSingle) {
        begin();
        cancelSuggestions();
        const q = input.value.trim();
        if (!q) { message.textContent = translate("Escribe o escanea para ver sugerencias."); return; }
        message.textContent = translate("Buscando productos…");
        const items = await request("products", { handler: "Products", q }, response => response.json());
        if (items === undefined) return;
        consultationStatus();
        if (!items.length) { message.textContent = translate("No se encontraron productos. Prueba otro código o descripción."); return; }
        if (selectSingle && items.length === 1) { await selectProduct(items[0]); return; }
        message.textContent = items.length >= 5 ? translate("Mostrando hasta cinco productos. Precisa la búsqueda si falta el que necesitas.") : translate("Usa flechas y Enter para elegir.");
        for (const [index, item] of items.entries()) {
          const button = node("button", null, "list-group-item list-group-item-action");
          button.type = "button";
          button.id = `display-product-option-${index}`;
          button.setAttribute("role", "option");
          button.setAttribute("aria-selected", "false");
          button.append(node("strong", item.sku), node("small", item.description || translate("Sin descripción"), "d-block text-body-secondary"));
          if (item.externalReference) button.append(node("small", item.externalReference, "d-block text-body-secondary"));
          if (!item.isActive) button.append(node("small", translate("Producto inactivo"), "d-block text-body-secondary"));
          button.addEventListener("mousedown", event => event.preventDefault());
          button.addEventListener("click", () => void selectProduct(item));
          options.append(button);
        }
        input.setAttribute("aria-expanded", "true");
      }
      panel.querySelector("[data-display-search-form]").addEventListener("submit", event => {
        event.preventDefault();
        void searchProducts(true);
      });
      input.addEventListener("input", () => {
        cancelSuggestions();
        if (!input.value.trim()) { message.textContent = ""; return; }
        message.textContent = translate("Buscando productos…");
        searchTimer = window.setTimeout(() => void searchProducts(false), 250);
      });
      input.addEventListener("keydown", event => {
        if (event.key === "ArrowDown" && options.children.length) { event.preventDefault(); activateOption(activeOption + 1); }
        else if (event.key === "ArrowUp" && options.children.length) { event.preventDefault(); activateOption(activeOption < 0 ? -1 : activeOption - 1); }
        else if (event.key === "Enter" && activeOption >= 0) { event.preventDefault(); options.children[activeOption]?.click(); }
        else if (event.key === "Escape" && options.children.length) { event.preventDefault(); event.stopPropagation(); cancelSuggestions(); }
      });
      panel.addEventListener("focusout", () => window.setTimeout(() => {
        if (!panel.querySelector("[data-display-search-form]").contains(document.activeElement)) cancelSuggestions();
      }, 100));
      for (const element of elements) {
        const select = () => {
          const parameters = element.dataset.mapRackNumber ? { rowCode: element.dataset.mapRow, rackNumber: element.dataset.mapRackNumber }
            : element.dataset.mapLocation ? { locationId: element.dataset.mapLocation } : null;
          if (parameters) void inspect(parameters);
        };
        element.addEventListener("click", select);
        element.addEventListener("keydown", event => {
          if (event.key === "Enter" || event.key === " ") { event.preventDefault(); event.stopPropagation(); select(); }
        });
      }
      openButton.addEventListener("click", open);
      panel.querySelector("[data-display-search-close]").addEventListener("click", close);
      panel.addEventListener("keydown", event => { if (event.key === "Escape") { event.preventDefault(); close(); } });
      return {
        get active() { return active; },
        refresh() {
          if (!active) return;
          if (product && !requests.has("locations")) void loadLocations(product.id);
          if (target && !requests.has("inspect")) void inspect(target, false);
        },
        resume() {
          epoch++;
          cancelSuggestions();
          failures.clear();
          for (const kind of [...requests.keys()]) abort(kind);
          active = false; product = null; target = null; locationData = null; lastLocations = "";
          delete root.dataset.displayConsulting;
          delete root.dataset.displayInspecting;
          stage.classList.toggle("display-stage-split", originalSplit);
          panel.hidden = true; inspection.hidden = true; holder.hidden = false;
          openButton.setAttribute("aria-expanded", "false");
          lastInspection = "";
          inspection.replaceChildren(); locationList.replaceChildren(); selectedProduct.replaceChildren(); selectedProduct.hidden = true;
          input.value = ""; message.textContent = "";
          highlight();
          elements.forEach(element => {
            element.classList.remove("is-product-match", "is-assigned-match");
            element.setAttribute("aria-pressed", "false");
          });
        }
      };
    }
  };
})();
