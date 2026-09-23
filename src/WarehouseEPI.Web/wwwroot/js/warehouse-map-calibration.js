(() => {
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  const root = document.querySelector("[data-calibration-editor]");
  if (!root) return;
  const form = root.closest("form");
  const svg = root.querySelector("[data-calibration-map]");
  const markerLayer = root.querySelector("[data-calibration-markers]");
  const list = root.querySelector("[data-calibration-points]");
  const status = root.querySelector("[data-calibration-status]");
  const instruction = root.querySelector("[data-calibration-instruction]");
  const jsonInput = form.querySelector("[data-calibration-json]");
  const width = Number(root.dataset.canvasWidth);
  const height = Number(root.dataset.canvasHeight);
  const draftKey = `warehouse-map-calibration:${root.dataset.user || "admin"}:${root.dataset.layoutVersion}`;
  const uuid = () => globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const read = value => {
    try {
      return (JSON.parse(value || "[]") || []).map(item => ({
        id: item.id || item.Id || uuid(), kind: (item.kind || item.Kind || "REFERENCE").toUpperCase(),
        name: item.name || item.Name || "", mapX: Number(item.mapX ?? item.MapX), mapY: Number(item.mapY ?? item.MapY),
        samples: (item.samples || item.Samples || []).map(sample => ({
          latitude: Number(sample.latitude ?? sample.Latitude), longitude: Number(sample.longitude ?? sample.Longitude),
          accuracy: Number(sample.accuracy ?? sample.Accuracy), timestamp: Number(sample.timestamp ?? sample.Timestamp)
        }))
      }));
    } catch { return []; }
  };
  if (root.dataset.clearDraft === "true") sessionStorage.removeItem(draftKey);
  let points = read(sessionStorage.getItem(draftKey) || jsonInput.value);
  let placingKind = null;
  let movingId = null;
  let capturingId = null;

  const sync = () => {
    jsonInput.value = JSON.stringify(points);
    sessionStorage.setItem(draftKey, jsonInput.value);
  };
  const announce = (message, kind = "info") => {
    status.className = `alert alert-${kind} py-2`;
    status.textContent = message;
  };
  const node = (name, attributes = {}, text = null) => {
    const item = document.createElement(name);
    Object.entries(attributes).forEach(([key, value]) => item.setAttribute(key, value));
    if (text !== null) item.textContent = text;
    return item;
  };
  const svgNode = (name, attributes = {}, text = null) => {
    const item = document.createElementNS("http://www.w3.org/2000/svg", name);
    Object.entries(attributes).forEach(([key, value]) => item.setAttribute(key, value));
    if (text !== null) item.textContent = text;
    return item;
  };
  const render = () => {
    markerLayer.replaceChildren();
    list.replaceChildren();
    points.forEach((point, index) => {
      const marker = svgNode("g", { class: `calibration-marker${point.kind === "CHECK" ? " is-check" : ""}`, transform: `translate(${point.mapX} ${point.mapY})` });
      marker.append(svgNode("circle", { r: "12" }), svgNode("text", { x: "17", y: "5" }, `${point.kind === "CHECK" ? "C" : "R"}${index + 1}`));
      markerLayer.append(marker);

      const card = node("article", { class: `calibration-point${movingId === point.id ? " is-selected" : ""}` });
      const heading = node("div", { class: "d-flex justify-content-between gap-2" });
      heading.append(node("strong", {}, point.kind === "CHECK" ? "Comprobación" : "Referencia"),
        node("span", { class: `badge ${point.samples.length ? "text-bg-success" : "text-bg-secondary"}` }, point.samples.length ? `${point.samples.length} lecturas` : "Sin capturar"));
      const label = node("label", { class: "form-label small mt-2 mb-1" }, "Nombre");
      const name = node("input", { class: "form-control", maxlength: "100", value: point.name });
      name.addEventListener("input", () => { point.name = name.value; sync(); });
      const coordinates = node("small", { class: "text-body-secondary d-block mt-2" }, `Croquis: ${point.mapX.toFixed(1)}, ${point.mapY.toFixed(1)}`);
      const actions = node("div", { class: "calibration-point-actions" });
      const capture = node("button", { type: "button", class: "btn btn-sm btn-primary" }, capturingId === point.id ? "Capturando…" : point.samples.length ? "Repetir captura" : "Capturar aquí");
      capture.disabled = capturingId !== null;
      capture.addEventListener("click", () => capturePoint(point));
      const move = node("button", { type: "button", class: "btn btn-sm btn-outline-secondary" }, "Mover en croquis");
      move.addEventListener("click", () => {
        movingId = point.id; placingKind = null; svg.classList.add("is-placing");
        instruction.textContent = text("Toca la nueva posición de {0} en el croquis.", point.name || text("este punto")); render();
      });
      const remove = node("button", { type: "button", class: "btn btn-sm btn-outline-danger" }, "Eliminar");
      remove.addEventListener("click", () => { points = points.filter(item => item.id !== point.id); if (movingId === point.id) movingId = null; sync(); render(); });
      actions.append(capture, move, remove);
      card.append(heading, label, name, coordinates, actions);
      list.append(card);
    });
    jsonInput.value = JSON.stringify(points);
  };
  const capturePoint = point => {
    if (!window.isSecureContext) { announce(text("Abre Warehouse EPI por HTTPS para usar la ubicación."), "danger"); return; }
    if (!navigator.geolocation) { announce(text("Este equipo o navegador no ofrece geolocalización."), "danger"); return; }
    capturingId = point.id;
    point.samples = [];
    const started = Date.now();
    let finished = false;
    let watchId;
    const finish = () => {
      if (finished) return;
      finished = true;
      if (watchId !== undefined) navigator.geolocation.clearWatch(watchId);
      clearInterval(progress);
      capturingId = null;
      if (point.samples.length) announce(text("{0}: {1} lecturas capturadas.", point.name || text("Punto"), point.samples.length), "success");
      else announce(text("No se recibió ninguna lectura. Comprueba el permiso y repite la captura."), "warning");
      sync(); render();
    };
    const progress = setInterval(() => {
      const remaining = Math.max(0, Math.ceil((20000 - (Date.now() - started)) / 1000));
      announce(text("Capturando {0}: {1} lecturas, {2} s restantes.", point.name || text("Punto").toLocaleLowerCase(document.documentElement.lang), point.samples.length, remaining));
      if (remaining === 0) finish();
    }, 500);
    watchId = navigator.geolocation.watchPosition(position => {
      point.samples.push({ latitude: position.coords.latitude, longitude: position.coords.longitude,
        accuracy: position.coords.accuracy, timestamp: position.timestamp });
    }, error => {
      if (error.code === error.PERMISSION_DENIED) { finish(); announce(text("Permiso de ubicación denegado. Habilítalo en el navegador y vuelve a intentar."), "danger"); }
      else announce(text("Buscando ubicación… Mantén el equipo en este punto."), "warning");
    }, { enableHighAccuracy: true, maximumAge: 0, timeout: 20000 });
    render();
  };

  root.querySelectorAll("[data-add-calibration-point]").forEach(button => button.addEventListener("click", () => {
    placingKind = button.dataset.addCalibrationPoint;
    movingId = null;
    svg.classList.add("is-placing");
    instruction.textContent = text("Toca en el croquis dónde está la {0}.", placingKind === "CHECK" ? text("comprobación") : text("referencia"));
  }));
  svg.addEventListener("click", event => {
    if (!placingKind && !movingId) return;
    const screenPoint = svg.createSVGPoint(); screenPoint.x = event.clientX; screenPoint.y = event.clientY;
    const location = screenPoint.matrixTransform(svg.getScreenCTM().inverse());
    const mapX = Math.max(0, Math.min(width, location.x));
    const mapY = Math.max(0, Math.min(height, location.y));
    if (movingId) {
      const point = points.find(item => item.id === movingId);
      if (point) { point.mapX = mapX; point.mapY = mapY; }
      movingId = null;
    } else {
      const count = points.filter(item => item.kind === placingKind).length + 1;
      points.push({ id: uuid(), kind: placingKind, name: `${placingKind === "CHECK" ? "Comprobación" : "Referencia"} ${count}`, mapX, mapY, samples: [] });
      placingKind = null;
    }
    svg.classList.remove("is-placing");
    instruction.textContent = text("Elige un tipo y toca su posición exacta en el croquis.");
    sync(); render();
  });
  root.querySelector("[data-discard-calibration]").addEventListener("click", () => {
    points = [];
    sessionStorage.removeItem(draftKey);
    jsonInput.value = "[]";
    announce(text("Borrador descartado."));
    render();
  });
  form.addEventListener("submit", sync);
  render();
})();
