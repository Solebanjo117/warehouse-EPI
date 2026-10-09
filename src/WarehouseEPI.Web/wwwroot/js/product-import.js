(() => {
    "use strict";
    const panel = document.getElementById("internal-inventory-form");
    const form = document.querySelector("[data-internal-inventory-form]");
    if (!panel || !form) return;
    panel.addEventListener("shown.bs.collapse", () => form.querySelector("input[type=password]")?.focus());
    form.addEventListener("submit", () => {
        form.querySelector("button[type=submit]").disabled = true;
        form.querySelector("[data-inventory-loading]").hidden = false;
        form.setAttribute("aria-busy", "true");
    });
    window.addEventListener("pageshow", () => {
        form.reset();
        form.querySelector("button[type=submit]").disabled = false;
        form.querySelector("[data-inventory-loading]").hidden = true;
        form.removeAttribute("aria-busy");
    });
    form.querySelector("[role=alert]")?.focus();
})();
