(() => {
  const dashboard = document.querySelector("[data-dashboard]");
  if (!dashboard || typeof Chart === "undefined") return;
  const canvas = dashboard.querySelector("[data-dashboard-chart]");
  if (!canvas) return;

  const status = dashboard.querySelector("[data-dashboard-status]");
  const refreshButton = dashboard.querySelector("[data-dashboard-refresh]");
  const shell = dashboard.querySelector("[data-dashboard-chart-shell]");
  const fallback = dashboard.querySelector("[data-dashboard-fallback]");
  const ranges = [...dashboard.querySelectorAll("[data-dashboard-range]")];
  const intervalMilliseconds = 60000;
  const number = (value) => Number(value || 0);
  const text = (value) => String(value ?? "");
  const format = (value) => number(value).toLocaleString("es-MX");
  const timestamp = (value) => {
    const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value || "");
    return match ? `${match[3]}/${match[2]}/${match[1]} ${match[4]}:${match[5]}` : "hora no disponible";
  };
  const color = (name) => getComputedStyle(dashboard).getPropertyValue(name).trim();
  let requestInProgress = false;
  let timerId;
  let selectedRange = 14;
  let selectedIndex = -1;
  let selectedDate = "";
  let currentPoints = [];
  let chart;

  const updateMetric = (name, value) => {
    const element = dashboard.querySelector(`[data-dashboard-metric="${name}"]`);
    if (element) element.textContent = format(value);
  };
  const visiblePoints = () => currentPoints.slice(-selectedRange);
  const detail = (point) => {
    if (!point) return;
    const values = { day: point.dayLabel, total: point.totalEffectiveOperations, entry: point.entryCount, exit: point.exitCount, transfer: point.transferCount, adjustment: point.adjustmentCount, skus: point.distinctSkusCount };
    Object.entries(values).forEach(([name, value]) => {
      const element = dashboard.querySelector(`[data-dashboard-detail-${name}]`);
      if (element) element.textContent = name === "day" ? text(value) : format(value);
    });
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
    tooltipBorder: color("--dashboard-chart-tooltip-border")
  });
  const rgba = (hex, alpha) => {
    const value = hex.replace("#", "").trim();
    if (!/^[0-9a-f]{6}$/i.test(value)) return hex;
    return `rgba(${parseInt(value.slice(0, 2), 16)}, ${parseInt(value.slice(2, 4), 16)}, ${parseInt(value.slice(4, 6), 16)}, ${alpha})`;
  };
  const gradient = (context, base) => {
    const area = context.chart.chartArea;
    if (!area) return base;
    const fill = context.chart.ctx.createLinearGradient(0, area.bottom, 0, area.top);
    fill.addColorStop(0, base);
    fill.addColorStop(1, rgba(base, .72));
    return fill;
  };
  const setSummary = (points) => {
    const total = points.reduce((sum, point) => sum + number(point.totalEffectiveOperations), 0);
    const busiest = [...points].sort((a, b) => number(b.totalEffectiveOperations) - number(a.totalEffectiveOperations) || text(b.date).localeCompare(text(a.date)))[0];
    const totalElement = dashboard.querySelector("[data-dashboard-total]");
    const busiestElement = dashboard.querySelector("[data-dashboard-busiest]");
    if (totalElement) totalElement.textContent = format(total);
    if (busiestElement) busiestElement.textContent = busiest ? `${text(busiest.dayLabel)} · ${format(busiest.totalEffectiveOperations)}` : "Sin actividad";
  };
  const chartData = () => {
    const points = visiblePoints();
    const colors = chartColors();
    return {
      labels: points.map((point) => text(point.dayLabel)),
      datasets: [
        { label: "Entradas", data: points.map((point) => number(point.entryCount)), backgroundColor: (context) => gradient(context, colors.entry), hoverBackgroundColor: colors.entry, borderColor: colors.entry, borderWidth: 1, borderRadius: 7, borderSkipped: false },
        { label: "Salidas", data: points.map((point) => number(point.exitCount)), backgroundColor: (context) => gradient(context, colors.exit), hoverBackgroundColor: colors.exit, borderColor: colors.exit, borderWidth: 1, borderRadius: 7, borderSkipped: false },
        { label: "Transferencias", data: points.map((point) => number(point.transferCount)), backgroundColor: (context) => gradient(context, colors.transfer), hoverBackgroundColor: colors.transfer, borderColor: colors.transfer, borderWidth: 1, borderRadius: 7, borderSkipped: false },
        { label: "Ajustes", data: points.map((point) => number(point.adjustmentCount)), backgroundColor: (context) => gradient(context, colors.adjustment), hoverBackgroundColor: colors.adjustment, borderColor: colors.adjustment, borderWidth: 1, borderRadius: 7, borderSkipped: false }
      ]
    };
  };
  const dashboardColumnHighlight = {
    id: "dashboardColumnHighlight",
    beforeDatasetsDraw: (instance) => {
      const points = visiblePoints();
      const x = instance.scales.x;
      const area = instance.chartArea;
      if (!points.length || !x || !area) return;
      const todayIndex = points.findIndex((point) => text(point.date) === dashboard.dataset.warehouseDate);
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
      drawColumn(todayIndex, colors.today);
      if (selectedIndex !== todayIndex) drawColumn(selectedIndex, colors.selected);
    }
  };
  const dashboardStackTotals = {
    id: "dashboardStackTotals",
    afterDatasetsDraw: (instance) => {
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
    chart.setActiveElements([{ datasetIndex: 0, index: selectedIndex }]);
    chart.tooltip.setActiveElements([{ datasetIndex: 0, index: selectedIndex }], { x: 0, y: 0 });
    chart.update("none");
  };
  const refreshChart = (mode = "none") => {
    const points = visiblePoints();
    const data = chartData();
    chart.data = data;
    const colors = chartColors();
    chart.options.scales.x.ticks.color = (context) => text(points[context.index]?.date) === dashboard.dataset.warehouseDate ? colors.text : colors.label;
    chart.options.scales.y.ticks.color = colors.label;
    chart.options.scales.x.grid.color = colors.grid;
    chart.options.scales.y.grid.color = colors.grid;
    chart.options.plugins.tooltip.backgroundColor = colors.tooltip;
    chart.options.plugins.tooltip.borderColor = colors.tooltipBorder;
    setSummary(points);
    const selectedByDate = points.findIndex((point) => text(point.date) === selectedDate);
    if (selectedByDate >= 0) selectedIndex = selectedByDate;
    else selectedIndex = points.length - 1;
    detail(points[selectedIndex]);
    chart.update(mode);
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
        datasets: { bar: { barPercentage: .78, categoryPercentage: .72, maxBarThickness: 42 } },
        interaction: { mode: "index", intersect: false },
        onClick: (_, elements) => { if (elements[0]) select(elements[0].index); },
        onHover: (_, elements) => { if (elements[0]) detail(visiblePoints()[elements[0].index]); },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: colors.tooltip,
            borderColor: colors.tooltipBorder,
            borderWidth: 1,
            cornerRadius: 10,
            padding: 12,
            caretPadding: 8,
            titleFont: { size: 13, weight: "700" },
            bodyFont: { size: 12, weight: "600" },
            bodySpacing: 5,
            displayColors: true,
            boxPadding: 4,
            callbacks: {
              title: (items) => visiblePoints()[items[0].dataIndex]?.dayLabel ?? "",
              label: (item) => `${item.dataset.label}: ${format(item.parsed.y)}`,
              afterBody: (items) => `\nTotal: ${format(visiblePoints()[items[0].dataIndex]?.totalEffectiveOperations)} operaciones\nSKUs distintos: ${format(visiblePoints()[items[0].dataIndex]?.distinctSkusCount)}`
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
    if (!metrics || !Array.isArray(metrics.recentActivityTrend)) throw new Error("Respuesta del tablero incompleta.");
    currentPoints = metrics.recentActivityTrend;
    updateMetric("effectiveMovementsToday", metrics.effectiveMovementsToday);
    updateMetric("negativePositionsCount", metrics.negativePositionsCount);
    updateMetric("lowStockProductsCount", metrics.lowStockProductsCount);
    updateMetric("effectiveAdjustmentsToday", metrics.effectiveAdjustmentsToday);
    selectedIndex = Math.min(selectedIndex, visiblePoints().length - 1);
    refreshChart("none");
    if (status) { status.classList.remove("is-stale"); status.replaceChildren(document.createTextNode("Actualizado: ")); const time = document.createElement("time"); time.dateTime = snapshot.generatedAtLocal; time.textContent = timestamp(snapshot.generatedAtLocal); status.appendChild(time); }
  };
  const schedule = () => { window.clearTimeout(timerId); if (!document.hidden) timerId = window.setTimeout(refresh, intervalMilliseconds); };
  const refresh = async () => {
    if (requestInProgress || document.hidden) return;
    requestInProgress = true; shell.setAttribute("aria-busy", "true"); refreshButton?.setAttribute("disabled", "disabled"); if (status) status.textContent = "Actualizando datos…";
    try { const response = await fetch(dashboard.dataset.metricsUrl, { headers: { Accept: "application/json" }, cache: "no-store" }); if (!response.ok) throw new Error(`HTTP ${response.status}`); renderSnapshot(await response.json()); }
    catch { if (status) { status.classList.add("is-stale"); status.textContent = "Datos sin actualizar. Se conserva el último snapshot válido y reintentaremos automáticamente."; } }
    finally { requestInProgress = false; shell.setAttribute("aria-busy", "false"); refreshButton?.removeAttribute("disabled"); schedule(); }
  };

  currentPoints = JSON.parse(canvas.dataset.points || "[]");
  createChart();
  ranges.forEach((button) => button.addEventListener("click", () => { selectedRange = number(button.dataset.dashboardRange); ranges.forEach((candidate) => { const active = number(candidate.dataset.dashboardRange) === selectedRange; candidate.classList.toggle("active", active); candidate.setAttribute("aria-pressed", String(active)); }); refreshChart("none"); }));
  canvas.addEventListener("keydown", (event) => { if (event.key === "ArrowLeft" || event.key === "ArrowRight") { event.preventDefault(); select(selectedIndex + (event.key === "ArrowLeft" ? -1 : 1)); } if (event.key === " " || event.key === "Enter") { event.preventDefault(); select(selectedIndex); } });
  refreshButton?.addEventListener("click", refresh);
  document.addEventListener("visibilitychange", () => { if (document.hidden) window.clearTimeout(timerId); else refresh(); });
  new MutationObserver(() => refreshChart("none")).observe(document.documentElement, { attributes: true, attributeFilter: ["data-bs-theme"] });
  schedule();
})();
