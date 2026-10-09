# Componentes y patrones de interfaz existentes

## Eliminación de productos sin uso

`Pages/Admin/Catalogs/Products/Edit.cshtml` enlaza a `Delete.cshtml(.cs)` para
revisar SKU y descripción y confirmar mediante POST con antiforgery, solo ADMIN.
Reutiliza tarjetas, alertas, botones Bootstrap y `CatalogTexts` ES/EN; no requiere
JavaScript. Ejemplo: Editar producto → Eliminar producto → Confirmar eliminación
regresa al catálogo con confirmación. Cancelar regresa al editor.
`ProductDeletionService` comprueba todas las relaciones EF hacia Product: solo
códigos de barras y asignaciones de ubicación pueden eliminarse junto al producto.
Saldos (incluso cero o negativos), movimientos, lotes, documentos, producción y
otras dependencias bloquean el borrado e indican la alternativa de desactivación.
El POST revalida bajo transacción y bloqueo del producto en PostgreSQL; GET no
escribe. Pruebas: `ProductDeletionTests` y `ProductDeletionPostgreSqlTests`;
la segunda requiere credenciales con permiso para crear una base temporal propia
con prefijo `warehouse_epi_product_delete_test_`.

Catálogo para agentes y desarrolladores. Consultarlo antes de implementar UI y
preferir lo existente sin requerir una petición explícita del usuario.
Las rutas enlazadas son la fuente de verdad; verificar su estado antes de usarlas.

No todo lo listado es un componente independiente. Los parciales y las APIs
exportadas se pueden reutilizar con sus contratos; los controladores ligados a
una página son referencias para adaptar o extraer una parte común, no scripts
intercambiables. Este catálogo cubre las piezas principales, no cada pantalla.

## Selección de productos y ubicaciones

### Descripción por fragmentos

`Infrastructure/Inventory/ProductTextSearch.cs` aporta `WhereProductText` para
extender una consulta de productos: exige todos los fragmentos de descripción,
separados por espacios, sin importar su orden ni mayúsculas. Por ejemplo,
«transparente bolsa» encuentra «Bolsa de polietileno transparente». No corrige
errores ortográficos ni elimina acentos. Conserva como alternativa el predicado
original de SKU, referencia, códigos de barras y otros campos de cada consumidor.

Depende de expresiones LINQ traducibles por EF Core y se aplica antes del límite
o la paginación. Integración real: `OperationalInventoryQueryService` llama
`ProductQuery(true).WhereProductText(term, product => ...)` para operaciones y
usa `ProductQuery(false)` para Existencias/croquis. También lo usan
`ProductPageSupport.ApplySearch` (catálogo y exportación), el handler `Products`
de Kárdex y `ProductionDailyProductLookup` (sugerencias por grupo).
Conserva los filtros de actividad, permisos, orden y contratos JSON existentes;
la resolución exacta de SKU/código de barras para HID/cámara no cambia.

Cuando un formulario necesite elegir un producto, partir del buscador que muestra
coincidencias al escribir. Elegir la variante según el contexto:

| Caso de uso | Implementación y ejemplo real | Contrato que conservar |
| --- | --- | --- |
| Seleccionar producto y ubicación en movimientos | [_GuidedMovementForm.cshtml](../src/WarehouseEPI.Web/Pages/Operations/_GuidedMovementForm.cshtml), [operations.js](../src/WarehouseEPI.Web/wwwroot/js/operations.js), [Lookup.cshtml.cs](../src/WarehouseEPI.Web/Pages/Operations/Lookup.cshtml.cs). Ejemplo: [Transfer.cshtml](../src/WarehouseEPI.Web/Pages/Operations/Transfer.cshtml). | Controlador ligado a `[data-operation]`, `data-lookup-url` y `[data-operation-form]`. Los campos usan `data-lookup-field`, `data-lookup-input`, `data-lookup-results` y el registro seleccionado. Conservar los IDs enviados al servidor, la resolución de códigos y las relaciones producto/ubicación. |
| Consultar movimientos de un producto en Kardex | [Index.cshtml](../src/WarehouseEPI.Web/Pages/Reports/Kardex/Index.cshtml), [kardex-product-lookup.js](../src/WarehouseEPI.Web/wwwroot/js/kardex-product-lookup.js), [Index.cshtml.cs](../src/WarehouseEPI.Web/Pages/Reports/Kardex/Index.cshtml.cs). | `[data-kardex-search-form]`, `data-products-url`, `data-kardex-product-input`, `data-kardex-product-results` y `data-kardex-search-feedback`. Usa el handler `Products` de Kardex. Elegir una sugerencia pone el SKU y envía el formulario; no equivale a seleccionar un ID para registrar un movimiento. |
| Agregar productos a captura diaria de producción | [_CaptureGroup.cshtml](../src/WarehouseEPI.Web/Pages/Operations/Production/_CaptureGroup.cshtml) y [production-daily-products.js](../src/WarehouseEPI.Web/wwwroot/js/production-daily-products.js). | `[data-daily-product-field]`, `data-url`, `data-resolve-url`, `data-product-input`, `data-product-id`, `data-product-results` y `data-daily-product-add`. Mantener los grupos de productos programados, pendientes y otros, y la resolución del producto antes de agregarlo. |

