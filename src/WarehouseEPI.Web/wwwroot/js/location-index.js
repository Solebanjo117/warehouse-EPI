(() => {
  const form = document.querySelector("[data-location-search-form]");
  const input = form?.querySelector("[data-location-search-input]");
  if (!form || !input) return;

  window.WarehouseEpiHidCapture?.listen({
    root: document.body,
    onScan: (code) => {
      input.value = code;
      form.requestSubmit();
    }
  });
})();
