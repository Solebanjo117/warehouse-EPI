(() => {
  const editor = document.querySelector("[data-map-editor]");
  if (!editor) return;

  const ns = "http://www.w3.org/2000/svg";
  const svg = editor.querySelector("[data-editor-svg]");
  const field = editor.querySelector("[data-editor-reference-state]");
  const tokenField = editor.querySelector("[data-editor-reference-token]");
  const controls = editor.querySelector("[data-reference-controls]");
  const status = editor.querySelector("[data-reference-status]");
  const opacity = editor.querySelector("[data-reference-opacity]");
  const visibility = editor.querySelector("[data-reference-visible]");
  const scaleField = editor.querySelector("[data-editor-scale]");
  const visibilityKey = "warehouseEpi.mapEditor.referenceVisible.v1";
  let previewUrl = "";
  let interaction = null;
  let frame = 0;
  let calibrationPoints = [];
  let calibrating = false;

  const read = (value, name, fallback = null) => value?.[name] ?? value?.[name[0].toLowerCase() + name.slice(1)] ?? fallback;
  let states = [];
  try { states = JSON.parse(field?.value || "[]"); } catch { states = []; }
  const active = () => states.find((item) => !read(item, "IsArchived", false));
  const find = (id) => states.find((item) => String(read(item, "Id")) === String(id));
  const set = (item, name, value) => { item[name] = value; const lower = name[0].toLowerCase() + name.slice(1); if (lower in item) delete item[lower]; };
  const sync = () => { if (field) field.value = JSON.stringify(states); };
  const pushUndo = () => editor.dispatchEvent(new CustomEvent("warehouse-map:push-undo"));
  const canvasPoint = (event) => {
    const box = svg.getBoundingClientRect(); const view = svg.viewBox.baseVal;
    return { x: view.x + (event.clientX - box.left) * view.width / box.width, y: view.y + (event.clientY - box.top) * view.height / box.height };
  };
  const imageUrl = (item) => previewUrl || `${location.pathname}?handler=Reference&id=${encodeURIComponent(read(item, "Id"))}`;
  const clampGeometry = (item) => {
    const rotated = [90, 270].includes(Number(read(item, "Rotation", 0)));
    let width = Math.max(20, Number(read(item, "Width", 20))); let height = Math.max(20, Number(read(item, "Height", 20)));
    const factor = Math.min(1, (rotated ? 900 : 1600) / width, (rotated ? 1600 : 900) / height);
    width *= factor; height *= factor;
    set(item, "Width", width); set(item, "Height", height);
    let centerX = Number(read(item, "X", 0)) + width / 2; let centerY = Number(read(item, "Y", 0)) + height / 2;
    const halfWidth = (rotated ? height : width) / 2; const halfHeight = (rotated ? width : height) / 2;
    centerX = Math.max(halfWidth, Math.min(1600 - halfWidth, centerX)); centerY = Math.max(halfHeight, Math.min(900 - halfHeight, centerY));
    set(item, "X", centerX - width / 2); set(item, "Y", centerY - height / 2);
  };
  const calibrationInches = () => {
    const raw = Number(editor.querySelector("[data-editor-calibration-distance]")?.value);
    const unit = editor.querySelector("[data-editor-calibration-unit]")?.value;
    if (!(raw > 0)) return null;
    return unit === "YD" ? raw * 36 : unit === "CM" ? raw / 2.54 : unit === "M" ? raw * 100 / 2.54 : raw;
  };
  const updateScaleFromReference = (item) => {
    const ax = Number(read(item, "CalibrationAX")); const ay = Number(read(item, "CalibrationAY"));
    const bx = Number(read(item, "CalibrationBX")); const by = Number(read(item, "CalibrationBY"));
    const inches = Number(read(item, "CalibrationDistanceInches"));
    if (![ax, ay, bx, by, inches].every(Number.isFinite) || inches <= 0) return;
    const dx = (bx - ax) * Number(read(item, "Width")); const dy = (by - ay) * Number(read(item, "Height"));
    if (scaleField) scaleField.value = String(Math.hypot(dx, dy) / inches);
    editor.dispatchEvent(new CustomEvent("warehouse-map:scale-changed"));
  };
  const localNormalizedPoint = (item, point) => {
    const x = Number(read(item, "X")); const y = Number(read(item, "Y"));
    const width = Number(read(item, "Width")); const height = Number(read(item, "Height"));
    const angle = -Number(read(item, "Rotation", 0)) * Math.PI / 180;
    const dx = point.x - x - width / 2; const dy = point.y - y - height / 2;
    const localX = dx * Math.cos(angle) - dy * Math.sin(angle) + width / 2;
    const localY = dx * Math.sin(angle) + dy * Math.cos(angle) + height / 2;
    return { x: Math.max(0, Math.min(1, localX / width)), y: Math.max(0, Math.min(1, localY / height)) };
  };
  const renderCalibration = (group, item) => {
    group.querySelectorAll(".editor-reference-calibration").forEach((node) => node.remove());
    const values = ["CalibrationAX", "CalibrationAY", "CalibrationBX", "CalibrationBY"].map((name) => Number(read(item, name)));
    if (!values.every(Number.isFinite)) return;
    const [ax, ay, bx, by] = values; const width = Number(read(item, "Width")); const height = Number(read(item, "Height"));
    const line = document.createElementNS(ns, "line"); line.classList.add("editor-reference-calibration");
    line.setAttribute("x1", ax * width); line.setAttribute("y1", ay * height); line.setAttribute("x2", bx * width); line.setAttribute("y2", by * height); group.append(line);
  };
  const wireGroup = (group) => {
    group.addEventListener("pointerdown", (event) => {
      event.stopPropagation(); const item = active(); if (!item) return;
      editor.dispatchEvent(new CustomEvent("warehouse-map:clear-selection"));
      group.classList.add("is-selected");
      if (calibrating) {
        const normalized = localNormalizedPoint(item, canvasPoint(event)); calibrationPoints.push(normalized);
        if (calibrationPoints.length === 2) {
          const inches = calibrationInches();
          if (!inches) { status.textContent = "Introduce una distancia real positiva antes de calibrar."; calibrationPoints = []; return; }
          set(item, "CalibrationAX", calibrationPoints[0].x); set(item, "CalibrationAY", calibrationPoints[0].y);
          set(item, "CalibrationBX", calibrationPoints[1].x); set(item, "CalibrationBY", calibrationPoints[1].y);
          set(item, "CalibrationDistanceInches", inches); calibrationPoints = []; calibrating = false;
          updateScaleFromReference(item); sync(); render(); status.textContent = "Escala calibrada y vinculada al fondo.";
        } else status.textContent = "Marca el segundo punto sobre el fondo.";
        return;
      }
      if (read(item, "IsLocked", true)) { status.textContent = "Desbloquea el fondo antes de transformarlo."; return; }
      pushUndo();
      const start = canvasPoint(event); interaction = {
        kind: event.target.classList.contains("editor-reference-resize") ? "resize" : "move", start,
        x: Number(read(item, "X")), y: Number(read(item, "Y")), width: Number(read(item, "Width")), height: Number(read(item, "Height")),
        ratio: Number(read(item, "Width")) / Number(read(item, "Height"))
      };
      event.currentTarget.setPointerCapture?.(event.pointerId);
    });
    group.addEventListener("keydown", (event) => {
      if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return;
      const item = active(); if (!item || read(item, "IsLocked", true)) return; event.preventDefault(); pushUndo();
      const step = event.shiftKey ? 10 : 1; set(item, "X", Number(read(item, "X")) + (event.key === "ArrowLeft" ? -step : event.key === "ArrowRight" ? step : 0));
      set(item, "Y", Number(read(item, "Y")) + (event.key === "ArrowUp" ? -step : event.key === "ArrowDown" ? step : 0)); clampGeometry(item); sync(); render();
    });
  };
  const ensureGroup = (item) => {
    let group = svg.querySelector("[data-editor-reference-image]");
    if (!group) {
      group = document.createElementNS(ns, "g"); group.classList.add("editor-reference-image"); group.dataset.editorReferenceImage = read(item, "Id"); group.tabIndex = 0; group.setAttribute("role", "button");
      const image = document.createElementNS(ns, "image"); image.setAttribute("preserveAspectRatio", "none"); group.append(image);
      svg.querySelector("[data-editor-grid]")?.before(group); wireGroup(group);
    }
    if (!group.querySelector(".editor-reference-resize")) { const handle = document.createElementNS(ns, "rect"); handle.classList.add("editor-reference-resize"); handle.setAttribute("width", 24); handle.setAttribute("height", 24); group.append(handle); }
    return group;
  };
  const render = () => {
    const item = active(); let group = svg.querySelector("[data-editor-reference-image]");
    if (!item) { group?.remove(); if (controls) controls.hidden = true; return; }
    clampGeometry(item); group = ensureGroup(item); const width = Number(read(item, "Width")); const height = Number(read(item, "Height"));
    const image = group.querySelector("image"); image.setAttribute("href", imageUrl(item)); image.setAttribute("width", width); image.setAttribute("height", height);
    group.dataset.editorReferenceImage = read(item, "Id"); group.setAttribute("transform", `translate(${read(item, "X")} ${read(item, "Y")}) rotate(${read(item, "Rotation", 0)} ${width / 2} ${height / 2})`);
    group.style.opacity = String(read(item, "Opacity", .35)); group.hidden = visibility?.checked === false; group.classList.toggle("is-locked", read(item, "IsLocked", true));
    const handle = group.querySelector(".editor-reference-resize"); handle.setAttribute("x", width - 12); handle.setAttribute("y", height - 12);
    renderCalibration(group, item); if (controls) controls.hidden = false; if (opacity) { opacity.value = String(read(item, "Opacity", .35)); opacity.disabled = read(item, "IsLocked", true); }
    const lockButton = editor.querySelector("[data-reference-lock]"); if (lockButton) lockButton.textContent = read(item, "IsLocked", true) ? "Desbloquear fondo" : "Bloquear fondo";
    sync();
  };
  const restore = (id) => {
    if (tokenField?.value && states.length) { states = states.slice(0, -1); tokenField.value = ""; previewUrl = ""; }
    const restoring = find(id); if (!restoring) return; pushUndo(); const current = active(); if (current) set(current, "IsArchived", true);
    set(restoring, "IsArchived", false); sync(); render(); renderArchived(); status.textContent = "Fondo restaurado. Guarda la revisión para confirmarlo.";
  };
  const renderArchived = () => {
    const container = editor.querySelector("[data-reference-archived-items]"); if (!container) return;
    const items = states.filter((item) => read(item, "IsArchived", false));
    container.replaceChildren(...items.map((item) => { const button = document.createElement("button"); button.type = "button"; button.className = "btn btn-outline-secondary w-100 text-start mb-2"; button.dataset.referenceRestore = read(item, "Id"); button.textContent = `Restaurar ${read(item, "OriginalFileName")}`; button.addEventListener("click", () => restore(read(item, "Id"))); return button; }));
    if (!items.length) { const empty = document.createElement("span"); empty.className = "small text-body-secondary"; empty.textContent = "No hay fondos archivados."; container.append(empty); }
  };
  const scheduleRender = () => { if (!frame) frame = requestAnimationFrame(() => { frame = 0; render(); }); };

  window.addEventListener("pointermove", (event) => {
    if (!interaction) return; const item = active(); if (!item) return; const current = canvasPoint(event);
    if (interaction.kind === "move") { set(item, "X", interaction.x + current.x - interaction.start.x); set(item, "Y", interaction.y + current.y - interaction.start.y); }
    else {
      let width = Math.max(20, interaction.width + current.x - interaction.start.x); let height = width / interaction.ratio;
      if (height > 900 - interaction.y) { height = 900 - interaction.y; width = height * interaction.ratio; }
      set(item, "Width", width); set(item, "Height", height);
    }
    clampGeometry(item); if (interaction.kind === "resize") updateScaleFromReference(item); scheduleRender();
  });
  window.addEventListener("pointerup", () => { if (!interaction) return; interaction = null; sync(); render(); });

  const uploadForm = editor.querySelector("[data-reference-upload-form]");
  uploadForm?.addEventListener("submit", async (event) => {
    event.preventDefault(); const button = uploadForm.querySelector("[data-reference-upload]"); button.disabled = true; button.textContent = "Validando…";
    try {
      const response = await fetch(uploadForm.action, { method: "POST", body: new FormData(uploadForm), headers: { "X-Requested-With": "XMLHttpRequest" } });
      const data = await response.json(); if (!response.ok) throw new Error(data.error || "No se pudo preparar la imagen.");
      pushUndo(); if (tokenField?.value && states.length) states = states.slice(0, -1);
      const current = active(); if (current) set(current, "IsArchived", true);
      const ratio = data.pixelWidth / data.pixelHeight; let width = Math.min(1500, 800 * ratio); let height = width / ratio; if (height > 800) { height = 800; width = height * ratio; }
      states.push({ Id: data.id, OriginalFileName: data.originalFileName, StoredFileName: data.storedFileName, ContentType: data.contentType, Sha256: data.sha256, PixelWidth: data.pixelWidth, PixelHeight: data.pixelHeight, X: (1600 - width) / 2, Y: (900 - height) / 2, Width: width, Height: height, Rotation: 0, Opacity: .35, IsLocked: true, IsArchived: false, CalibrationAX: null, CalibrationAY: null, CalibrationBX: null, CalibrationBY: null, CalibrationDistanceInches: null });
      if (tokenField) tokenField.value = data.token; previewUrl = data.previewUrl; visibility.checked = true; sync(); render(); renderArchived(); status.textContent = "Fondo preparado. Revisa sus cambios antes de guardarlo.";
    } catch (error) { status.textContent = error.message; } finally { button.disabled = false; button.textContent = "Preparar fondo"; }
  });
  opacity?.addEventListener("focus", pushUndo);
  opacity?.addEventListener("input", () => { const item = active(); if (!item) return; set(item, "Opacity", Number(opacity.value)); sync(); scheduleRender(); });
  visibility?.addEventListener("change", () => { localStorage.setItem(visibilityKey, String(visibility.checked)); render(); });
  editor.querySelector("[data-reference-lock]")?.addEventListener("click", () => { const item = active(); if (!item) return; pushUndo(); set(item, "IsLocked", !read(item, "IsLocked", true)); sync(); render(); });
  editor.querySelector("[data-reference-rotate]")?.addEventListener("click", () => { const item = active(); if (!item || read(item, "IsLocked", true)) return; const rotation = (Number(read(item, "Rotation")) + 90) % 360; const rotated = [90, 270].includes(rotation); if ((rotated && (read(item, "Width") > 900 || read(item, "Height") > 1600)) || (!rotated && (read(item, "Width") > 1600 || read(item, "Height") > 900))) { status.textContent = "Redimensiona el fondo antes de girarlo para mantenerlo dentro del lienzo."; return; } pushUndo(); set(item, "Rotation", rotation); clampGeometry(item); sync(); render(); });
  editor.querySelector("[data-reference-fit]")?.addEventListener("click", () => { const item = active(); if (!item || read(item, "IsLocked", true)) return; pushUndo(); const ratio = read(item, "PixelWidth") / read(item, "PixelHeight"); let width = 1600; let height = width / ratio; if (height > 900) { height = 900; width = height * ratio; } set(item, "X", (1600 - width) / 2); set(item, "Y", (900 - height) / 2); set(item, "Width", width); set(item, "Height", height); clampGeometry(item); updateScaleFromReference(item); sync(); render(); });
  editor.querySelector("[data-reference-calibrate]")?.addEventListener("click", () => { const item = active(); if (!item) return; if (read(item, "IsLocked", true)) { status.textContent = "Desbloquea el fondo antes de calibrarlo."; return; } pushUndo(); calibrating = true; calibrationPoints = []; status.textContent = "Marca dos puntos sobre el fondo."; });
  editor.querySelector("[data-reference-unlink-calibration]")?.addEventListener("click", () => { const item = active(); if (!item || read(item, "IsLocked", true)) return; pushUndo(); ["CalibrationAX", "CalibrationAY", "CalibrationBX", "CalibrationBY", "CalibrationDistanceInches"].forEach((name) => set(item, name, null)); sync(); render(); status.textContent = "La escala del plano se conserva, pero ya no está vinculada al fondo."; });
  editor.querySelector("[data-reference-archive]")?.addEventListener("click", () => { const item = active(); if (!item || read(item, "IsLocked", true)) { status.textContent = "Desbloquea el fondo antes de archivarlo."; return; } pushUndo(); if (tokenField?.value && states.at(-1) === item) { states.pop(); tokenField.value = ""; previewUrl = ""; sync(); render(); renderArchived(); status.textContent = "El fondo nuevo se descartó antes de guardarlo."; return; } set(item, "IsArchived", true); sync(); render(); renderArchived(); status.textContent = "Fondo archivado de forma reversible. Guarda la revisión para publicarlo."; });
  editor.querySelectorAll("[data-reference-restore]").forEach((button) => button.addEventListener("click", () => restore(button.dataset.referenceRestore)));

  if (visibility) visibility.checked = localStorage.getItem(visibilityKey) !== "false";
  const initialGroup = svg.querySelector("[data-editor-reference-image]"); if (initialGroup) wireGroup(initialGroup);
  editor.addEventListener("warehouse-map:clear-reference-selection", () => svg.querySelector("[data-editor-reference-image]")?.classList.remove("is-selected"));
  editor.addEventListener("warehouse-map:restore-references", (event) => { try { states = JSON.parse(event.detail?.references || "[]"); } catch { states = []; } previewUrl = event.detail?.token ? `${location.pathname}?handler=ReferencePreview&token=${encodeURIComponent(event.detail.token)}` : ""; render(); renderArchived(); });
  render(); renderArchived();
})();