Antes de adaptar un buscador:

- **Motor compartido de sugerencias de consulta:** `wwwroot/js/suggestion-lookup.js`
  extrae de Kárdex la búsqueda con espera de 250 ms, descarte de respuestas anteriores,
  lista Bootstrap, flechas/Enter/Escape y anuncios accesibles. Se configura mediante
  `window.warehouseSuggestionLookup({ form, input, results, feedback, url, code,
  describe, selected, edited, empty, found })`. No registra movimientos ni decide
  las reglas del catálogo. Depende de `site.css` (`lookup-field`, `lookup-results`)
  y `warehouseText`/`ClientTexts` para ES/EN. Cargarlo antes del adaptador de página.
  Las opciones `.lookup-results > .list-group-item` de `site.css` no se comprimen
  en el contenedor flex de Bootstrap: conservan altura natural, mínimo táctil de
  44 px y ajuste de palabras largas; el desplazamiento ocurre en la lista.

- En movimientos, el handler `ProductLocations` de `Lookup.cshtml.cs` reutiliza
  `OperationalInventoryQueryService.GetProductLocationsAsync` para las sugerencias
  relacionadas y la selección automática de `operations.js` (por ejemplo, Transfer).
  Excluye ubicaciones con función WIP, tengan saldo o asignación; conserva el destino
  principal sin saldo de Entrada si es de almacén. Los destinos de surtimiento a WIP
  se consultan por separado mediante `WipLocations`.

- Revisar juntos el Razor, el JavaScript y el handler; no basta con copiar la apariencia del input.
- Mantener SKU, descripción y demás datos que ayuden a distinguir coincidencias.
- Conservar los estados de búsqueda, sin resultados y error de red, y el texto capturado para reintentar.
- Preservar la selección por teclado y los atributos accesibles de la variante usada.
- Verificar las reglas de productos activos/inactivos y los permisos del endpoint de destino. Un buscador de consulta no define las reglas de captura.
- Mantener separados texto escrito y selección válida cuando el servidor requiera un ID.

### Aviso configurable al compartir un área

- **Caso de uso:** áreas con varios materiales pendientes de acomodo, como
  STAGING, pueden omitir la confirmación de compartir sin cambiar su función
  operativa ni el control de existencias.
- **Archivos:** `Pages/Admin/Catalogs/Locations/Area.cshtml` y `Area.cshtml.cs`,
  `Core/Entities/Location.cs` e `Infrastructure/Inventory/InventoryMovementStore.cs`.
- **Dependencias:** `Location.WarnOnMixedProducts`, persistencia EF Core,
  `CatalogTexts` ES/EN y la confirmación existente en `_GuidedMovementForm.cshtml`.
- **Integración real:** ADMIN desmarca `Input.WarnOnMixedProducts` en
  `/Admin/Catalogs/Locations/Area?locationId=...` para STAGING. Entrada,
  Transferencia y recepción contra documento reutilizan la decisión del servidor.
  El aviso está activado por defecto; los racks conservan su confirmación y WIP
  conserva sus reglas documentales. Solo el saldo neto positivo de otros productos
  cuenta como ocupación para el aviso; cero y negativo no lo disparan. El saldo
  se suma por producto/ubicación entre lotes. Se conservan NIP, antiforgery,
  idempotencia, disponibilidad, saldos e historial.

