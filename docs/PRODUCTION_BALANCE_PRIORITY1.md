# Tabla y balance — prioridad 1

Implementación local, septiembre de 2026. No publicada y sin reiniciar el servicio.

## Comportamiento

- Se escriben **totales del día y turno**, no cantidades para sumar. Se mantienen revisión, NIP, reducciones ADMIN y transacción/idempotencia existentes.
- Búsqueda por SKU/descripción/HID, agrupada y con teclado. Agregar un producto consulta sus cifras y crea solo una fila temporal; no crea programación ni movimientos. Un SKU repetido enfoca la misma fila.
- `GetEditableSummaryAsync` incorpora productos activos sin actividad usando la resolución de áreas existente. La revisión consume este mismo origen para admitir productos fuera de programa.
- Contexto fijo con fecha/semana/estado y nombres configurados de T1/T2. Cambiar semana conserva el día de la semana. La selección inicial fuera de la semana actual es lunes.
- Campos de 48 px, total registrado y pendiente de registrar, errores locales, deshacer y contador de 100 cambios. Debajo de 992 px, cada área presenta ambos turnos juntos y sus tres saldos debajo.
- Borrador versionado `epi.balance.v1:<weekId>:<date>` en `sessionStorage`, independiente por contexto. Guarda textos originales, cantidades inválidas, productos temporales, programación/versiones, motivo, foco y operación. No guarda NIP, antiforgery ni autorización de revisión.
- Recuperación automática mediante POST antiforgery, sin escritura de movimientos. Los cambios concurrentes se resuelven por celda; no se suman diferencias automáticamente. Una operación ya registrada se reconoce antes de consultar el estado actual de la semana.
- El bloqueo/corrupción del almacenamiento conserva el trabajo en memoria y advierte antes de abandonar cambios. Los borradores bloqueados por semana/producto/permisos permanecen intactos hasta descartarlos explícitamente.
- Los borradores anteriores de captura permanecen separados y conservan su recuperación anterior. La extracción del cálculo de fingerprint mantiene el nombre JSON `Id` anterior para compatibilidad de comprobantes.

## Interfaces

- GET `BalanceProducts`: búsqueda agrupada sobre las tres áreas.
- GET `BalanceRows`: parcial de una fila calculada por el servidor; solo consulta.
- POST `BalanceEditRestore`: contexto, operación, productos y cambios originales. Responde filas, valores actuales, conflictos, bloqueos o resultado de una operación registrada. Permite recuperar hasta 1000 entradas de borrador como límite defensivo; revisar/confirmar mantiene el límite de **100 cambios**.
- `BalanceEditConfirm` conserva su contrato y añade `operationId` y `recordId` a la respuesta.
- No se añadieron entidades ni migraciones. Los filtros/exportaciones normales siguen representando información registrada.

## Verificación

- Suite focal .NET: 30 pruebas aprobadas. Incluye búsqueda fuera de programa, las tres áreas, confirmación/reintento, recuperación de respuesta perdida, fingerprint anterior, texto inválido, concurrencia, semana cerrada, producto inactivo, antiforgery, permisos, contratos de UI, localización y CSP.
- Suite JavaScript: 23 pruebas aprobadas. Incluye navegación semanal, Enter, valores vacíos/cero, cuatro decimales cerca del límite numérico, invalidación de revisión y reintento de envío incierto.
- Navegador Edge sin ventana: `tests/javascript/production-balance-workspace.browser.cjs`, sobre HTML generado por las pruebas HTTP y respuestas controladas. Comprueba agregar/deduplicar, recarga y cambio de día con recuperación, conflictos por celda, ausencia de secretos, resultado ya registrado, borrador corrupto y almacenamiento bloqueado/cancelación.
- Se generaron y revisaron capturas en 360, 768, 900 y 1280 px, claro/oscuro. Ver `artifacts/balance-priority1/fixtures/`. No es una validación física ni una prueba contra el servicio publicado.
- Evidencia inicial .NET: `artifacts/balance-priority1/balance-final.trx` y binlogs históricos `artifacts/balance-final-*.binlog`. Las ejecuciones actuales usan los binlogs automáticos de `.binlogs/`. `node --check` y `git diff --check` sin errores.

### Reproducción

