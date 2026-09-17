(() => {
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (!mapRoot) return;
  const svg = mapRoot.querySelector("svg");
  const viewport = mapRoot.querySelector("[data-map-viewport]");
  const placeholder = mapRoot.querySelector(".map-detail-placeholder");
  const MIN_ZOOM = 1;
  const MAX_ZOOM = 4;
  const ZOOM_STEP = 1.25;
  const highlightedLocationId = mapRoot.dataset.highlightLocation;
  let zoom = MIN_ZOOM;
  let pinchGesture = null;
  let suppressClicksUntil = 0;
  const applyZoom = (nextZoom, reset = false) => {
    if (!svg || !viewport) return;
    const previousZoom = zoom;
    const centerX = viewport.scrollLeft + viewport.clientWidth / 2;
    const centerY = viewport.scrollTop + viewport.clientHeight / 2;
    zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextZoom));
    svg.style.setProperty("--warehouse-map-query-zoom", `${zoom * 100}%`);
    if (reset) {
      viewport.scrollLeft = 0;
      viewport.scrollTop = 0;
      return;
    }
    const scale = zoom / previousZoom;
    viewport.scrollLeft = centerX * scale - viewport.clientWidth / 2;
    viewport.scrollTop = centerY * scale - viewport.clientHeight / 2;
  };
  const touchDistance = (touches) => Math.hypot(
    touches[0].clientX - touches[1].clientX,
    touches[0].clientY - touches[1].clientY);
  const touchMidpoint = (touches) => ({
    x: (touches[0].clientX + touches[1].clientX) / 2,
    y: (touches[0].clientY + touches[1].clientY) / 2
  });
  const finishPinch = () => {
    if (!pinchGesture) return;
    pinchGesture = null;
    suppressClicksUntil = performance.now() + 500;
  };
  viewport?.addEventListener("touchstart", (event) => {
    if (event.touches.length !== 2) return;
    event.preventDefault();
    const midpoint = touchMidpoint(event.touches);
    const bounds = viewport.getBoundingClientRect();
    const viewportX = midpoint.x - bounds.left;
    const viewportY = midpoint.y - bounds.top;
    pinchGesture = {
      distance: Math.max(touchDistance(event.touches), 1),
      zoom,
      contentX: (viewport.scrollLeft + viewportX) / zoom,
      contentY: (viewport.scrollTop + viewportY) / zoom
    };
  }, { passive: false });
  viewport?.addEventListener("touchmove", (event) => {
    if (!pinchGesture || event.touches.length !== 2) return;
    event.preventDefault();
    const midpoint = touchMidpoint(event.touches);
    const bounds = viewport.getBoundingClientRect();
    const viewportX = midpoint.x - bounds.left;
    const viewportY = midpoint.y - bounds.top;
    const nextZoom = pinchGesture.zoom * touchDistance(event.touches) / pinchGesture.distance;
    zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextZoom));
    svg.style.setProperty("--warehouse-map-query-zoom", `${zoom * 100}%`);
    viewport.scrollLeft = pinchGesture.contentX * zoom - viewportX;
    viewport.scrollTop = pinchGesture.contentY * zoom - viewportY;
  }, { passive: false });
  viewport?.addEventListener("touchend", (event) => { if (event.touches.length < 2) finishPinch(); });
  viewport?.addEventListener("touchcancel", finishPinch);
  viewport?.addEventListener("click", (event) => {
    if (performance.now() >= suppressClicksUntil) return;
    event.preventDefault();
    event.stopPropagation();
  }, true);
  const open = (id) => {
    mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = item.dataset.mapDetail !== id; });
    mapRoot.querySelectorAll("[data-map-open]").forEach((item) => item.classList.toggle("is-selected", item.dataset.mapOpen === id));
    if (placeholder) placeholder.hidden = true;
    const panel = mapRoot.querySelector(`[data-map-detail="${CSS.escape(id)}"]`);
    const highlightedPosition = highlightedLocationId
      ? panel?.querySelector(`[data-map-position="${CSS.escape(highlightedLocationId)}"]`)
      : null;
    const matchedPosition = panel?.querySelector("[data-map-position-match='true']");
    (highlightedPosition || matchedPosition || panel?.querySelector("[data-map-position]"))?.click();
    panel?.scrollIntoView({ block: "nearest" });
  };
  mapRoot.querySelectorAll("[data-map-open]").forEach((item) => {
    item.addEventListener("click", () => open(item.dataset.mapOpen));
    item.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(item.dataset.mapOpen); } });
  });
  mapRoot.querySelectorAll("[data-map-position]").forEach((button) => button.addEventListener("click", () => {
    const section = button.closest("[data-map-detail]");
    section?.querySelectorAll("[data-position-detail]").forEach((item) => { item.hidden = item.dataset.positionDetail !== button.dataset.mapPosition; });
    section?.querySelectorAll("[data-map-position]").forEach((item) => item.classList.toggle("is-selected", item === button));
  }));
  mapRoot.querySelectorAll("[data-map-close]").forEach((button) => button.addEventListener("click", () => { button.closest("[data-map-detail]").hidden = true; if (placeholder) placeholder.hidden = false; }));
  document.querySelector("[data-map-zoom='in']")?.addEventListener("click", () => applyZoom(zoom * ZOOM_STEP));
  document.querySelector("[data-map-zoom='out']")?.addEventListener("click", () => applyZoom(zoom / ZOOM_STEP));
  document.querySelector("[data-map-fit]")?.addEventListener("click", () => applyZoom(MIN_ZOOM, true));
  const heatmapForm = document.querySelector("[data-heatmap-form]");
  if (heatmapForm) {
    const metricControl = heatmapForm.querySelector("[name='mapMetric']");
    const periodControl = heatmapForm.querySelector("[name='period']");
    const errorBox = document.querySelector("[data-heatmap-error]");
    const updateControlVisibility = () => {
      const activity = metricControl?.value === "activity";
      const custom = activity && periodControl?.value === "custom";
      document.querySelectorAll("[data-heatmap-activity-control]").forEach((item) => item.classList.toggle("d-none", !activity));
      document.querySelectorAll("[data-heatmap-custom-control]").forEach((item) => item.classList.toggle("d-none", !custom));
      document.querySelector("[data-heatmap-unavailable-legend]")?.classList.toggle("d-none", activity);
      const zeroLabel = document.querySelector("[data-heatmap-zero-label]");
      if (zeroLabel) zeroLabel.textContent = activity ? "Sin actividad" : "Sin ocupación";
    };
    const setError = (message = "") => {
      if (!errorBox) return;
      errorBox.textContent = message;
      errorBox.classList.toggle("d-none", message.length === 0);
    };
    const applyRackValues = (rack, metric) => {
      const element = mapRoot.querySelector(`[data-map-open="${CSS.escape(rack.elementId)}"]`);
      if (element) {
        Array.from(element.classList).filter((name) => name.startsWith("map-heat-")).forEach((name) => element.classList.remove(name));
        const unavailable = metric === "occupancy" && rack.totalPositions === 0;
        element.classList.add(unavailable ? "map-heat-unavailable" : `map-heat-${rack.heatLevel}`);
        element.dataset.heatLevel = `${rack.heatLevel}`;
        const valueLabel = metric === "activity"
          ? rack.accessCount === 0 ? ", sin actividad" : `, ${rack.accessCount} operaciones`
          : unavailable ? ", sin posiciones evaluables" : `, ${rack.occupancyPercent}% ocupado`;
        element.setAttribute("aria-label", `${element.dataset.mapBaseLabel}${valueLabel}`);
        const visibleValue = element.querySelector("[data-heatmap-value]");
        if (visibleValue) visibleValue.textContent = metric === "activity"
          ? `${rack.accessCount} mov.`
          : unavailable ? "N/D" : `${rack.occupancyPercent}%`;
      }
      const row = document.querySelector(`[data-heatmap-rack-row="${CSS.escape(rack.elementId)}"]`);
      if (row) {
        const access = row.querySelector("[data-heatmap-access]");
        const occupancy = row.querySelector("[data-heatmap-occupancy]");
        const incidents = row.querySelector("[data-heatmap-incidents]");
        if (access) access.textContent = `${rack.accessCount}`;
        if (occupancy) occupancy.textContent = `${rack.occupiedPositions}/${rack.totalPositions} (${rack.occupancyPercent}%)`;
        if (incidents) incidents.textContent = `${rack.negativePositions} negativas · ${rack.blockedPositions} bloqueadas`;
      }
    };
    const refreshHeatmap = async () => {
      updateControlVisibility();
      const formData = new FormData(heatmapForm);
      if (formData.get("period") === "custom" && (!formData.get("from") || !formData.get("to"))) return;
      const query = new URLSearchParams(formData);
      query.set("handler", "HeatmapData");
      heatmapForm.setAttribute("aria-busy", "true");
      setError();
      try {
        const response = await fetch(`${window.location.pathname}?${query}`, { headers: { Accept: "application/json" } });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const result = await response.json();
        result.racks.forEach((rack) => applyRackValues(rack, result.metric));
        const periodLabel = document.querySelector("[data-heatmap-period-label]");
        const generated = document.querySelector("[data-heatmap-generated]");
        const timeZone = document.querySelector("[data-heatmap-timezone]");
        if (periodLabel) periodLabel.textContent = result.periodLabel;
        if (generated) generated.textContent = result.generatedAtLocal;
        if (timeZone) timeZone.textContent = result.timeZoneId;
        if (periodControl) periodControl.value = result.period;
        const fromControl = heatmapForm.querySelector("[name='from']");
        const toControl = heatmapForm.querySelector("[name='to']");
        if (fromControl) fromControl.value = result.from || "";
        if (toControl) toControl.value = result.to || "";
        document.querySelectorAll("[data-heatmap-export]").forEach((link) => {
          const exportUrl = new URL(link.href);
          exportUrl.searchParams.set("mapMetric", result.metric);
          exportUrl.searchParams.set("period", result.period);
          result.from ? exportUrl.searchParams.set("from", result.from) : exportUrl.searchParams.delete("from");
          result.to ? exportUrl.searchParams.set("to", result.to) : exportUrl.searchParams.delete("to");
          link.href = exportUrl.toString();
        });
        query.delete("handler");
        window.history.replaceState(null, "", `${window.location.pathname}?${query}`);
      } catch {
        setError("No fue posible calcular el mapa de calor. Los valores anteriores se conservaron; vuelve a intentarlo.");
      } finally {
        heatmapForm.removeAttribute("aria-busy");
      }
    };
    heatmapForm.addEventListener("submit", (event) => { event.preventDefault(); refreshHeatmap(); });
    heatmapForm.querySelectorAll("[data-heatmap-submit]").forEach((control) => control.addEventListener("change", () => {
      updateControlVisibility();
      if (control === periodControl && periodControl.value === "custom") return;
      refreshHeatmap();
    }));
  }
  mapRoot.querySelector("[data-map-target='true']")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
})();
