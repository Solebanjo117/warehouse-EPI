// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(() => {
  const themeKey = "warehouseEpi.theme";
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  const applyTheme = () => {
    const selected = localStorage.getItem(themeKey) || "system";
    document.documentElement.dataset.bsTheme = selected === "system" ? (systemTheme.matches ? "dark" : "light") : selected;
    document.querySelectorAll("[data-theme-choice]").forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.themeChoice === selected)));
  };
  document.querySelectorAll("[data-theme-choice]").forEach((button) => button.addEventListener("click", () => {
    localStorage.setItem(themeKey, button.dataset.themeChoice || "system");
    applyTheme();
  }));
  systemTheme.addEventListener("change", () => {
    if ((localStorage.getItem(themeKey) || "system") === "system") applyTheme();
  });
  applyTheme();

  const body = document.body;
  const sidebar = document.getElementById("app-sidebar");
  const menuButton = document.querySelector("[data-nav-toggle]");
  const collapseButton = document.querySelector("[data-nav-collapse]");
  const navDismiss = document.querySelector("[data-nav-dismiss]");
  const desktopMedia = window.matchMedia("(min-width: 1200px)");

  const setDrawerOpen = (isOpen) => {
    body.classList.toggle("nav-open", isOpen);
    menuButton?.setAttribute("aria-expanded", String(isOpen));
    if (isOpen) sidebar?.querySelector(".app-nav-link")?.focus();
    else if (document.activeElement && sidebar?.contains(document.activeElement)) menuButton?.focus();
  };

  const setCollapsed = (isCollapsed, persist = true) => {
    body.classList.toggle("nav-collapsed", isCollapsed);
    collapseButton?.setAttribute("aria-expanded", String(!isCollapsed));
    collapseButton?.setAttribute("title", isCollapsed ? "Expandir menú" : "Contraer menú");
    const accessibleLabel = collapseButton?.querySelector(".visually-hidden");
    if (accessibleLabel) accessibleLabel.textContent = isCollapsed ? "Expandir menú" : "Contraer menú";
    if (persist && desktopMedia.matches) localStorage.setItem("warehouseEpi.navCollapsed", String(isCollapsed));
  };

  if (desktopMedia.matches) {
    setCollapsed(localStorage.getItem("warehouseEpi.navCollapsed") === "true", false);
  } else {
    setCollapsed(true, false);
  }

  menuButton?.addEventListener("click", () => setDrawerOpen(!body.classList.contains("nav-open")));
  navDismiss?.addEventListener("click", () => setDrawerOpen(false));
  collapseButton?.addEventListener("click", () => {
    if (desktopMedia.matches) setCollapsed(!body.classList.contains("nav-collapsed"));
    else setDrawerOpen(!body.classList.contains("nav-open"));
  });
  sidebar?.querySelectorAll("a").forEach((link) => link.addEventListener("click", () => {
    if (!desktopMedia.matches) setDrawerOpen(false);
  }));
  window.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && body.classList.contains("nav-open")) setDrawerOpen(false);
  });
  desktopMedia.addEventListener("change", (event) => {
    setDrawerOpen(false);
    setCollapsed(event.matches && localStorage.getItem("warehouseEpi.navCollapsed") === "true", false);
  });

  const cameraScanner = document.querySelector("[data-camera-scanner]");
  cameraScanner?.addEventListener("show.bs.modal", () => body.classList.add("camera-active"));
  cameraScanner?.addEventListener("hidden.bs.modal", () => body.classList.remove("camera-active"));

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

  const copyStatus = document.querySelector("[data-copy-status]");
  const announceCopy = (message) => {
    if (copyStatus) copyStatus.textContent = message;
  };
  const fallbackCopy = (value) => {
    const textarea = document.createElement("textarea");
    textarea.value = value;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.append(textarea);
    textarea.select();
    const copied = document.execCommand("copy");
    textarea.remove();
    return copied;
  };
  document.addEventListener("click", async (event) => {
    const button = event.target.closest("[data-copy-value]");
    if (!button) return;
    const value = button.dataset.copyValue || "";
    if (!value) { announceCopy("No hay valor para copiar."); return; }
    try {
      if (navigator.clipboard?.writeText && window.isSecureContext) await navigator.clipboard.writeText(value);
      else if (!fallbackCopy(value)) throw new Error("copy-failed");
      announceCopy("Identificador completo copiado.");
    } catch {
      announceCopy("No fue posible copiar el identificador.");
    }
  });
})();
