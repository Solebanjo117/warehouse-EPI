# Importación de salidas a WIP

Desde **WIP → Importar salidas a WIP**, una sesión ADMIN puede cargar el
reporte XLSX. La confirmación requiere además un NIP ADMIN válido. Analizar,
corregir y consultar la vista previa no modifica existencias.

## Lectura y corrección

- Se lee exclusivamente `TRANSFER LOG`: `Transfer Date`, `Part Number`,
  `UOM`, `Qty Delivered`, `Delivery WIP Location` y `Status`. Las hojas de
  resumen y sus totales no se importan.
- UOM admite el resultado guardado de la fórmula del reporte. No se evalúan
  fórmulas ni se consultan libros vinculados. La unidad debe coincidir con la
  unidad base activa del catálogo; no se convierte una cantidad entre unidades.
- Se admiten entregas positivas, con hasta cuatro decimales, sin fecha futura.
  Los estados admitidos son vacío, Issued, Received y Partially Received.
  Una fila incompleta permanece visible y bloquea la confirmación hasta
  corregir el archivo o excluirla expresamente con un motivo.
- El origen se prellena si hay un único rack operativo con stock neto positivo
  del producto, fuera de WIP. Una asignación sin stock no basta. Se excluyen
  saldos cero/negativos, otros productos y racks bloqueados, inactivos o retirados.
  Al corregir una fila, el selector muestra solo estos orígenes con su saldo.
  Guardar una corrección individual o masiva valida la elegibilidad por cada
  producto; rechaza el lote completo de correcciones si el origen no corresponde.
  La revisión previa a confirmar vuelve a consultar las existencias actuales.
- Si hay varios orígenes, aparecen botones con código y saldo. Elegir uno
  completa las filas de ese producto que todavía no tienen origen. No
  reemplaza elecciones anteriores. La corrección masiva explícita sí permite
  reemplazar el origen de todo un producto.
- El destino se reconoce por código o descripción exactos, sin adivinar el
  significado de nombres como `WIP 6 IWM AREA`. Puede redirigirse por fila,
  producto, área original, filas con errores o todas las pendientes.
- El selector de producto filtra los SKU presentes en el archivo; no cambia
  el producto de una entrega. Por eso usa una lista acotada del archivo en vez
  del buscador del catálogo. Productos inexistentes o unidades incorrectas se
  corrigen en catálogo/Excel y se vuelve a analizar.

## Confirmación y trazabilidad

### Selección por producto o entrega

El buscador usa sugerencias de los SKU contenidos en el Excel, incluidos los
que aún no existen en el catálogo. Un SKU exacto (sin distinguir mayúsculas)
selecciona únicamente ese producto; un texto parcial muestra las coincidencias.
Los filtros **Fecha desde (incluida)** y **Fecha hasta (incluida)** usan la fecha
de entrega del Excel. Admiten extremos abiertos y se combinan con SKU y fila.
Las filas sin fecha no coinciden con un periodo; sin filtro siguen visibles.
Un intervalo invertido o una fecha inválida no habilitan la confirmación.
El periodo se conserva en navegación y correcciones; limita también el stock
proyectado y las filas que se confirman.

Cada entrega aparece separada por fecha y número de fila, ordenada por esos dos
datos. Se conservan todas las coincidencias con paginación de 25 registros.

El resumen, las correcciones masivas, los saldos proyectados y la confirmación
se limitan al producto o productos encontrados. **Revisar solo esta entrega**
limita además la selección a una fila. La confirmación incluye todas las
páginas de la selección, aunque el filtro de estado o la página actual solo
muestren parte de ellas. Los registros ajenos a la selección permanecen
pendientes y sus errores no bloquean el lote seleccionado.

Buscar no cambia stock. Confirmar mantiene una operación por fila del Excel;
no agrupa cantidades ni modifica fechas. La selección conserva las identidades
originales y el hash del archivo completo, incluso para filas repetidas, de modo
que después se puede confirmar otro producto o el resto del reporte sin volver
a descontar lo ya importado. La búsqueda y la selección de entrega se conservan
al corregir, paginar, confirmar o recibir un error.

