(() => {
  const translate = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));
  const presentationLocale = document.documentElement.lang === "en" ? "en-US" : "es-MX";
  const dashboard = document.querySelector("[data-dashboard]");
  if (!dashboard || typeof Chart === "undefined") return;
  const canvas = dashboard.querySelector("[data-dashboard-chart]");
  if (!canvas) return;

  const status = dashboard.querySelector("[data-dashboard-status]");
  const refreshButton = dashboard.querySelector("[data-dashboard-refresh]");
  const shell = dashboard.querySelector("[data-dashboard-chart-shell]");
  const fallback = dashboard.querySelector("[data-dashboard-fallback]");
  const ranges = [...dashboard.querySelectorAll("[data-dashboard-range]")];
  const periodLabel = dashboard.querySelector("[data-dashboard-period-label]");
  const intervalMilliseconds = 60000;
  const number = (value) => Number(value || 0);
  const text = (value) => String(value ?? "");
  const format = (value) => number(value).toLocaleString(presentationLocale);
  const timestamp = (value) => {
    const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value || "");
    return match ? `${match[3]}/${match[2]}/${match[1]} ${match[4]}:${match[5]}` : translate("hora no disponible");
  };
  const color = (name) => getComputedStyle(dashboard).getPropertyValue(name).trim();
  let requestInProgress = false;
  let timerId;
  let selectedRange = 14;
  let selectedIndex = -1;
  let selectedDate = "";
  let currentPoints = [];
  let tooltipIndex = -1;
  let chart;
  let activeView = "bar";
  let chartRange = 14;
  let productPage = number(dashboard.querySelector("[data-dashboard-products]")?.dataset.page) || 1;
  let requestVersion = 0;
  let controller;
  let retryRequest;
  const views = [...dashboard.querySelectorAll("[data-dashboard-view]")];
  const calendar = dashboard.querySelector("[data-dashboard-calendar]");
  const calendarGrid = dashboard.querySelector("[data-dashboard-calendar-grid]");
  const activityStatus = dashboard.querySelector("[data-dashboard-activity-status]");
  const retryButton = dashboard.querySelector("[data-dashboard-retry]");
  const productList = dashboard.querySelector("[data-dashboard-product-list]");
  const localPoints = (points) => points.map(point => ({ ...point, dayLabel: new Intl.DateTimeFormat(presentationLocale,
    { weekday: "short", day: "2-digit", month: "2-digit", timeZone: "UTC" }).format(new Date(`${point.date}T12:00:00Z`)) }));
  const element = (tag, value, className) => {
    const node = document.createElement(tag);
    if (value !== undefined) node.textContent = text(value);
    if (className) node.className = className;
    return node;
  };

  const updateMetric = (name, value) => {
    const element = dashboard.querySelector(`[data-dashboard-metric="${name}"]`);
    if (element) element.textContent = format(value);
  };
  const comparisonLabel = (item) => {
    if (number(item?.state) === 1) return "Nuevo";
    if (number(item?.state) === 0) return "Sin actividad";
    const delta = number(item?.delta);
    return `${delta > 0 ? "+" : ""}${delta} · ${number(item?.percentChange).toLocaleString(presentationLocale, { maximumFractionDigits: 1 })}%`;
  };
  const renderComparison = (snapshot) => {
    const comparison = snapshot?.comparison;
    if (!comparison) return;
    ["todayOperations", "todayAdjustments", "sevenDayOperations", "sevenDayDistinctSkus"].forEach((name) => {
      const value = dashboard.querySelector(`[data-dashboard-comparison="${name}"]`);
      const label = dashboard.querySelector(`[data-dashboard-comparison-label="${name}"]`);
      if (value) value.textContent = format(comparison[name]?.current);
      if (label) label.textContent = comparisonLabel(comparison[name]);
    });
    const driverGroups = { product: comparison.products, row: comparison.rows, location: comparison.locations };
    Object.entries(driverGroups).forEach(([kind, drivers]) => {
      const list = dashboard.querySelector(`[data-dashboard-drivers="${kind}"]`);
      if (!list || !Array.isArray(drivers)) return;
      list.replaceChildren();
      drivers.forEach((driver) => {
        const item = document.createElement("li");
        const link = document.createElement("a");
        if (dashboard.dataset.isAdmin === "true") {
          const target = new URL("/Admin/Inventory/Movements", window.location.origin);
          target.searchParams.set("view", "effective");
          const warehouseDate = new Date(`${snapshot.warehouseDate}T12:00:00`);
          const from = new Date(warehouseDate); from.setDate(from.getDate() - 13);
          target.searchParams.set("from", from.toISOString().slice(0, 10)); target.searchParams.set("to", snapshot.warehouseDate);
          target.searchParams.set(kind === "product" ? "sku" : "locationCode", driver.code);
          link.href = target.toString();
        } else link.href = driver.targetUrl;
        const code = document.createElement("strong"); code.textContent = driver.code;
        const value = document.createElement("span"); const delta = number(driver.delta); value.textContent = `${format(driver.current)} operación(es) · ${delta > 0 ? "+" : ""}${delta}`;
        link.append(code, value); item.append(link); list.append(item);
      });
      if (!drivers.length) { const emptyItem = document.createElement("li"); emptyItem.className = "text-body-secondary"; emptyItem.textContent = translate("Sin actividad en el período."); list.append(emptyItem); }
    });
  };
  const visiblePoints = () => currentPoints.slice(-selectedRange);
  const detail = (point) => {
    if (!point) return;
    const values = { day: point.dayLabel, total: point.totalEffectiveOperations, entry: point.entryCount, exit: point.exitCount, transfer: point.transferCount, adjustment: point.adjustmentCount, skus: point.distinctSkusCount };
    Object.entries(values).forEach(([name, value]) => {
      const element = dashboard.querySelector(`[data-dashboard-detail-${name}]`);
      if (element) element.textContent = name === "day" ? text(value) : format(value);
    });
    const empty = dashboard.querySelector("[data-dashboard-detail-empty]");
    if (empty) empty.hidden = number(point.totalEffectiveOperations) > 0;
    const link = dashboard.querySelector("[data-dashboard-detail-link]");
    if (link && dashboard.dataset.isAdmin === "true") {
      const target = new URL("/Admin/Inventory/Movements", window.location.origin);
      target.searchParams.set("view", "effective");
      target.searchParams.set("period", "custom");
      target.searchParams.set("from", text(point.date));
      target.searchParams.set("to", text(point.date));
      link.href = target.toString();
    }
  };
  const chartColors = () => ({
    entry: color("--dashboard-entry"),
    exit: color("--dashboard-exit"),
    transfer: color("--dashboard-transfer"),
    adjustment: color("--dashboard-adjustment"),
    grid: color("--dashboard-chart-grid"),
    label: color("--bs-secondary-color"),
    text: color("--bs-body-color"),
    today: color("--dashboard-chart-today"),
    selected: color("--dashboard-chart-selected"),
    tooltip: color("--dashboard-chart-tooltip"),
    tooltipBorder: color("--dashboard-chart-tooltip-border"),
    tooltipText: color("--dashboard-chart-tooltip-text")
  });
  const setSummary = (points) => {
    const total = points.reduce((sum, point) => sum + number(point.totalEffectiveOperations), 0);
    const busiest = [...points]
      .filter((point) => number(point.totalEffectiveOperations) > 0)
      .sort((a, b) => number(b.totalEffectiveOperations) - number(a.totalEffectiveOperations) || text(b.date).localeCompare(text(a.date)))[0];
    const totalElement = dashboard.querySelector("[data-dashboard-total]");
    const busiestElement = dashboard.querySelector("[data-dashboard-busiest]");
    if (totalElement) totalElement.textContent = format(total);
    if (busiestElement) busiestElement.textContent = busiest ? `${text(busiest.dayLabel)} · ${format(busiest.totalEffectiveOperations)}` : translate("Sin actividad en el período");
    if (periodLabel) periodLabel.textContent = translate("Últimos {0} días", selectedRange);
  };
  const segmentKeys = ["entryCount", "exitCount", "transferCount", "adjustmentCount"];
  const segmentRadius = (context) => {
    const point = visiblePoints()[context.dataIndex];
    if (!point || !number(point[segmentKeys[context.datasetIndex]])) return 0;
    const hasPositiveSegmentAbove = segmentKeys
      .slice(context.datasetIndex + 1)
      .some((key) => number(point[key]) > 0);
    return hasPositiveSegmentAbove ? 0 : { topLeft: 6, topRight: 6, bottomLeft: 0, bottomRight: 0 };
  };
  const chartData = () => {
    const points = visiblePoints();
    const colors = chartColors();
    return {
      labels: points.map((point) => text(point.dayLabel)),
      datasets: [
        { label: translate("Entradas"), data: points.map((point) => number(point.entryCount)), backgroundColor: colors.entry, hoverBackgroundColor: colors.entry, borderColor: colors.entry, borderWidth: 1, borderRadius: segmentRadius, borderSkipped: false },
        { label: translate("Salidas"), data: points.map((point) => number(point.exitCount)), backgroundColor: colors.exit, hoverBackgroundColor: colors.exit, borderColor: colors.exit, borderWidth: 1, borderRadius: segmentRadius, borderSkipped: false },
        { label: translate("Transferencias"), data: points.map((point) => number(point.transferCount)), backgroundColor: colors.transfer, hoverBackgroundColor: colors.transfer, borderColor: colors.transfer, borderWidth: 1, borderRadius: segmentRadius, borderSkipped: false },
        { label: translate("Ajustes"), data: points.map((point) => number(point.adjustmentCount)), backgroundColor: colors.adjustment, hoverBackgroundColor: colors.adjustment, borderColor: colors.adjustment, borderWidth: 1, borderRadius: segmentRadius, borderSkipped: false }
      ].map(dataset => ({ ...dataset, borderWidth: activeView === "line" ? 2 : 1, tension: 0, fill: false, pointRadius: 3 }))
    };
  };
  const dashboardColumnHighlight = {
    id: "dashboardColumnHighlight",
    beforeDatasetsDraw: (instance) => {
      const points = visiblePoints();
      const x = instance.scales.x;
      const area = instance.chartArea;
      if (!points.length || !x || !area) return;
      const columnWidth = area.width / points.length;
      const drawColumn = (index, fill) => {
        if (index < 0) return;
        const left = x.getPixelForValue(index) - (columnWidth * .42);
        const width = columnWidth * .84;
        const context = instance.ctx;
        context.save();
        context.fillStyle = fill;
        context.beginPath();
        if (typeof context.roundRect === "function") context.roundRect(left, area.top, width, area.bottom - area.top, 10);
        else context.rect(left, area.top, width, area.bottom - area.top);
        context.fill();
        context.restore();
      };
      const colors = chartColors();
      drawColumn(selectedIndex, colors.selected);
    }
  };
  const dashboardStackTotals = {
    id: "dashboardStackTotals",
    afterDatasetsDraw: (instance) => {
      if (activeView !== "bar") return;
      const points = visiblePoints();
      const x = instance.scales.x;
      const y = instance.scales.y;
      if (!points.length || !x || !y) return;
      const todayIndex = points.findIndex((point) => text(point.date) === dashboard.dataset.warehouseDate);
      const showEveryTotal = instance.width >= 620;
      const context = instance.ctx;
      context.save();
      context.fillStyle = chartColors().text;
      context.font = "600 11px system-ui, sans-serif";
      context.textAlign = "center";
      context.textBaseline = "bottom";
      points.forEach((point, index) => {
        const total = number(point.totalEffectiveOperations);
        if (!total || (!showEveryTotal && index !== selectedIndex && index !== todayIndex)) return;
        context.fillText(format(total), x.getPixelForValue(index), y.getPixelForValue(total) - 7);
      });
      context.restore();
    }
  };
  const select = (index) => {
    const points = visiblePoints();
    if (!points.length) return;
    selectedIndex = Math.max(0, Math.min(index, points.length - 1));
    selectedDate = text(points[selectedIndex].date);
    detail(points[selectedIndex]);
    if (activeView === "calendar") { markCalendarSelection(); return; }
    chart.setActiveElements([{ datasetIndex: 0, index: selectedIndex }]);
    chart.tooltip.setActiveElements([], { x: 0, y: 0 });
    chart.update("none");
  };
  const refreshChart = (mode = "none") => {
    const points = visiblePoints();
    const data = chartData();
    chart.setActiveElements([]);
    chart.tooltip.setActiveElements([], { x: 0, y: 0 });
    chart.data = data;
    chart.config.type = activeView === "line" ? "line" : "bar";
    canvas.setAttribute("aria-label", `${translate("Vista de actividad")}: ${translate(activeView === "line" ? "Líneas" : "Barras")}`);
    chart.options.scales.x.stacked = activeView !== "line";
    chart.options.scales.y.stacked = activeView !== "line";
    const colors = chartColors();
    chart.options.scales.x.ticks.color = (context) => text(points[context.index]?.date) === dashboard.dataset.warehouseDate ? colors.text : colors.label;
    chart.options.scales.y.ticks.color = colors.label;
    chart.options.scales.x.grid.color = colors.grid;
    chart.options.scales.y.grid.color = colors.grid;
    chart.options.plugins.tooltip.backgroundColor = colors.tooltip;
    chart.options.plugins.tooltip.borderColor = colors.tooltipBorder;
    chart.options.plugins.tooltip.titleColor = colors.tooltipText;
    chart.options.plugins.tooltip.bodyColor = colors.tooltipText;
    setSummary(points);
    const selectedByDate = points.findIndex((point) => text(point.date) === selectedDate);
    if (selectedByDate >= 0) selectedIndex = selectedByDate;
    else selectedIndex = Math.max(0, points.findIndex(point => point.date === dashboard.dataset.warehouseDate));
    selectedDate = text(points[selectedIndex]?.date);
    detail(points[selectedIndex]);
    renderDayTable(points);
    if (activeView === "calendar") renderCalendar(points);
    else chart.update(mode);
  };
  const createChart = () => {
    const data = chartData();
    const colors = chartColors();
    shell.classList.add("is-ready");
    try {
      chart = new Chart(canvas, {
      type: "bar",
      data,
      plugins: [dashboardColumnHighlight, dashboardStackTotals],
      options: {
        responsive: true, maintainAspectRatio: false,
        animation: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? false : { duration: 260, easing: "easeOutQuart" },
        layout: { padding: { top: 24, right: 8, bottom: 2, left: 4 } },
        datasets: { bar: { barPercentage: .74, categoryPercentage: .68, maxBarThickness: 36 } },
        interaction: { mode: "index", intersect: false },
        onClick: (event, elements, instance) => {
          const index = elements[0]?.index ?? Math.round(instance.scales.x.getValueForPixel(event.x));
          if (Number.isInteger(index) && index >= 0 && index < visiblePoints().length) select(index);
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: colors.tooltip,
            borderColor: colors.tooltipBorder,
            borderWidth: 1,
            titleColor: colors.tooltipText,
            bodyColor: colors.tooltipText,
            cornerRadius: 10,
            padding: 12,
            caretPadding: 8,
            titleFont: { size: 13, weight: "700" },
            bodyFont: { size: 12, weight: "600" },
            bodySpacing: 5,
            displayColors: true,
            boxPadding: 4,
            filter: (item) => { tooltipIndex = item.dataIndex; return number(item.parsed.y) > 0; },
            callbacks: {
              title: (items) => visiblePoints()[items.length ? items[0].dataIndex : tooltipIndex]?.dayLabel ?? "",
              label: (item) => `${item.dataset.label}: ${format(item.parsed.y)}`,
              afterBody: (items) => {
                const point = visiblePoints()[items.length ? items[0].dataIndex : tooltipIndex];
                if (!point) return "";
                const total = number(point.totalEffectiveOperations);
                return total ? `\n${translate("Total: {0} operaciones", format(total))}\n${translate("SKUs distintos: {0}", format(point.distinctSkusCount))}` : translate("Sin actividad registrada");
              }
            }
          }
        },
        scales: {
          x: {
            stacked: true,
            border: { display: false },
            grid: { display: false, color: colors.grid },
            ticks: {
              color: (context) => text(visiblePoints()[context.index]?.date) === dashboard.dataset.warehouseDate ? colors.text : colors.label,
              font: (context) => ({ size: 11, weight: text(visiblePoints()[context.index]?.date) === dashboard.dataset.warehouseDate ? "700" : "500" }),
              callback: (_, index) => {
                const point = visiblePoints()[index];
                if (!point) return "";
                return text(point.date) === dashboard.dataset.warehouseDate ? [text(point.dayLabel), translate("Hoy")] : text(point.dayLabel);
              },
              padding: 10,
              maxRotation: 0
            }
          },
          y: {
            stacked: true,
            beginAtZero: true,
            grace: "12%",
            border: { display: false },
            ticks: { precision: 0, color: colors.label, padding: 8, font: { size: 11, weight: "500" } },
            grid: { color: colors.grid, drawTicks: false }
          }
        }
      }
      });
      fallback.hidden = true;
    } catch (error) {
      shell.classList.remove("is-ready");
      throw error;
    }
    selectedIndex = visiblePoints().length - 1;
    select(selectedIndex);
  };
  const renderSnapshot = (snapshot) => {
    const metrics = snapshot?.metrics;
    if (!metrics || !Array.isArray(metrics.recentActivityTrend)) throw new Error(translate("Respuesta del tablero incompleta."));
    currentPoints = localPoints(metrics.recentActivityTrend);
    dashboard.dataset.warehouseDate = snapshot.warehouseDate;
    updateMetric("effectiveMovementsToday", metrics.effectiveMovementsToday);
    updateMetric("negativePositionsCount", metrics.negativePositionsCount);
    updateMetric("lowStockProductsCount", metrics.lowStockProductsCount);
    updateMetric("effectiveAdjustmentsToday", metrics.effectiveAdjustmentsToday);
    renderComparison(snapshot);
    selectedIndex = Math.min(selectedIndex, visiblePoints().length - 1);
    refreshChart("none");
    if (status) { status.classList.remove("is-stale"); status.replaceChildren(document.createTextNode(translate("Datos generados: "))); const time = document.createElement("time"); time.dateTime = snapshot.generatedAtLocal; time.textContent = timestamp(snapshot.generatedAtLocal); status.appendChild(time); }
  };
  const markCalendarSelection = () => calendarGrid?.querySelectorAll("[data-calendar-date]").forEach(button => {
    button.setAttribute("aria-pressed", String(button.dataset.calendarDate === selectedDate));
  });
  const renderCalendar = (points) => {
    if (!calendarGrid) return;
    const fragment = document.createDocumentFragment();
    // Rows are Monday-Sunday; columns are weeks. Blank cells are outside the 90-day interval.
    for (let day = 0; day < 7; day++) fragment.append(element("span", new Intl.DateTimeFormat(presentationLocale,
      { weekday: "short", timeZone: "UTC" }).format(new Date(Date.UTC(2026, 0, 5 + day))), "dashboard-calendar-weekday"));
    const offset = (new Date(`${points[0].date}T12:00:00Z`).getUTCDay() + 6) % 7;
    const maximum = Math.max(1, ...points.map(point => number(point.totalEffectiveOperations)));
    for (let index = 0; index < offset; index++) fragment.append(element("span", "", "dashboard-calendar-blank"));
    points.forEach((point, index) => {
      const count = number(point.totalEffectiveOperations);
      const button = element("button", new Intl.DateTimeFormat(presentationLocale, { day: "2-digit", month: "2-digit", timeZone: "UTC" }).format(new Date(`${point.date}T12:00:00Z`)));
      button.type = "button";
      button.dataset.calendarDate = point.date;
      button.dataset.level = count === 0 ? "0" : String(Math.ceil(count * 4 / maximum));
      const label = `${point.date} · ${point.dayLabel} · ${translate("Total: {0} operaciones", format(count))}`;
      button.setAttribute("aria-label", label); button.title = label;
      button.addEventListener("click", () => select(index));
      button.addEventListener("keydown", event => {
        const step = { ArrowLeft: -7, ArrowRight: 7, ArrowUp: -1, ArrowDown: 1 }[event.key];
        if (!step) return;
        event.preventDefault();
        const next = Math.max(0, Math.min(points.length - 1, index + step));
        select(next); calendarGrid.querySelectorAll("button")[next]?.focus();
      });
      fragment.append(button);
    });
    const focusedDate = calendarGrid.contains(document.activeElement) ? document.activeElement.dataset.calendarDate : null;
    calendarGrid.replaceChildren(fragment); markCalendarSelection();
    if (focusedDate) calendarGrid.querySelector(`[data-calendar-date="${focusedDate}"]`)?.focus({ preventScroll: true });
  };
  const renderDayTable = (points) => {
    const rows = dashboard.querySelector("[data-dashboard-day-rows]");
    if (!rows) return;
    const fragment = document.createDocumentFragment();
    points.forEach(point => {
      const row = element("tr");
      const day = element("th", point.dayLabel); day.scope = "row"; row.append(day);
      ["totalEffectiveOperations", ...segmentKeys, "distinctSkusCount"].forEach(key => row.append(element("td", format(point[key]))));
      fragment.append(row);
    });
    rows.replaceChildren(fragment); dashboard.querySelector("[data-dashboard-table]").hidden = false;
  };
  const renderProducts = (products) => {
    if (!productList) return;
    const expanded = new Set([...productList.querySelectorAll("details[open]")].map(node => node.dataset.productId));
    const focusedProduct = document.activeElement?.closest("[data-product-id]")?.dataset.productId;
    const focusedPage = document.activeElement?.dataset.dashboardProductPage;
    const fragment = document.createDocumentFragment();
    if (!products.items.length) fragment.append(element("p", translate("Sin actividad en el período."), "text-body-secondary"));
    products.items.forEach(product => {
      const row = element("details", undefined, "dashboard-product"); row.dataset.productId = product.productId; row.open = expanded.has(product.productId);
      const summary = element("summary"); const title = element("span");
      title.append(element("strong", product.sku), element("small", product.description));
      summary.append(title, element("span", `${format(product.operations)} ${translate("operaciones")}`),
        element("span", `${number(product.percent).toLocaleString(presentationLocale, { maximumFractionDigits: 1 })} %`));
      const body = element("div", undefined, "dashboard-product-detail"); const counts = element("dl");
      segmentKeys.forEach((key, index) => { const group = element("div"); group.append(element("dt", translate(["Entradas", "Salidas", "Transferencias", "Ajustes"][index])), element("dd", format(product[key]))); counts.append(group); });
      body.append(counts, element("p", translate("Ubicaciones con mayor actividad"), "fw-semibold"));
      const locations = element("ul");
      product.locations.forEach(location => locations.append(element("li", `${location.code} · ${format(location.operations)} ${translate("operaciones")}`)));
      body.append(locations);
      if (!product.locations.length) body.append(element("p", translate("Sin ubicaciones registradas")));
      const link = element("a", translate("Ver movimientos del producto"));
      const target = new URL("/Admin/Inventory/Movements", window.location.origin);
      Object.entries({ view: "effective", period: "custom", from: products.from, to: products.to, sku: product.sku }).forEach(([key, value]) => target.searchParams.set(key, value));
      link.href = target.toString(); body.append(link); row.append(summary, body); fragment.append(row);
    });
    const nav = element("nav", undefined, "dashboard-product-pages"); nav.setAttribute("aria-label", translate("Páginas de productos"));
    const pageButton = (page, label) => { const button = element("button", translate(label), "btn btn-outline-secondary"); button.type = "button"; button.dataset.dashboardProductPage = text(page); return button; };
    if (products.pageNumber > 1) nav.append(pageButton(products.pageNumber - 1, "Anterior"));
    const pageLabel = element("span", translate("Página {0} de {1}", products.pageNumber, products.totalPages)); pageLabel.tabIndex = -1;
    nav.append(pageLabel);
    if (products.pageNumber < products.totalPages) nav.append(pageButton(products.pageNumber + 1, "Siguiente"));
    fragment.append(nav); productList.replaceChildren(fragment);
    dashboard.querySelector("[data-dashboard-products-period]").textContent = translate("Últimos {0} días", products.days);
    productPage = products.pageNumber;
    if (focusedProduct) productList.querySelector(`[data-product-id="${focusedProduct}"] summary`)?.focus({ preventScroll: true });
    if (focusedPage) (productList.querySelector(`[data-dashboard-product-page="${focusedPage}"]`) || pageLabel).focus({ preventScroll: true });
  };
  const schedule = () => { window.clearTimeout(timerId); if (!document.hidden) timerId = window.setTimeout(() => refresh(false), intervalMilliseconds); };
  const load = async (view, days, page, forceRefresh = false) => {
    if (document.hidden) return;
    const version = ++requestVersion;
    controller?.abort(); controller = new AbortController();
    const signal = controller.signal;
    window.clearTimeout(timerId);
    retryRequest = [view, days, page, forceRefresh];
    requestInProgress = true; shell.setAttribute("aria-busy", "true");
    calendar?.setAttribute("aria-busy", "true");
    dashboard.querySelector("[data-dashboard-products]")?.setAttribute("aria-busy", "true");
    refreshButton?.setAttribute("disabled", "disabled");
    activityStatus.hidden = false; activityStatus.textContent = translate("Actualizando datos…"); retryButton.hidden = true;
    const fetchJson = async (address, values) => {
      const url = new URL(address, window.location.href);
      Object.entries(values).forEach(([key, value]) => url.searchParams.set(key, text(value)));
      if (forceRefresh) url.searchParams.set("refresh", "true");
      const response = await fetch(url, { headers: { Accept: "application/json" }, cache: "no-store", signal });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    };
    try {
      const [activity, products] = await Promise.all([
        fetchJson(view === "calendar" ? dashboard.dataset.activityUrl : dashboard.dataset.metricsUrl, view === "calendar" ? { days } : {}),
        fetchJson(dashboard.dataset.productsUrl, { days, pageNumber: page })
      ]);
      if (version !== requestVersion) return;
      const points = view === "calendar" ? activity.points : activity.metrics?.recentActivityTrend;
      if (!Array.isArray(points) || !points.length || !Array.isArray(products.items)) throw new Error("Incomplete dashboard response");
      activeView = view; selectedRange = days;
      if (view !== "calendar") chartRange = days;
      shell.hidden = view === "calendar"; calendar.hidden = view !== "calendar";
      dashboard.querySelector("[data-dashboard-ranges]").hidden = view === "calendar";
      dashboard.querySelector(".dashboard-warehouse-legend").hidden = view === "calendar";
      views.forEach(button => { const active = button.dataset.dashboardView === view; button.classList.toggle("active", active); button.setAttribute("aria-pressed", String(active)); });
      ranges.forEach(button => { const active = number(button.dataset.dashboardRange) === chartRange; button.classList.toggle("active", active); button.setAttribute("aria-pressed", String(active)); });
      if (view === "calendar") {
        currentPoints = localPoints(points); dashboard.dataset.warehouseDate = activity.warehouseDate; refreshChart();
      } else renderSnapshot(activity);
      renderProducts(products);
      activityStatus.textContent = `${translate("Datos generados: ")}${timestamp(activity.generatedAtLocal)}`;
      if (status) status.classList.remove("is-stale");
      retryRequest = null;
    } catch (error) {
      if (version !== requestVersion || error.name === "AbortError") return;
      const message = translate("Datos sin actualizar. Se conserva el último snapshot válido y reintentaremos automáticamente.");
      activityStatus.textContent = message; retryButton.hidden = false;
      if (status) { status.classList.add("is-stale"); status.textContent = message; }
    } finally {
      if (version === requestVersion) {
        requestInProgress = false; shell.setAttribute("aria-busy", "false");
        calendar?.setAttribute("aria-busy", "false");
        dashboard.querySelector("[data-dashboard-products]")?.setAttribute("aria-busy", "false");
        refreshButton?.removeAttribute("disabled"); schedule();
      }
    }
  };
  const refresh = (forceRefresh = false) => {
    if (requestInProgress || document.hidden) return;
    return retryRequest ? load(retryRequest[0], retryRequest[1], retryRequest[2], forceRefresh || retryRequest[3])
      : load(activeView, selectedRange, productPage, forceRefresh);
  };

  currentPoints = localPoints(JSON.parse(canvas.dataset.points || "[]"));
  createChart();
  setSummary(visiblePoints());
  renderDayTable(visiblePoints());
  views.forEach(button => button.addEventListener("click", () => {
    const view = button.dataset.dashboardView;
    if (view !== "calendar" && activeView !== "calendar" && !requestInProgress && !retryRequest) {
      activeView = view; views.forEach(candidate => { const active = candidate === button; candidate.classList.toggle("active", active); candidate.setAttribute("aria-pressed", String(active)); }); refreshChart();
    } else load(view, view === "calendar" ? 90 : chartRange, 1);
  }));
  ranges.forEach(button => button.addEventListener("click", () => load(activeView === "calendar" ? "bar" : activeView, number(button.dataset.dashboardRange), 1)));
  productList?.addEventListener("click", event => {
    const button = event.target.closest("[data-dashboard-product-page]");
    if (!button) return;
    event.preventDefault(); load(activeView, selectedRange, number(button.dataset.dashboardProductPage));
  });
  retryButton?.addEventListener("click", () => retryRequest ? load(...retryRequest) : refresh(true));
  canvas.addEventListener("keydown", (event) => { if (event.key === "ArrowLeft" || event.key === "ArrowRight") { event.preventDefault(); select(selectedIndex + (event.key === "ArrowLeft" ? -1 : 1)); } if (event.key === " " || event.key === "Enter") { event.preventDefault(); select(selectedIndex); } });
  refreshButton?.addEventListener("click", () => refresh(true));
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      window.clearTimeout(timerId); controller?.abort(); ++requestVersion; requestInProgress = false;
      refreshButton?.removeAttribute("disabled");
    } else refresh(false);
  });
  new MutationObserver(() => refreshChart("none")).observe(document.documentElement, { attributes: true, attributeFilter: ["data-bs-theme"] });
  schedule();
})();
