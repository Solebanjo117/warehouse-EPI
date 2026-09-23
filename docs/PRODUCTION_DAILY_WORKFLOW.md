# Programa semanal y tandas de producción

Flujo: **Semana → Agregar productos → Abrir semana para capturar → Registrar una tanda**.

## Programa

- Se agrupan las líneas de lunes a sábado. El buscador de SKU conserva selección de catálogo y navegación con Enter; después de guardar se conserva el día.
- Las referencias y notas están en «Más detalles». Procesos y turnos quedan en una sección secundaria, abierta cuando falta configuración.
- «Copiar productos de la semana anterior» toma la última semana existente anterior a la seleccionada. Presenta líneas sin cantidades, sin arrastres, referencias ni notas. Se seleccionan hasta 100 líneas por operación; dos líneas del mismo SKU siguen siendo independientes. Se advierten coincidencias de producto/día en el destino. Las líneas no seleccionadas no se guardan.
- Abrir requiere NIP ADMIN y configuración válida. También se permite abrir sin nuevas líneas si existen pendientes elegibles anteriores.
- Agregar una línea a una semana abierta requiere NIP del ADMIN autenticado. Línea, orden liberada y lote se guardan en una transacción. Las restricciones de edición con actividad se conservan.

## Arrastre programado opcional

Las sugerencias consultan la disponibilidad del motor de captura en órdenes de cualquier semana anterior. Guardar una selección expresa una intención para un día/producto/área: no crea órdenes, lotes, resultados ni movimientos.

Programar 15 de 20 deja **20 disponibles físicamente**, de los cuales 15 están programados y 5 no. Las capturas posteriores no cambian esa intención ni quedan limitadas por ella. El balance por orden y la apertura del importador mantienen sus reglas.

`production_carryover_plans` conserva la cantidad, versión y último responsable/fecha. Las revisiones del programa registran antes/después y operación. Al guardar se comprueban la versión y el disponible revisado; los errores conservan las cantidades escritas. Cero permite retirar la programación sin alterar el pendiente físico.

## Tandas

La fecha determina la semana efectiva, que debe estar abierta. Fecha, área y turno se eligen una vez. Vacío/cero no se incluye. Se admiten hasta 100 productos por tanda; el buscador permite acotar el listado.

Fecha, área y turno actualizan la lista al cambiarlos; «Buscar» aplica el texto de SKU/descripción. Antes de abandonar cantidades sin confirmar se pide decidir si descartarlas; cancelar restablece los selectores al contexto original. La pantalla muestra la semana y su estado y distingue fecha inválida, semana inexistente/borrador/cerrada, búsqueda sin coincidencias y área sin pendientes. Cuando otras áreas sí tienen productos disponibles, ofrece accesos con sus cantidades de productos. Se ocultan la tabla y la acción de revisión cuando no hay filas para capturar.

«Revisar captura» no requiere NIP y muestra productos, cantidades y reparto FIFO desplegable. La huella de revisión incluye órdenes y versiones. Al confirmar se revalida toda la tanda: si cambió, se solicita revisar de nuevo. Se preservan entradas y notas y se limpia el NIP ante errores.

`production_capture_submissions` guarda operación, huella sin NIP, responsable y fecha. `production_capture_submission_items` relaciona las capturas individuales. La transacción serializable envuelve cabecera, capturas, resultados y traspasos; no hay confirmaciones independientes desde JavaScript. Un reintento idéntico devuelve el resultado original; contenido diferente para la misma operación produce conflicto. Los conflictos de serialización, incluidos los envueltos por Npgsql, vuelven a revisión.

Los registros individuales conservan su historial y reverso. Tras confirmar se limpian cantidades y NIP, conservando fecha/área/turno. Captura, balance e historial mantienen la semana seleccionada; Excel continúa usando el balance existente.

## Migración y aceptación

