"use strict";
(() => {
    const board = document.querySelector("[data-backup-running='true']");
    if (!board) return;
    const poll = async () => {
        if (!document.hidden) {
            try {
                const response = await fetch(board.dataset.backupStatusUrl, { cache: "no-store", credentials: "same-origin", redirect: "error" });
                if (response.status === 401 || response.status === 403) return;
                if (response.ok) {
                    const status = await response.json();
                    if (!status.running) { window.location.reload(); return; }
                }
            } catch { /* A transient network failure must not create another backup. */ }
        }
        window.setTimeout(poll, 5000);
    };
    window.setTimeout(poll, 5000);
})();