### Vigencia y registro

La vista previa vive una hora en memoria y pertenece al usuario que la creó.
Cada corrección incrementa una revisión; una pestaña antigua no puede confirmar
otra versión. Reiniciar la aplicación requiere volver a cargar el archivo.

La confirmación registra una salida `ProductionIssue` por fila mediante
`InventoryMovementService`. Descuenta el rack una sola vez y crea una entrega
documental cuyo destino informativo es `OperationalAreaId`. No suma stock WIP
ni registra consumo de producción. Respeta reservas, asignación de lotes/pallets y validación
de ubicaciones del motor existente. Compartir un rack con otros productos exige
aceptación expresa. El lote completo es transaccional: un fallo revierte todas
las filas de ese intento.

`OccurredAt` usa la fecha original, a medianoche en la zona del almacén.
`RecordedAt` conserva la fecha real de importación. La asignación de lotes y
pallets usa existencias actuales; el archivo no contiene su historia física.
La referencia identifica el archivo y las notas conservan fila, fecha, área
original, hash SHA256 y las aceptaciones de filas repetidas y ubicación compartida.
La exclusión se conserva en el borrador durante su vigencia; las filas excluidas
no crean movimientos ni un registro persistente de exclusiones.

## Evitar descuentos duplicados

La identidad de cada entrega se deriva de fecha, SKU, unidad, cantidad, área
original y número de ocurrencia entre filas idénticas. No depende del nombre del
archivo, orden de las filas, usuario ni correcciones de origen/destino.
`InventoryMovement.OperationId` y su índice único conservan esta identidad tras
reinicios. Archivos solapados se serializan en PostgreSQL antes de comprobarla.

No existe un folio externo único en este reporte. Las entregas idénticas se
identifican conservadoramente como ya importadas; las repeticiones dentro de un
archivo requieren revisión explícita. Cambiar alguno de los datos de identidad
en Excel puede crear una entrega nueva. No se detectan automáticamente las
salidas capturadas manualmente ni se reaplica una importación anulada mediante
corrección: deben revisarse en el historial. La página explica este límite.

## Archivos y verificación

- Lectura: `Infrastructure/Imports/WipTransferSpreadsheetReader.cs`.
- Validación y confirmación: `Infrastructure/Imports/WipTransferImportService.cs`.
- Selección sin alterar identidades: `Infrastructure/Imports/WipTransferSelection.cs`.
- Borrador: `Web/Imports/WipTransferPreviewStore.cs`.
- Interfaz: `Web/Pages/Admin/Inventory/WipImport.cshtml` y su PageModel.
- Pruebas: `WipTransferImportTests`, `WipTransferImportRouteTests` y
  `WipTransferImportPostgreSqlTests`.

La prueba opcional `WAREHOUSE_EPI_WIP_WORKBOOK` verifica únicamente la lectura
del reporte facilitado el 5 de octubre de 2026; no lo importa a una base real.
La prueba PostgreSQL crea y elimina una base temporal propia con prefijo
`warehouse_epi_wip_import_test_` y necesita permisos para ello.

No se requieren migraciones nuevas ni paquetes adicionales.

Verificación del 5 de octubre de 2026: compilación aislada y 72 pruebas
correctas (0 fallidas, 0 omitidas), incluida la lectura del XLSX original y
PostgreSQL temporal. Cubre rollback, fechas, saldos negativos, reinicio,
archivos solapados, dos confirmaciones simultáneas, origen único/múltiple,
corrección individual/masiva, revisión antigua, propiedad del borrador,
antiforgery, localización, CSP y regresión del motor de movimientos.

