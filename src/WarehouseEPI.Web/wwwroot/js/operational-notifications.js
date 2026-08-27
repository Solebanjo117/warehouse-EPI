(() => {
  "use strict";
  const root = document.querySelector("[data-notifications]");
  if (!root) return;
  const list = root.querySelector("[data-notification-list]");
  const empty = root.querySelector("[data-notification-empty]");
  const summary = root.querySelector("[data-notification-summary]");
  const live = root.querySelector("[data-notification-live]");
  const refreshButton = root.querySelector("[data-notification-refresh]");
  const intervalMilliseconds = 60000;
  let timerId;
  let requestInProgress = false;
  let previousCounts = null;

  const setBadges = (count) => document.querySelectorAll("[data-notification-count]").forEach((badge) => {
    badge.textContent = count > 99 ? "99+" : String(count);
    badge.hidden = count === 0;
  });
  const severityName = (severity) => severity === 0 ? "Crítica" : severity === 1 ? "Advertencia" : "Información";
  const severityClass = (severity) => severity === 0 ? "is-critical" : severity === 1 ? "is-warning" : "is-information";
  const appendText = (parent, tag, text, className) => { const element = document.createElement(tag); element.textContent = text; if (className) element.className = className; parent.append(element); return element; };

  const render = (snapshot) => {
    list.replaceChildren();
    const currentCounts = new Map();
    snapshot.items.forEach((item) => {
      currentCounts.set(String(item.category), item.count);
      const article = document.createElement("article"); article.className = `notification-item ${severityClass(item.severity)}`;
      const heading = document.createElement("div"); heading.className = "notification-item-heading";
      appendText(heading, "span", severityName(item.severity), "notification-severity");
      appendText(heading, "strong", String(item.count), "notification-item-count"); article.append(heading);
      appendText(article, "h3", item.title, "h6"); appendText(article, "p", item.description);
      const link = appendText(article, "a", "Revisar alerta", "stretched-link"); link.href = item.targetUrl;
      list.append(article);
    });
    if (previousCounts) {
      const increases = snapshot.items.filter((item) => item.count > (previousCounts.get(String(item.category)) || 0));
      if (increases.length) live.textContent = `${increases.length} categoría(s) de alerta aumentaron.`;
    }
    previousCounts = currentCounts;
    setBadges(snapshot.totalVisible);
    empty.hidden = snapshot.totalVisible !== 0;
    list.hidden = snapshot.totalVisible === 0;
    const generated = new Date(snapshot.generatedAtLocal);
    summary.textContent = `Actualizado ${generated.toLocaleString("es-MX", { dateStyle: "short", timeStyle: "short" })} · ${snapshot.totalVisible} condición(es)`;
    summary.classList.remove("is-stale");
  };
  const schedule = () => { window.clearTimeout(timerId); if (!document.hidden) timerId = window.setTimeout(() => refresh(false), intervalMilliseconds); };
  const refresh = async (manual) => {
    if (requestInProgress) return;
    requestInProgress = true; refreshButton.disabled = true; root.setAttribute("aria-busy", "true");
    try {
      const url = new URL(root.dataset.snapshotUrl, window.location.origin); if (manual) url.searchParams.set("refresh", "true");
      const response = await fetch(url, { cache: "no-store", headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      render(await response.json());
    } catch {
      summary.textContent = "Datos desactualizados · vuelve a intentar"; summary.classList.add("is-stale");
    } finally { requestInProgress = false; refreshButton.disabled = false; root.removeAttribute("aria-busy"); schedule(); }
  };
  refreshButton.addEventListener("click", () => refresh(true));
  document.addEventListener("visibilitychange", () => { if (document.hidden) window.clearTimeout(timerId); else refresh(false); });
  root.addEventListener("show.bs.offcanvas", () => document.querySelectorAll("[data-notification-trigger]").forEach((x) => x.setAttribute("aria-expanded", "true")));
  root.addEventListener("hidden.bs.offcanvas", () => document.querySelectorAll("[data-notification-trigger]").forEach((x) => x.setAttribute("aria-expanded", "false")));
  refresh(false);
})();
