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

En el historial de placas, **Imprimir placa** de una transferencia utiliza solo
placas del producto que siguen en su ubicación destino. No ofrece el remanente
del origen. `PalletTrackingService.PrintablePlatesForMovementsAsync` aplica este
filtro y `SuggestionAsync` toma el destino de la transferencia. Al abrir una placa
por ID, `PalletLabels/Index` respeta su ubicación actual aunque conserve el UUID de
una entrada original en STAGING; las reglas por entrada de STAGING solo se aplican
mientras permanezca allí. Ejemplo: STAGING → A-1-1 imprime la placa en A-1-1,
tanto en traslados completos como parciales.

## Lista de staging y transferencia con datos fijos

- **Destinos propuestos al acomodar:** `PutawayModel` carga `DestinationProposals`
  mediante `GetProductLocationsAsync`, conserva solo saldo neto positivo por ubicación
  y excluye el origen. Se reutilizan sus filtros de almacén operativo y suma de lotes.
  `_DestinationProposals.cshtml`, dentro del paso destino del formulario guiado,
  muestra código, descripción y saldo/unidad en opciones `relationship-choice` con
  lista desplazable. `ShowDestinationProposals` está desactivado por defecto en otros
  consumidores. Los botones `data-destination-proposal` llaman a la selección normal
  de `operations.js` solo al pulsarlos; nunca precargan el destino, ni siquiera con una
  única opción. GET y POST fallido cargan propuestas; el POST conserva el destino
  capturado y limpia el NIP mediante el flujo existente. Sin sugerencias, el mensaje
  invita a buscar/escanear. Dependencias: Bootstrap, `site.css`, textos OperationsTexts
  ES/EN y validación de transferencia existente. Ejemplo: `/Operations/Staging/Putaway?arrival=...`.

### Acceso Recibir en STAGING

- **Caso de uso:** la tarjeta de Operaciones abre `/Operations/Entry?mode=staging`
  con STAGING preseleccionado y editable, sin registrar movimientos mediante GET.
- **Archivos:** `Navigation/ModuleNavigation.cs`, `Pages/Operations/Entry.cshtml.cs`
  y `Pages/Operations/OperationPageModel.cs`.
- **Dependencias:** `OperationalInventoryQueryService.ResolveLocationAsync`, aviso
  `PrefillWarning`, recursos SharedTexts/OperationsTexts ES/EN y formulario guiado existente.
- **Integración real:** la acción usa `Mode: "staging"`; `RouteValues` conserva
  también `view`. Entrada resuelve el código exacto, sin GUID fijo. Si falta, está
  inactivo, bloqueado, ausente físicamente o es WIP, deja el destino vacío con aviso.
  El destino explícito se conserva al seleccionar productos y se puede cambiar.
  Entrada normal y sus enlaces con `destinationLocationId` conservan su comportamiento.

### Continuar desde el comprobante de STAGING

- **Caso de uso:** entradas normales, documentales y de producción muestran acciones
  por línea hacia su llegada exacta y «Registrar siguiente entrada» hacia Entrada
  con `mode=staging`. Los flujos del documento/orden conservan sus acciones.
- **Archivos:** `Pages/Operations/Receipt.cshtml(.cs)`, `Staging/Index.cshtml(.cs)`,
  `PalletLabels/Index.cshtml(.cs)`, `OperationalInventoryQueryService.cs`,
  `StagingArrivalQuery.cs` y `StagingEntryPlates.cs`.
- **Contrato:** `InventoryReceiptLine.LineId` y `ForMovementAsync` enlazan líneas,
  no SKU. `/Operations/Staging?arrivalLineId=...` muestra solo esa llegada,
  incluso agotada, y conserva el contexto tras errores de identificación.
  «Ver todas las llegadas» restaura la lista normal.
- **Impresión:** `?handler=StagingArrival&arrivalLineId=...#label-preview`
  reutiliza el motor existente. Valida placas, versión y cobertura por lotes antes
  y después de construir la previsualización; no identifica ni mueve stock mediante GET.
  Las llegadas históricas por identificar enlazan a su captura física existente.
  Las agotadas muestran estado y seguimiento. Identificadores inválidos o de
  movimientos corregidos devuelven 404; un reemplazo vigente conserva sus propias acciones.
- **Dependencias:** recursos OperationsTexts ES/EN, Bootstrap, consulta de
  llegadas registrada en DI y plantilla publicada PLT-LICENSE-PLATE.
  Ejemplo: dos líneas del mismo SKU en una recepción imprimen sus placas por
  separado, mientras una tercera línea destinada a otra ubicación conserva su flujo.

### Dividir en pallets o rollos

- **Caso de uso:** Entrada en `mode=staging` ofrece una distribución opcional de
  2 a 100 cantidades cuya suma determina automáticamente la cantidad recibida.
  Activarla oculta la cantidad manual y muestra solo el total recibido calculado.
  Desactivarla recupera el campo manual con la suma actual; las partes permanecen
  disponibles durante la captura y se recuperan al reactivarla.
  Está disponible únicamente con destino real STAGING. Cambiar el destino desactiva
  la opción; la captura normal conserva el comportamiento existente.