### Búsqueda de productos en el croquis y las ubicaciones

`Pages/Locations/_LocationIndex.cshtml`, `wwwroot/js/location-index.js` y
`Pages/Locations/LocationIndexPageModel.cs` comparten el buscador de croquis,
racks y lista. Depende de `InventorySearch` en `Pages/Operations/Lookup.cshtml.cs`,
`WarehouseDbContext` y `WarehouseMapService`; conserva sugerencias, teclado/HID,
localización y filtros GET.

Ejemplo real: «Croquis y ubicaciones» en
`Pages/Admin/Catalogs/Products/Details.cshtml` abre `/Locations?search=SKU`.
La consulta pública busca por SKU, descripción, referencia o barras activas
del producto y marca solamente ubicaciones de almacenamiento cuyo saldo neto
por producto/ubicación sea distinto de cero, sumando todos los lotes. Incluye
saldos negativos como incidencias; un saldo histórico cero o una asignación
vacía no indica presencia. El mapa completo sigue disponible y la búsqueda
directa por código o descripción de ubicación permite consultar posiciones
vacías y WIP. La vista ADMIN conserva además coincidencias por asignación
activa para mantenimiento, aunque no tengan saldo.

## Otras piezas disponibles

### Exportación completa del catálogo de productos

El botón ADMIN de `Pages/Admin/Catalogs/Products/Index.cshtml` llama a
`?handler=Export`; el handler comprueba ADMIN además de `ProductsRead`.
Reutiliza Bootstrap, `CatalogTexts` y `ProductCatalogExportService` en
`Infrastructure/Catalogs`, con ClosedXML y la detección de texto peligroso de
`ReportExportService.SanitizeText`. No aplica los filtros ni la página visible:
incluye activos e inactivos y rechaza más de 10,000 filas sin truncarlas.

El XLSX tiene una hoja `ITEM LISTING`, seis columnas exactas: CLASS,
PACKED BY (AREA), ITEM (Short), DESCRIPTION, U/M, ITEM (COMPLETE).
CLASS contiene el código guardado; PACKED BY (AREA) queda vacío al no existir
un campo persistido. U/M usa `Nombre (CÓDIGO)` y queda vacío para UNASSIGNED.
Todas las celdas se guardan como texto; los prefijos de fórmula llevan la
protección de Excel sin alterar SKU ni referencia. Ejemplo: exportar mientras
se consulta `?status=active&pageNumber=2` descarga igualmente el catálogo completo.
El formato reducido de seis columnas es de auditoría: el importador actual
espera la referencia completa en la columna 12 y no admite este layout directamente.

