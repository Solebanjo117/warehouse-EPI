(() => {
  const root = document.querySelector("[data-location-display]");
  if (!root) return;

  const holder = root.querySelector("[data-display-slides]");
  const progress = root.querySelector("[data-display-progress]");
  const status = root.querySelector("[data-display-status]");
  const updated = root.querySelector("[data-display-updated]");
  const live = root.querySelector("[data-display-live]");
  const pauseButton = root.querySelector("[data-display-pause]");
  const seconds = Math.min(120, Math.max(5, Number(root.dataset.seconds) || 20));
  let slides = [...holder.querySelectorAll("[data-display-slide]")];
  let index = 0;
  let paused = false;
  let touchStart = null;
  let autoTimer = null;
  let pendingRow = null;
  let refreshing = false;

  function show(next, announce = false) {
    if (!slides.length) return;
    index = (next + slides.length) % slides.length;
    slides.forEach((slide, position) => { slide.hidden = position !== index; });
    progress.textContent = `${index + 1} de ${slides.length}`;
    if (announce) live.textContent = slides[index].getAttribute("aria-label") || "";
  }

  function scheduleNext() {
    window.clearTimeout(autoTimer);
    if (paused || slides.length < 2) return;
    autoTimer = window.setTimeout(() => {
      show(index + 1);
      requestVisibleRefresh();
      scheduleNext();
    }, seconds * 1000);
  }

  function navigate(next) {
    show(next, true);
    requestVisibleRefresh();
    scheduleNext();
  }

  function requestVisibleRefresh() {
    pendingRow = slides[index]?.dataset.displayRow || null;
    if (pendingRow && !refreshing) void refreshRows();
  }

  async function refreshRows() {
    refreshing = true;
    while (pendingRow) {
      const row = pendingRow;
      pendingRow = null;
      if (slides[index]?.dataset.displayRow === row) status.textContent = "Actualizando existencias…";
      try {
        const url = new URL(window.location.href);
        url.searchParams.delete("rows");
        url.searchParams.append("rows", row);
        const response = await fetch(url.toString(), { cache: "no-store" });
        if (!response.ok) throw new Error("No se pudo actualizar");
        const documentCopy = new DOMParser().parseFromString(await response.text(), "text/html");
        const replacement = documentCopy.querySelector("[data-display-slides]");
        const timestamp = documentCopy.querySelector("[data-display-updated]");
        const freshSlides = [...(replacement?.querySelectorAll("[data-display-slide]") || [])];
        const oldSlides = slides.filter(slide => slide.dataset.displayRow === row);
        if (!timestamp || !freshSlides.length || !oldSlides.length) throw new Error("Respuesta incompleta");

        const active = slides[index];
        const activeRow = active.dataset.displayRow;
        const activePart = Number(active.dataset.displayPart);
        const hadMultipleSlides = slides.length > 1;
        oldSlides[0].before(...freshSlides);
        oldSlides.forEach(slide => slide.remove());
        slides = [...holder.querySelectorAll("[data-display-slide]")];
        const matching = activeRow === row
          ? slides.findIndex(slide => slide.dataset.displayRow === row && Number(slide.dataset.displayPart) === activePart)
          : slides.indexOf(active);
        const rowFallback = slides.findIndex(slide => slide.dataset.displayRow === row);
        show(matching >= 0 ? matching : rowFallback);
        if (slides[index]?.dataset.displayRow === row) {
          updated.textContent = timestamp.textContent;
          status.textContent = paused ? "Pausado" : "Reproduciendo";
        }
        if (hadMultipleSlides !== (slides.length > 1)) scheduleNext();
      } catch {
        if (slides[index]?.dataset.displayRow === row)
          status.textContent = "No se pudo actualizar · se muestra la última consulta";
      }
      if (pendingRow === row) pendingRow = null;
    }
    refreshing = false;
  }

  root.querySelector("[data-display-prev]").addEventListener("click", () => navigate(index - 1));
  root.querySelector("[data-display-next]").addEventListener("click", () => navigate(index + 1));
  pauseButton.addEventListener("click", () => {
    paused = !paused;
    pauseButton.textContent = paused ? "Reanudar" : "Pausar";
    pauseButton.setAttribute("aria-pressed", String(paused));
    status.textContent = paused ? "Pausado" : "Reproduciendo";
    scheduleNext();
  });
  root.querySelector("[data-display-fullscreen]").addEventListener("click", () => {
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen?.();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "ArrowLeft") navigate(index - 1);
    if (event.key === "ArrowRight") navigate(index + 1);
    if (event.key === " " && event.target === document.body) {
      event.preventDefault();
      pauseButton.click();
    }
  });
  holder.addEventListener("touchstart", event => {
    if (event.touches.length !== 1) {
      touchStart = null;
      scheduleNext();
      return;
    }
    const touch = event.changedTouches[0];
    touchStart = { id: touch.identifier, x: touch.screenX, y: touch.screenY };
    window.clearTimeout(autoTimer);
  }, { passive: true });
  holder.addEventListener("touchend", event => {
    if (!touchStart) return;
    const touch = [...event.changedTouches].find(item => item.identifier === touchStart.id);
    if (!touch) return;
    const distanceX = touch.screenX - touchStart.x;
    const distanceY = touch.screenY - touchStart.y;
    touchStart = null;
    if (Math.abs(distanceX) >= 60 && Math.abs(distanceX) > Math.abs(distanceY) * 1.25)
      navigate(index + (distanceX < 0 ? 1 : -1));
    else scheduleNext();
  }, { passive: true });
  holder.addEventListener("touchcancel", () => { touchStart = null; scheduleNext(); }, { passive: true });

  window.setInterval(requestVisibleRefresh, 60000);
  show(0);
  scheduleNext();
})();
