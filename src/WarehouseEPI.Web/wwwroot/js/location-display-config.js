(() => {
  const form = document.querySelector("[data-display-config]");
  if (!form) return;
  const texts = JSON.parse(form.dataset.uiTexts || "{}");
  const translate = (key, ...args) => (texts[key] || key).replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match);
  const rows = [...form.querySelectorAll('input[name="rows"]')];
  function validateRows() {
    rows[0]?.setCustomValidity(rows.some(row => row.checked) ? "" : translate("Selecciona al menos una fila."));
  }
  rows.forEach(row => row.addEventListener("change", validateRows));
  validateRows();

  const orientations = [...form.querySelectorAll('input[name="orientation"]')];
  const racks = [...form.querySelectorAll('input[type="radio"][name="racks"]')];
  const savedRacks = form.querySelector("[data-display-saved-racks]");
  const preview = form.querySelector("[data-display-preview]");
  const caption = form.querySelector("[data-display-preview-caption]");
  const racksHelp = form.querySelector("[data-display-racks-help]");
  const narrow = window.matchMedia("(max-width: 899.98px)");
  const portrait = window.matchMedia("(orientation: portrait)");
  function updatePreview() {
    const orientation = orientations.find(input => input.checked)?.value || "auto";
    const vertical = orientation === "portrait" || narrow.matches || orientation === "auto" && portrait.matches;
    const count = vertical ? 1 : Number(savedRacks.value);
    const forced = orientation === "portrait";
    savedRacks.disabled = !forced;
    racks.forEach(input => {
      input.disabled = forced;
      input.checked = Number(input.value) === (forced ? 1 : Number(savedRacks.value));
    });
    preview.dataset.previewOrientation = vertical ? "portrait" : "landscape";
    preview.dataset.previewRacks = String(count);
    caption.textContent = translate("Vista previa: {0}, {1} y croquis {2}.", vertical ? translate("vertical") : translate("horizontal"), count === 1 ? translate("un rack") : translate("{0} racks", count), !vertical && count === 1 && !window.matchMedia("(max-width: 1199.98px)").matches ? translate("al lado") : translate("debajo"));
    racksHelp.textContent = forced
      ? translate("En vertical se muestra un rack para facilitar la lectura. La cantidad elegida se conserva para horizontal.")
      : translate("En vertical o en pantallas estrechas se muestra un rack. La cantidad elegida se conserva para horizontal.");
  }
  orientations.forEach(input => input.addEventListener("change", updatePreview));
  racks.forEach(input => input.addEventListener("change", () => {
    if (input.checked) savedRacks.value = input.value;
    updatePreview();
  }));
  narrow.addEventListener("change", updatePreview);
  portrait.addEventListener("change", updatePreview);
  window.matchMedia("(max-width: 1199.98px)").addEventListener("change", updatePreview);
  updatePreview();

  const map = form.querySelector("[data-display-here-map]");
  if (!map) return;
  const input = form.querySelector("[data-display-here-input]");
  const pin = map.querySelector("[data-display-here-pin]");
  const status = form.querySelector("[data-display-here-status]");
  const bounds = map.viewBox.baseVal;
  function mark(x, y) {
    x = Math.round(Math.max(bounds.x, Math.min(bounds.x + bounds.width, x)));
    y = Math.round(Math.max(bounds.y, Math.min(bounds.y + bounds.height, y)));
    input.value = `${x},${y}`;
    pin.setAttribute("transform", `translate(${x} ${y})`);
    pin.classList.remove("is-empty");
    const left = x > Number(pin.dataset.mapMiddle);
    const text = pin.querySelector("text");
    text.setAttribute("x", (left ? -1 : 1) * Number(pin.dataset.pinRadius) * 1.5);
    text.setAttribute("text-anchor", left ? "end" : "start");
    status.textContent = translate("Ubicación del monitor marcada.");
  }
  map.addEventListener("click", event => {
    const matrix = map.getScreenCTM();
    if (!matrix) return;
    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
    // Ignore the letterboxed area outside the drawn map.
    if (point.x < bounds.x || point.x > bounds.x + bounds.width || point.y < bounds.y || point.y > bounds.y + bounds.height) return;
    mark(point.x, point.y);
  });
  map.addEventListener("keydown", event => {
    const directions = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
    const direction = directions[event.key];
    if (!direction) return;
    event.preventDefault();
    const point = input.value ? input.value.split(',').map(Number) : [bounds.x + bounds.width / 2, bounds.y + bounds.height / 2];
    const step = Math.max(1, Math.min(bounds.width, bounds.height) / 50);
    mark(point[0] + direction[0] * step, point[1] + direction[1] * step);
  });
  form.querySelector("[data-display-here-clear]").addEventListener("click", () => {
    input.value = "";
    pin.classList.add("is-empty");
    status.textContent = translate("Sin ubicación del monitor.");
  });
})();
