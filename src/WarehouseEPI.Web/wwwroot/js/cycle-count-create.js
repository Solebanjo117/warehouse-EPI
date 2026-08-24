(() => {
  const root = document.querySelector("[data-cycle-count-create]");
  if (!root) return;

  root.classList.add("is-enhanced");

  const locations = [...root.querySelectorAll("[data-cycle-location]")];
  const groupToggles = [...root.querySelectorAll("[data-cycle-group-toggle]")];
  const search = root.querySelector("[data-cycle-search]");
  const selectedOnly = root.querySelector("[data-cycle-selected-only]");
  const clearButton = root.querySelector("[data-cycle-clear]");
  const reviewButton = root.querySelector("[data-cycle-review-selected]");
  const total = root.querySelector("[data-cycle-total]");
  const mobileTotal = root.querySelector("[data-cycle-mobile-total]");
  const summary = root.querySelector("[data-cycle-summary-groups]");
  const status = root.querySelector("[data-cycle-status]");
  const submit = root.querySelector("[data-cycle-submit]");

  const normalize = value => (value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("es");

  const membersFor = toggle => {
    if (toggle.dataset.groupType === "area") {
      return locations.filter(item => item.dataset.area === "true");
    }
    if (toggle.dataset.groupType === "row") {
      return locations.filter(item => item.dataset.row === toggle.dataset.row);
    }
    return locations.filter(item => item.dataset.row === toggle.dataset.row && item.dataset.rack === toggle.dataset.rack);
  };

  const setGroupState = (toggle, members) => {
    const selected = members.filter(item => item.checked).length;
    toggle.checked = members.length > 0 && selected === members.length;
    toggle.indeterminate = selected > 0 && selected < members.length;
    toggle.setAttribute("aria-checked", toggle.indeterminate ? "mixed" : String(toggle.checked));
  };

  const updateGroupCounts = () => {
    root.querySelectorAll("[data-cycle-row-group]").forEach(group => {
      const members = locations.filter(item => item.dataset.row === group.dataset.row);
      const selected = members.filter(item => item.checked).length;
      const output = group.querySelector("[data-cycle-row-count]");
      if (output) output.textContent = `${selected} seleccionadas`;
    });

    root.querySelectorAll("[data-cycle-rack-group]").forEach(group => {
      const members = locations.filter(item => item.dataset.row === group.dataset.row && item.dataset.rack === group.dataset.rack);
      const selected = members.filter(item => item.checked).length;
      const output = group.querySelector("[data-cycle-rack-count]");
      if (output) output.textContent = `${selected} / ${members.length}`;
    });

    const areaOutput = root.querySelector("[data-cycle-area-count]");
    if (areaOutput) {
      const areaMembers = locations.filter(item => item.dataset.area === "true");
      areaOutput.textContent = `${areaMembers.filter(item => item.checked).length} seleccionadas`;
    }
  };

  const updateSummary = () => {
    const selected = locations.filter(item => item.checked);
    total.textContent = String(selected.length);
    mobileTotal.textContent = String(selected.length);
    submit.disabled = selected.length === 0;
    summary.replaceChildren();

    if (selected.length === 0) {
      const empty = document.createElement("li");
      empty.textContent = "Selecciona al menos una ubicación.";
      empty.dataset.cycleEmptySummary = "";
      summary.append(empty);
      return;
    }

    const groups = new Map();
    selected.forEach(item => groups.set(item.dataset.summaryGroup, (groups.get(item.dataset.summaryGroup) || 0) + 1));
    groups.forEach((count, label) => {
      const item = document.createElement("li");
      const name = document.createElement("span");
      const amount = document.createElement("strong");
      name.textContent = label;
      amount.textContent = String(count);
      item.append(name, amount);
      summary.append(item);
    });
  };

  const updateVisibility = () => {
    const query = normalize(search.value.trim());
    locations.forEach(input => {
      const choice = input.closest("[data-cycle-location-choice]");
      const matchesSearch = query.length === 0 || normalize(choice.dataset.searchText).includes(query);
      const matchesSelection = !selectedOnly.checked || input.checked;
      choice.hidden = !(matchesSearch && matchesSelection);
    });

    root.querySelectorAll("[data-cycle-rack-group]").forEach(group => {
      group.hidden = ![...group.querySelectorAll("[data-cycle-location-choice]")].some(choice => !choice.hidden);
    });
    root.querySelectorAll("[data-cycle-row-group]").forEach(group => {
      group.hidden = ![...group.querySelectorAll("[data-cycle-location-choice]")].some(choice => !choice.hidden);
    });
    const areaGroup = root.querySelector("[data-cycle-area-group]");
    if (areaGroup) areaGroup.hidden = ![...areaGroup.querySelectorAll("[data-cycle-location-choice]")].some(choice => !choice.hidden);
  };

  const refresh = message => {
    groupToggles.forEach(toggle => setGroupState(toggle, membersFor(toggle)));
    updateGroupCounts();
    updateSummary();
    updateVisibility();
    if (message) status.textContent = message;
  };

  groupToggles.forEach(toggle => toggle.addEventListener("change", () => {
    const members = membersFor(toggle);
    members.forEach(item => { item.checked = toggle.checked; });
    const label = toggle.closest("label")?.innerText.trim() || "Grupo";
    refresh(`${label}: ${toggle.checked ? "seleccionado" : "limpiado"}.`);
  }));

  locations.forEach(input => input.addEventListener("change", () => {
    refresh(`${input.closest("label").querySelector("strong").textContent}: ${input.checked ? "seleccionada" : "retirada"}.`);
  }));

  search.addEventListener("input", updateVisibility);
  selectedOnly.addEventListener("change", updateVisibility);
  clearButton.addEventListener("click", () => {
    locations.forEach(item => { item.checked = false; });
    selectedOnly.checked = false;
    refresh("Selección limpiada.");
  });
  reviewButton.addEventListener("click", () => {
    const selected = locations.filter(item => item.checked);
    if (selected.length === 0) {
      status.textContent = "Selecciona al menos una ubicación antes de revisar.";
      return;
    }
    selectedOnly.checked = true;
    updateVisibility();
    selected.forEach(item => {
      let parent = item.closest("details");
      while (parent) {
        parent.open = true;
        parent = parent.parentElement?.closest("details");
      }
    });
    selected[0].closest("[data-cycle-location-choice]").scrollIntoView({
      behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
      block: "center"
    });
    status.textContent = `Mostrando ${selected.length} ubicaciones seleccionadas.`;
  });
  root.querySelector("[data-cycle-go-confirm]").addEventListener("click", () => {
    root.querySelector(".cycle-create-summary").scrollIntoView({
      behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
      block: "center"
    });
  });

  refresh();
})();
