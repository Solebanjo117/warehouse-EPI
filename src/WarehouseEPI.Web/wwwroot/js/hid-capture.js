(() => {
  const editableSelector = 'input, textarea, select, [contenteditable]:not([contenteditable="false"])';

  const listen = ({ root, isEnabled = () => true, onScan, maxGapMs = 100, resetMs = 250, minLength = 2 }) => {
    if (!(root instanceof Element) || typeof onScan !== "function") return () => {};

    let buffer = "";
    let lastKeyAt = 0;
    let resetTimer;
    let resolving = false;

    const reset = () => {
      buffer = "";
      lastKeyAt = 0;
      window.clearTimeout(resetTimer);
      resetTimer = undefined;
    };

    const accepts = (event) => {
      const target = event.target;
      if (!(target instanceof Element) || (target !== document.body && !root.contains(target))) return false;
      if (document.querySelector(".modal.show")) return false;
      if (target.closest(editableSelector)) return false;
      return !event.isComposing && !event.repeat && !event.ctrlKey && !event.altKey && !event.metaKey;
    };

    const keydown = (event) => {
      if (resolving || !isEnabled() || !accepts(event)) {
        reset();
        return;
      }

      const now = performance.now();
      if (event.key === "Enter") {
        const code = buffer;
        const isValid = code.length >= minLength && now - lastKeyAt <= maxGapMs;
        reset();
        if (!isValid) return;

        event.preventDefault();
        event.stopPropagation();
        resolving = true;
        void Promise.resolve()
          .then(() => onScan(code))
          .catch(error => console.error("No fue posible procesar el escaneo HID.", error))
          .finally(() => { resolving = false; });
        return;
      }

      if (event.key === "Shift") return;
      if (event.key === " ") {
        reset();
        return;
      }
      if (event.key === "Escape" || event.key.length !== 1) {
        reset();
        return;
      }

      if (buffer && now - lastKeyAt > maxGapMs) reset();
      buffer += event.key;
      lastKeyAt = now;
      event.preventDefault();
      window.clearTimeout(resetTimer);
      resetTimer = window.setTimeout(reset, resetMs);
    };

    const stop = () => {
      reset();
      document.removeEventListener("keydown", keydown, { capture: true });
      window.removeEventListener("pagehide", reset);
    };

    document.addEventListener("keydown", keydown, { capture: true });
    window.addEventListener("pagehide", reset);
    return stop;
  };

  window.WarehouseEpiHidCapture = { listen };
})();
