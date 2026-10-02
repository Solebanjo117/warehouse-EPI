# Pendientes contra programación

La tabla diaria, el balance, el cierre y las exportaciones muestran trabajo pendiente por SKU y área:

`saldo = apertura del área + programación acumulada aplicable − capturas efectivas acumuladas`.

La corrección también aplica al consultar semanas históricas y cerradas. No modifica capturas, órdenes, versiones, cierres, arrastres ni el archivo Excel de origen. No necesita migración ni reimportación.

## Turnos y aperturas

El pendiente para T2 descuenta T1 del día y la producción de días anteriores. El final descuenta ambos turnos. Los saldos conservan el signo al cambiar de día: un adelanto de 20 reduce un programa posterior de 100 a 80. Una captura revertida no participa en la producción efectiva. Las asignaciones no multiplican la cantidad capturada.

En semanas explícitas se conservan las admisiones guardadas por área. En semanas históricas se conservan sus aperturas físicas anteriores y los renglones de apertura en su fecha y área originales; no se convierten en programación nueva ni se duplican en las demás áreas. Las líneas retiradas del programa no agregan trabajo programado, pero su producción efectiva sigue descontándose. Las rutas aplicables proceden de la orden existente o, sin orden, de la ruta del producto.

## Separación del saldo físico

`GetAsync` es la consulta del reporte. `GetPhysicalAsync` identifica expresamente el cálculo operativo anterior y conserva su uso para la captura. Copiar semana consulta el pendiente de trabajo histórico por área para admitir arrastre, conservando su apertura y sus raíces y descontando lo realizado y los compromisos de otros destinos. Esa admisión nunca autoriza consumir material ni crea disponibilidad física, órdenes nuevas o movimientos de inventario. La columna por conciliar mantiene el cálculo operativo independiente. El flujo se documenta en [Arrastre explícito por semana](PRODUCTION_EXPLICIT_CARRYOVER.md).

`NetPending` conserva el saldo firmado y `Pending` su parte positiva. Tabla, cierre y exportación usan el saldo firmado. Los importes de producción y las unidades conservan sus contratos existentes.

## Excel

El importador lee filas de programación, incluyendo Tipo vacío, y no copia resultados calculados de las columnas de pendiente. El reemplazo de programación continúa conservando capturas y aperturas. No interpreta textos como YA CORTADO como capturas nuevas. Por ello, corregir el programa no garantiza que coincida con fórmulas defectuosas o con capturas distintas del archivo.

## Verificación

Las pruebas `ProductionProgrammingPendingTests` y `ProductionProgrammingPendingRouteTests` verifican 300 − 298 = 2, turnos, reversos, adelantos, semanas cerradas y explícitas, aperturas con fecha, procesos omitidos, valores numéricos de exportación, consulta sin escrituras y Tipo vacío (200 + 400 + 400 = 1,000). La regresión existente comprueba asignaciones, edición y previsualización, reemplazo y disponibilidad física por separado.

Los resultados TRX y las capturas del navegador de fixtures se guardan en `artifacts/programming-pending/`. La ejecución PostgreSQL continúa pendiente del permiso para crear una base temporal aislada. Una prueba InMemory o una vista HTTP de fixture no demuestra PostgreSQL, despliegue ni funcionamiento físico en tablet.

Resultado final: **Passed: 285, Failed: 0, Skipped: 0**, `programming-pending-final.trx`. Cuatro casos ajenos a esta corrección se excluyeron después de reproducir los mismos fallos desactivando temporalmente la proyección de pendientes (restaurada antes de la ejecución final): los dos casos de `Capture_totals_include_only_active_records_for_the_selected_day_area_and_shift`, `Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets` y `Real_form_preserves_bad_decimal_clears_pin_and_activates_explicit_rework`. Evidencia comparativa: `physical-baseline.trx` (0 aprobadas, 4 fallidas). Sus fallos son índices de filas y mensajes esperados por pruebas antiguas de captura/recepción; no se cambiaron esas pruebas para ocultarlos.

Comando final (VSTest, binlog automático en `.binlogs/`):

```powershell
$env:WAREHOUSE_BALANCE_FIXTURES = Join-Path (Get-Location) 'artifacts/programming-pending/fixtures'
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c CaptureUi --no-restore -m:1 -nr:false -p:UseAppHost=false -p:BuildInParallel=false --filter '((FullyQualifiedName~Production&FullyQualifiedName!~PostgreSql)|FullyQualifiedName~Resource_source_keys_do_not_collide|FullyQualifiedName~English_catalogs_have_all_neutral_keys|FullyQualifiedName~ContentSecurityPolicyContractTests)&FullyQualifiedName!~Capture_totals_include_only_active_records_for_the_selected_day_area_and_shift&FullyQualifiedName!~Real_form_preserves_bad_decimal_clears_pin_and_activates_explicit_rework&FullyQualifiedName!~Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets' --logger 'trx;LogFileName=programming-pending-final.trx' --results-directory artifacts/programming-pending --verbosity minimal
```

Playwright sobre fixtures HTTP pasó las ocho combinaciones de tamaño/tema de la suite del balance y seis capturas adicionales del caso cerrado 300 − 298 = 2, sin errores JavaScript. Se inspeccionaron imágenes de escritorio y tablet emulada, incluido modo oscuro. No se inició ni reinició el servicio operativo.