- Migración generada: `20260922153940_DailyProductionWorkflow`; agrega las tres tablas, índices únicos y claves foráneas. No transforma datos históricos.
- La prueba PostgreSQL crea una base temporal con nombre aleatorio, ejecuta las migraciones y la elimina al terminar. Comprueba rollback con fallo inyectado al guardar el segundo producto, concurrencia y reintentos.
- Las pruebas web ejercitan antiforgery, revisión sin NIP, PIN incorrecto, conservación de cantidades/notas, limpieza de secretos y copia editable. Las pruebas JavaScript ejercitan buscador, Enter, invalidación de revisión y doble envío.
- No se aplica la migración a la base operativa, no se despliega ni se reinicia el servicio. La instancia anterior necesita actualizarse y migrarse antes de validar visualmente este flujo; no constituye validación de la interfaz nueva.
- Limitación fuera de esta corrección: una captura nueva sobre ciertas órdenes con reverso previo puede fallar al entregar («La cantidad excede lo disponible para entregar»). El reverso individual se prueba; ese fallo posterior pertenece al motor existente y no se modifica aquí.


## Captura flexible y conciliación (22 de septiembre de 2026)

- Las órdenes nuevas del programa usan Corte → Costura → Ready to Pack desde el área de apertura. No consultan la ruta del catálogo; las órdenes publicadas conservan sus etapas guardadas. ADMIN puede abrir una semana vacía.
- El pendiente es una referencia. La tanda acepta extras y cualquier SKU activo mediante «Agregar producto», aunque no aparezca en el programa. Buscar y agregar conservan cantidades/notas; cambiar de contexto advierte antes de descartarlas. Se confirma con el mismo NIP ADMIN/OPERATOR, sin motivo adicional obligatorio.
- Cada captura flexible guarda el total real, separado de sus asignaciones FIFO. Costura y RTP pueden tener una parte sin asignación: esa parte resta de su área y alimenta la siguiente una sola vez. Los saldos negativos se muestran como «Por conciliar» y sobreviven al cambio de semana.
- Corte por encima de las órdenes disponibles crea una orden liberada y un lote complementarios. Su vínculo interno usa `ProductionScheduleLine.IsExtra`; no se expone como línea editable del programa ni se copia o exporta como cantidad programada. Las capturas conservan el vínculo con estas órdenes en su historial.
- Al confirmar se concilian capturas activas del SKU por área, fecha efectiva, fecha de registro e identificador. El orden de áreas se aplica sobre el enum en memoria: la conversión textual de PostgreSQL no debe ordenar RTP antes de Costura. Los resultados/traspasos solo se materializan cuando existe entrada real en el motor.
- Las asignaciones son inmutables y tienen operación única, resultado, orden, etapa, cantidad, fecha y responsable de conciliación. Una conciliación parcial posterior añade otra asignación; nunca modifica ni vuelve a sumar la captura original. El responsable original permanece en la captura, y el responsable de la conciliación en la asignación.
- Balance y Excel usan las mismas cantidades por orden, más las partes no asignadas. Una conciliación tardía no crea antecedentes en fechas anteriores: Costura 130 y Corte 100 dejan 30 por conciliar; Corte adicional 30 salda la diferencia sin generar otros 30 pendientes de Costura. Los extras se muestran por área, sin sumar las tres áreas como producción final.
- Importaciones históricas se distinguen de capturas flexibles mediante `IsFlexible=false`. No se reclasifican datos históricos ni se alteran los paquetes de apertura del importador. «Arrastre programado» sigue siendo una intención separada del saldo.
- Cabecera de tanda, capturas, órdenes/lotes extras, asignaciones, resultados y traspasos comparten transacción serializable. La revisión incorpora el estado de capturas/asignaciones. Los reintentos idénticos devuelven la tanda original; los cambios exigen revisión.
- Revertir conserva las restricciones del motor cuando existen operaciones posteriores. Las capturas sin asignación tienen reverso idempotente propio. Revertir una captura extra de Corte cancela su vínculo interno y su orden para no dejar un pendiente ficticio. El fallo previo de recaptura en ciertas órdenes reversadas sigue fuera de alcance.
- Migración generada: `20260922171239_FlexibleDailyProduction`. Añade marcadores de extra/captura flexible, auditoría de conciliación e identidad del reverso; no transforma cantidades, rutas ni historial existente.

