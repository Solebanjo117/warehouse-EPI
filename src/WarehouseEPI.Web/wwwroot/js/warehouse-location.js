(() => {
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (!mapRoot) return;
  const frame = mapRoot.closest("[data-map-frame]");
  const startButton = frame.querySelector("[data-location-start]");
  const centerButton = frame.querySelector("[data-location-center]");
  const stopButton = frame.querySelector("[data-location-stop]");
  const status = frame.querySelector("[data-location-status]");
  const layer = mapRoot.querySelector("[data-location-layer]");
  const accuracyCircle = mapRoot.querySelector("[data-location-accuracy]");
  const dot = mapRoot.querySelector("[data-location-dot]");
  if (!startButton || !centerButton || !stopButton || !status || !layer || !accuracyCircle || !dot) return;
  const canvasWidth = Number(mapRoot.dataset.canvasWidth);
  const canvasHeight = Number(mapRoot.dataset.canvasHeight);
  const earthRadius = 6378137;
  let calibration = null;
  let watchId = null;
  let wanted = false;
  let lastPosition = null;
  let firstPosition = true;
  let revisionTimer = null;

  const show = (message, kind = "secondary") => {
    status.className = `small mb-2 alert alert-${kind} py-2`;
    status.textContent = message;
  };
  const setButtons = active => {
    startButton.classList.toggle("d-none", active);
    centerButton.classList.toggle("d-none", !active);
    stopButton.classList.toggle("d-none", !active);
  };
  const loadCalibration = async () => {
    const response = await fetch("/Locations/MapCalibration", { credentials: "same-origin", cache: "no-store" });
    if (!response.ok) throw new Error("calibration");
    return response.json();
  };
  const project = position => {
    const transform = calibration.transform;
    const radians = Math.PI / 180;
    const localX = earthRadius * (position.coords.longitude - transform.originLongitude) * radians
      * Math.cos(transform.originLatitude * radians);
    const localY = earthRadius * (position.coords.latitude - transform.originLatitude) * radians;
    return {
      x: transform.a11 * localX + transform.a12 * localY + transform.a13,
      y: transform.a21 * localX + transform.a22 * localY + transform.a23,
      radius: (Math.max(0, position.coords.accuracy) + Math.max(0, transform.checkErrorMeters))
        * transform.maximumScaleSvgPerMeter
    };
  };
  const renderPosition = (position, center = false) => {
    const point = project(position);
    lastPosition = { position, point };
    accuracyCircle.setAttribute("cx", point.x); accuracyCircle.setAttribute("cy", point.y);
    accuracyCircle.setAttribute("r", point.radius);
    dot.setAttribute("cx", point.x); dot.setAttribute("cy", point.y);
    layer.classList.remove("d-none");
    const age = Math.max(0, Math.round((Date.now() - position.timestamp) / 1000));
    const outside = point.x < 0 || point.y < 0 || point.x > canvasWidth || point.y > canvasHeight;
    show(outside
      ? text("La ubicación estimada está fuera del croquis. Precisión reportada: {0} m.", Math.round(position.coords.accuracy))
      : text("Ubicación aproximada · precisión reportada {0} m · hace {1} s.", Math.round(position.coords.accuracy), age), outside ? "warning" : "info");
    if (center || firstPosition) {
      mapRoot.dispatchEvent(new CustomEvent("warehouse-map-center", { detail: { x: point.x, y: point.y } }));
      firstPosition = false;
    }
  };
  const clearWatch = () => {
    if (watchId !== null) navigator.geolocation.clearWatch(watchId);
    watchId = null;
  };
  const beginWatch = () => {
    clearWatch();
    show(text("Buscando ubicación… Acepta el permiso del navegador."), "info");
    watchId = navigator.geolocation.watchPosition(position => renderPosition(position), error => {
      if (error.code === error.PERMISSION_DENIED) {
        wanted = false; clearWatch(); setButtons(false);
        show(text("Permiso de ubicación denegado. Habilítalo en el navegador y pulsa Mi ubicación."), "danger");
      } else if (error.code === error.TIMEOUT) show(text("La ubicación tardó demasiado. Mantén la página abierta para reintentar."), "warning");
      else show(text("No se pudo obtener la ubicación en este momento. El equipo seguirá intentando."), "warning");
    }, { enableHighAccuracy: true, maximumAge: 0, timeout: 20000 });
  };
  const verifyAndStart = async () => {
    if (!window.isSecureContext) { show(text("Abre Warehouse EPI por HTTPS para usar la ubicación."), "danger"); return; }
    if (!navigator.geolocation) { show(text("Este equipo o navegador no ofrece geolocalización."), "danger"); return; }
    try { calibration = await loadCalibration(); }
    catch { show(text("No se pudo consultar la calibración. Pulsa Mi ubicación para reintentar."), "warning"); return; }
    if (!calibration.exists) { show(text("Aún no hay una calibración publicada para este croquis."), "warning"); return; }
    if (!calibration.isCurrent) { show(text("La calibración está pendiente de revisión porque cambió el croquis."), "warning"); return; }
    wanted = true; firstPosition = true; setButtons(true); beginWatch();
    clearInterval(revisionTimer);
    revisionTimer = setInterval(async () => {
      try {
        const current = await loadCalibration();
        if (!current.exists || !current.isCurrent || current.revision !== calibration.revision) {
          wanted = false; clearWatch(); setButtons(false); layer.classList.add("d-none");
          show(text("La calibración cambió. Pulsa Mi ubicación para cargar la revisión actual."), "warning");
        }
      } catch { show(text("No se pudo comprobar la vigencia de la calibración. El marcador usa la última revisión conocida."), "warning"); }
    }, 60000);
  };
  const stop = () => {
    wanted = false; clearWatch(); clearInterval(revisionTimer); revisionTimer = null;
    lastPosition = null; layer.classList.add("d-none"); status.classList.add("d-none"); setButtons(false);
  };
  startButton.addEventListener("click", verifyAndStart);
  centerButton.addEventListener("click", () => { if (lastPosition) renderPosition(lastPosition.position, true); });
  stopButton.addEventListener("click", stop);
  document.addEventListener("visibilitychange", async () => {
    if (document.hidden) clearWatch();
    else if (wanted) {
      try {
        const current = await loadCalibration();
        if (current.exists && current.isCurrent && current.revision === calibration.revision) beginWatch();
        else { stop(); show(text("La calibración cambió. Pulsa Mi ubicación para cargar la revisión actual."), "warning"); }
      } catch { show(text("No se pudo comprobar la calibración. Pulsa Detener ubicación o espera para reintentar."), "warning"); }
    }
  });
  window.addEventListener("pagehide", clearWatch);
  setInterval(() => {
    if (!lastPosition) return;
    const age = Math.max(0, Math.round((Date.now() - lastPosition.position.timestamp) / 1000));
    if (age > 30) show(text("Última posición desactualizada: hace {0} s.", age), "warning");
  }, 5000);
})();