- **Componente compartido:** `Pages/Operations/_PalletDistribution.cshtml`,
  `PalletDistributionInput.cs` y `wwwroot/js/pallet-distribution.js`. Recibe
  `PalletDistributionEditor` con cantidades, total, unidad, precisión y modalidad
  opcional. `CalculateTotal: true` distingue el total calculado de Entrada del
  total fijo al dividir una placa existente (valor predeterminado `false`).
  Reutiliza Bootstrap y OperationsTexts ES/EN; valida totales con precisión
  de cuatro decimales y respeta unidades enteras. `operations.js` le comunica
  producto/destino/cantidad mediante `WarehousePalletDistribution.update` y añade
  `summary()` a la revisión existente, conservando buscador, HID, cámara y NIP.
  El editor notifica `palletdistributionchange` solo por interacción; `update`
  sincroniza sin emitir otro evento. `isValid()` controla la revisión y
  `quantityTarget()` dirige el foco a la primera parte cuando el total es automático.
- **Integración real:** `EntryModel` valida las partes, calcula `Input.Quantity`
  mediante `PalletDistribution.TryCalculateTotal` y reemplaza su `ModelState`
  antes de la validación general, ignorando la cantidad manual enviada cuando se
  divide. Entrega `PalletQuantities` al servicio de entrada existente:
  capturar 40/35/25 calcula 100 y registra una sola
  línea y tres PLT. No son tres llegadas. Un POST fallido conserva las cantidades.
- **División posterior:** Lista de staging enlaza a
  `/Operations/Staging/Split?arrivalLineId=...`; el operador selecciona una placa
  y distribuye únicamente su remanente. `Split.cshtml(.cs)` reutiliza el editor
  obligatorio, revisión Bootstrap, antiforgery y NIP. `StagingPlateSplit.cs`
  valida llegada/placa/versiones, reservas, preparaciones y cobertura de lotes
  bajo bloqueo de ubicación. Su clave de operación hace los reintentos idempotentes.
- **Trazabilidad:** eventos `StagingSplitSource`/`StagingSplitChild`, ligados a la
  línea original y al responsable, sustituyen la placa por nuevos folios con la
  misma composición agregada por lote. No crean movimientos ni alteran saldos.
  Las demás placas permanecen intactas. La original queda en cero, se muestra
  «Dividida», no se imprime ni reutiliza; el seguimiento enlaza sus descendientes.
  Una corrección incompatible posterior queda bloqueada por los eventos existentes.
- **Consulta e impresión:** `StagingArrivalQuery` reconoce los hijos sin duplicar
  recibido ni pendientes. `PalletLabels?handler=StagingArrival&arrivalLineId=...`
  imprime placas actuales en grupos de 100 con `arrivalBatch`; el servidor comprueba
  pertenencia, versión y lotes de cada grupo. El límite por llegada es 1000 placas
  pendientes. Identificación histórica precede a la división si falta identidad.
  Los GET consultan únicamente; no identifican, dividen ni mueven material.
- **Alcance:** cantidades y folios PLT propios, sin número externo de rollo,
  redistribución entre placas, migraciones ni cambios de permisos. La captura
  opcional inicial no se añade a recepciones documentales o de producción.

### Contador y acomodo de llegadas

- **Antigüedad y prioridad (solo Lista de staging):** `StagingArrivalQuery.OverviewAsync`
  recorre lotes de 100 y reutiliza la evaluación de pendientes para obtener página
  de 25 y resumen global, incluyendo históricos por identificar. Busca por SKU,
  descripción o referencia y filtra prioridad antes de paginar, con orden por
  fecha e ID. `ListAsync` y `CountPendingAsync` conservan sus contratos anteriores.
  `StagingArrivalAge` calcula duración UTC y clasifica Normal (<24 h), Atender
  pronto (24–<48 h) y Urgente (>=48 h); los umbrales son fijos y continuos.
  El PageModel captura `TimeProvider.GetUtcNow()` una vez y convierte las fechas
  locales en lote con `WarehouseClock`. Una fecha futura tiene antigüedad cero.
- **Integración real:** `/Operations/Staging?priority=urgent&search=SKU&pageNumber=2`
  conserva el resumen global sin filtros. `priority` acepta `normal`, `soon`,
  `urgent`; desconocido equivale a Todas. Buscar reinicia página, Actualizar
  conserva filtros/página y Limpiar filtros restaura el listado completo.
  El POST de identificación conserva el contexto ante errores. La vista por
  `arrivalLineId` ignora filtros y omite urgencia si ya no hay pendiente.
  Identificar, dividir o acomodar parcialmente no reinicia la fecha de llegada;
  salir y regresar crea una llegada con fecha propia. Tarjetas Bootstrap y textos
  OperationsTexts ES/EN presentan edad, fecha exacta y prioridad accesible sin
  depender solo del color. No hay sondeo, configuración, escrituras GET ni cambios
  en Entrada, comprobantes o el contador de Operaciones.