```powershell
$env:WAREHOUSE_BALANCE_FIXTURES = Join-Path (Get-Location) 'artifacts/balance-priority1/fixtures'
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c CaptureUi --no-restore -m:1 -nr:false -p:UseAppHost=false -p:BuildInParallel=false --filter 'FullyQualifiedName~ProductionBalanceWorkspaceTests|FullyQualifiedName~ProductionBalanceEntryTests|FullyQualifiedName~ProductionDailyFlexibleTests|FullyQualifiedName~ProductionDailyUxContractTests|FullyQualifiedName~ContentSecurityPolicyContractTests|FullyQualifiedName~Resource_source_keys_do_not_collide|FullyQualifiedName~English_catalogs_have_all_neutral_keys' --logger 'trx;LogFileName=balance-final.trx' --results-directory artifacts/balance-priority1 --verbosity minimal
node --test --test-isolation=none tests/javascript/production-balance-editor.test.cjs tests/javascript/production-daily.test.cjs
# Con playwright disponible en NODE_PATH y Edge instalado:
node tests/javascript/production-balance-workspace.browser.cjs
```

## Pendientes externos

- Rollback real en PostgreSQL aislado: las pruebas existentes `ProductionBalanceEditPostgreSqlTests` incluyen fallos inyectados dentro de la transacción. El rol de esta máquina no puede crear la base aislada (`rolcreatedb OR rolsuper = false`, comprobado con consulta de solo lectura). No se ejecutaron contra la base operativa ni se modificaron permisos.
- Prueba física con tablet Android, teclado virtual y lector HID.
- Publicación, reinicio y comprobación posterior en el entorno desplegado.

Se verificó el alcance afectado; no se declara pasada la suite completa del repositorio ni el script global de calidad, que incluye pruebas PostgreSQL y otras áreas con cambios locales ajenos.

## Pendientes inmediatos al editar producción

- T1 actualiza al escribir el pendiente para T2 y el pendiente final; T2 actualiza solo el pendiente final. La proyección aplica diferencias a los saldos calculados por el servidor y conserva cuatro decimales mediante cantidades enteras escaladas, incluidos saldos negativos y otros turnos ya incorporados al saldo original.
- Los cambios de programación, entradas inválidas y conflictos con valores originales actualizados esperan la validación del servidor para evitar proyectar recorridos distintos con una regla de cliente. Revisar y confirmar siguen usando los cálculos, permisos y fingerprint del servidor.
- La validación automática conserva la pausa de 350 ms; una nueva edición cancela la consulta anterior. El control de generación también descarta respuestas tardías. Mientras tanto, el cierre semanal conserva su estructura, sus paneles abiertos y una advertencia de actualización pendiente.
- Verificación focal: 16 pruebas JavaScript y 23 pruebas .NET aprobadas. Edge con fixtures HTTP comprobó actualización inmediata combinando T1/T2, deshacer, estabilidad del cierre semanal, recuperación de borradores y scroll en cuatro anchos con ambos temas. No se midió la latencia de la aplicación operativa ni se realizó validación física en tablet.

## Tanda 4 — Presentación compacta y revisión

El contexto único contiene el rango completo de semana, la fecha y el estado. Mantiene la asociación del selector externo con el formulario GET mediante `form="balance-filters"`; los handlers usan `.form`. Producto filtra resultados; «Buscar o agregar producto» incorpora filas temporales para capturar. Área y Referencia permanecen en «Más filtros». Los siete días, parámetros de paginación y advertencias operativas conservan su comportamiento. Las dos ayudas son desplegables después del buscador para dejarlo visible en móvil.

Desde 992 px se conserva la comparación tabular y el orden SKU, programación, cinco cantidades por área y Status %. Solo SKU se fija horizontalmente, con ancho de 14 rem, código completo ajustable y unidad visible. Las barras sincronizadas siguen usando scroll nativo. Por debajo de 992 px, el mismo DOM forma tarjetas: T1 y T2 juntos; tres saldos en tres columnas desde 576 px y dos columnas con Pendiente final a todo el ancho por debajo. No se duplican cantidades editables. Los campos mantienen 48 px y los controles al menos 44 px. Por ajuste solicitado durante la revisión, se conservan los fondos distintivos de área: Corte azul, Costura ámbar y Ready to Pack verde, mediante tokens Bootstrap adaptados al tema.

Área, Diario/Acumulado, porcentaje y color permanecen visibles con el desglose cerrado. Diario y Acumulado se muestran como texto coloreado sin recuadros, fondo propio ni borde, según el ajuste solicitado. El desglose único por SKU reúne cobertura, base/meta, adelanto aplicado y arrastres por área. No calcula coberturas nuevas: usa los valores del modelo y de la vista previa. Los umbrales son peligro por debajo de 75 %, advertencia desde 75 % hasta menos de 100 % y éxito desde 100 %. Se conservan porcentajes mayores de 100 %, negativos, «Sin meta», «Cubierto por adelanto» y «No aplica». La presentación impide que un valor inferior a 100 % se redondee a completado.

Una línea ADMIN conserva edición directa. Varias líneas muestran el total de consulta y «Editar programación (n)», con secuencias, unidades y referencias independientes. Los desplegables sobreviven al cambio responsive porque conservan el mismo DOM. La recuperación abre el contenedor antes de enfocar la cantidad; reutiliza el campo de foco existente sin agregar claves ni alterar el formato del borrador.