| Pieza | Cuándo usarla | Archivos y dependencias |
| --- | --- | --- |
| Formulario guiado de movimiento | Pasos de producto, ubicación, cantidad, resumen y confirmación con NIP compatibles con los movimientos existentes. | [_GuidedMovementForm.cshtml](../src/WarehouseEPI.Web/Pages/Operations/_GuidedMovementForm.cshtml) y [operations.js](../src/WarehouseEPI.Web/wwwroot/js/operations.js). Seguir el modelo y orden de scripts de [Transfer.cshtml](../src/WarehouseEPI.Web/Pages/Operations/Transfer.cshtml); no trasplantar el parcial a un modelo incompatible. |
| Captura con lector HID | Recibir un código de lector sin obligar a enfocar un input, dentro de un área operativa. | [hid-capture.js](../src/WarehouseEPI.Web/wwwroot/js/hid-capture.js): `WarehouseEpiHidCapture.listen({ root, isEnabled, onScan, ... })`. Devuelve una función de limpieza; respeta campos editables y modales. Cargar antes del controlador que lo consume, como en Transfer. |
| Escáner por cámara | Leer códigos mediante cámara con recuperación mediante foto y selección de cámara. | [operations.js](../src/WarehouseEPI.Web/wwwroot/js/operations.js): `WarehouseEpiCreateCameraScanner({ element, onCode, onAccepted, onClosed, instruction })`. Revisar el modal `data-camera-scanner` del formulario guiado y la carga de ZXing en Transfer; no basta con agregar un botón. |
| Tema claro, oscuro y de sistema | Controles de apariencia o nuevas pantallas que deban respetar el tema actual. | [_ThemeControl.cshtml](../src/WarehouseEPI.Web/Pages/Shared/_ThemeControl.cshtml), [theme-init.js](../src/WarehouseEPI.Web/wwwroot/js/theme-init.js), [site.js](../src/WarehouseEPI.Web/wwwroot/js/site.js) y [site.css](../src/WarehouseEPI.Web/wwwroot/css/site.css). Integrados en el layout; reutilizar `data-bs-theme` y los estilos existentes. |
| Idioma y textos de JavaScript | Nuevas etiquetas, mensajes y controles localizados. | [_LanguageControl.cshtml](../src/WarehouseEPI.Web/Pages/Shared/_LanguageControl.cshtml), [site.js](../src/WarehouseEPI.Web/wwwroot/js/site.js) (`window.warehouseText`) y [LOCALIZATION.md](LOCALIZATION.md). Respetar los recursos y la provisión de textos del layout. |
| Navegación y alertas operativas | Páginas dentro de la estructura común y acceso a alertas existentes. | [_Layout.cshtml](../src/WarehouseEPI.Web/Pages/Shared/_Layout.cshtml), [site.js](../src/WarehouseEPI.Web/wwwroot/js/site.js) y [operational-notifications.js](../src/WarehouseEPI.Web/wwwroot/js/operational-notifications.js). Ya se inicializan desde el layout; conservar las condiciones de acceso y el endpoint configurado. |
| Copiar identificadores e imprimir | Acciones sencillas de copiar un valor o imprimir la página. | [site.js](../src/WarehouseEPI.Web/wwwroot/js/site.js): `data-copy-value`, `data-copy-status`, `data-print-page` y `data-print-validation`. Copiar anuncia el resultado cuando existe el nodo de estado; imprimir llama a `window.print()` y requiere revisar el diseño de impresión de la página. |

## Mantenimiento

### Administración de áreas

`Pages/Admin/Catalogs/Locations/Areas.cshtml` y su `PageModel` ofrecen consulta
ADMIN de áreas por código/descripción y estado, con 25 registros por página.
El acceso desde `_LocationIndex.cshtml` no hereda filtros de rack o fila.
Las tarjetas reutilizan Bootstrap y `CatalogTexts`, con enlaces explícitos al
editor existente `Area.cshtml?locationId=...` y a la ficha por ID.

El editor conserva el ID y usa `Location.Description` también para las sugerencias
de operaciones. Integra `ProductionProcessConfigurationService`,
`LocationWipRules` y `LocationAreaAdministrationService`; mantiene antiforgery,
ADMIN, versión de procesos y comprobación de `OriginalUpdatedAt` bajo bloqueo
PostgreSQL. Los errores no actualizan silenciosamente las versiones del formulario.
La desactivación limpia el bloqueo; la eliminación sigue siendo un formulario
separado con NIP y confirmación. Ejemplo real: Áreas → Editar área → Guardar área
abre la ficha con confirmación y permite volver a Áreas.

Verificación: `AreaAdministrationTests` cubre los POST reales, permisos,
descripciones, estados, filtros, paginación y conflictos. Si se define
`WAREHOUSE_EPI_AREA_UI_OUTPUT` como `artifacts/ui/playwright/area-fixtures`
(preferentemente con ruta absoluta), exporta HTML de prueba sin tokens antiforgery.
`tests/javascript/area-editor.browser.cjs` usa esos fixtures y assets locales
para revisar idiomas, temas, tamaños, teclado y controles de estado en Edge,
sin conectarse a la aplicación operativa.

### Formato físico de racks

Para dibujar un rack, usar `RackFormat.PalletOrder` y `RackFormat.Columns` de
[`RackFormat.cs`](../src/WarehouseEPI.Core/RackFormat.cs). Numera de izquierda a
derecha y desde el suelo: en 2 × 2 devuelve `3, 4, 1, 2`. Los racks sin formato
guardado conservan 3 × 3; las dimensiones admitidas son de una a tres columnas
y de uno a tres niveles, dentro del límite existente de nueve pallets.

