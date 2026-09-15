(() => {
    "use strict";
    const root = document.querySelector("[data-supply-queue]");
    if (!root) return;
    const initial = Number(root.dataset.pendingOrders || "0");
    const refresh = async () => {
        if (document.hidden || root.querySelector("input:focus, select:focus, details[open]")) return;
        try {
            const response = await fetch(root.dataset.snapshotUrl, { headers: { Accept: "application/json" }, cache: "no-store" });
            if (!response.ok) return;
            const snapshot = await response.json();
            if (Number(snapshot.pendingOrders) !== initial) window.location.reload();
        } catch { /* La cola conserva su estado y vuelve a intentar. */ }
    };
    window.setInterval(refresh, 30000);
})();