No se ha migrado la base operativa ni se ha desplegado o reiniciado el servicio. La validación visual de la versión nueva requiere una instancia compatible; el navegador disponible no tenía pestañas abiertas del módulo.


### Evidencia de verificación de la captura flexible

- Regresión de módulo diario, apertura/importación, exportación, rutas web y localización: **Passed: 127, Failed: 0, Skipped: 0**. Registro: `artifacts/flexible-production-final.log`.
- Revisión focal posterior de conciliación, balance y PostgreSQL: **Passed: 13, Failed: 0, Skipped: 0**. Cierre de casos de fechas/adelantos y balance: **Passed: 11, Failed: 0, Skipped: 0**, incluyendo una prueba nueva que diferencia adelanto del calendario y falta de antecedente. Registros: `artifacts/flexible-production-check.log` y `artifacts/flexible-production-edge-check.log`.
- JavaScript: **6/6**; comprobación sintáctica aprobada. Incluye Enter del buscador sin perder cantidades, selección de SKU, invalidación de revisión y protección contra doble envío.
- PostgreSQL temporal: migraciones aplicadas únicamente a bases aisladas; se comprueban conciliación de RTP → Costura → Corte, extras, balance/Excel, fallo inyectado en segunda fila con rollback de órdenes/lotes/resultados, concurrencia y reintentos. Las bases temporales se eliminan al finalizar.
- EF confirma que no existen cambios pendientes entre el modelo y la migración generada. `git diff --check` no detectó errores en los archivos revisados.
- Sin validación visual de esta versión en navegador, tablet o dispositivo físico. No se inició una instancia ni se modificó el servicio operativo.

