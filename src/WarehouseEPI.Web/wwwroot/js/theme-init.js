(() => {
  const selected = localStorage.getItem("warehouseEpi.theme") || "system";
  document.documentElement.dataset.bsTheme = selected === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : selected;
})();
