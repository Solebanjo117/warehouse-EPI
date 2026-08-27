(() => {
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (!mapRoot) return;
  const svg = mapRoot.querySelector("svg");
  const viewport = mapRoot.querySelector("[data-map-viewport]");
  const placeholder = mapRoot.querySelector(".map-detail-placeholder");
  const MIN_ZOOM = 1;
  const MAX_ZOOM = 4;
  const ZOOM_STEP = 1.25;
  let zoom = MIN_ZOOM;
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
  const open = (id) => {
    mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = item.dataset.mapDetail !== id; });
    mapRoot.querySelectorAll("[data-map-open]").forEach((item) => item.classList.toggle("is-selected", item.dataset.mapOpen === id));
    if (placeholder) placeholder.hidden = true;
    const panel = mapRoot.querySelector(`[data-map-detail="${CSS.escape(id)}"]`);
    panel?.querySelector("[data-map-position]")?.click(); panel?.scrollIntoView({ block: "nearest" });
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
  mapRoot.querySelector("[data-map-target='true']")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  const highlightedLocationId = mapRoot.dataset.highlightLocation;
  if (highlightedLocationId) {
    mapRoot.querySelector(`[data-map-position="${CSS.escape(highlightedLocationId)}"]`)?.click();
  }
})();
