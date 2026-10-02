# Captura conjunta de producción

## Entrada única desde Tabla y balance

La navegación principal de Producción y el acceso desde Programa semanal ahora abren **Tabla y balance**. La página diaria también abre el balance por defecto y conserva el día recibido en los enlaces. Se retiró la pestaña de captura separada; Historial continúa disponible como consulta.

En T1/T2 se escribe el **total** producido del día: cambiar 20 a 25 agrega 5. La ayuda visible explica este comportamiento antes de editar y conserva revisión, NIP y permisos para reducciones. Los contratos y las direcciones explícitas `Tab=capture` siguen disponibles para compatibilidad con capturas, comprobantes y recuperaciones anteriores; no se ofrecen como una segunda entrada en la navegación habitual.

La programación semanal continúa siendo la preparación administrativa del trabajo. Esta simplificación de navegación no cambia los cálculos ni registra cantidades automáticamente.

Verificación de esta entrada única: compilación correcta y **Passed: 54, Failed: 0, Skipped: 0** en `artifacts/balance-entry-tests/single-balance-final.trx`. Incluye entrada por defecto, contexto de fecha, menú único, recursos ES/EN, CSP y una prueba HTTP que registra 20 en las tres áreas, cambia los totales a 25, rechaza un NIP incorrecto y repite cada confirmación sin duplicados. No se publicó ni reinició la aplicación; esta revisión no valida físicamente tablet/HID.

La ejecución ampliada inicial tuvo 49 aprobadas y tres fallidas (`single-balance-entry.trx`). Las tres corresponden a dos pruebas antiguas de captura: `Capture_totals_include_only_active_records_for_the_selected_day_area_and_shift` (dos casos) espera que el segundo producto esté en `Rows[1]`, aunque ahora cada producto ocupa tres partidas; `Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets` espera una lista vacía al seleccionar Ready to Pack, aunque la matriz reúne las tres áreas. Se conservaron y excluyeron de la selección final; no se declara aprobada toda la suite.