[`LocationRackFormats.cs`](../src/WarehouseEPI.Infrastructure/Locations/LocationRackFormats.cs)
carga el formato de un rack o todos los formatos en una consulta. Depende de
`WarehouseDbContext.LocationRackFormats` y de la migración `AddLocationRackFormats`.
En cuadrículas del layout, combinar `rack-format-grid` de
[`site.css`](../src/WarehouseEPI.Web/wwwroot/css/site.css) con
`data-rack-columns="@Model.Format.Columns"`; ejemplo real:
[`Details.cshtml`](../src/WarehouseEPI.Web/Pages/Locations/Details.cshtml).
Exhibición usa las variantes equivalentes de `location-display.css`, pues se
reproduce sin el layout. Impresión también recibe `data-rack-levels`.

El editor
[`Edit.cshtml`](../src/WarehouseEPI.Web/Pages/Admin/Catalogs/Locations/Rack/Edit.cshtml)
y [`rack-editor.js`](../src/WarehouseEPI.Web/wwwroot/js/rack-editor.js) son
controladores de esa página. Sus selectores de columnas y niveles reordenan los
nodos existentes, seleccionan las posiciones del formato y rechazan la reducción
si excluye una posición protegida. Conservar `PresentPallets`, `WipPallets`, los
códigos de ubicación, revisión, motivo, NIP ADMIN y la validación del servicio.
El formato se guarda junto con la corrección física y su auditoría; no cambia
la geometría arquitectónica del croquis ni renumera ubicaciones existentes.

### Resumen del día de producción

La pestaña `summary` de `/Operations/Production` reutiliza la navegación de
`Index.cshtml`, el reloj `WarehouseClock` y `GetDailySummaryAsync`. Su carga está
en `Index.Overview.cs`, la agregación de presentación en
`Web/Production/ProductionDayOverview.cs` y la vista en `_DayOverview.cshtml`.
Depende de `ProductionTexts`, los tokens Bootstrap y `production-overview.css`;
`production-overview.js` anuncia la navegación GET y restaura el estado al volver.

Ejemplo: `?Tab=summary&Day=2026-09-21&OverviewShift=1` muestra T1; los enlaces
a balance conservan fecha y SKU y la captura abre el flujo existente con NIP.
El turno solo filtra producción; meta, arrastre y pendientes son del día completo.
Las etapas nunca se suman entre sí; las cantidades se agrupan por unidad y
UNASSIGNED queda separado por producto. No se compensan pendientes de un SKU
con adelantos de otro. La meta utiliza `DailyCoverage.Target` y solo se agrega
si toda la población del grupo tiene meta positiva; no se muestran porcentajes.
La lista de atención (25 filas por página) usa saldo pendiente, conciliación y
unidad faltante, sin umbrales de retraso. Los enlaces antiguos con semana/fecha
o SKU sin `Tab` siguen abriendo balance; la entrada sin filtros abre el resumen.

Pruebas: `ProductionDayOverviewTests` verifica unidades, turnos, reversos y rutas;
`WAREHOUSE_OVERVIEW_FIXTURES` permite exportar HTML aislado sin antiforgery para
`tests/javascript/production-overview.browser.cjs`, que revisa Edge y assets locales.

### Captura T1/T2 dentro de Tabla y balance

Para renderizar o recuperar filas editables, reutilizar
[`_BalanceRows.cshtml`](../src/WarehouseEPI.Web/Pages/Operations/Production/_BalanceRows.cshtml)
y la carga de metadatos de
[`Index.BalanceWorkspace.cs`](../src/WarehouseEPI.Web/Pages/Operations/Production/Index.BalanceWorkspace.cs).
La elegibilidad usa `ProductionDailyBalanceService.GetCaptureEligibilityAsync`
en [`ProductionBalanceCaptureEligibility.cs`](../src/WarehouseEPI.Infrastructure/Production/ProductionBalanceCaptureEligibility.cs):
semana abierta, fecha dentro de semana, producto activo y procesos configurados y activos.
Este editor admite los tres procesos aunque no estén en la ruta del producto.
El handler `BalanceRows` es un ejemplo de integración; la recuperación de borradores
y `PreviewBalanceEditAsync` usan la misma decisión. `area.Applies` describe actividad
del balance, no autoriza captura. Conservar los selectores T1/T2, revisión, NIP,
idempotencia y validación de cantidades. Habilitar una celda no crea programación,
arrastre ni disponibilidad física en ese proceso.

