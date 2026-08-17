// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(() => {
  document.querySelectorAll("[data-print-validation]").forEach((button) => {
    button.addEventListener("click", () => window.print());
  });

  const blockLocationId = document.getElementById("blockLocationId");
  const blockLocationText = document.getElementById("blockLocationText");
  const blockReason = document.getElementById("blockReason");
  document.querySelectorAll(".block-location").forEach((button) => {
    button.addEventListener("click", () => {
      if (!blockLocationId || !blockLocationText || !blockReason) return;
      blockLocationId.value = button.dataset.locationId || "";
      blockLocationText.textContent = `Ubicación ${button.dataset.locationCode || ""}`;
      blockReason.value = "";
    });
  });

  const correctionForm = document.getElementById("correction-form");
  correctionForm?.addEventListener("submit", (event) => {
    const button = event.currentTarget.querySelector("button[type='submit']");
    if (!button) return;
    button.disabled = true;
    button.textContent = "Confirmando…";
  });
})();