Comando final (VSTest):

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c CaptureUi --no-restore -m:1 -nr:false -p:UseAppHost=false -p:BuildInParallel=false --filter '(FullyQualifiedName~ProductionBalanceEntryTests|FullyQualifiedName~ModuleNavigationTests|FullyQualifiedName~ProductionDailyRouteTests|FullyQualifiedName~ProductionDailyGroupRouteTests|FullyQualifiedName~ProductionCaptureRecoveryTests|FullyQualifiedName~ProductionMatrixCaptureTests|FullyQualifiedName~ProductionDailyUxContractTests|FullyQualifiedName~Resource_source_keys_do_not_collide|FullyQualifiedName~English_catalogs_have_all_neutral_keys|FullyQualifiedName~ContentSecurityPolicyContractTests)&FullyQualifiedName!~Capture_totals_include_only_active_records_for_the_selected_day_area_and_shift&FullyQualifiedName!~Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets' --logger 'trx;LogFileName=single-balance-final.trx' --results-directory artifacts/balance-entry-tests '-bl:artifacts/balance-entry-final-{}.binlog' --verbosity minimal
```

Actualización del 25 de septiembre de 2026. Sustituye la organización por una sola área descrita en `PRODUCTION_CAPTURE_UI.md`.

## Comportamiento

La captura muestra semana, siete pestañas de días y turno. Cambiar cualquiera de ellos navega automáticamente por GET; cambiar de semana conserva el día de la semana. Se retiraron el selector de área, la fecha nativa y el paso Cambiar/Aplicar del flujo con JavaScript. Permanece un botón de envío únicamente como alternativa sin JavaScript.

Cada SKU ocupa una fila con Corte, Costura y Ready to Pack. Cada área contiene su cantidad a sumar, registrado del día y turno, total previsto, pendiente, por conciliar y notas. Las tres áreas permanecen visibles; por debajo de 900 px se apilan dentro del producto. Totales separados por área y unidad; un SKU cuenta una vez aunque tenga cantidades en varias áreas.

En escritorio, los encabezados de las áreas permanecen visibles al desplazarse por las filas, para identificar el destino incluso al capturar productos al final de la tabla.

La lista y el buscador reúnen las tres áreas, conservan el agrupamiento de resultados y no duplican SKU. Enter recorre Corte, Costura, Ready to Pack y el siguiente producto. Captura rápida utiliza Corte inicialmente y después la última columna enfocada, indicada junto al buscador; Enter vuelve a buscar. Limpiar/deshacer afecta solo al área correspondiente. Se mantienen 100 productos, hasta 300 cantidades, y el límite HTTP del formulario admite sus campos completos.

La revisión identifica producto, área, fecha y turno. Un NIP confirma todas las partidas. Los pendientes y repartos son referencias previas al registro, explícitamente identificadas como tales; el motor existente concilia al confirmar. La respuesta incluye un comprobante con las áreas de cada movimiento.

## Contratos y persistencia

- `ProductionMatrixCommand` contiene operación, fecha, turno, partidas por producto/área, huella de revisión y NIP. `ProductionMatrixPreview` conserva el área y producto de cada previsualización.
- `PreviewMatrixAsync`, `ConfirmMatrixAsync` y `FindRecordedMatrixAsync` se añaden a `ProductionDailyCaptureService`. La confirmación utiliza una transacción serializable y una sola tanda; aplica Corte, Costura y Ready to Pack en ese orden. La identidad de cada movimiento incluye producto y área. Un fallo devuelve conflicto/error y revierte la transacción.
- Los handlers existentes eligen el contrato nuevo mediante `Group.AllAreas`. `Group.Rows[i].Area` distingue las cantidades. Los contratos anteriores de una sola área siguen disponibles.
- `SearchMatrixProductsAsync` añade consulta agrupada de las tres áreas; `SearchDailyProductsAsync` conserva su firma anterior.
- Sin cambios de entidades ni migraciones. Se conservan permisos, NIP, producción a sumar, extras y conciliación operativa.

## Borradores

El formato v2 usa `sessionStorage` por fecha/turno y conserva cantidades originales y notas por producto/área, modo, área de foco, filtro e identidad de operación. Nunca contiene NIP, antiforgery ni una revisión reutilizable.

Al regresar a un contexto, el borrador v2 se envía automáticamente al handler de recuperación, que valida y reconstruye sus datos desde servidor. Después se anuncia «Captura pendiente recuperada». Si la operación ya existe, devuelve el comprobante; si el contenido difiere, conserva el borrador y bloquea el reenvío. Las semanas bloqueadas conservan el borrador y no permiten capturar. El fallo de almacenamiento permite cancelar una navegación para conservar los campos abiertos.

Los borradores v1 se ofrecen de forma explícita, uno por vez. Antes de convertirlos se consulta su operación anterior. Si no se registraron, se trasladan a su área con una nueva operación conjunta; se conserva la referencia de origen hasta confirmar o descartar. No se mezclan automáticamente varios borradores anteriores. Un comprobante elimina únicamente el borrador correspondiente a su operación.

## Evidencia

- Compilación correcta en la salida aislada `bin/CaptureUi/`.
- Cierre focal de la captura conjunta: 8/8 pruebas aprobadas, incluidas concurrencia, 300 celdas y borradores anteriores. Evidencia: `artifacts/capture-ui-tests/matrix-focused-final.trx`. Estas pruebas se solapan con la regresión ampliada y no deben sumarse como casos distintos.
- Regresión ampliada: 56 pruebas .NET aprobadas. Incluye captura conjunta, corrección de cantidades, notas por área, límite de 100 productos/300 celdas, importación v1, NIP incorrecto, concurrencia, cierre de semana, duplicados, respuesta perdida, rutas, localización y CSP. Archivo: `artifacts/capture-ui-tests/matrix-final.trx`.
- JavaScript: 40/40 aprobadas. Incluye cambio de semana conservando el día, guardado antes de navegar, restauración automática, separación de áreas, edición y errores, borradores corruptos, almacenamiento bloqueado, recuperación explícita v1 y limpieza por operación. Archivo: `artifacts/capture-ui-tests/matrix-javascript-final.log`.
- La prueba nueva PostgreSQL `Failure_in_second_area_rolls_back_first_area_then_retry_commits_once` está implementada con fallo inyectado al guardar Costura, después de Corte. Su ejecución quedó bloqueada antes de iniciar por permiso `42501: permission denied to create database`. No se declara comprobado el rollback real en este entorno. Evidencia: `matrix-regression.trx`.
- La prueba preexistente de resumen/buscador sobre PostgreSQL aislado encontró el mismo bloqueo. Por eso la ejecución ampliada final tiene 56 aprobadas y una fallida por infraestructura. El detector global preexistente de traducciones que confunde el helper DOM `text(...)` con claves de idioma se excluyó, según la evidencia del documento anterior.

## Validación visual y límites

Se revisaron vistas Razor generadas por las pruebas HTTP en una previsualización local de solo lectura con los estilos y scripts reales: 360, 768, 900 y 1280 px, temas claro/oscuro, las tres áreas, SKU largo, errores de formato, Enter entre áreas, totales separados, limpiar/deshacer por área y volver de revisión a edición. Campo de cantidad medido en 48 px a 360 px; no se observó desbordamiento horizontal de la captura. Las pestañas de días permiten desplazamiento horizontal propio. Se comprobó el encabezado de área fijo al capturar el último SKU.

La previsualización no ejecuta confirmaciones contra la base operativa; las confirmaciones y recuperaciones se verificaron mediante las pruebas HTTP. Quedan pendientes la prueba física en tablet Android, teclado virtual, lector HID y evaluación de SKU largos en el dispositivo, además de ejecutar las pruebas relacionales con una cuenta capaz de crear la base de pruebas aislada.

Se preservaron los cambios locales previos. No se publicó ni se reinició el servicio operativo.