Al añadir o modificar una pieza compartida, actualizar su entrada en este archivo.
Incluir cuándo usarla, dónde está, cómo se integra y qué límites tiene. Si se
extrae un componente común de un controlador de página, sustituir aquí la
referencia anterior y enlazar al menos un consumidor real. Evitar duplicar
implementaciones o crear abstracciones generales para necesidades hipotéticas.


## Contrato WIP documental

- **Caso de uso:** registrar uso, merma o devolución de una entrega a WIP,
  general o asignada a orden, sin tratar WIP como almacén.
- **Archivos:** `Pages/Operations/WipProcess.cshtml`, `WipProcess.cshtml.cs`,
  `wwwroot/js/wip-process.js`, `Pages/Reports/Wip/Document.cshtml` y
  `Pages/Reports/Wip/_TrackedReport.cshtml`.
- **Dependencias:** buscador/resolución de productos y cámara existentes,
  Bootstrap modal, localizadores `OperationsTexts`/`CatalogTexts`,
  `WipDocumentService` y `WipReportService.GetTrackedPageAsync`.
- **Integración real:** el detalle `/Reports/Wip/Document?id=...` abre
  `/Operations/Wip?documentId=...&wipCode=...&productCode=...`. El identificador
  de documento limita la aplicación; el servidor valida producto, WIP, lotes,
  pendiente, asignaciones a orden y NIP. Conservar antiforgery, identidad de
  operación, permisos y revisión de destino compartido. Los controladores y
  el script siguen siendo específicos de esta página, no componentes genéricos.
- **Ubicaciones:** `PositionRow.IsWip` en impresión y `OperationalRole` en
  croquis/ficha suprimen stock y asignaciones; el rótulo es
  `WIP · Sin control de existencias`. Los racks mixtos calculan ocupación
  exclusivamente sobre almacenamiento.

### Asociación de racks a un área WIP

- **Caso de uso:** varios racks mixtos comparten el reporte de un área WIP,
  únicamente para sus posiciones WIP. No representa distribución de material
  entre racks, ni cambia el destino de documentos o capturas existentes.
- **Archivos:** `Pages/Shared/_RackWipAssociation.cshtml`,
  `Infrastructure/Locations/LocationRackWipAssociations.cs` y el editor
  `Pages/Admin/Catalogs/Locations/Rack/Edit.cshtml`.
- **Dependencias:** `RackWipAssociationView`, `CatalogTexts` español/inglés,
  Bootstrap y `/Reports/Wip?wipAreaId=...`. La consulta en listados se carga
  una vez por página, no desde el parcial ni por posición.
- **Integración real:** la ficha de una posición WIP usa
  `<partial name="/Pages/Shared/_RackWipAssociation.cshtml" model="Model.WipAssociation" />`.
  El listado, croquis, exhibición e impresión reutilizan el mismo parcial.
  Una asociación nula no muestra contenido; una asociación inactiva/bloqueada
  conserva el destino y advierte que no está disponible para nuevas operaciones.
- **Edición:** ADMIN, revisión, motivo, NIP e idempotencia existentes.
  `ExpectedWipAreaId` impide sobrescribir una asociación cambiada por otra sesión.
  Eliminar la última posición WIP elimina la asociación y se muestra en revisión.

### Trazabilidad de importaciones WIP