Comando ejecutado desde la raíz, con `WAREHOUSE_EPI_TEST_CONNECTION` dirigida
a la instancia PostgreSQL temporal y `WAREHOUSE_EPI_WIP_WORKBOOK` al archivo
local del usuario:

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore --artifacts-path artifacts/wip-import-check -m:1 -p:UseAppHost=false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~WipTransferImport|FullyQualifiedName~InventoryMovementServiceTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~ContentSecurityPolicyContractTests|FullyQualifiedName~WipExitFlowContractTests' --logger 'trx;LogFileName=wip-import-verified.trx'
```

La restauración inicial aislada usó los paquetes disponibles localmente y
`-p:NuGetAudit=false` solo para esa ejecución porque NuGet no era accesible.
No se cambió la configuración de auditoría del repositorio. Formato de espacios,
estilo y `git diff --check` revisados para los archivos de esta tarea. No se
ejecutó la suite completa de `quality.ps1`. Revisión visual en navegador y
prueba física pendientes; no se publicó ni se importó el archivo a stock real.

Verificación adicional del filtro por producto y entrega: 36 pruebas correctas
(0 fallidas, 0 omitidas). Incluye coincidencia exacta/parcial, selección a través
de varias páginas, correcciones limitadas al producto, selección vacía,
confirmación de una fila seguida del resto del producto en PostgreSQL y
conservación de registros ajenos con errores. No modifica stock operativo.

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore --artifacts-path artifacts/wip-import-check -m:1 -p:UseAppHost=false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~WipTransferImport|FullyQualifiedName~LocalizationTests|FullyQualifiedName~ContentSecurityPolicyContractTests' --logger 'trx;LogFileName=wip-selection.trx'
```

Validación del filtro de fechas (5 de octubre de 2026): se añadieron casos de
límites inclusivos, fechas abiertas, filas sin fecha, combinación con SKU/fila,
intervalo invertido y protección de correcciones/confirmación fuera del periodo.
La ejecución focal no llegó a pruebas: la compilación encontró CS0103 por
`IncludeChange` fuera de alcance en `InventoryReversalService.cs:196`, un cambio
ajeno a este filtro. Pendiente repetir pruebas cuando compile ese archivo.

Corrección del selector de origen: opciones por fila con stock neto positivo del
producto fuera de WIP, con cantidad visible y validación de servidor al guardar.
La suite focal incluye renderizado del selector, rechazo de orígenes ajenos,
stock cero/negativo y ubicaciones no disponibles. La fixture PostgreSQL tiene
saldo inicial suficiente para probar lotes sucesivos bajo esta nueva regla;
no se ejecutó PostgreSQL en esta corrección. Validación visual/física pendiente.

## Corte documental

Las identidades de las entregas importadas se conservan al ejecutar el corte;
reimportarlas no vuelve a descontar material. Los saldos históricos requieren
la conversión ADMIN descrita en [WIP documental](WIP_DOCUMENTARY.md). La importación
no ejecuta ese corte automáticamente.

## Consulta de entregas capturadas después de su fecha

La entrega conserva la fecha del Excel (`OccurredAt`) y el momento de captura
(`RecordedAt`). Al volver a analizar una fila ya importada, la tarjeta obtiene
origen y destino del movimiento persistido, incluso si el origen quedó vacío o
inactivo. Muestra la fecha de captura en la zona del almacén y enlaces al detalle
ADMIN del movimiento y al documento WIP, cuando existe. Consultar no escribe ni
repite el descuento. Un registro histórico sin documento conserva el enlace al
movimiento; no se crea una apertura ni se ejecuta el corte desde la consulta.

El reporte WIP muestra todo el historial por defecto, con paginación. Los límites
de fecha vacíos no restringen el intervalo; los límites explícitos filtran la
fecha original de entrega. Pantalla y exportación mantienen ese mismo contrato y
el límite de exportación sigue siendo 10,000 filas.

## Ocultar entregas ya importadas

«Excluir las ya importadas» filtra las tarjetas antes de paginar; conserva las
excluidas manualmente y no cambia las filas que se confirmarán. La confirmación
omite siempre las entregas ya registradas.

La identidad incluye fecha de entrega, producto, unidad, cantidad, área y número
de ocurrencia para filas idénticas. Una entrega de otro día es nueva aunque se
reinicie la hoja. Renombrar o reordenar un archivo no permite duplicar entregas.
No existe una opción para forzar su reimportación como nuevas.