La revisión y el motivo/NIP permanecen debajo de la tabla, dentro del formulario original. Una sola barra inferior contiene contador, estado, revisión y descarte; confirmar aparece únicamente tras revisar. La fila de contexto se adhiere debajo de la topbar antes de 900 px y en `top: 0` desde 900 px. Hasta 900 px, foco en el contexto/editor devuelve el contexto al flujo y foco en el editor devuelve también la barra inferior al flujo. La reducción del viewport por teclado desactiva ambos elementos adheridos; el tamaño de la barra se incorpora al margen de foco. En consulta sin borrador la barra queda oculta; recuperación bloqueada conserva avisos y descarte.

La proyección local y la vista previa siguen identificadas como «sin guardar». Vista previa y descarte actualizan indicadores y desgloses, aunque estén cerrados. El cierre semanal mantiene su diseño y actualización existentes; XLSX conserva datos guardados. Se preservan contratos POST, antiforgery, idempotencia, NIP vacío tras errores, concurrencia por celda, límite de 100 cambios, navegación protegida y reintento incierto. No hay cambios de servicio, API, modelos ni esquema.

Tras confirmar, se recargan los datos guardados y se devuelve el foco al campo de trabajo, abriendo su programación múltiple si hace falta. Si el producto queda fuera del filtro, vuelve al buscador; si la semana queda cerrada, vuelve al SKU de consulta. Se evita restaurar el desplazamiento del panel de NIP, que podía terminar en el pie de página vacío. Solo se conserva un ancla temporal de interfaz en la entrada de historial del navegador (campo, producto y contexto), que se elimina tras usarla. No contiene cantidades ni secretos y no cambia `sessionStorage`, sus claves, el borrador o el comprobante. Una operación ya registrada al recuperar también vuelve al trabajo usando el foco existente del borrador.

La evidencia exclusiva frente al trabajo local inicial está en `artifacts/tablet-tanda4/verification.html`, con `tanda4.diff`, fixtures HTTP renovados y capturas comparables. No se publica ni se reinicia el servicio. Android físico, teclado virtual y lector HID permanecen pendientes.

### Resultados de la tanda 4

Compilación aislada sin errores ni advertencias, con binlog automático en `.binlogs/`. Pasan 109 pruebas .NET focales (69 HTTP/contratos y 40 de dominio/exportación) y 39 JavaScript. La matriz de Balance cubre 486 casos en nueve anchos, ES/EN y Claro/Oscuro/Sistema, con colores de área distintos, porcentajes sin recuadros, contraste operativo de 4,5:1, objetivos de 44 px, contexto y buscador iniciales y ausencia de desbordamiento de página. Ocho flujos de edición comprueban recuperación de planificación plegada, Enter/Escape, errores, invalidación de revisión, NIP vacío y reintento idéntico ante confirmación incierta. Se añaden doce casos de zoom 200 % emulado mediante viewport CSS reducido, con foco visible después del desplazamiento nativo.

La suite existente de Balance vuelve a comprobar búsqueda/deduplicación, proyección local, Deshacer, recuperación por día, conflictos, almacenamiento bloqueado/corrupto, ausencia de secretos y barras nativas. Las regresiones de las tandas anteriores pasan 270 casos tablet y 648 de cabecera/métodos, además de 34 casos de zoom emulado.

Veinte comprobaciones adicionales de confirmación exitosa verifican el retorno al campo, al buscador fuera del filtro y al SKU de una semana que quedó cerrada, en ES/EN y escritorio/tablet/móvil. Comprueban foco visible, sin solapamiento con SKU, cantidades guardadas recargadas, NIP vacío y eliminación del ancla temporal de historial sin residuos de borrador.

Con un único SKU corto, una línea y tres áreas aplicables, la altura pasa de 1289,9 a 955,6 px a 768 px (25,9 %) y de 146,5 a 106,5 px a 1280 px (27,3 %). El ancho de su tabla pasa de 2672,7 a 2176,1 px (18,6 %). La medición aislada evita atribuir al SKU corto el ancho adicional de los códigos largos; estos se incluyen por separado en la matriz y las capturas generales. Los detalles permanecen cerrados en ambos casos.

Tres fallos de `ProductionDailyGroupRouteTests` se reproducen también con los binarios anteriores de la tanda 3 (`results/group-baseline.trx`): dos expectativas de producto por posición y una expectativa de `colspan` del total del cierre semanal. No se cambia Grupo ni cierre para ajustar estas expectativas. Se conserva la evidencia y no se declara aprobada la suite completa. La ejecución focal usa proveedores aislados en memoria; las pruebas PostgreSQL no se ejecutan contra la base operativa.