El croquis reutiliza `Pages/Locations/_AssociatedWipSummary.cshtml` dentro de
`_LocationIndex.cshtml` para mostrar las cinco últimas entregas del área asociada,
también en racks mixtos. Recibe `LocationIndexPageModel.AssociatedWipSummary`;
la integración usa `name="/Pages/Locations/_AssociatedWipSummary.cshtml"` para
resolver el parcial desde `/Locations` y `/Admin/Catalogs/Locations`.
El PageModel conserva acceso ADMIN y consulta `WipReportService.GetTrackedPageAsync` una vez por área
distinta, con fechas abiertas y límite de cinco. Reutiliza tarjetas
`map-wip-issue`, detalles nativos de teclado, `CatalogTexts` y enlaces al documento.
El resumen se abre inicialmente y aclara que es compartido; no calcula stock ni
combina entregas directas a posiciones. Las aperturas se identifican como tales.

Las tarjetas de `Pages/Admin/Inventory/WipImport.cshtml` reutilizan enlaces Razor
al detalle ADMIN de movimientos y a `Reports/Wip/Document`. Sus datos proceden
de `WipTransferReviewRow` (ubicaciones guardadas, `RecordedAt` local y `DocumentId`),
no del selector de ubicaciones disponibles. Ejemplo: volver a abrir una entrega
cuyo origen ya no tiene saldo conserva su origen y ofrece «Ver documento WIP».
Depende de `WipTransferImportService`, `WarehouseClock` y `CatalogTexts`.
El reporte permite borrar ambos límites de fecha para consultar todo el historial.

El selector «Mostrar» de la importación WIP reutiliza Bootstrap y `CatalogTexts`.
Su opción `notImported` excluye las entregas ya importadas antes de paginar,
sin cambiar las identidades ni las reglas de confirmación.

### Importación de productos desde Inventario interno

- **Caso de uso:** consultar la fuente fija de EP Industrial como alternativa al
  Excel desde `/Admin/Catalogs/Products/Import`, exclusivamente para ADMIN.
- **Archivos:** `Pages/Admin/Catalogs/Products/Import.cshtml(.cs)`,
  `wwwroot/js/product-import.js`, `Imports/ProductImportService.cs` (Web) y
  `Imports/InternalInventoryClient.cs`, `InternalInventoryReader.cs`,
  `ProductImportRowProcessor.cs` (Infrastructure).
- **Dependencias:** Bootstrap Collapse, CatalogTexts ES/EN, AngleSharp y el
  almacén de vistas previas existente. Los lectores Excel y HTML comparten
  validación, normalización y consolidación mediante `ProductImportRowProcessor`.
- **Integración real:** `OnPostInternalInventoryAsync` obtiene un
  `ProductSpreadsheetReadResult` y llama a `PrepareInternalInventoryAsync`.
  El servidor fija altas y actualizaciones; reutiliza resolución de unidades/duplicados,
  propietario, caducidad, antiforgery y confirmación existentes. Analizar no escribe.
- **Contrato:** último segmento del SKU, referencia completa y descripción del origen;
  conserva la clase existente y deja sin clase los productos nuevos. Los campos
  vacíos conservan valores existentes. La U/M se actualiza solo sin movimientos;
  con movimientos, `PendingUnitCode` muestra la unidad recibida como advertencia
  y conserva la local, permitiendo actualizar los demás campos. El resumen y la
  confirmación incluyen el número de conflictos pendientes. Ejemplo: un producto
  en EA con movimientos recibe BX y otra descripción; se actualiza la descripción,
  conserva EA y muestra BX como pendiente. Una discrepancia aislada no cuenta como
  actualización. Depende también de `ProductImportComparison.cs` y
  `ProductImportModels.cs`; Excel conserva su bloqueo por cambio de U/M.
  Se conserva la exclusión de agrupaciones sin cantidades y sin descripción o
  unidad. Las cantidades
  únicamente identifican agrupaciones. Se conservan filas originales y un resumen
  de origen, filas consultadas, agrupaciones excluidas y productos analizados.
- **Credenciales y red:** campo de contraseña sin valor, nunca almacenado ni
  devuelto; transporte/cookies por consulta. Autenticación solo en
  `https://www.epindustrial.com`; visor en `https://extrapackaging.ws` sin cookies
  WordPress. Redirecciones restringidas al origen de cada etapa; 30 segundos,
  10 MB descomprimidos por página y 10,000 filas antes de excluir agrupaciones.
- **Interacción:** el desplegable enfoca la contraseña, informa carga y errores,
  y limpia el campo al regresar. Excel conserva su selector independiente.
