(() => {
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (mapRoot) {
    const svg = mapRoot.querySelector("svg");
    const placeholder = mapRoot.querySelector(".map-detail-placeholder");
    let box = { x: 0, y: 0, width: 1600, height: 900 };
    const applyBox = () => svg?.setAttribute("viewBox", `${box.x} ${box.y} ${box.width} ${box.height}`);
    const open = (id) => {
      mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = item.dataset.mapDetail !== id; });
      mapRoot.querySelectorAll("[data-map-open]").forEach((item) => item.classList.toggle("is-selected", item.dataset.mapOpen === id));
      if (placeholder) placeholder.hidden = true;
      const panel = mapRoot.querySelector(`[data-map-detail="${CSS.escape(id)}"]`);
      panel?.querySelector("[data-map-position]")?.click();
      panel?.scrollIntoView({ block: "nearest" });
    };
    mapRoot.querySelectorAll("[data-map-open]").forEach((item) => {
      item.addEventListener("click", () => open(item.dataset.mapOpen));
      item.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(item.dataset.mapOpen); }
      });
    });
    mapRoot.querySelectorAll("[data-map-position]").forEach((button) => button.addEventListener("click", () => {
      const section = button.closest("[data-map-detail]");
      section?.querySelectorAll("[data-position-detail]").forEach((item) => { item.hidden = item.dataset.positionDetail !== button.dataset.mapPosition; });
      section?.querySelectorAll("[data-map-position]").forEach((item) => item.classList.toggle("is-selected", item === button));
    }));
    mapRoot.querySelectorAll("[data-map-close]").forEach((button) => button.addEventListener("click", () => {
      button.closest("[data-map-detail]").hidden = true;
      if (placeholder) placeholder.hidden = false;
    }));
    document.querySelector("[data-map-zoom='in']")?.addEventListener("click", () => { box.width *= .8; box.height *= .8; applyBox(); });
    document.querySelector("[data-map-zoom='out']")?.addEventListener("click", () => { box.width = Math.min(1600, box.width * 1.25); box.height = Math.min(900, box.height * 1.25); applyBox(); });
    document.querySelector("[data-map-fit]")?.addEventListener("click", () => { box = { x: 0, y: 0, width: 1600, height: 900 }; applyBox(); });
    mapRoot.querySelector("[data-map-target='true']")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  }

  const editor = document.querySelector("[data-map-editor]");
  if (!editor) return;
  const ns = "http://www.w3.org/2000/svg";
  const canvas = { width: 1600, height: 900, minimum: 20 };
  const svg = editor.querySelector("[data-editor-svg]");
  const field = editor.querySelector("[data-editor-geometry]");
  const architectureField = editor.querySelector("[data-editor-architecture]");
  const layerStateField = editor.querySelector("[data-editor-layer-state]");
  const scaleField = editor.querySelector("[data-editor-scale]");
  const measurementField = editor.querySelector("[data-editor-measurement-field]");
  const selectionLayer = editor.querySelector("[data-editor-selection]");
  const guidesLayer = editor.querySelector("[data-editor-guides]");
  const layerButtons = [...editor.querySelectorAll("[data-editor-layer-lock]")];
  const selected = new Set();
  let operationalElements = [...editor.querySelectorAll("[data-editor-element]")];
  let architectureElements = [...editor.querySelectorAll("[data-architecture-element]")];
  let elements = [...operationalElements, ...architectureElements];
  let archivedItems = [];
  try {
    archivedItems = JSON.parse(architectureField?.value || "[]").filter((item) => item.IsArchived).map((item) => ({
      id: item.Id, architecture: true, layerCode: item.LayerCode, kind: item.Kind, label: item.Label || "", x: item.X, y: item.Y,
      width: item.Width, height: item.Height, rotation: item.Rotation, radius: item.CornerRadius, points: (item.Points || []).map((point) => `${point.X},${point.Y}`).join(" "),
      strokeToken: item.StrokeToken, fillToken: item.FillToken, strokeWidth: item.StrokeWidth, isDashed: item.IsDashed,
      zIndex: item.ZIndex, isLocked: item.IsLocked, groupId: item.GroupId || "", isArchived: true, isVisible: true, persisted: true
    }));
  } catch { archivedItems = []; }
  let activeElement = null;
  let activeVertex = null;
  let interaction = null;
  let undo = [];
  let redo = [];
  let multiMode = false;
  let tool = "select";
  let spaceHeld = false;
  let viewBox = { x: 0, y: 0, width: canvas.width, height: canvas.height };
  const pointers = new Map();
  const visibilityKey = "warehouseEpi.mapEditor.layers.v1";
  const workspaceKey = "warehouseEpi.mapEditor.workspace.v2";
  const preferences = { gridSize: 25, gridVisible: true, snap: true };
  const number = (element, key) => Number(element.dataset[key] || 0);
  const isArchitecture = (element) => element?.hasAttribute("data-architecture-element");
  const elementId = (element) => isArchitecture(element) ? element.dataset.architectureElement : element.dataset.editorElement;
  const layerCode = (element) => isArchitecture(element) ? element.dataset.layerCode : "OPERATIONS";
  const layerIsLocked = (code) => editor.querySelector(`[data-editor-layer-lock="${CSS.escape(code)}"]`)?.getAttribute("aria-pressed") === "true";
  const layerIsVisible = (code) => editor.querySelector(`[data-editor-layer-visible="${CSS.escape(code)}"]`)?.checked !== false;
  const elementIsLocked = (element) => isArchitecture(element)
    ? element.dataset.elementLocked === "true" || layerIsLocked(layerCode(element))
    : layerIsLocked("OPERATIONS");
  const selectedElements = () => [...selected].filter((item) => elements.includes(item));
  const architectureSelection = () => selectedElements().filter(isArchitecture);
  const operationalSelection = () => selectedElements().filter((element) => !isArchitecture(element));
  const parsePoints = (value) => (value || "").trim().split(/\s+/).filter(Boolean).map((pair) => {
    const [x, y] = pair.split(",").map(Number); return { x, y };
  });
  const pointsText = (points) => points.map((point) => `${point.x},${point.y}`).join(" ");
  const status = (message) => { const output = editor.querySelector("[data-editor-status]"); if (output) output.textContent = message; };
  const applyViewBox = () => {
    viewBox.x = Math.max(0, Math.min(canvas.width - viewBox.width, viewBox.x));
    viewBox.y = Math.max(0, Math.min(canvas.height - viewBox.height, viewBox.y));
    svg.setAttribute("viewBox", `${viewBox.x} ${viewBox.y} ${viewBox.width} ${viewBox.height}`);
    editor.querySelector("[data-editor-zoom]").textContent = `${Math.round(canvas.width / viewBox.width * 100)} %`;
  };
  const point = (event) => {
    const rect = svg.getBoundingClientRect();
    return { x: viewBox.x + (event.clientX - rect.left) * viewBox.width / rect.width, y: viewBox.y + (event.clientY - rect.top) * viewBox.height / rect.height };
  };
  const activeItemSnapshot = () => elements.map((element) => ({
    id: elementId(element), architecture: isArchitecture(element), layerCode: layerCode(element), kind: element.dataset.kind || "Operational", label: element.dataset.label || "",
    x: number(element, "x"), y: number(element, "y"), width: number(element, "width"), height: number(element, "height"), rotation: number(element, "rotation"), radius: number(element, "radius"),
    points: element.dataset.points || "", strokeToken: element.dataset.strokeToken || "NONE", fillToken: element.dataset.fillToken || "NONE", strokeWidth: number(element, "strokeWidth"),
    isDashed: element.dataset.dashed === "true", zIndex: number(element, "z"), isLocked: element.dataset.elementLocked === "true", groupId: element.dataset.groupId || "", isArchived: false, isVisible: element.dataset.visible !== "false", persisted: element.dataset.persisted === "true"
  }));
  const itemSnapshot = () => [...activeItemSnapshot(), ...archivedItems.map((item) => ({ ...item }))];
  const layerSnapshot = () => layerButtons.map((button) => ({ code: button.dataset.editorLayerLock, locked: button.getAttribute("aria-pressed") === "true", visible: layerIsVisible(button.dataset.editorLayerLock) }));
  const snapshot = () => ({ items: itemSnapshot(), layers: layerSnapshot(), scale: scaleField?.value || "", measurementSystem: measurementField?.value || "IMPERIAL" });
  const pushUndo = (state = snapshot()) => {
    undo.push(state); if (undo.length > 50) undo.shift(); redo = [];
    editor.querySelector("[data-editor-undo]").disabled = false; editor.querySelector("[data-editor-redo]").disabled = true;
  };
  const updateFields = () => {
    if (field) field.value = JSON.stringify(activeItemSnapshot().filter((item) => !item.architecture).map((item) => ({ Id: item.id, X: item.x, Y: item.y, Width: item.width, Height: item.height, Rotation: item.rotation, ZIndex: item.zIndex, IsVisible: item.isVisible })));
    if (architectureField) architectureField.value = JSON.stringify(itemSnapshot().filter((item) => item.architecture).map((item) => ({
      Id: item.id, LayerCode: item.layerCode, Kind: item.kind, Label: item.label || null, X: item.x, Y: item.y, Width: item.width, Height: item.height, Rotation: item.rotation,
      CornerRadius: item.radius, Points: parsePoints(item.points).map((point) => ({ X: point.x, Y: point.y })), StrokeToken: item.strokeToken, FillToken: item.fillToken, StrokeWidth: item.strokeWidth,
      IsDashed: item.isDashed, ZIndex: item.zIndex, IsLocked: item.isLocked, GroupId: item.groupId || null, IsArchived: item.isArchived === true
    })));
    if (layerStateField) layerStateField.value = JSON.stringify(layerSnapshot().map((item) => ({ Code: item.code, IsLocked: item.locked })));
  };
  const bounds = (items = selectedElements()) => {
    if (!items.length) return { left: 0, top: 0, right: 0, bottom: 0 };
    return { left: Math.min(...items.map((item) => number(item, "x"))), top: Math.min(...items.map((item) => number(item, "y"))), right: Math.max(...items.map((item) => number(item, "x") + number(item, "width"))), bottom: Math.max(...items.map((item) => number(item, "y") + number(item, "height"))) };
  };
  const clearGuides = () => { guidesLayer.replaceChildren(); };
  const showGuides = (vertical, horizontal) => {
    clearGuides();
    if (vertical !== null) { const line = document.createElementNS(ns, "line"); line.setAttribute("x1", vertical); line.setAttribute("x2", vertical); line.setAttribute("y1", 0); line.setAttribute("y2", canvas.height); guidesLayer.append(line); }
    if (horizontal !== null) { const line = document.createElementNS(ns, "line"); line.setAttribute("x1", 0); line.setAttribute("x2", canvas.width); line.setAttribute("y1", horizontal); line.setAttribute("y2", horizontal); guidesLayer.append(line); }
  };
  const anchors = (excluded = []) => {
    const excludedSet = new Set(excluded);
    return elements.filter((item) => !excludedSet.has(item) && item.dataset.visible !== "false" && layerIsVisible(layerCode(item))).reduce((result, item) => {
      const x = number(item, "x"); const y = number(item, "y"); const width = number(item, "width"); const height = number(item, "height");
      result.x.push(x, x + width / 2, x + width); result.y.push(y, y + height / 2, y + height); return result;
    }, { x: [], y: [] });
  };
  const nearest = (value, values, tolerance) => values.reduce((best, candidate) => Math.abs(candidate - value) < Math.abs(best - value) && Math.abs(candidate - value) <= tolerance ? candidate : best, Number.POSITIVE_INFINITY);
  const snapPoint = (raw, event, excluded = []) => {
    if (!preferences.snap || event?.altKey) return { x: raw.x, y: raw.y, guideX: null, guideY: null };
    const rect = svg.getBoundingClientRect(); const tolerance = 8 * viewBox.width / Math.max(rect.width, 1); const values = anchors(excluded);
    const anchorX = nearest(raw.x, values.x, tolerance); const anchorY = nearest(raw.y, values.y, tolerance);
    return { x: Number.isFinite(anchorX) ? anchorX : Math.round(raw.x / preferences.gridSize) * preferences.gridSize, y: Number.isFinite(anchorY) ? anchorY : Math.round(raw.y / preferences.gridSize) * preferences.gridSize, guideX: Number.isFinite(anchorX) ? anchorX : null, guideY: Number.isFinite(anchorY) ? anchorY : null };
  };
  const normalizePolyline = (globalPoints) => {
    const minX = Math.min(...globalPoints.map((item) => item.x)); const minY = Math.min(...globalPoints.map((item) => item.y)); const maxX = Math.max(...globalPoints.map((item) => item.x)); const maxY = Math.max(...globalPoints.map((item) => item.y));
    return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY), points: globalPoints.map((item) => ({ x: item.x - minX, y: item.y - minY })) };
  };
  const globalPoints = (element) => parsePoints(element.dataset.points).map((item) => ({ x: number(element, "x") + item.x, y: number(element, "y") + item.y }));
  const applyNormalizedPolyline = (element, points) => { const normalized = normalizePolyline(points); Object.assign(element.dataset, { x: normalized.x, y: normalized.y, width: normalized.width, height: normalized.height, points: pointsText(normalized.points) }); };
  const styleClass = (prefix, token) => `architecture-${prefix}-${String(token || "NONE").toLowerCase()}`;
  const renderArchitectureShape = (element) => {
    let shape = element.querySelector("rect, polyline, text"); const kind = element.dataset.kind; const requiredName = kind === "Rectangle" ? "rect" : kind === "Polyline" ? "polyline" : "text";
    if (!shape || shape.localName !== requiredName) { element.replaceChildren(); shape = document.createElementNS(ns, requiredName); element.append(shape); }
    shape.setAttribute("class", `${styleClass("stroke", element.dataset.strokeToken)} ${styleClass("fill", element.dataset.fillToken)}${element.dataset.dashed === "true" ? " is-dashed" : ""}`);
    if (kind === "Rectangle") { shape.setAttribute("width", number(element, "width")); shape.setAttribute("height", number(element, "height")); shape.setAttribute("rx", number(element, "radius")); shape.setAttribute("stroke-width", number(element, "strokeWidth")); }
    else if (kind === "Polyline") { shape.setAttribute("points", element.dataset.points || ""); shape.setAttribute("stroke-width", number(element, "strokeWidth")); }
    else { shape.textContent = element.dataset.label || "Texto"; shape.setAttribute("x", 0); shape.setAttribute("y", 18); }
  };
  const renderElement = (element) => {
    element.setAttribute("transform", `translate(${number(element, "x")} ${number(element, "y")}) rotate(${number(element, "rotation")})`); element.classList.toggle("is-selected", selected.has(element));
    if (isArchitecture(element)) renderArchitectureShape(element);
    else { const rect = element.querySelector("rect:not(.editor-resize-handle)"); const handle = element.querySelector(".editor-resize-handle"); rect?.setAttribute("width", number(element, "width")); rect?.setAttribute("height", number(element, "height")); if (handle) { handle.setAttribute("x", number(element, "width") - 10); handle.setAttribute("y", number(element, "height") - 10); } element.classList.toggle("is-hidden", element.dataset.visible === "false"); }
  };
  const setLayerLocked = (code, locked) => {
    const button = editor.querySelector(`[data-editor-layer-lock="${CSS.escape(code)}"]`); if (!button) return;
    button.setAttribute("aria-pressed", String(locked)); button.textContent = locked ? "Bloqueada" : "Editable"; button.classList.toggle("btn-outline-secondary", locked); button.classList.toggle("btn-outline-primary", !locked);
    editor.querySelectorAll(`[data-layer-code="${CSS.escape(code)}"], [data-architecture-layer="${CSS.escape(code)}"] [data-architecture-element]`).forEach((element) => element.classList.toggle("is-layer-locked", locked));
    if (locked) selectedElements().filter((element) => layerCode(element) === code).forEach((element) => selected.delete(element));
  };
  const applyLayerVisibility = (code, visible) => { editor.querySelectorAll(`[data-architecture-layer="${CSS.escape(code)}"]`).forEach((group) => group.classList.toggle("is-layer-hidden", !visible)); if (!visible) selectedElements().filter((element) => layerCode(element) === code).forEach((element) => selected.delete(element)); };
  const persistVisibility = () => localStorage.setItem(visibilityKey, JSON.stringify(layerSnapshot().map((item) => ({ code: item.code, visible: item.visible }))));
  const restoreVisibility = () => {
    try { JSON.parse(localStorage.getItem(visibilityKey) || "[]").forEach((item) => { const input = editor.querySelector(`[data-editor-layer-visible="${CSS.escape(item.code)}"]`); if (input) input.checked = item.visible !== false; }); } catch { localStorage.removeItem(visibilityKey); }
    layerButtons.forEach((button) => applyLayerVisibility(button.dataset.editorLayerLock, layerIsVisible(button.dataset.editorLayerLock)));
  };
  const applyWorkspacePreferences = () => {
    try { Object.assign(preferences, JSON.parse(localStorage.getItem(workspaceKey) || "{}")); } catch { localStorage.removeItem(workspaceKey); }
    if (![10, 25, 50].includes(Number(preferences.gridSize))) preferences.gridSize = 25;
    editor.querySelector("[data-editor-grid-size]").value = String(preferences.gridSize); editor.querySelector("[data-editor-grid-visible]").checked = preferences.gridVisible !== false; editor.querySelector("[data-editor-snap]").checked = preferences.snap !== false;
    const pattern = editor.querySelector("[data-editor-grid-pattern]"); pattern.setAttribute("width", preferences.gridSize); pattern.setAttribute("height", preferences.gridSize); pattern.querySelector("path").setAttribute("d", `M ${preferences.gridSize} 0 L 0 0 0 ${preferences.gridSize}`); editor.querySelector("[data-editor-grid]").hidden = preferences.gridVisible === false;
  };
  const persistWorkspace = () => { localStorage.setItem(workspaceKey, JSON.stringify(preferences)); applyWorkspacePreferences(); };
  const createArchitectureElement = (item) => {
    const group = editor.querySelector(`[data-architecture-layer="${CSS.escape(item.layerCode)}"]`); if (!group) return null;
    const element = document.createElementNS(ns, "g"); element.setAttribute("class", "map-architecture-element editor-architecture-element"); element.setAttribute("data-architecture-element", item.id); element.setAttribute("tabindex", "0"); element.setAttribute("role", "button");
    Object.assign(element.dataset, { layerCode: item.layerCode, kind: item.kind, label: item.label || "", x: item.x, y: item.y, width: item.width, height: item.height, rotation: item.rotation || 0, radius: item.radius || 0, points: typeof item.points === "string" ? item.points : pointsText(item.points || []), strokeToken: item.strokeToken, fillToken: item.fillToken, strokeWidth: item.strokeWidth, dashed: String(item.isDashed), z: item.zIndex, elementLocked: String(item.isLocked === true), groupId: item.groupId || "", archived: "false", persisted: String(item.persisted === true) });
    element.setAttribute("aria-label", `Elemento arquitectónico ${item.label || item.kind}`); group.append(element); architectureElements.push(element); elements.push(element); bindElement(element); renderElement(element); return element;
  };
  const removeArchitectureElement = (element) => { selected.delete(element); element.remove(); architectureElements = architectureElements.filter((item) => item !== element); elements = elements.filter((item) => item !== element); if (activeElement === element) activeElement = null; };
  const updateProperties = () => {
    const fields = editor.querySelector("[data-editor-properties-fields]"); const empty = editor.querySelector("[data-editor-properties-empty]"); const items = architectureSelection(); fields.hidden = items.length === 0; empty.hidden = items.length !== 0;
    editor.querySelector("[data-editor-discard-new]").disabled = items.length !== 1 || items[0].dataset.persisted === "true"; if (!items.length) return;
    const item = activeElement && items.includes(activeElement) ? activeElement : items[0]; const single = items.length === 1; const kind = item.dataset.kind;
    editor.querySelector("[data-property-kind]").textContent = single ? kind : `${items.length} elementos`; editor.querySelector("[data-property-layer]").textContent = single ? layerCode(item) : "Selección múltiple";
    editor.querySelectorAll("[data-property-group='rectangle']").forEach((group) => { group.hidden = !single || kind !== "Rectangle"; }); editor.querySelectorAll("[data-property-group='text']").forEach((group) => { group.hidden = !single || kind !== "Text"; }); editor.querySelectorAll("[data-property-group='single']").forEach((group) => { group.hidden = !single; });
    ["x", "y", "width", "height", "radius", "rotation", "label"].forEach((key) => { const input = editor.querySelector(`[data-property="${key}"]`); if (input) { input.value = single ? item.dataset[key] || "" : ""; input.disabled = !single; } });
    const labelInput = editor.querySelector("[data-property='label']"); if (labelInput && single && layerCode(item) === "DIMENSIONS") labelInput.disabled = true;
    ["strokeToken", "fillToken", "strokeWidth"].forEach((key) => { const input = editor.querySelector(`[data-property="${key}"]`); const values = [...new Set(items.map((value) => value.dataset[key] || ""))]; input.value = values.length === 1 ? values[0] : ""; });
    const dashed = editor.querySelector("[data-property='dashed']"); const dashedValues = [...new Set(items.map((value) => value.dataset.dashed))]; dashed.indeterminate = dashedValues.length !== 1; dashed.checked = dashedValues.length === 1 && dashedValues[0] === "true";
    const vertexPanel = editor.querySelector("[data-property-vertex]"); vertexPanel.hidden = !single || kind !== "Polyline" || activeVertex === null;
    if (!vertexPanel.hidden) { const values = globalPoints(item)[activeVertex]; editor.querySelector("[data-vertex-property='x']").value = values.x; editor.querySelector("[data-vertex-property='y']").value = values.y; }
  };
  const updateSelection = () => {
    selectionLayer.replaceChildren(); elements.forEach((element) => element.classList.toggle("is-selected", selected.has(element))); const items = selectedElements(); const architectureMode = items.some(isArchitecture);
    editor.querySelector("[data-editor-selection-count]").textContent = items.length ? `${items.length} seleccionado${items.length === 1 ? "" : "s"}${architectureMode ? " de arquitectura" : ""}` : "Sin selección";
    ["rotate", "mirror", "hide"].forEach((action) => { const button = editor.querySelector(`[data-editor-${action}]`); if (button) button.disabled = items.length === 0 || architectureMode; }); editor.querySelector("[data-editor-align-menu]").disabled = architectureMode || items.length < 2; editor.querySelector("[data-editor-distribute-menu]").disabled = items.length < 3 || items.some(elementIsLocked); editor.querySelector("[data-editor-size-menu]").disabled = architectureMode || items.length < 2;
    const architectureOnly = architectureMode && items.length === architectureSelection().length;
    const sameLayer = architectureOnly && new Set(items.map(layerCode)).size === 1;
    const sameGroup = architectureOnly && items[0]?.dataset.groupId && items.every((item) => item.dataset.groupId === items[0].dataset.groupId);
    editor.querySelector("[data-editor-duplicate]").disabled = !architectureOnly || items.some(elementIsLocked);
    editor.querySelector("[data-editor-group]").disabled = !sameLayer || items.length < 2 || items.some((item) => item.dataset.groupId || elementIsLocked(item));
    editor.querySelector("[data-editor-ungroup]").disabled = !sameGroup || items.some(elementIsLocked);
    editor.querySelector("[data-editor-element-lock]").disabled = !architectureOnly || items.some((item) => layerIsLocked(layerCode(item)));
    editor.querySelector("[data-editor-order-menu]").disabled = !sameLayer || items.some(elementIsLocked);
    editor.querySelector("[data-editor-archive]").disabled = !architectureOnly || items.some((item) => item.dataset.persisted !== "true" || elementIsLocked(item));
    if (!items.length) { activeVertex = null; updateProperties(); return; }
    const box = bounds(items); const outline = document.createElementNS(ns, "rect"); outline.setAttribute("class", "editor-group-outline"); outline.setAttribute("x", box.left); outline.setAttribute("y", box.top); outline.setAttribute("width", box.right - box.left); outline.setAttribute("height", box.bottom - box.top); selectionLayer.append(outline);
    if (!architectureMode) { const handle = document.createElementNS(ns, "rect"); handle.setAttribute("class", "editor-group-resize-handle"); handle.setAttribute("data-editor-group-resize", "true"); handle.setAttribute("x", box.right - 10); handle.setAttribute("y", box.bottom - 10); handle.setAttribute("width", 14); handle.setAttribute("height", 14); selectionLayer.append(handle); }
    else if (items.length === 1 && items[0].dataset.kind === "Rectangle") { const handle = document.createElementNS(ns, "rect"); handle.setAttribute("class", "editor-architecture-resize-handle"); handle.setAttribute("data-editor-architecture-resize", "true"); handle.setAttribute("x", box.right - 8); handle.setAttribute("y", box.bottom - 8); handle.setAttribute("width", 16); handle.setAttribute("height", 16); selectionLayer.append(handle); }
    else if (items.length === 1 && items[0].dataset.kind === "Polyline") { globalPoints(items[0]).forEach((value, index) => { const handle = document.createElementNS(ns, "circle"); handle.setAttribute("class", "editor-vertex-handle"); handle.setAttribute("data-editor-vertex", index); handle.setAttribute("cx", value.x); handle.setAttribute("cy", value.y); handle.setAttribute("r", 7); selectionLayer.append(handle); }); }
    updateProperties();
  };
  const renderAll = () => { updateDimensionLabels(); elements.forEach(renderElement); updateSelection(); updateFields(); };
  const renderArchivedList = () => {
    const list = editor.querySelector("[data-editor-archived-list]"); if (!list) return; list.replaceChildren();
    if (!archivedItems.length) { const empty = document.createElement("span"); empty.className = "text-body-secondary"; empty.textContent = "No hay elementos archivados."; list.append(empty); return; }
    archivedItems.forEach((item) => { const button = document.createElement("button"); button.type = "button"; button.className = "btn btn-outline-secondary text-start"; button.dataset.editorRestore = item.id; button.textContent = `Restaurar ${item.label || item.kind}`; button.addEventListener("click", () => restoreArchived(item.id)); list.append(button); });
  };
  const restoreArchived = (id) => {
    const item = archivedItems.find((value) => value.id === id); const restoring = item?.groupId ? archivedItems.filter((value) => value.groupId === item.groupId) : item ? [item] : []; if (!restoring.length || restoring.some((value) => layerIsLocked(value.layerCode) || value.isLocked)) { status("Desbloquea la capa y los elementos antes de restaurarlos."); return; }
    pushUndo(); const ids = new Set(restoring.map((value) => value.id)); archivedItems = archivedItems.filter((value) => !ids.has(value.id)); const restored = restoring.map((value) => { value.isArchived = false; return createArchitectureElement({ ...value, points: value.points }); }); setSelection(restored); renderArchivedList(); renderAll(); status("Elemento o grupo restaurado. Revisa los cambios antes de guardar.");
  };
  const restore = (state) => {
    const architectureState = state.items.filter((item) => item.architecture && !item.isArchived); const architectureIds = new Set(architectureState.map((item) => item.id)); architectureElements.filter((item) => !architectureIds.has(elementId(item))).forEach(removeArchitectureElement); archivedItems = state.items.filter((item) => item.architecture && item.isArchived).map((item) => ({ ...item }));
    state.items.filter((item) => !item.isArchived).forEach((item) => { let element = elements.find((value) => elementId(value) === item.id && isArchitecture(value) === item.architecture); if (!element && item.architecture) element = createArchitectureElement({ ...item, points: parsePoints(item.points), radius: item.radius }); if (!element) return; Object.assign(element.dataset, { layerCode: item.layerCode, kind: item.kind, label: item.label, x: item.x, y: item.y, width: item.width, height: item.height, rotation: item.rotation, radius: item.radius, points: item.points, strokeToken: item.strokeToken, fillToken: item.fillToken, strokeWidth: item.strokeWidth, dashed: String(item.isDashed), z: item.zIndex, elementLocked: String(item.isLocked), groupId: item.groupId || "", archived: "false", visible: String(item.isVisible), persisted: String(item.persisted) }); });
    if (scaleField) scaleField.value = state.scale || ""; if (measurementField) measurementField.value = state.measurementSystem || "IMPERIAL"; const measurement = editor.querySelector("[data-editor-measurement]"); if (measurement) measurement.value = measurementField.value; updateScaleStatus(); renderArchivedList();
    state.layers.forEach((item) => { setLayerLocked(item.code, item.locked); const input = editor.querySelector(`[data-editor-layer-visible="${CSS.escape(item.code)}"]`); if (input) input.checked = item.visible; applyLayerVisibility(item.code, item.visible); }); selected.clear(); activeElement = null; activeVertex = null; persistVisibility(); renderAll();
  };
  const undoAction = () => { if (!undo.length) return; redo.push(snapshot()); restore(undo.pop()); editor.querySelector("[data-editor-redo]").disabled = false; editor.querySelector("[data-editor-undo]").disabled = undo.length === 0; };
  const redoAction = () => { if (!redo.length) return; undo.push(snapshot()); restore(redo.pop()); editor.querySelector("[data-editor-undo]").disabled = false; editor.querySelector("[data-editor-redo]").disabled = redo.length === 0; };
  const clearSelection = () => { selected.clear(); activeElement = null; activeVertex = null; updateSelection(); };
  const setSelection = (items, active = items[0] || null) => { selected.clear(); items.forEach((item) => selected.add(item)); activeElement = active; activeVertex = null; updateSelection(); };
  const toggleSelection = (element) => { const current = selectedElements()[0]; if (current && isArchitecture(current) !== isArchitecture(element)) selected.clear(); if (selected.has(element)) selected.delete(element); else selected.add(element); activeElement = element; activeVertex = null; updateSelection(); };
  const beginTransform = (event, kind) => { interaction = { pointer: event.pointerId, kind, start: point(event), before: snapshot(), box: bounds(), items: selectedElements() }; svg.setPointerCapture(event.pointerId); };
  const snapMove = (rawDx, rawDy, event) => {
    const box = interaction.box; const moving = interaction.items; if (!preferences.snap || event.altKey) return { dx: rawDx, dy: rawDy }; const values = anchors(moving); const rect = svg.getBoundingClientRect(); const tolerance = 8 * viewBox.width / Math.max(rect.width, 1);
    const movingX = [box.left + rawDx, (box.left + box.right) / 2 + rawDx, box.right + rawDx]; const movingY = [box.top + rawDy, (box.top + box.bottom) / 2 + rawDy, box.bottom + rawDy]; let dx = rawDx; let dy = rawDy; let guideX = null; let guideY = null;
    for (const value of movingX) { const target = nearest(value, values.x, tolerance); if (Number.isFinite(target)) { dx += target - value; guideX = target; break; } } for (const value of movingY) { const target = nearest(value, values.y, tolerance); if (Number.isFinite(target)) { dy += target - value; guideY = target; break; } }
    if (guideX === null) dx = Math.round((box.left + rawDx) / preferences.gridSize) * preferences.gridSize - box.left; if (guideY === null) dy = Math.round((box.top + rawDy) / preferences.gridSize) * preferences.gridSize - box.top; showGuides(guideX, guideY); return { dx, dy };
  };
  const moveGroup = (event) => {
    const current = point(event); let { dx, dy } = snapMove(current.x - interaction.start.x, current.y - interaction.start.y, event); dx = Math.max(-interaction.box.left, Math.min(canvas.width - interaction.box.right, dx)); dy = Math.max(-interaction.box.top, Math.min(canvas.height - interaction.box.bottom, dy));
    interaction.before.items.filter((item) => interaction.items.some((element) => elementId(element) === item.id && isArchitecture(element) === item.architecture)).forEach((item) => { const element = elements.find((value) => elementId(value) === item.id && isArchitecture(value) === item.architecture); element.dataset.x = String(item.x + dx); element.dataset.y = String(item.y + dy); });
  };
  const resizeGroup = (event) => {
    const current = point(event); const width = interaction.box.right - interaction.box.left; const height = interaction.box.bottom - interaction.box.top; const items = interaction.before.items.filter((item) => !item.architecture && interaction.items.some((element) => elementId(element) === item.id)); const minScaleX = Math.max(...items.map((item) => canvas.minimum / item.width)); const minScaleY = Math.max(...items.map((item) => canvas.minimum / item.height)); const scaleX = Math.max(minScaleX, Math.min((canvas.width - interaction.box.left) / width, (width + current.x - interaction.start.x) / width)); const scaleY = Math.max(minScaleY, Math.min((canvas.height - interaction.box.top) / height, (height + current.y - interaction.start.y) / height)); items.forEach((item) => { const element = operationalElements.find((value) => elementId(value) === item.id); element.dataset.x = String(interaction.box.left + (item.x - interaction.box.left) * scaleX); element.dataset.y = String(interaction.box.top + (item.y - interaction.box.top) * scaleY); element.dataset.width = String(item.width * scaleX); element.dataset.height = String(item.height * scaleY); });
  };
  const resizeArchitecture = (event) => { const element = interaction.items[0]; const current = snapPoint(point(event), event, [element]); element.dataset.width = String(Math.max(1, Math.min(canvas.width - number(element, "x"), current.x - number(element, "x")))); element.dataset.height = String(Math.max(1, Math.min(canvas.height - number(element, "y"), current.y - number(element, "y")))); };
  const moveVertex = (event) => { const element = interaction.items[0]; const points = interaction.globalPoints.map((item) => ({ ...item })); const value = snapPoint(point(event), event, [element]); points[interaction.vertex] = { x: Math.max(0, Math.min(canvas.width, value.x)), y: Math.max(0, Math.min(canvas.height, value.y)) }; if (interaction.closed && (interaction.vertex === 0 || interaction.vertex === points.length - 1)) { points[0] = { ...points[interaction.vertex] }; points[points.length - 1] = { ...points[interaction.vertex] }; } applyNormalizedPolyline(element, points); activeVertex = interaction.vertex === points.length - 1 && interaction.closed ? 0 : interaction.vertex; showGuides(value.guideX, value.guideY); };
  const toolDefinitions = {
    wall: { layerCode: "STRUCTURE", kind: "Polyline", strokeToken: "SECONDARY", fillToken: "NONE", strokeWidth: 4, isDashed: false }, rectangle: { layerCode: "STRUCTURE", kind: "Rectangle", strokeToken: "SECONDARY", fillToken: "NONE", strokeWidth: 2, isDashed: false }, polygon: { layerCode: "ZONES", kind: "Polyline", strokeToken: "WARNING", fillToken: "WARNING", strokeWidth: 2, isDashed: false, closed: true }, door: { layerCode: "STRUCTURE", kind: "Polyline", strokeToken: "PRIMARY", fillToken: "NONE", strokeWidth: 5, isDashed: true, maximumPoints: 2 }, aisle: { layerCode: "AISLES", kind: "Rectangle", strokeToken: "INFO", fillToken: "NONE", strokeWidth: 2, isDashed: true }, zone: { layerCode: "ZONES", kind: "Rectangle", strokeToken: "WARNING", fillToken: "WARNING", strokeWidth: 2, isDashed: false }, text: { layerCode: "TEXT", kind: "Text", strokeToken: "NONE", fillToken: "SECONDARY", strokeWidth: 0, isDashed: false }, dimension: { layerCode: "DIMENSIONS", special: true }
  };
  const uuid = () => crypto.randomUUID ? crypto.randomUUID() : `${crypto.getRandomValues(new Uint32Array(1))[0].toString(16).padStart(8, "0")}-0000-4000-8000-${crypto.getRandomValues(new Uint32Array(2)).join("").slice(0, 12).padEnd(12, "0")}`;
  const nextZ = () => Math.max(0, ...architectureElements.map((item) => number(item, "z"))) + 1;
  const newElement = (definition, origin) => createArchitectureElement({ id: uuid(), ...definition, label: definition.kind === "Text" ? "Texto" : "", x: origin.x, y: origin.y, width: definition.kind === "Text" ? 160 : 1, height: definition.kind === "Text" ? 24 : 1, radius: 0, points: [], rotation: 0, zIndex: nextZ(), groupId: "", isLocked: false, persisted: false });
  const formatNumber = (value) => Number(value.toFixed(2)).toString();
  const formatDistance = (inches) => {
    if ((measurementField?.value || "IMPERIAL") === "METRIC") { const centimeters = inches * 2.54; return centimeters < 100 ? `${formatNumber(centimeters)} cm` : `${formatNumber(centimeters / 100)} m`; }
    if (inches < 36) return `${formatNumber(inches)} in`; const yards = Math.floor(inches / 36); const remainder = inches - yards * 36; return remainder < .005 ? `${yards} yd` : `${yards} yd ${formatNumber(remainder)} in`;
  };
  const scaleValue = () => { const value = Number(scaleField?.value); return Number.isFinite(value) && value > 0 ? value : null; };
  const updateScaleStatus = () => { const output = editor.querySelector("[data-editor-scale-status]"); if (output) output.textContent = scaleValue() ? `${formatNumber(scaleValue())} unidades SVG/in` : "Sin calibrar"; };
  const updateDimensionLabels = () => {
    const groups = new Set(architectureElements.filter((item) => layerCode(item) === "DIMENSIONS" && item.dataset.groupId).map((item) => item.dataset.groupId)); const scale = scaleValue(); if (!scale) return;
    groups.forEach((groupId) => { const pair = architectureElements.filter((item) => item.dataset.groupId === groupId); const line = pair.find((item) => item.dataset.kind === "Polyline"); const text = pair.find((item) => item.dataset.kind === "Text"); if (!line || !text) return; const points = globalPoints(line); if (points.length < 2) return; const start = points[0]; const end = points.at(-1); text.dataset.label = formatDistance(Math.hypot(end.x - start.x, end.y - start.y) / scale); text.dataset.x = String((start.x + end.x) / 2); text.dataset.y = String((start.y + end.y) / 2 - 12); });
    archivedItems.filter((item) => item.layerCode === "DIMENSIONS" && item.groupId).forEach((item) => { if (item.kind !== "Text") return; const line = archivedItems.find((value) => value.groupId === item.groupId && value.kind === "Polyline"); if (!line) return; const points = parsePoints(line.points); const start = points[0]; const end = points.at(-1); item.label = formatDistance(Math.hypot(end.x - start.x, end.y - start.y) / scale); });
  };
  const calibrationInches = () => { const distance = Number(editor.querySelector("[data-editor-calibration-distance]")?.value); const unit = editor.querySelector("[data-editor-calibration-unit]")?.value; if (!Number.isFinite(distance) || distance <= 0) return null; return unit === "YD" ? distance * 36 : unit === "CM" ? distance / 2.54 : unit === "M" ? distance * 100 / 2.54 : distance; };
  const finishPolyline = () => {
    if (interaction?.kind !== "drawPolyline") return; const minimum = interaction.definition.closed ? 3 : 2; if (interaction.committed.length < minimum) { status(`Agrega al menos ${minimum} puntos.`); return; }
    let points = interaction.committed.map((item) => ({ ...item })); if (interaction.definition.closed) points.push({ ...points[0] }); applyNormalizedPolyline(interaction.element, points); pushUndo(interaction.before); setSelection([interaction.element]); interaction = null; editor.querySelector("[data-editor-finish]").hidden = true; editor.querySelector("[data-editor-cancel]").hidden = true; clearGuides(); status("Trazado finalizado. Guarda la revisión para persistirlo."); renderAll();
  };
  const cancelActiveTool = () => { if (interaction?.kind?.startsWith("draw") || ["calibrate", "dimension"].includes(interaction?.kind)) { if (interaction.before) restore(interaction.before); interaction = null; editor.querySelector("[data-editor-finish]").hidden = true; editor.querySelector("[data-editor-cancel]").hidden = true; status("Operación cancelada."); clearGuides(); return true; } return false; };
  const setTool = (value) => {
    if (interaction?.kind?.startsWith("draw") || ["calibrate", "dimension"].includes(interaction?.kind)) cancelActiveTool(); const definition = toolDefinitions[value]; if (definition && layerIsLocked(definition.layerCode)) { status(`Desbloquea la capa ${definition.layerCode} antes de dibujar.`); return; } if (value === "dimension" && !scaleValue()) { status("Calibra la escala antes de crear cotas."); return; }
    tool = value; editor.querySelectorAll("[data-editor-tool]").forEach((button) => { const active = button.dataset.editorTool === value; button.classList.toggle("btn-primary", active); button.classList.toggle("btn-outline-secondary", !active); button.classList.toggle("active", active); button.setAttribute("aria-pressed", String(active)); }); svg.classList.toggle("is-pan-tool", tool === "pan"); status(`Herramienta ${editor.querySelector(`[data-editor-tool="${CSS.escape(value)}"]`)?.textContent.trim() || value}.`);
  };
  function bindElement(element) {
    element.addEventListener("pointerdown", (event) => { if (tool !== "select" || layerIsLocked(layerCode(element)) || !layerIsVisible(layerCode(element))) return; event.preventDefault(); event.stopPropagation(); const additive = event.ctrlKey || event.metaKey || multiMode; const groupId = isArchitecture(element) ? element.dataset.groupId : ""; const groupItems = groupId && !event.altKey ? architectureElements.filter((item) => item.dataset.groupId === groupId) : [element]; if (additive) groupItems.forEach(toggleSelection); else if (!selected.has(element)) setSelection(groupItems, element); else { activeElement = element; updateSelection(); } if (selected.has(element) && !groupItems.some(elementIsLocked)) beginTransform(event, !isArchitecture(element) && event.target.classList.contains("editor-resize-handle") ? "resize" : "move"); });
  }
  elements.forEach(bindElement);
  selectionLayer.addEventListener("pointerdown", (event) => {
    if (event.target.matches("[data-editor-group-resize]")) { event.preventDefault(); beginTransform(event, "resize"); }
    else if (event.target.matches("[data-editor-architecture-resize]")) { event.preventDefault(); beginTransform(event, "architectureResize"); }
    else if (event.target.matches("[data-editor-vertex]")) { event.preventDefault(); activeVertex = Number(event.target.dataset.editorVertex); const element = architectureSelection()[0]; const parsed = parsePoints(element.dataset.points); interaction = { pointer: event.pointerId, kind: "vertex", before: snapshot(), items: [element], vertex: activeVertex, globalPoints: globalPoints(element), closed: parsed.length > 2 && parsed[0].x === parsed.at(-1).x && parsed[0].y === parsed.at(-1).y }; svg.setPointerCapture(event.pointerId); }
  });
  svg.addEventListener("pointerdown", (event) => {
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY }); if (event.pointerType === "touch" && pointers.size === 2) { const values = [...pointers.values()]; interaction = { kind: "pinch", distance: Math.hypot(values[1].x - values[0].x, values[1].y - values[0].y), box: { ...viewBox } }; event.preventDefault(); return; }
    const targetElement = event.target.closest("[data-editor-element], [data-architecture-element]");
    if (event.target.closest("[data-editor-selection]") || (targetElement && !layerIsLocked(layerCode(targetElement)) && layerIsVisible(layerCode(targetElement)))) return;
    if (tool === "pan" || spaceHeld) { event.preventDefault(); interaction = { pointer: event.pointerId, kind: "pan", clientX: event.clientX, clientY: event.clientY, box: { ...viewBox } }; svg.setPointerCapture(event.pointerId); return; }
    if (tool === "calibrate") {
      const value = snapPoint(point(event), event); if (!interaction || interaction.kind !== "calibrate") { interaction = { kind: "calibrate", before: snapshot(), start: value }; editor.querySelector("[data-editor-cancel]").hidden = false; status("Marca el segundo punto de calibración."); return; }
      const inches = calibrationInches(); const svgDistance = Math.hypot(value.x - interaction.start.x, value.y - interaction.start.y); if (!inches || svgDistance < 1) { status("Captura una distancia real válida y marca dos puntos distintos."); return; } pushUndo(interaction.before); scaleField.value = String(svgDistance / inches); interaction = null; editor.querySelector("[data-editor-cancel]").hidden = true; updateScaleStatus(); updateDimensionLabels(); renderAll(); status("Escala calibrada. Revisa los cambios antes de guardar."); return;
    }
    if (tool === "dimension") {
      if (layerIsLocked("DIMENSIONS") || !scaleValue()) { status("Desbloquea Medidas y calibra la escala antes de crear cotas."); return; }
      const value = snapPoint(point(event), event); if (!interaction || interaction.kind !== "dimension") { interaction = { kind: "dimension", before: snapshot(), start: value }; editor.querySelector("[data-editor-cancel]").hidden = false; status("Marca el segundo extremo de la cota."); return; }
      if (Math.hypot(value.x - interaction.start.x, value.y - interaction.start.y) < 1) { status("Los extremos de la cota deben ser distintos."); return; }
      const groupId = uuid(); const line = createArchitectureElement({ id: uuid(), layerCode: "DIMENSIONS", kind: "Polyline", label: "", x: interaction.start.x, y: interaction.start.y, width: 1, height: 1, rotation: 0, radius: 0, points: [], strokeToken: "PRIMARY", fillToken: "NONE", strokeWidth: 2, isDashed: false, zIndex: nextZ(), groupId, persisted: false }); applyNormalizedPolyline(line, [interaction.start, value]); const text = createArchitectureElement({ id: uuid(), layerCode: "DIMENSIONS", kind: "Text", label: "", x: 0, y: 0, width: 140, height: 24, rotation: 0, radius: 0, points: [], strokeToken: "NONE", fillToken: "PRIMARY", strokeWidth: 0, isDashed: false, zIndex: nextZ(), groupId, persisted: false }); updateDimensionLabels(); pushUndo(interaction.before); interaction = null; editor.querySelector("[data-editor-cancel]").hidden = true; setSelection([line, text], line); renderAll(); status("Cota creada. Revisa los cambios antes de guardar."); return;
    }
    const definition = toolDefinitions[tool]; if (definition) {
      if (definition.special) return;
      if (layerIsLocked(definition.layerCode)) { status(`Desbloquea la capa ${definition.layerCode} antes de dibujar.`); return; } const value = snapPoint(point(event), event); showGuides(value.guideX, value.guideY);
      if (definition.kind === "Text") { const before = snapshot(); const element = newElement(definition, value); pushUndo(before); setSelection([element]); renderAll(); editor.querySelector("[data-property='label']")?.focus(); return; }
      if (definition.kind === "Rectangle") { const before = snapshot(); const element = newElement(definition, value); interaction = { pointer: event.pointerId, kind: "drawRectangle", before, element, start: value, definition }; svg.setPointerCapture(event.pointerId); editor.querySelector("[data-editor-cancel]").hidden = false; return; }
      if (definition.kind === "Polyline") { if (interaction?.kind === "drawPolyline") { interaction.committed.push({ x: value.x, y: value.y }); if (interaction.definition.maximumPoints && interaction.committed.length >= interaction.definition.maximumPoints) finishPolyline(); return; } const before = snapshot(); const element = newElement(definition, value); interaction = { kind: "drawPolyline", before, element, definition, committed: [{ x: value.x, y: value.y }] }; editor.querySelector("[data-editor-finish]").hidden = definition.maximumPoints === 2; editor.querySelector("[data-editor-cancel]").hidden = false; status("Toca para agregar puntos; Enter o Finalizar concluye el trazado."); return; }
    }
    if (tool !== "select") return;
    const previous = selectedElements();
    const additive = event.ctrlKey || event.metaKey || multiMode;
    const selectionType = previous.length ? (isArchitecture(previous[0]) ? "architecture" : "operational") : null;
    if (!additive) clearSelection();
    const start = point(event);
    interaction = { pointer: event.pointerId, kind: "marquee", start, changed: false, additive, selectionType, previous: additive ? previous : [] };
    svg.setPointerCapture(event.pointerId);
  });
  svg.addEventListener("dblclick", (event) => { if (interaction?.kind === "drawPolyline") { event.preventDefault(); if (interaction.committed.length > 1) interaction.committed.pop(); finishPolyline(); } });
  svg.addEventListener("pointermove", (event) => {
    editor.querySelector("[data-editor-coordinates]").textContent = `${Math.round(point(event).x)}, ${Math.round(point(event).y)}`; if (pointers.has(event.pointerId)) pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (interaction?.kind === "pinch" && pointers.size >= 2) { const values = [...pointers.values()]; const distance = Math.hypot(values[1].x - values[0].x, values[1].y - values[0].y); const factor = Math.max(.25, Math.min(4, canvas.width / interaction.box.width * distance / interaction.distance)); const centerX = interaction.box.x + interaction.box.width / 2; const centerY = interaction.box.y + interaction.box.height / 2; viewBox.width = canvas.width / factor; viewBox.height = canvas.height / factor; viewBox.x = centerX - viewBox.width / 2; viewBox.y = centerY - viewBox.height / 2; applyViewBox(); return; }
    if (interaction?.kind === "drawPolyline") { const value = snapPoint(point(event), event); applyNormalizedPolyline(interaction.element, [...interaction.committed, { x: value.x, y: value.y }]); showGuides(value.guideX, value.guideY); renderElement(interaction.element); return; }
    if (!interaction || interaction.pointer !== event.pointerId) return;
    if (interaction.kind === "pan") { const rect = svg.getBoundingClientRect(); viewBox.x = interaction.box.x - (event.clientX - interaction.clientX) * interaction.box.width / rect.width; viewBox.y = interaction.box.y - (event.clientY - interaction.clientY) * interaction.box.height / rect.height; applyViewBox(); return; }
    if (interaction.kind === "drawRectangle") { const value = snapPoint(point(event), event); const x = Math.min(interaction.start.x, value.x); const y = Math.min(interaction.start.y, value.y); Object.assign(interaction.element.dataset, { x, y, width: Math.max(1, Math.abs(value.x - interaction.start.x)), height: Math.max(1, Math.abs(value.y - interaction.start.y)) }); showGuides(value.guideX, value.guideY); renderElement(interaction.element); return; }
    if (interaction.kind === "marquee") { const current = point(event); const left = Math.min(interaction.start.x, current.x); const top = Math.min(interaction.start.y, current.y); const width = Math.abs(interaction.start.x - current.x); const height = Math.abs(interaction.start.y - current.y); let marquee = selectionLayer.querySelector(".editor-marquee"); if (!marquee) { marquee = document.createElementNS(ns, "rect"); marquee.setAttribute("class", "editor-marquee"); selectionLayer.append(marquee); } marquee.setAttribute("x", left); marquee.setAttribute("y", top); marquee.setAttribute("width", width); marquee.setAttribute("height", height); interaction.changed = width > 2 || height > 2; return; }
    if (interaction.kind === "move") moveGroup(event); else if (interaction.kind === "resize") resizeGroup(event); else if (interaction.kind === "architectureResize") resizeArchitecture(event); else if (interaction.kind === "vertex") moveVertex(event); renderAll();
  });
  const endPointer = (event) => {
    pointers.delete(event.pointerId); if (!interaction) return; if (interaction.kind === "pinch") { if (pointers.size < 2) interaction = null; return; } if (interaction.pointer !== event.pointerId) return;
    if (interaction.kind === "drawRectangle") { const valid = number(interaction.element, "width") >= 4 && number(interaction.element, "height") >= 4; if (valid) { pushUndo(interaction.before); setSelection([interaction.element]); status("Elemento nuevo listo para guardar."); } else restore(interaction.before); editor.querySelector("[data-editor-cancel]").hidden = true; interaction = null; clearGuides(); renderAll(); return; }
    if (interaction.kind === "marquee") {
      const marquee = selectionLayer.querySelector(".editor-marquee");
      if (marquee) {
        const left = Number(marquee.getAttribute("x"));
        const top = Number(marquee.getAttribute("y"));
        const right = left + Number(marquee.getAttribute("width"));
        const bottom = top + Number(marquee.getAttribute("height"));
        const intersects = (element) => number(element, "x") < right && number(element, "x") + number(element, "width") > left && number(element, "y") < bottom && number(element, "y") + number(element, "height") > top;
        const operationalMatches = operationalElements.filter((element) => element.dataset.visible !== "false" && !layerIsLocked("OPERATIONS") && layerIsVisible("OPERATIONS") && intersects(element));
        let architectureMatches = architectureElements.filter((element) => !layerIsLocked(layerCode(element)) && layerIsVisible(layerCode(element)) && intersects(element));
        const matchedGroups = new Set(architectureMatches.map((element) => element.dataset.groupId).filter(Boolean));
        if (matchedGroups.size) architectureMatches = architectureElements.filter((element) => architectureMatches.includes(element) || matchedGroups.has(element.dataset.groupId));
        const matches = interaction.selectionType === "architecture"
          ? architectureMatches
          : interaction.selectionType === "operational"
            ? operationalMatches
            : operationalMatches.length ? operationalMatches : architectureMatches;
        const combined = interaction.additive ? [...interaction.previous, ...matches] : matches;
        setSelection([...new Set(combined)]);
        marquee.remove();
      }
      interaction = null;
      return;
    }
    if (interaction.kind !== "pan") pushUndo(interaction.before); interaction = null; clearGuides(); renderAll();
  };
  svg.addEventListener("pointerup", endPointer); svg.addEventListener("pointercancel", endPointer);
  const organize = (minimum, apply, allowArchitecture = false) => { const chosen = selectedElements(); const items = allowArchitecture ? chosen : operationalSelection(); if (items.length < minimum || items.length !== selected.size || items.some(elementIsLocked)) return; pushUndo(); apply(items); renderAll(); };
  const alignmentCoordinate = (mode, box, element) => mode === "left" ? box.left : mode === "center-x" ? (box.left + box.right - number(element, "width")) / 2 : mode === "right" ? box.right - number(element, "width") : mode === "top" ? box.top : mode === "center-y" ? (box.top + box.bottom - number(element, "height")) / 2 : box.bottom - number(element, "height");
  const align = (mode) => organize(2, (items) => { const box = bounds(items); items.forEach((element) => { const coordinate = alignmentCoordinate(mode, box, element); if (["left", "center-x", "right"].includes(mode)) element.dataset.x = String(coordinate); else element.dataset.y = String(coordinate); }); });
  const distributedCenters = (items, axis) => { const sizeKey = axis === "x" ? "width" : "height"; const sorted = [...items].sort((a, b) => number(a, axis) + number(a, sizeKey) / 2 - number(b, axis) - number(b, sizeKey) / 2); const first = number(sorted[0], axis) + number(sorted[0], sizeKey) / 2; const last = number(sorted.at(-1), axis) + number(sorted.at(-1), sizeKey) / 2; return sorted.map((element, index) => ({ element, center: first + (last - first) * index / (sorted.length - 1) })); };
  const distribute = (axis) => organize(3, (items) => distributedCenters(items, axis).forEach(({ element, center }) => { const size = number(element, axis === "x" ? "width" : "height"); element.dataset[axis] = String(center - size / 2); }), true);
  const fitSize = (element, width, height) => ({ width: Math.min(canvas.width, width), height: Math.min(canvas.height, height), x: Math.min(number(element, "x"), canvas.width - Math.min(canvas.width, width)), y: Math.min(number(element, "y"), canvas.height - Math.min(canvas.height, height)) });
  const equalSize = (mode) => organize(2, (items) => { const reference = activeElement || items[0]; items.filter((item) => item !== reference).forEach((item) => { const fitted = fitSize(item, mode === "height" ? number(item, "width") : number(reference, "width"), mode === "width" ? number(item, "height") : number(reference, "height")); Object.assign(item.dataset, { x: fitted.x, y: fitted.y }); if (mode !== "height") item.dataset.width = fitted.width; if (mode !== "width") item.dataset.height = fitted.height; }); });
  const sortSelectedRow = () => { const items = operationalSelection(); const rowCode = items[0]?.dataset.rowCode; if (items.length < 2 || items.length !== selected.size || !rowCode || items.some((element) => element.dataset.visible !== "true" || element.dataset.rowCode !== rowCode || !element.dataset.rackNumber || !Number.isFinite(Number(element.dataset.rackNumber)))) return; pushUndo(); const slots = [...items].sort((a, b) => number(a, "x") - number(b, "x")).map((item) => number(item, "x")); [...items].sort((a, b) => Number(a.dataset.rackNumber) - Number(b.dataset.rackNumber)).forEach((item, index) => { item.dataset.x = String(slots[index]); }); renderAll(); };
  let internalClipboard = [];
  const copyArchitecture = () => { const items = architectureSelection(); if (!items.length || items.length !== selected.size) return; internalClipboard = items.map((item) => activeItemSnapshot().find((value) => value.id === elementId(item))); status(`${items.length} elemento${items.length === 1 ? "" : "s"} copiado${items.length === 1 ? "" : "s"} dentro del editor.`); };
  const pasteArchitecture = () => {
    if (!internalClipboard.length) return; const layerCodes = new Set(internalClipboard.map((item) => item.layerCode)); if ([...layerCodes].some(layerIsLocked)) { status("Desbloquea las capas de destino antes de pegar."); return; } pushUndo(); const groupMap = new Map(); const created = internalClipboard.map((item) => { const groupId = item.groupId ? (groupMap.get(item.groupId) || (groupMap.set(item.groupId, uuid()), groupMap.get(item.groupId))) : ""; return createArchitectureElement({ ...item, id: uuid(), x: Math.min(canvas.width - item.width, item.x + 20), y: Math.min(canvas.height - item.height, item.y + 20), zIndex: nextZ(), groupId, persisted: false, points: item.points }); }); setSelection(created); renderAll(); status("Copia creada con nuevos identificadores.");
  };
  const groupArchitecture = () => { const items = architectureSelection(); if (items.length < 2 || items.some((item) => item.dataset.groupId || elementIsLocked(item)) || new Set(items.map(layerCode)).size !== 1) return; pushUndo(); const groupId = uuid(); items.forEach((item) => { item.dataset.groupId = groupId; }); renderAll(); status("Elementos agrupados."); };
  const ungroupArchitecture = () => { const items = architectureSelection(); const groupId = items[0]?.dataset.groupId; if (!groupId || items.some((item) => item.dataset.groupId !== groupId || elementIsLocked(item))) return; pushUndo(); items.forEach((item) => { item.dataset.groupId = ""; }); renderAll(); status("Grupo separado."); };
  const toggleElementLock = () => { const items = architectureSelection(); if (!items.length || items.some((item) => layerIsLocked(layerCode(item)))) return; pushUndo(); const locked = !items.every((item) => item.dataset.elementLocked === "true"); items.forEach((item) => { item.dataset.elementLocked = String(locked); }); if (locked) clearSelection(); else renderAll(); status(locked ? "Elementos bloqueados." : "Elementos desbloqueados."); };
  const orderArchitecture = (mode) => { const items = architectureSelection(); if (!items.length || new Set(items.map(layerCode)).size !== 1 || items.some(elementIsLocked)) return; pushUndo(); const layerItems = architectureElements.filter((item) => layerCode(item) === layerCode(items[0])).sort((a, b) => number(a, "z") - number(b, "z")); const moving = layerItems.filter((item) => selected.has(item)); let remaining = layerItems.filter((item) => !selected.has(item)); if (mode === "front") remaining = [...remaining, ...moving]; else if (mode === "back") remaining = [...moving, ...remaining]; else { const index = Math.min(...moving.map((item) => layerItems.indexOf(item))); const target = mode === "forward" ? Math.min(remaining.length, index + 1) : Math.max(0, index - 1); remaining.splice(target, 0, ...moving); } remaining.forEach((item, index) => { item.dataset.z = String(index + 1); item.parentElement?.append(item); }); renderAll(); };
  const archiveArchitecture = () => { const items = architectureSelection(); if (!items.length || items.some((item) => item.dataset.persisted !== "true" || elementIsLocked(item))) return; pushUndo(); items.forEach((item) => { const state = activeItemSnapshot().find((value) => value.id === elementId(item)); state.isArchived = true; archivedItems.push(state); removeArchitectureElement(item); }); clearSelection(); renderArchivedList(); renderAll(); status("Elementos archivados de forma reversible."); };
  editor.querySelectorAll("[data-editor-tool]").forEach((button) => button.addEventListener("click", () => setTool(button.dataset.editorTool))); editor.querySelector("[data-editor-finish]").addEventListener("click", finishPolyline); editor.querySelector("[data-editor-cancel]").addEventListener("click", cancelActiveTool);
  editor.querySelector("[data-editor-discard-new]").addEventListener("click", () => { const item = architectureSelection()[0]; if (!item || item.dataset.persisted === "true") { status("Los elementos guardados no pueden eliminarse en esta fase."); return; } pushUndo(); removeArchitectureElement(item); clearSelection(); renderAll(); status("Objeto nuevo descartado."); });
  editor.querySelector("[data-editor-multi]")?.addEventListener("click", (event) => { multiMode = !multiMode; event.currentTarget.classList.toggle("active", multiMode); event.currentTarget.setAttribute("aria-pressed", String(multiMode)); }); editor.querySelectorAll("[data-editor-align]").forEach((button) => button.addEventListener("click", () => align(button.dataset.editorAlign))); editor.querySelectorAll("[data-editor-distribute]").forEach((button) => button.addEventListener("click", () => distribute(button.dataset.editorDistribute))); editor.querySelectorAll("[data-editor-size]").forEach((button) => button.addEventListener("click", () => equalSize(button.dataset.editorSize))); editor.querySelector("[data-editor-sort-row]")?.addEventListener("click", sortSelectedRow);
  editor.querySelector("[data-editor-duplicate]")?.addEventListener("click", () => { copyArchitecture(); pasteArchitecture(); }); editor.querySelector("[data-editor-group]")?.addEventListener("click", groupArchitecture); editor.querySelector("[data-editor-ungroup]")?.addEventListener("click", ungroupArchitecture); editor.querySelector("[data-editor-element-lock]")?.addEventListener("click", toggleElementLock); editor.querySelectorAll("[data-editor-order]").forEach((button) => button.addEventListener("click", () => orderArchitecture(button.dataset.editorOrder))); editor.querySelector("[data-editor-archive]")?.addEventListener("click", archiveArchitecture);
  editor.querySelector("[data-editor-rotate]")?.addEventListener("click", () => { const items = operationalSelection(); if (!items.length || items.length !== selected.size) return; pushUndo(); items.forEach((item) => { item.dataset.rotation = String((number(item, "rotation") + 90) % 360); }); renderAll(); }); editor.querySelector("[data-editor-mirror]")?.addEventListener("click", () => { const items = operationalSelection(); if (!items.length || items.length !== selected.size) return; pushUndo(); const box = bounds(items); items.forEach((item) => { item.dataset.x = String(box.left + box.right - number(item, "x") - number(item, "width")); }); renderAll(); }); editor.querySelector("[data-editor-hide]")?.addEventListener("click", () => { const items = operationalSelection(); if (!items.length || items.length !== selected.size) return; pushUndo(); items.forEach((item) => { item.dataset.visible = "false"; }); clearSelection(); renderAll(); });
  editor.querySelectorAll("[data-editor-place]").forEach((button) => button.addEventListener("click", () => { const item = operationalElements.find((element) => elementId(element) === button.dataset.editorPlace); if (!item || layerIsLocked("OPERATIONS")) return; pushUndo(); Object.assign(item.dataset, { visible: "true", x: "100", y: "100" }); button.hidden = true; setSelection([item]); renderAll(); })); editor.querySelectorAll("[data-editor-layer-visible]").forEach((input) => input.addEventListener("change", () => { applyLayerVisibility(input.dataset.editorLayerVisible, input.checked); persistVisibility(); renderAll(); })); layerButtons.forEach((button) => button.addEventListener("click", () => { pushUndo(); setLayerLocked(button.dataset.editorLayerLock, button.getAttribute("aria-pressed") !== "true"); renderAll(); })); editor.querySelector("[data-editor-undo]").addEventListener("click", undoAction); editor.querySelector("[data-editor-redo]").addEventListener("click", redoAction);
  editor.querySelectorAll("[data-property]").forEach((input) => input.addEventListener("change", () => { const items = architectureSelection(); if (!items.length || items.some(elementIsLocked)) return; const key = input.dataset.property; if (["x", "y", "width", "height", "radius", "rotation", "label"].includes(key) && items.length !== 1) return; pushUndo(); items.forEach((item) => { if (key === "dashed") item.dataset.dashed = String(input.checked); else if (key === "label") { item.dataset.label = input.value.slice(0, 120); item.setAttribute("aria-label", `Elemento arquitectónico ${item.dataset.label || item.dataset.kind}`); } else item.dataset[key] = input.value; }); renderAll(); }));
  editor.querySelectorAll("[data-vertex-property]").forEach((input) => input.addEventListener("change", () => { const item = architectureSelection()[0]; if (!item || activeVertex === null || elementIsLocked(item)) return; const points = globalPoints(item); pushUndo(); points[activeVertex][input.dataset.vertexProperty] = Math.max(0, Math.min(input.dataset.vertexProperty === "x" ? canvas.width : canvas.height, Number(input.value))); if (points.length > 2 && points[0].x === points.at(-1).x && points[0].y === points.at(-1).y && (activeVertex === 0 || activeVertex === points.length - 1)) { points[0] = { ...points[activeVertex] }; points[points.length - 1] = { ...points[activeVertex] }; } applyNormalizedPolyline(item, points); renderAll(); }));
  editor.querySelector("[data-editor-grid-size]").addEventListener("change", (event) => { preferences.gridSize = Number(event.target.value); persistWorkspace(); }); editor.querySelector("[data-editor-grid-visible]").addEventListener("change", (event) => { preferences.gridVisible = event.target.checked; persistWorkspace(); }); editor.querySelector("[data-editor-snap]").addEventListener("change", (event) => { preferences.snap = event.target.checked; persistWorkspace(); });
  editor.querySelector("[data-editor-measurement]")?.addEventListener("change", (event) => { pushUndo(); measurementField.value = event.target.value; updateDimensionLabels(); renderAll(); status("Sistema de presentación actualizado; la escala no cambió."); });
  const zoomTo = (factor, center = { x: viewBox.x + viewBox.width / 2, y: viewBox.y + viewBox.height / 2 }) => { const zoom = Math.max(.25, Math.min(4, canvas.width / viewBox.width * factor)); const width = canvas.width / zoom; const height = canvas.height / zoom; const ratioX = (center.x - viewBox.x) / viewBox.width; const ratioY = (center.y - viewBox.y) / viewBox.height; viewBox = { x: center.x - width * ratioX, y: center.y - height * ratioY, width, height }; applyViewBox(); };
  editor.querySelector("[data-editor-zoom-in]").addEventListener("click", () => zoomTo(1.25)); editor.querySelector("[data-editor-zoom-out]").addEventListener("click", () => zoomTo(.8)); editor.querySelector("[data-editor-fit]").addEventListener("click", () => { viewBox = { x: 0, y: 0, width: canvas.width, height: canvas.height }; applyViewBox(); }); svg.addEventListener("wheel", (event) => { event.preventDefault(); zoomTo(event.deltaY < 0 ? 1.1 : .9, point(event)); }, { passive: false });
  const reviewButton = editor.querySelector("[data-editor-review-button]"); const reviewModalElement = editor.querySelector("[data-editor-review-modal]"); const saveReviewed = editor.querySelector("[data-editor-save-reviewed]"); const reviewPin = editor.querySelector("[data-editor-review-pin]"); const warningAck = editor.querySelector("[data-editor-warning-ack]"); let reviewHasWarnings = false;
  const updateReviewedSave = () => { if (saveReviewed) saveReviewed.disabled = !reviewPin?.value.trim() || (reviewHasWarnings && !warningAck?.checked); };
  reviewPin?.addEventListener("input", updateReviewedSave); warningAck?.addEventListener("change", updateReviewedSave);
  reviewButton?.addEventListener("click", async () => {
    updateFields(); reviewHasWarnings = false; if (reviewPin) reviewPin.value = ""; if (warningAck) warningAck.checked = false; updateReviewedSave(); const modal = bootstrap.Modal.getOrCreateInstance(reviewModalElement); modal.show(); const statusOutput = editor.querySelector("[data-editor-review-status]"); const content = editor.querySelector("[data-editor-review-content]"); const warningBox = editor.querySelector("[data-editor-review-warnings]"); statusOutput.hidden = false; statusOutput.textContent = "Validando el plano en el servidor…"; content.hidden = true;
    try { const form = editor.querySelector("[data-map-save-form]"); const response = await fetch(`${form.action}?handler=Review`, { method: "POST", body: new FormData(form), headers: { "X-Requested-With": "XMLHttpRequest" } }); const data = await response.json(); if (!response.ok || data.errors?.length) { statusOutput.textContent = (data.errors || ["No fue posible revisar el plano."]).join(" "); return; } const summary = data.summary; const summaryList = editor.querySelector("[data-editor-review-summary]"); const rows = [["Ubicaciones modificadas", summary.operationalModified], ["Capas modificadas", summary.layerLocksChanged], ["Arquitectura nueva", summary.added], ["Arquitectura modificada", summary.modified], ["Archivados", summary.archived], ["Restaurados", summary.restored], ["Escala", summary.scaleChanged ? "Cambiará" : "Sin cambio"], ["Unidades", summary.measurementSystemChanged ? "Cambiarán" : "Sin cambio"]]; summaryList.replaceChildren(...rows.flatMap(([label, value]) => { const term = document.createElement("dt"); term.className = "col-7"; term.textContent = label; const detail = document.createElement("dd"); detail.className = "col-5"; detail.textContent = String(value); return [term, detail]; })); const warnings = data.warnings || []; reviewHasWarnings = warnings.length > 0; warningBox.hidden = !reviewHasWarnings; const list = warningBox.querySelector("ul"); list.replaceChildren(...warnings.map((warning) => { const item = document.createElement("li"); item.textContent = `${warning.message} Elementos: ${warning.elementIds.join(", ")}.`; return item; })); statusOutput.hidden = true; content.hidden = false; updateReviewedSave(); reviewPin?.focus(); } catch { statusOutput.textContent = "No se pudo completar la revisión. Conservamos el borrador; inténtalo nuevamente."; }
  });
  window.addEventListener("keydown", (event) => {
    if (event.code === "Space" && !event.target.matches("input, textarea, select")) { spaceHeld = true; event.preventDefault(); } if (event.target.matches("input, textarea, select")) return; const modifier = event.ctrlKey || event.metaKey;
    if (modifier && event.key.toLowerCase() === "z") { event.preventDefault(); if (event.shiftKey) redoAction(); else undoAction(); return; } if (modifier && event.key.toLowerCase() === "y") { event.preventDefault(); redoAction(); return; } if (modifier && event.key.toLowerCase() === "c") { event.preventDefault(); copyArchitecture(); return; } if (modifier && event.key.toLowerCase() === "v") { event.preventDefault(); pasteArchitecture(); return; } if (event.key === "Escape") { if (!cancelActiveTool()) { clearSelection(); setTool("select"); } return; } if (event.key === "Enter" && interaction?.kind === "drawPolyline") { event.preventDefault(); finishPolyline(); return; }
    if ((event.key === "Delete" || event.key === "Backspace") && architectureSelection().length === 1) { const item = architectureSelection()[0]; if (item.dataset.persisted !== "true") { event.preventDefault(); editor.querySelector("[data-editor-discard-new]").click(); } else status("Los elementos guardados no pueden eliminarse en esta fase."); return; }
    if (!selected.size || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return; event.preventDefault(); pushUndo(); const step = event.shiftKey ? 10 : 1; const box = bounds(); const dx = event.key === "ArrowLeft" ? -step : event.key === "ArrowRight" ? step : 0; const dy = event.key === "ArrowUp" ? -step : event.key === "ArrowDown" ? step : 0; const offsetX = Math.max(-box.left, Math.min(canvas.width - box.right, dx)); const offsetY = Math.max(-box.top, Math.min(canvas.height - box.bottom, dy)); selectedElements().forEach((item) => { item.dataset.x = String(number(item, "x") + offsetX); item.dataset.y = String(number(item, "y") + offsetY); }); renderAll();
  });
  window.addEventListener("keyup", (event) => { if (event.code === "Space") spaceHeld = false; }); editor.querySelector("[data-map-save-form]")?.addEventListener("submit", (event) => { updateFields(); const button = event.submitter; if (button) { button.disabled = true; button.textContent = "Guardando…"; } });
  restoreVisibility(); applyWorkspacePreferences(); layerButtons.forEach((button) => setLayerLocked(button.dataset.editorLayerLock, button.getAttribute("aria-pressed") === "true")); if (measurementField) editor.querySelector("[data-editor-measurement]").value = measurementField.value || "IMPERIAL"; updateScaleStatus(); renderArchivedList(); applyViewBox(); renderAll();
})();