Comando de regresión ejecutado:

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c Workflow --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName~ProductionDaily|FullyQualifiedName~ProductionOpening|FullyQualifiedName~ProductionScheduleImport|FullyQualifiedName~Localization' --logger 'console;verbosity=normal'
```

Los cierres focales usan el mismo proyecto/configuración con filtros para `ProductionDailyFlexibleTests`, `ProductionDailyWorkflowTests` y `ProductionDailyBalanceTests`; la última ejecución excluye PostgreSQL, ya comprobado en el cierre anterior.


## Selector único y resumen semanal (22 de septiembre de 2026)

- Captura usa «Buscar o agregar producto»: el endpoint propio `DailyProducts` clasifica antes de paginar por grupo (10 coincidencias, más una para detectar continuación). Prioriza productos de líneas vigentes e intenciones de arrastre, después pendientes anteriores del área y finalmente catálogo activo. Sin texto ofrece los primeros dos grupos; catálogo requiere dos caracteres. No cambia el lookup general.
- Seleccionar una fila existente enfoca su cantidad; seleccionar otro SKU hace el POST antiforgery `GroupAdd` con toda la tanda y devuelve una fila vacía. Conserva cantidades/notas, no registra producción ni modifica el programa. Enter, flechas, Escape, toque y «Cargar más» usan el mismo selector; respuestas de búsquedas obsoletas se descartan.
- Balance muestra una fila por SKU y el plan completo de la semana. Los saldos y realizados son el snapshot al corte, nunca la suma de acumulados diarios. El arrastre inicial por área incluye el saldo firmado de órdenes anteriores y aperturas importadas; las intenciones permanecen separadas. Los borradores se identifican como proyección.
- La fecha predeterminada es hoy dentro de la semana, o sábado para semanas anteriores/futuras. «Día del detalle» y «Turno del detalle» filtran solo el desglose; no alteran el resumen. Las capturas activas por día/área/turno se agregan en SQL, sin el límite de 500 filas del historial.
- La hoja adicional `Weekly summary` comparte `GetWeeklyAsync` con la pantalla y recibe el mismo corte y filtros de producto, referencia y área. Las hojas anteriores mantienen sus contratos. Extras, pendientes y diferencias se muestran por proceso; no se suman como productos adicionales.
- Solo contratos y consultas de lectura nuevos; sin migración, despliegue, reinicio ni cambios en capturas, rutas, reglas del importador o conciliación.


### Verificación del selector y resumen

- Primera pasada focal: **Passed: 28, Failed: 0, Skipped: 0**, incluyendo consultas agrupadas y paginación en PostgreSQL aislado, 503 capturas activas, corte efectivo y comparación numérica del XLSX.
- Regresión: **Passed: 110, Failed: 1, Skipped: 0**. El único fallo fue la prueba ampliada del handler `DailyProducts`: validaba campos de captura ajenos al lookup. Corregido para validar exclusivamente los parámetros de consulta y usar `category` sin colisionar con `Group`.
- Cierre del fallo, con build de la corrección: **Passed: 1, Failed: 0, Skipped: 0**. Cubre lookup HTTP real, Balance semanal, cantidades/notas preservadas, incorporación repetida sin duplicar fila, NIP inválido y confirmación. Registros: `artifacts/weekly-selector-regression.log` y `artifacts/weekly-selector-route-final.log`.
- JavaScript: **Passed: 12, Failed: 0, Skipped: 0** (incluye selección, foco, Enter/Escape, paginación por grupo, respuestas obsoletas y errores de red). Los cuatro casos del selector se repitieron tras ajustar el nombre del parámetro de paginación y pasaron.
- Sin validación visual: el navegador disponible no tenía una instancia abierta. No se inició servicio ni se accedió a la base operativa; PostgreSQL se verificó en bases temporales con nombre `warehouse_epi_balance_test_*` creadas y eliminadas por las pruebas.

Comandos ejecutados (VSTest, configuración aislada Workflow):

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c Workflow --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName~ProductionDaily|FullyQualifiedName~ProductionWeeklySummaryTests|FullyQualifiedName~ProductionOpeningImportTests|FullyQualifiedName~ProductionScheduleImportRouteTests|FullyQualifiedName~LocalizationTests' --logger 'console;verbosity=normal'
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c Workflow --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName~ProductionDailyGroupRouteTests' --logger 'console;verbosity=normal'
node --test tests/javascript/production-daily.test.cjs tests/javascript/production-daily-products.test.cjs
```


### Corrección de revisión sin NIP visible (22 de septiembre de 2026)

El navegador envía `Group.Fingerprint` vacío antes de la primera revisión. Al declararlo como `string` no nullable, MVC lo convertía a null y aplicaba Required implícito: el POST no creaba la revisión. El resumen ModelOnly ocultaba el error del campo hidden. Las pruebas anteriores omitían ese campo en lugar de enviar el HTML real, por lo que no reproducían el fallo.

