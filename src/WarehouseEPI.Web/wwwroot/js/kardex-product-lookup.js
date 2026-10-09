(async () => {
  "use strict";
  const form = document.querySelector("[data-kardex-search-form]");
  if (!form) return;
  if (!window.warehouseSuggestionLookup) await import('./suggestion-lookup.js');
  const text = window.warehouseText;
  window.warehouseSuggestionLookup({
    form,
    input: form.querySelector("[data-kardex-product-input]"),
    results: form.querySelector("[data-kardex-product-results]"),
    feedback: form.querySelector("[data-kardex-search-feedback]"),
    url: form.dataset.productsUrl,
    code: item => item.sku,
    describe: item => `${item.description || text("Sin descripción")}${item.externalReference ? text(" · Ref. {0}", item.externalReference) : ""}${item.isActive ? "" : text(" · Inactivo")}`,
    selected: () => form.requestSubmit(),
    empty: text("No se encontraron productos. Puedes consultar el texto escrito para confirmar."),
    found: count => text("{0} {1}. Usa flechas y Enter para elegir.", count, count === 1 ? text("producto encontrado") : text("productos encontrados"))
  });
})();