- **Contador de llegadas:** `StagingArrivalQuery.CountPendingAsync` comparte la
  evaluación de pendientes del listado y recorre lotes de 100, sin filtros ni
  límite de página. Incluye pendientes por identificar; excluye agotados y
  correcciones con las mismas reglas de la lista. Cuenta llegadas, no SKU ni unidades.
- **Integración:** `Pages/Modules/Index.cshtml(.cs)` carga una vez el total solo
  para Operaciones y lo muestra en la tarjeta Lista de staging con `notification-count`.
  `Pages/Operations/Entry.cshtml(.cs)` lo carga solo en modo staging y presenta
  «Lista de staging · N pendientes» encima del formulario, incluso con cero.
  `Mode` se conserva mediante un input asociado al `id="operation-form"` del
  formulario guiado; un POST con errores recalcula el total sin repetir la precarga.
  Depende de la consulta registrada en DI, Bootstrap y recursos SharedTexts/OperationsTexts
  ES/EN con singular/plural y nombres accesibles. Se actualiza al cargar, sin sondeo.

- **Caso de uso:** `/Operations/Staging` lista cada llegada (entrada o traslado),
  con su cantidad pendiente independiente; `/Operations/Staging/Putaway?arrival=...`
  permite elegir únicamente el destino y confirmar con NIP.
- **Archivos:** `Pages/Operations/Staging/*`, `OperationPageModel.cs`,
  `_GuidedMovementForm.cshtml`, `wwwroot/js/operations.js` y
  `Infrastructure/Inventory/StagingArrivalQuery.cs`.
- **Dependencias:** buscador de `Operations/Lookup`, HID, cámara/ZXing, revisión
  y confirmación de ubicación compartida del formulario guiado. `FixedTransfer`
  fija producto, origen y cantidad, oculta cambios/cámara en esos pasos y conserva
  solo la captura del destino. No es un controlador nuevo ni una copia del script.
- **Integración real:** `Staging/PutawayModel` usa `ConfirmStagingAsync` con llegada,
  versión, destino y NIP; el servidor reconstruye producto/origen y valida las placas
  bajo bloqueo transaccional. Nunca usar una transferencia automática por SKU para
  sustituir este contrato. La versión enviada no autoriza por sí sola un movimiento.
- Los históricos ambiguos solicitan cantidad física en la lista y reutilizan
  `IdentifyStagingEntryAsync` con `ArrivalLineId`; identificar no mueve existencias.
  Fechas mediante `WarehouseClock`, textos ES/EN y paginación después de excluir agotados.

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

### Impresión por entrada en STAGING

- **Caso de uso:** identificar e imprimir por separado entradas de un producto en
  el área cuyo código es `STAGING`, incluidas las anteriores a esta función.
- **Archivos:** `Pages/Operations/PalletLabels/Index.cshtml`, su PageModel y
  `_StagingEntries.cshtml`; `Infrastructure/Inventory/StagingEntryPlates.cs` y
  `PalletPlateEngine.cs`.
- **Dependencias:** buscador bidireccional existente de `pallet-labels.js`, servicios
  de seguimiento y etiquetas, plantilla publicada `PLT-LICENSE-PLATE`, hora del
  almacén y recursos `OperationsTexts` ES/EN. El parcial depende del PageModel de
  esta pantalla; no es un formulario genérico.
- **Integración real:** `/Operations/PalletLabels?location=STAGING` muestra entradas
  paginadas de 25 en 25; `productId` conserva el filtro del buscador existente.
  Desde los movimientos recientes, `?handler=StagingEntry&movementId=...#label-preview`
  previsualiza exclusivamente las placas vigentes de esa entrada mediante GET sin
  escrituras. Si requiere identificación histórica, muestra únicamente esa entrada
  para imprimir; no abre el listado completo del producto ni pide capturar cantidad.
  `PrintStagingSelection` previsualiza una o varias placas vigentes mediante POST
  con antiforgery. `IdentifyStagingEntry` obtiene en el servidor la cantidad recibida de la entrada
  y registra la separación sin modificar movimientos ni saldos. Cada placa
  conserva folio, origen y composición por lote.
- **Límites:** las entradas deben estar vigentes y ser de una sola línea. Las
  históricas sin identidad individual usan la cantidad recibida registrada y requieren
  saldo disponible suficiente; no se deduce un remanente por entrada a partir del saldo acumulado. La recuperación
  respeta lotes documentados cuando existen; para registros anteriores al detalle
  por lote asigna lotes disponibles actuales y los registra en el evento nuevo.
  No toma material de otras placas individuales ni de reservas/preparaciones.
  Las placas agotadas o trasladadas no vuelven a identificarse automáticamente.
  Las demás áreas conservan su flujo de impresión.
