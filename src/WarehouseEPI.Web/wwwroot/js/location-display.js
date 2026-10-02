(() => {
  const root = document.querySelector("[data-location-display]");
  if (!root) return;
  const texts = JSON.parse(root.dataset.uiTexts || "{}");
  const translate = (key, ...args) => (texts[key] || key).replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match);

  const holder = root.querySelector("[data-display-slides]");
  const progress = root.querySelector("[data-display-progress]");
  const status = root.querySelector("[data-display-status]");
  const updated = root.querySelector("[data-display-updated]");
  const live = root.querySelector("[data-display-live]");
  const pauseButton = root.querySelector("[data-display-pause]");
  const upcoming = root.querySelector("[data-display-upcoming]");
  const bar = root.querySelector("[data-display-bar]");
  const rowButtons = [...root.querySelectorAll("[data-display-row-jump]")];
  const map = root.querySelector("[data-display-map-svg]");
  const mapCaption = root.querySelector("[data-display-map-caption]");
  const seconds = Math.min(120, Math.max(5, Number(root.dataset.seconds) || 20));
  const refreshMilliseconds = 2000;
  let slides = [...holder.querySelectorAll("[data-display-slide]")];
  let index = 0;
  let paused = false;
  let touchStart = null;
  let settling = false;
  let autoTimer = null;
  let pendingRow = null;
  let refreshing = false;
  const grouped = Number(root.dataset.racksPerScreen) > 1;
  const orientation = root.dataset.displayOrientation || "auto";
  const narrowScreen = window.matchMedia("(max-width: 899.98px)");
  const portraitScreen = window.matchMedia("(orientation: portrait)");
  function isVertical() {
    return orientation === "portrait" || narrowScreen.matches || orientation === "auto" && portraitScreen.matches;
  }
  let sourceSlides = slides;
  let layoutPending = false;
  let focusedRack = null;
  let inspection = null;

  function responsiveSlides(source) {
    if (!grouped) return source;
    if (!isVertical()) return source.map(slide => slide.cloneNode(true));
    const result = source.flatMap(slide => {
      const racks = [...slide.querySelectorAll("[data-display-rack]")];
      return racks.map((rack, rackIndex) => {
        const copy = slide.cloneNode(true);
        copy.querySelectorAll("[data-display-rack]").forEach((item, position) => {
          if (position !== rackIndex) item.remove();
        });
        const label = rack.dataset.displayRack;
        copy.dataset.displayRacks = label;
        copy.dataset.displayAsNext = translate("Sigue el rack {0}", label);
        copy.querySelector(".display-heading-title").textContent = translate("Rack {0}", label);
        const ends = copy.querySelectorAll(".display-bay-ends span");
        if (rackIndex > 0) ends[0].textContent = `‹ ${racks[rackIndex - 1].dataset.displayRack}`;
        if (rackIndex < racks.length - 1) ends[1].textContent = `${racks[rackIndex + 1].dataset.displayRack} ›`;
        return copy;
      });
    });
    const rows = new Map();
    result.forEach(slide => {
      const row = slide.dataset.displayRow;
      if (!rows.has(row)) rows.set(row, []);
      rows.get(row).push(slide);
    });
    rows.forEach((rowSlides, row) => rowSlides.forEach((slide, part) => {
      slide.dataset.displayPart = String(part + 1);
      slide.querySelector(".display-heading-meta").textContent = translate("Fila {0} · Pantalla {1} de {2}", row, part + 1, rowSlides.length);
      slide.setAttribute("aria-label", translate("Fila {0}, rack {1}, pantalla {2} de {3}", row, slide.dataset.displayRacks, part + 1, rowSlides.length));
    }));
    return result;
  }

  function applyLayout() {
    if (touchStart || settling) return;
    layoutPending = false;
    const vertical = isVertical();
    root.style.setProperty("--display-racks", vertical ? "1" : root.dataset.racksPerScreen || "1");
    root.dataset.displayCompact = String(vertical);
    root.dataset.displayEffectiveOrientation = vertical ? "portrait" : "landscape";
    if (!grouped) return;
    const active = slides[index];
    const row = active?.dataset.displayRow;
    const rack = focusedRack || active?.dataset.displayRacks?.split(" ")[0];
    slides = responsiveSlides(sourceSlides);
    holder.replaceChildren(...slides);
    const matching = slides.findIndex(slide => slide.dataset.displayRow === row && slide.dataset.displayRacks.split(" ").includes(rack));
    show(matching >= 0 ? matching : 0);
  }

  function queueLayout() {
    layoutPending = true;
    scheduleNext();
  }
  narrowScreen.addEventListener("change", queueLayout);
  portraitScreen.addEventListener("change", queueLayout);

  function show(next, announce = false) {
    if (!slides.length) return;
    index = (next + slides.length) % slides.length;
    slides.forEach((slide, position) => { slide.hidden = position !== index; });
    progress.textContent = translate("{0} de {1}", index + 1, slides.length);
    const active = slides[index];
    const activeRacks = (active.dataset.displayRacks || "").split(" ");
    if (!activeRacks.includes(focusedRack)) focusedRack = activeRacks[0];
    const following = slides[(index + 1) % slides.length];
    if (upcoming) upcoming.textContent = slides.length < 2 ? "" : following.dataset.displayRow === active.dataset.displayRow
      ? following.dataset.displayAsNext : translate("Sigue la fila {0}", following.dataset.displayRow);
    rowButtons.forEach(button => button.setAttribute("aria-current", String(button.dataset.displayRowJump === active.dataset.displayRow)));
    if (map && !inspection?.active) {
      const racks = (active.dataset.displayRacks || "").split(" ");
      if (mapCaption) mapCaption.textContent = translate("Fila {0} · {1} {2}", active.dataset.displayRow, racks.length === 1 ? translate("Rack") : translate("Racks"), racks.join(", "));
      map.setAttribute("aria-label", translate("Croquis del almacén con la fila {0} resaltada", active.dataset.displayRow));
      map.querySelectorAll("[data-map-row]").forEach(element => {
        element.classList.toggle("is-row", element.dataset.mapRow === active.dataset.displayRow);
        element.classList.toggle("is-current", element.dataset.mapRow === active.dataset.displayRow && racks.includes(element.dataset.mapRack));
      });
    }
    if (announce) live.textContent = slides[index].getAttribute("aria-label") || "";
  }

  function scheduleNext() {
    if (layoutPending) applyLayout();
    window.clearTimeout(autoTimer);
    if (bar) {
      bar.classList.remove("is-running");
      void bar.offsetWidth;
    }
    if (paused || slides.length < 2 || touchStart || settling) return;
    bar?.classList.add("is-running");
    autoTimer = window.setTimeout(() => {
      show(index + 1);
      requestVisibleRefresh();
      scheduleNext();
    }, seconds * 1000);
  }

  function navigate(next) {
    if (inspection?.active || settling || touchStart?.dragging) return;
    show(next, true);
    requestVisibleRefresh();
    scheduleNext();
  }

  function requestVisibleRefresh() {
    if (inspection?.active) { inspection.refresh(); return; }
    pendingRow = slides[index]?.dataset.displayRow || null;
    if (pendingRow && !refreshing && !touchStart && !settling) void refreshRows();
  }

  async function refreshRows() {
    refreshing = true;
    while (pendingRow) {
      const row = pendingRow;
      pendingRow = null;
      try {
        const url = new URL(window.location.href);
        url.searchParams.set("orientation", orientation);
        url.searchParams.delete("rows");
        url.searchParams.append("rows", row);
        url.searchParams.set("refresh", "true");
        const controller = new AbortController();
        const timeout = window.setTimeout(() => controller.abort(), 8000);
        let response;
        try {
          response = await fetch(url.toString(), { cache: "no-store", signal: controller.signal });
        } finally {
          window.clearTimeout(timeout);
        }
        if (!response.ok) throw new Error("No se pudo actualizar");
        const documentCopy = new DOMParser().parseFromString(await response.text(), "text/html");
        const replacement = documentCopy.querySelector("[data-display-slides]");
        const timestamp = documentCopy.querySelector("[data-display-updated]");
        const freshSource = [...(replacement?.querySelectorAll("[data-display-slide]") || [])];
        if (inspection?.active) { pendingRow = null; break; }
        const freshSlides = responsiveSlides(freshSource);
        const oldSlides = slides.filter(slide => slide.dataset.displayRow === row);
        if (!timestamp || !freshSlides.length || !oldSlides.length) throw new Error("Respuesta incompleta");
        if (touchStart || settling) {
          pendingRow = row;
          break;
        }

        if (grouped) {
          const first = sourceSlides.findIndex(slide => slide.dataset.displayRow === row);
          const count = sourceSlides.filter(slide => slide.dataset.displayRow === row).length;
          sourceSlides.splice(first, count, ...freshSource);
        }

        const changed = oldSlides.length !== freshSlides.length ||
          oldSlides.some((slide, position) => slide.innerHTML !== freshSlides[position].innerHTML);
        if (changed) {
          const active = slides[index];
          const activeRow = active.dataset.displayRow;
          const activePart = Number(active.dataset.displayPart);
          const activeRack = focusedRack || active.dataset.displayRacks?.split(" ")[0];
          const hadMultipleSlides = slides.length > 1;
          freshSlides.forEach(slide => slide.classList.add("display-refresh-slide"));
          oldSlides[0].before(...freshSlides);
          oldSlides.forEach(slide => slide.remove());
          slides = [...holder.querySelectorAll("[data-display-slide]")];
          const matching = activeRow === row
            ? slides.findIndex(slide => slide.dataset.displayRow === row && (grouped
              ? slide.dataset.displayRacks.split(" ").includes(activeRack)
              : Number(slide.dataset.displayPart) === activePart))
            : slides.indexOf(active);
          const rowFallback = slides.findIndex(slide => slide.dataset.displayRow === row);
          show(matching >= 0 ? matching : rowFallback);
          if (hadMultipleSlides !== (slides.length > 1)) scheduleNext();
        }
        if (slides[index]?.dataset.displayRow === row) {
          updated.textContent = timestamp.textContent;
          status.textContent = paused ? translate("Pausado") : translate("Reproduciendo");
        }
      } catch {
        if (!inspection?.active && slides[index]?.dataset.displayRow === row)
          status.textContent = translate("No se pudo actualizar · se muestra la última consulta");
      }
      if (pendingRow === row) pendingRow = null;
    }
    refreshing = false;
  }

  root.querySelector("[data-display-prev]").addEventListener("click", () => navigate(index - 1));
  inspection = window.WarehouseDisplayInspection?.create(root, () => {
    paused = true;
    pendingRow = null;
    pauseButton.textContent = translate("Reanudar");
    pauseButton.setAttribute("aria-pressed", "true");
    scheduleNext();
  }) || null;
  root.querySelector("[data-display-next]").addEventListener("click", () => navigate(index + 1));
  rowButtons.forEach(button => button.addEventListener("click", () => {
    const target = slides.findIndex(slide => slide.dataset.displayRow === button.dataset.displayRowJump);
    if (target >= 0) navigate(target);
  }));
  pauseButton.addEventListener("click", () => {
    if (inspection?.active) { inspection.resume(); show(index, true); requestVisibleRefresh(); }
    paused = !paused;
    pauseButton.textContent = paused ? translate("Reanudar") : translate("Pausar");
    pauseButton.setAttribute("aria-pressed", String(paused));
    status.textContent = paused ? translate("Pausado") : translate("Reproduciendo");
    scheduleNext();
  });
  const fullscreenButton = root.querySelector("[data-display-fullscreen]");
  fullscreenButton.addEventListener("click", async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else if (document.documentElement.requestFullscreen) await document.documentElement.requestFullscreen();
      else throw new Error("Pantalla completa no disponible");
    } catch {
      live.textContent = translate("No se pudo abrir la pantalla completa. Usa la opción del navegador.");
    }
  });
  document.addEventListener("fullscreenchange", () => {
    fullscreenButton.textContent = document.fullscreenElement ? translate("Salir de pantalla completa") : translate("Pantalla completa");
  });
  document.addEventListener("keydown", event => {
    if (event.target?.matches?.("input, textarea, select, [contenteditable='true']")) return;
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      navigate(index + (event.key === "ArrowLeft" ? -1 : 1));
    }
    if (event.key === " " && event.target === document.body) {
      event.preventDefault();
      pauseButton.click();
    }
  });
  function resetDrag(current, neighbor) {
    for (const slide of [current, neighbor]) {
      if (!slide) continue;
      slide.classList.remove("display-drag-current", "display-drag-neighbor");
      slide.style.removeProperty("transform");
    }
    holder.classList.remove("display-is-dragging", "display-is-settling");
  }

  function finishDrag(commit) {
    const drag = touchStart;
    touchStart = null;
    holder.classList.remove("display-pointer-active");
    if (drag?.pointer && holder.hasPointerCapture(drag.id)) holder.releasePointerCapture(drag.id);
    if (!drag?.dragging) {
      scheduleNext();
      if (pendingRow && !refreshing) void refreshRows();
      return;
    }
    const current = slides[index];
    const neighbor = slides[drag.neighborIndex];
    settling = true;
    holder.classList.remove("display-is-dragging");
    holder.classList.add("display-is-settling");
    current.style.transform = `translate3d(${commit ? -drag.direction * drag.width : 0}px, 0, 0)`;
    neighbor.style.transform = `translate3d(${commit ? 0 : drag.direction * drag.width}px, 0, 0)`;
    const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    window.setTimeout(() => {
      resetDrag(current, neighbor);
      settling = false;
      if (commit) navigate(index + drag.direction);
      else {
        show(index);
        scheduleNext();
        if (pendingRow && !refreshing) void refreshRows();
      }
    }, reducedMotion ? 0 : 220);
  }

  function startDrag(id, x, y, pointer = false) {
    if (inspection?.active || settling || slides.length < 2) return false;
    touchStart = { id, x, y, pointer,
      dragging: false, vertical: false, direction: 0, neighborIndex: null,
      width: Math.max(1, holder.clientWidth) };
    window.clearTimeout(autoTimer);
    return true;
  }

  function moveDrag(x, y, event) {
    if (!touchStart || touchStart.vertical || slides.length < 2) return;
    const distanceX = Math.max(-touchStart.width, Math.min(touchStart.width, x - touchStart.x));
    const distanceY = y - touchStart.y;
    if (!touchStart.dragging) {
      if (Math.abs(distanceY) > Math.abs(distanceX) && Math.abs(distanceY) > 12) {
        touchStart.vertical = true;
        return;
      }
      if (Math.abs(distanceX) < 12 || Math.abs(distanceX) <= Math.abs(distanceY) * 1.25) return;
      touchStart.dragging = true;
      holder.classList.add("display-is-dragging");
    }
    if (event.cancelable !== false) event.preventDefault();
    const direction = distanceX < 0 ? 1 : -1;
    const neighborIndex = (index + direction + slides.length) % slides.length;
    if (touchStart.neighborIndex !== neighborIndex) {
      const previous = slides[touchStart.neighborIndex];
      if (previous) {
        previous.hidden = true;
        previous.classList.remove("display-drag-neighbor");
        previous.style.removeProperty("transform");
      }
      touchStart.neighborIndex = neighborIndex;
      slides[neighborIndex].hidden = false;
      slides[neighborIndex].classList.add("display-drag-neighbor");
    }
    touchStart.direction = direction;
    slides[index].classList.add("display-drag-current");
    slides[index].style.transform = `translate3d(${distanceX}px, 0, 0)`;
    slides[neighborIndex].style.transform = `translate3d(${distanceX + direction * touchStart.width}px, 0, 0)`;
  }

  function endDrag(x, y) {
    if (!touchStart) return;
    const distanceX = x - touchStart.x;
    const distanceY = y - touchStart.y;
    const threshold = Math.min(160, Math.max(56, touchStart.width * .12));
    finishDrag(touchStart.dragging && Math.abs(distanceX) >= threshold &&
      Math.abs(distanceX) > Math.abs(distanceY) * 1.25);
  }

  if (window.PointerEvent) {
    holder.addEventListener("pointerdown", event => {
      if (!event.isPrimary) {
        if (touchStart && event.pointerType === "touch") finishDrag(false);
        return;
      }
      if (event.button !== 0 || touchStart || event.target?.closest?.("a, button, input, textarea, select, [contenteditable='true']")) return;
      if (!startDrag(event.pointerId, event.clientX, event.clientY, true)) return;
      holder.setPointerCapture(event.pointerId);
      holder.classList.add("display-pointer-active");
      if (event.pointerType !== "touch") event.preventDefault();
    });
    holder.addEventListener("pointermove", event => {
      if (!touchStart || event.pointerId !== touchStart.id) return;
      if (event.pointerType === "mouse" && !(event.buttons & 1)) {
        finishDrag(false);
        return;
      }
      moveDrag(event.clientX, event.clientY, event);
    });
    holder.addEventListener("pointerup", event => {
      if (touchStart && event.pointerId === touchStart.id) endDrag(event.clientX, event.clientY);
    });
    const cancelPointer = event => {
      if (touchStart && event.pointerId === touchStart.id) finishDrag(false);
    };
    holder.addEventListener("pointercancel", cancelPointer);
    holder.addEventListener("lostpointercapture", cancelPointer);
  } else {
    holder.addEventListener("touchstart", event => {
      if (event.touches.length !== 1) {
        finishDrag(false);
        return;
      }
      const touch = event.changedTouches[0];
      startDrag(touch.identifier, touch.clientX, touch.clientY);
    }, { passive: true });
    holder.addEventListener("touchmove", event => {
      if (!touchStart) return;
      const touch = [...event.touches].find(item => item.identifier === touchStart.id);
      if (touch) moveDrag(touch.clientX, touch.clientY, event);
    }, { passive: false });
    holder.addEventListener("touchend", event => {
      if (!touchStart) return;
      const touch = [...event.changedTouches].find(item => item.identifier === touchStart.id);
      if (touch) endDrag(touch.clientX, touch.clientY);
    }, { passive: true });
    holder.addEventListener("touchcancel", () => finishDrag(false), { passive: true });
  }
  window.addEventListener("blur", () => { if (touchStart) finishDrag(false); });

  window.setInterval(requestVisibleRefresh, refreshMilliseconds);
  applyLayout();
  show(0);
  scheduleNext();
})();
