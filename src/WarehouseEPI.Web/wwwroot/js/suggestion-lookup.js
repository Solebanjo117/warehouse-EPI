(() => {
  "use strict";
  const text = window.warehouseText || ((key, ...args) => key.replace(/\{(\d+)\}/g, (match, index) => args[Number(index)] ?? match));

  window.warehouseSuggestionLookup = ({ form, input, results, feedback, url: lookupUrl, code, describe, selected, edited, empty, found }) => {
    if (!form || !input || !results || !feedback || !lookupUrl) return;

    let timer;
    let sequence = 0;
    let activeIndex = -1;

    const buttons = () => Array.from(results.querySelectorAll("button[data-suggestion]"));
    const setFeedback = (message) => { feedback.textContent = message; };
    const close = () => {
      sequence += 1; clearTimeout(timer);
      results.replaceChildren();
      activeIndex = -1;
      input.setAttribute("aria-expanded", "false");
      input.removeAttribute("aria-activedescendant");
    };
    const activate = (index) => {
      const items = buttons();
      if (!items.length) return;
      activeIndex = (index + items.length) % items.length;
      items.forEach((item, itemIndex) => { item.classList.toggle("active", itemIndex === activeIndex); item.setAttribute("aria-selected", String(itemIndex === activeIndex)); });
      input.setAttribute("aria-activedescendant", items[activeIndex].id);
      items[activeIndex].scrollIntoView({ block: "nearest" });
    };
    const select = (item) => {
      input.value = item.dataset.code;
      close();
      setFeedback("");
      selected?.(item.suggestion);
    };
    const render = (items) => {
      close();
      if (!items.length) {
        setFeedback(empty);
        return;
      }
      const fragment = document.createDocumentFragment();
      items.forEach((item, index) => {
        const button = document.createElement("button");
        button.type = "button";
        button.id = `${results.id}-${index}`;
        button.className = "list-group-item list-group-item-action";
        button.dataset.suggestion = ""; button.suggestion = item; button.tabIndex = -1;
        button.dataset.code = code(item);
        button.setAttribute("role", "option");
        button.setAttribute("aria-selected", "false");
        const title = document.createElement("strong");
        title.textContent = code(item);
        const detail = document.createElement("small");
        detail.className = "d-block text-muted";
        detail.textContent = describe(item);
        button.append(title, detail);
        button.addEventListener("mousedown", (event) => event.preventDefault());
        button.addEventListener("click", () => select(button));
        fragment.append(button);
      });
      results.append(fragment);
      input.setAttribute("aria-expanded", "true");
      setFeedback(found(items.length));
    };
    const search = async (requestSequence) => {
      const query = input.value.trim();
      if (!query) { close(); setFeedback(""); return; }
      try {
        const url = new URL(lookupUrl, location.origin); url.searchParams.set("q", query);
        const response = await fetch(url, { headers: { Accept: "application/json" } });
        if (!response.ok) throw new Error("lookup failed");
        const items = await response.json();
        if (requestSequence === sequence) render(items);
      } catch {
        if (requestSequence !== sequence) return;
        close();
        setFeedback(text("No fue posible buscar en la red local. Conservamos el texto para que puedas consultar o reintentar."));
      }
    };

    input.setAttribute("role", "combobox");
    input.setAttribute("aria-expanded", "false");
    input.addEventListener("input", () => {
      close();
      edited?.();
      if (!input.value.trim()) { setFeedback(""); return; }
      setFeedback(text("Buscando…"));
      const current = sequence;
      timer = setTimeout(() => void search(current), 250);
    });
    input.addEventListener("keydown", (event) => {
      const items = buttons();
      if (event.key === "ArrowDown" && items.length) { event.preventDefault(); activate(activeIndex + 1); }
      else if (event.key === "ArrowUp" && items.length) { event.preventDefault(); activate(activeIndex < 0 ? items.length - 1 : activeIndex - 1); }
      else if (event.key === "Escape") { event.preventDefault(); close(); setFeedback(""); }
      else if (event.key === "Enter" && activeIndex >= 0 && items[activeIndex]) {
        event.preventDefault(); select(items[activeIndex]);
      }
    });
    input.addEventListener("blur", () => window.setTimeout(() => {
      if (document.activeElement !== input) close();
    }, 100));
    return { setValue(value) { input.value = value; close(); setFeedback(""); } };
  };
})();