- `Fingerprint` y `Pin` de la entrada de tanda son opcionales en el binding; se normalizan a cadena vacía al construir el comando. Confirmar conserva la autenticación del NIP y la validación de huella del servicio. No cambia el motor ni se registra producción durante la revisión.
- El resumen muestra también errores de campos y recibe el foco cuando hay errores. Una revisión válida enfoca el paso de resumen/NIP.
- La prueba HTTP ahora envía el hidden vacío, preserva cantidades/notas, rechaza NIP vacío/inválido y comprueba que confirmar 2 de 20 deja 18 en Corte y muestra 2 en Costura en la página y en el servicio. Se comprobó primero el fallo y después la corrección.
- Verificación posterior: **Passed: 7, Failed: 0, Skipped: 0** en rutas/UI; **Passed: 14, Failed: 0, Skipped: 0** en JavaScript. Registro de reproducción: `artifacts/daily-pending-repro.log`; cierre: `artifacts/daily-pending-fix.log`.
- Diagnóstico previo: 13 pruebas de motor/balance aprobadas. Consulta del SKU informado en modo PostgreSQL read-only; no se modificaron datos operativos, no se desplegó ni se reinició servicio. Validación visual de la instancia operativa pendiente.

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c Workflow --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName~ProductionDailyGroupRouteTests|FullyQualifiedName~ProductionDailyRouteTests|FullyQualifiedName~ProductionDailyUxContractTests' --logger 'console;verbosity=normal'
node --test tests/javascript/production-daily.test.cjs tests/javascript/production-daily-products.test.cjs
```


## Balance diario por fecha (22 de septiembre de 2026)

Pendientes por turno: cada área muestra inicial, realizado T1, pendiente para T2, realizado T2 y pendiente final. El resumen conserva el total realizado para exportación y consumidores, pero la tabla sustituye esa columna redundante por el saldo intermedio. El motor por orden se consulta con las capturas de T1 para la fecha seleccionada: las entradas de T2 no adelantan disponibilidad al corte intermedio. Se conservan las fechas anteriores, rutas históricas, reversos y diferencias firmadas. Excel añade `Pending for T2`; el pendiente final diario conserva el signo y los extras se presentan por separado.

Verificación del saldo intermedio: **33/33** en balance, captura flexible, resumen/Excel, rutas web y localización, incluidas las pruebas PostgreSQL aisladas. Caso Corte T1=2, T2=3 y Costura T1=8: Costura pendiente para T2=-6 y final=-3. Registro: `artifacts/daily-shift-pending-tests.log`. Sin despliegue ni base real; validación visual pendiente.

Actualización de presentación: la tabla admite hasta 88vh, amplía SKU a un mínimo de 26rem sin partir el código y centra las cantidades. Cada proceso separa T1 y T2 según los turnos configurados, conservando el realizado total del día. La hoja `Balance diario` incluye ambos turnos con las mismas cantidades. En móvil cada proceso conserva su agrupación y el SKU largo permite desplazamiento horizontal. Los turnos se calculan desde los agregados activos de la fecha, sin incluir reversos.

Verificación de esta actualización: **20/20** en resumen, exportación, ruta web y localización, incluido PostgreSQL aislado. Se comprueba lunes T1=251/T2=250, martes sin capturas y sábado T1=1/T2=1; Excel coincide. Registro: `artifacts/daily-shift-columns-tests.log`. Validación visual pendiente; sin despliegue, reinicio ni cambios a la base operativa.

- Balance muestra una sola tabla para `Through` («Avance al día»), actualizada automáticamente al cambiar fecha o semana. Se retiraron «Ver días y turnos», «Día del detalle» y «Turno del detalle». Se conserva el SKU sin descripción, filtros de producto/área/referencia, cabeceras y SKU fijos, y disposición móvil.
- `GetDailySummaryAsync` comparte el motor por orden y la población completa de la semana: también muestra productos programados o producidos en otros días. Programado y realizado son exclusivamente del día; realizado suma ambos turnos activos, sin reversos ni límite del historial.
- Pendiente al iniciar es el saldo firmado del cierre anterior. El lunes usa la apertura semanal, retirando las aperturas de la semana y reintegrando solo las de esa fecha; los demás días añaden únicamente aperturas fechadas ese día. Cada apertura importada se cuenta una vez. `NetPending` conserva el saldo previo a aplicar ceros para la presentación, sin cambiar cantidades ni operaciones.
- Pendiente al cierre se toma del motor e incorpora entradas del proceso anterior; no es simplemente inicial menos realizado. Extras son el incremento de extras acumuladas del día. Las diferencias por conciliar siguen separadas, y el arrastre programado del día es una intención junto al SKU, sin sumar existencias.
- Excel conserva sus hojas y añade `Balance diario` con el mismo resumen, fecha y filtros que la pantalla. Los borradores y fechas futuras se identifican como proyección en pantalla.
- No hay migración, despliegue, reinicio ni modificación de datos operativos.

Verificación del balance diario: primera ejecución **33/33** (balance, resumen, captura flexible, rutas web y localización), seguida de **15/15** tras ampliar la cobertura de programación futura y apertura negativa conciliada entre semanas. Incluye PostgreSQL aislado, ambos turnos, reversos, aperturas fechadas y exportación con más de 500 capturas. JavaScript **15/15**; comprobación de sintaxis correcta. Registros: `artifacts/daily-date-balance-tests.log` y `artifacts/daily-date-balance-final-tests.log`. Las ejecuciones se solapan, no representan 48 pruebas distintas. Validación visual en escritorio/móvil y claro/oscuro pendiente: no había instancia compatible abierta y no se inició el servicio.


## Editor T1/T2 del balance (23 de septiembre de 2026)

- Las celdas muestran el total del SKU, fecha, área y turno; aumentar 2 → 5 agrega 3. Hasta 100 celdas se revisan y confirman juntas con un NIP. Cero es una reducción explícita; vacío, coma, notación científica y cantidades negativas se rechazan. Programación y pendientes siguen siendo de consulta.
- Previsualización sin escrituras, con snapshots desacoplados del motor de balance por orden, FIFO y recorridos guardados. Actualiza saldo inicial, pendiente para T2, pendiente final, extras y por conciliar. La confirmación verifica nuevamente el estado y compara el balance materializado con el revisado antes de hacer commit.
- Aumentos ADMIN/OPERATOR. Reducciones con ADMIN y motivo; seleccionan las capturas más recientes por fecha de registro e ID. Una reducción parcial reversa y sustituye la captura conservando fecha efectiva, área, turno y notas. Los resultados dependientes bloquean la corrección; no se borran otras capturas. La auditoría vincula originales y sustituciones.
- `ProductionBalanceEditCommand`, `PreviewBalanceEditAsync` y `ConfirmBalanceEditAsync` son independientes de la tanda tradicional. Capturas, reversos, extras, conciliación y auditoría se guardan en una transacción serializable. Identificador de operación y huella permiten reintentos idénticos; contenido diferente produce conflicto. El NIP no se almacena.
- Interfaz: Enter, Tab, Escape, deshacer y descartar; filtro/fecha/navegación advierten si hay cambios. Las respuestas atrasadas no reemplazan una edición nueva. Un fallo de red al confirmar conserva la operación para reintentar con nuevo ingreso del NIP, sin duplicar la producción. Excel exporta solo lo guardado.
- Se corrige el fallo de recaptura mencionado en entregas anteriores: el cálculo de entrega y recepción por lote excluye los traspasos reversados. Se mantienen las restricciones por resultados posteriores.
- Migración generada: `20260922192755_DailyBalanceCellEditing`, únicamente las tablas de auditoría `production_balance_edits` y `production_balance_edit_items`, sus índices y relaciones. No se aplicó a la base operativa. Debe incluirse en un futuro despliegue autorizado antes de usar confirmación del editor.
- Sin despliegue ni reinicio. No había instancia compatible abierta para validación visual; revisión de escritorio/móvil y temas pendiente.

Verificación del editor:

- Regresión inicial: 115/116; el único fallo era la comparación por serialización de decimales de PostgreSQL (`150` frente a `150.0000`). Se corrigió comparando los valores numéricos y los registros, conservando la validación antes del commit.
- Cierre posterior: **24/24**, incluidos edición multiárea, reducción parcial y a cero, permisos, reintento, HTTP con antiforgery, migración PostgreSQL, fallo en segunda celda con rollback y dos editores concurrentes. Las ejecuciones se solapan; no sumar los conteos como pruebas distintas.
- JavaScript: **19/19**, incluidas respuestas tardías, Escape/deshacer, vacío frente a cero, PIN y reintento de confirmación con respuesta incierta. Modelo sin diferencias frente a la migración generada.
- Evidencia: `artifacts/balance-editor-regression.log`, `artifacts/balance-editor-final.log`, `artifacts/balance-editor-js.log`, `artifacts/balance-editor-model-check.log`. No había pestaña de una instancia compatible para revisión visual.
