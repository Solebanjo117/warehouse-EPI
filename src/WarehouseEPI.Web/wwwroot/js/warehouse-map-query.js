(() => {
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (!mapRoot) return;
  const frame = mapRoot.closest("[data-map-frame]");
  const svg = mapRoot.querySelector("svg");
  const viewport = mapRoot.querySelector("[data-map-viewport]");
  const stage = mapRoot.querySelector("[data-map-stage]");
  const detail = mapRoot.querySelector(".warehouse-map-detail");
  if (!frame || !svg || !viewport || !stage || !detail) return;
  const focusButton = frame.querySelector("[data-map-focus]");
  const expandButton = frame.querySelector("[data-map-expand]");
  const hint = frame.querySelector("[data-map-hint]");
  const canvasWidth = Number(mapRoot.dataset.canvasWidth) || svg.viewBox.baseVal.width;
  const canvasHeight = Number(mapRoot.dataset.canvasHeight) || svg.viewBox.baseVal.height;
  if (!(canvasWidth > 0 && canvasHeight > 0)) return;
  const MAX_ZOOM = 4;
  const ZOOM_STEP = 1.25;
  const highlightedLocationId = mapRoot.dataset.highlightLocation;
  const state = { scale: 1, fit: true, selected: null, expanded: false };
  let geometry = null;
  let pinchGesture = null;
  let suppressClicksUntil = 0;
  let resizeFrame = 0;
  let returnTarget = null;
  let expansionRestore = null;
  const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));
  const refreshExpandedViewportHeight = () => {
    if (!state.expanded) return;
    const bounds = viewport.getBoundingClientRect();
    const bottom = window.visualViewport
      ? window.visualViewport.offsetTop + window.visualViewport.height : window.innerHeight;
    viewport.style.setProperty("--map-expanded-viewport-height", `${Math.max(80, bottom - Math.max(16, bounds.top) - 16)}px`);
  };
  const measure = () => {
    const bounds = viewport.getBoundingClientRect();
    detail.style.setProperty("--map-detail-height", `${viewport.clientHeight}px`);
    const width = Math.max(1, viewport.clientWidth);
    const height = Math.max(1, viewport.clientHeight);
    const overlap = !detail.hidden && window.innerWidth < 1200
      ? clamp(bounds.top + height - detail.getBoundingClientRect().top, 0, height - 1) : 0;
    return { width, height, visibleHeight: height - overlap, overlap };
  };
  const limits = (size) => ({
    min: Math.min(size.width / canvasWidth, size.visibleHeight / canvasHeight),
    max: size.width / canvasWidth * MAX_ZOOM
  });
  const center = () => geometry ? {
    x: (viewport.scrollLeft + geometry.width / 2 - geometry.left) / state.scale,
    y: (viewport.scrollTop + geometry.visibleHeight / 2 - geometry.top) / state.scale
  } : { x: canvasWidth / 2, y: canvasHeight / 2 };
  // Keep SVG coordinates unchanged; padding lets even edge racks clear the bottom sheet.
  const render = (size, point, anchor = null) => {
    const width = canvasWidth * state.scale;
    const height = canvasHeight * state.scale;
    const left = Math.max(0, (size.width - width) / 2);
    const top = Math.max(0, (size.visibleHeight - height) / 2);
    stage.style.width = `${Math.max(size.width, width)}px`;
    stage.style.height = `${Math.max(size.visibleHeight, height) + size.overlap}px`;
    svg.style.width = `${width}px`;
    svg.style.height = `${height}px`;
    svg.style.left = `${left}px`;
    svg.style.top = `${top}px`;
    geometry = { ...size, left, top };
    viewport.scrollLeft = clamp(point.x * state.scale + left - (anchor?.x ?? size.width / 2), 0, Math.max(0, width - size.width));
    viewport.scrollTop = clamp(point.y * state.scale + top - (anchor?.y ?? size.visibleHeight / 2), 0, Math.max(0, height - size.visibleHeight));
  };
  const layout = (point = center()) => {
    refreshExpandedViewportHeight();
    const size = measure();
    const range = limits(size);
    state.scale = state.fit ? range.min : clamp(state.scale, range.min, range.max);
    render(size, state.fit ? { x: canvasWidth / 2, y: canvasHeight / 2 } : point);
  };
  const scheduleLayout = () => {
    if (resizeFrame) return;
    resizeFrame = requestAnimationFrame(() => { resizeFrame = 0; layout(); });
  };
  const applyZoom = (scale, point = center(), anchor = null) => {
    const size = measure();
    const range = limits(size);
    state.scale = clamp(scale, range.min, range.max);
    state.fit = state.scale === range.min;
    render(size, point, anchor);
  };
  mapRoot.addEventListener("warehouse-map-center", event => {
    const point = event.detail;
    if (!point || !Number.isFinite(point.x) || !Number.isFinite(point.y)) return;
    const range = limits(measure());
    applyZoom(Math.max(state.scale, Math.min(1, range.max)), point);
  });
  const selectedElement = () => state.selected
    ? svg.querySelector(`[data-map-open="${CSS.escape(state.selected)}"]`) : null;
  const selectionBounds = () => {
    const element = selectedElement();
    if (!element) return null;
    const rect = element.getBoundingClientRect();
    const map = svg.getBoundingClientRect();
    return {
      x: (rect.left + rect.width / 2 - map.left) / state.scale,
      y: (rect.top + rect.height / 2 - map.top) / state.scale,
      width: rect.width / state.scale,
      height: rect.height / state.scale
    };
  };
  const focusSelection = (enlarge = false) => {
    const bounds = selectionBounds();
    if (!bounds) return;
    if (!enlarge) {
      // A simple selection retains the current zoom, including the overview mode.
      render(measure(), bounds);
      return;
    }
    const size = measure();
    const fitSelection = Math.min(Math.max(1, size.width - 64) / Math.max(1, bounds.width),
      Math.max(1, size.visibleHeight - 64) / Math.max(1, bounds.height));
    const isRack = selectedElement().dataset.mapKind === "Rack";
    const target = isRack ? Math.min(fitSelection, Math.max(state.scale, 1)) : fitSelection;
    applyZoom(target, bounds);
  };
  const open = (id, trigger, fromSearch = false) => {
    const panel = mapRoot.querySelector(`[data-map-detail="${CSS.escape(id)}"]`);
    if (!panel) return;
    const previousCenter = center();
    state.selected = id;
    returnTarget = trigger;
    mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = item !== panel; });
    frame.querySelectorAll("[data-map-open]").forEach((item) => {
      const selected = item.dataset.mapOpen === id;
      item.classList.toggle("is-selected", selected);
      item.setAttribute("aria-expanded", String(selected));
    });
    detail.hidden = false;
    mapRoot.classList.add("has-selection");
    hint.hidden = true;
    focusButton.disabled = false;
    const highlightedPosition = highlightedLocationId
      ? panel.querySelector(`[data-map-position="${CSS.escape(highlightedLocationId)}"]`) : null;
    const matchedPosition = panel.querySelector("[data-map-position-match='true']");
    (highlightedPosition || matchedPosition || panel.querySelector("[data-map-position]"))?.click();
    detail.scrollTop = 0;
    // On short tablets the page filters can leave the entire map behind the sheet.
    if (!state.expanded && window.innerWidth < 1200
      && detail.getBoundingClientRect().top - viewport.getBoundingClientRect().top < 160) {
      window.scrollTo({ left: window.scrollX, top: window.scrollY + viewport.getBoundingClientRect().top - 80, behavior: "instant" });
    }
    layout(previousCenter);
    focusSelection(fromSearch);
    panel.querySelector("[data-map-close]")?.focus({ preventScroll: true });
  };
  const close = () => {
    const previousCenter = center();
    state.selected = null;
    detail.hidden = true;
    mapRoot.classList.remove("has-selection");
    mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = true; });
    frame.querySelectorAll("[data-map-open]").forEach((item) => {
      item.classList.remove("is-selected");
      item.setAttribute("aria-expanded", "false");
    });
    hint.hidden = false;
    focusButton.disabled = true;
    layout(previousCenter);
    returnTarget?.focus({ preventScroll: true });
  };
  const toggleExpanded = () => {
    const previousCenter = center();
    state.expanded = !state.expanded;
    if (state.expanded) {
      const marker = document.createComment("map-frame-position");
      frame.before(marker);
      expansionRestore = {
        marker, x: window.scrollX, y: window.scrollY,
        overflow: document.body.style.overflow,
        siblings: Array.from(document.body.children).filter((item) => item !== frame).map((item) => [item, item.inert])
      };
      document.body.append(frame);
      expansionRestore.siblings.forEach(([item]) => { item.inert = true; });
      document.body.style.overflow = "hidden";
      frame.setAttribute("role", "dialog");
      frame.setAttribute("aria-modal", "true");
    } else {
      expansionRestore.marker.replaceWith(frame);
      expansionRestore.siblings.forEach(([item, inert]) => { item.inert = inert; });
      document.body.style.overflow = expansionRestore.overflow;
      frame.removeAttribute("role");
      frame.removeAttribute("aria-modal");
    }
    frame.classList.toggle("is-expanded", state.expanded);
    expandButton.textContent = state.expanded ? text("Salir de vista ampliada") : text("Ampliar croquis");
    expandButton.setAttribute("aria-expanded", String(state.expanded));
    if (!state.expanded) window.scrollTo({ left: expansionRestore.x, top: expansionRestore.y, behavior: "instant" });
    layout(previousCenter);
    expandButton.focus({ preventScroll: true });
  };
  frame.querySelectorAll("[data-map-open]").forEach((item) => {
    item.setAttribute("aria-expanded", "false");
    item.addEventListener("click", () => open(item.dataset.mapOpen, item));
    // Native buttons already implement Enter/Space; SVG elements do not.
    if (item.tagName.toLowerCase() !== "button") item.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(item.dataset.mapOpen, item); }
    });
  });
  mapRoot.querySelectorAll("[data-map-position]").forEach((button) => button.addEventListener("click", () => {
    const section = button.closest("[data-map-detail]");
    section?.querySelectorAll("[data-position-detail]").forEach((item) => { item.hidden = item.dataset.positionDetail !== button.dataset.mapPosition; });
    section?.querySelectorAll("[data-map-position]").forEach((item) => item.classList.toggle("is-selected", item === button));
  }));
  mapRoot.querySelectorAll("[data-map-close]").forEach((button) => button.addEventListener("click", close));
  frame.querySelector("[data-map-zoom='in']")?.addEventListener("click", () => applyZoom(state.scale * ZOOM_STEP));
  frame.querySelector("[data-map-zoom='out']")?.addEventListener("click", () => applyZoom(state.scale / ZOOM_STEP));
  frame.querySelector("[data-map-fit]")?.addEventListener("click", () => { state.fit = true; layout(); });
  focusButton.addEventListener("click", () => focusSelection(true));
  expandButton.addEventListener("click", toggleExpanded);
  frame.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || event.defaultPrevented) return;
    if (state.selected) { event.preventDefault(); event.stopPropagation(); close(); }
    else if (state.expanded) { event.preventDefault(); event.stopPropagation(); toggleExpanded(); }
  });
  const touchDistance = (touches) => Math.hypot(touches[0].clientX - touches[1].clientX, touches[0].clientY - touches[1].clientY);
  const touchMidpoint = (touches) => ({ x: (touches[0].clientX + touches[1].clientX) / 2, y: (touches[0].clientY + touches[1].clientY) / 2 });
  const finishPinch = () => {
    if (!pinchGesture) return;
    pinchGesture = null;
    suppressClicksUntil = performance.now() + 500;
  };
  viewport.addEventListener("touchstart", (event) => {
    if (event.touches.length !== 2) return;
    event.preventDefault();
    const midpoint = touchMidpoint(event.touches);
    const bounds = viewport.getBoundingClientRect();
    const x = midpoint.x - bounds.left;
    const y = midpoint.y - bounds.top;
    pinchGesture = {
      distance: Math.max(touchDistance(event.touches), 1), scale: state.scale,
      point: { x: (viewport.scrollLeft + x - geometry.left) / state.scale, y: (viewport.scrollTop + y - geometry.top) / state.scale }
    };
  }, { passive: false });
  viewport.addEventListener("touchmove", (event) => {
    if (!pinchGesture || event.touches.length !== 2) return;
    event.preventDefault();
    const midpoint = touchMidpoint(event.touches);
    const bounds = viewport.getBoundingClientRect();
    applyZoom(pinchGesture.scale * touchDistance(event.touches) / pinchGesture.distance, pinchGesture.point,
      { x: midpoint.x - bounds.left, y: midpoint.y - bounds.top });
  }, { passive: false });
  viewport.addEventListener("touchend", (event) => { if (event.touches.length < 2) finishPinch(); });
  viewport.addEventListener("touchcancel", finishPinch);
  viewport.addEventListener("click", (event) => {
    if (performance.now() >= suppressClicksUntil) return;
    event.preventDefault();
    event.stopPropagation();
  }, true);
  window.addEventListener("resize", scheduleLayout);
  if (typeof ResizeObserver !== "undefined") {
    const observer = new ResizeObserver(scheduleLayout);
    observer.observe(viewport);
    observer.observe(detail);
    observer.observe(frame.querySelector(".warehouse-map-toolbar"));
  }
  layout();
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
      if (zeroLabel) zeroLabel.textContent = activity ? text("Sin actividad") : text("Sin ocupación");
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
        if (incidents) incidents.textContent = text("{0} negativas · {1} bloqueadas", rack.negativePositions, rack.blockedPositions);
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
  const target = svg.querySelector("[data-map-target='true']");
  if (target) open(target.dataset.mapOpen, target, true);
})();
