# Revisión y confirmación del balance: optimización

## Cambios

- La disponibilidad admite una selección interna de productos. Aplica el filtro en SQL a programación, capturas, aperturas y catálogo, manteniendo el historial y las asignaciones de los productos seleccionados. Los consumidores públicos conservan la consulta completa.
- La revisión carga una vez configuración, etapas activas, turnos, productos y unidades. El SKU se resuelve desde ese conjunto. Cada área obtiene su disponibilidad una vez para los productos de la revisión; T1/T2 reutilizan esas lecturas. La consulta de pendientes de respaldo también se comparte dentro de la fase.
- El contexto de lectura es local a una fase sin escrituras. Nunca se conserva en el servicio ni pasa a la confirmación de una captura. Cada escritura obtiene una validación nueva; materiales, compromisos y asignaciones siguen validándose.
- Confirmar vuelve a revisar dentro de la transacción, pero omite el cierre semanal. El balance completo, fingerprint y comparación final se mantienen. La confirmación anidada evita una validación preliminar duplicada y conserva la inmediatamente anterior a la escritura.
- Se conservan autenticación, autorización, orden de reversos/conciliaciones, guardados, idempotencia y transacción Serializable. No se introdujeron migraciones, escrituras masivas ni cambios en los contratos HTTP.

## Instrumentación y medición reproducible

`ProductionBalancePerformanceTests` crea exclusivamente bases temporales con prefijo `warehouse_epi_perf_test_`. Comprueba primero el permiso de creación. Usa semanas normales y explícitas, 1/10/25/100 celdas, dos turnos por producto y 70 productos para detectar recorridos innecesarios del catálogo. Cada combinación usa un calentamiento y cinco repeticiones con datos equivalentes.

Registra tiempo total de revisión/confirmación, comandos SQL, duración acumulada reportada por EF y llamadas a SaveChanges. Los textos SQL no incluyen valores de parámetros ni conexiones. La duración SQL del interceptor no equivale a tiempo puro del servidor ni incluye toda la materialización.

La fuente ActivitySource `WarehouseEPI.Production.Balance` permite medir fases: metadatos, validación, disponibilidad, autenticación de captura, conciliación, proyección, cierre y verificación final. Sin suscriptor no se crean actividades. Las duraciones de fases anidadas se solapan y no deben sumarse como tiempo total; la autenticación interna de los motores de órdenes queda incluida en conciliación.

Con una conexión de pruebas autorizada en `WAREHOUSE_EPI_TEST_CONNECTION`:

```powershell
$env:WAREHOUSE_BALANCE_PERFORMANCE_OUTPUT = 'C:/warehouse-EPI/artifacts/balance-performance/after.json'
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore --filter 'FullyQualifiedName~ProductionBalancePerformanceTests' -m:1 -nr:false -p:UseSharedCompilation=false -p:UseAppHost=false -p:OutputPath=bin/Reports/net10.0/ --verbosity minimal
```

Ejecutar el mismo escenario sobre la versión de referencia en un checkout separado y guardar `before.json`. No sobrescribir el código del checkout de trabajo. El script siguiente exige cinco muestras por combinación y compara medianas, consultas y guardados:

```powershell
./scripts/compare-balance-performance.ps1 -Before artifacts/balance-performance/before.json -After artifacts/balance-performance/after.json
```

Directory.Build.rsp genera los binlogs automáticamente. No se añade `-bl`.

## Evidencia y límites de esta entrega

La referencia funcional anterior pasó las 21 pruebas de ProductionDailyFlexibleTests. La medición PostgreSQL se detuvo en la comprobación de permisos: el rol disponible no tiene CREATEDB ni SUPERUSER. No se midieron tiempos SQL antes/después ni se modificaron datos operativos. Las pruebas PostgreSQL de reportes y balance también encontraron `42501: permission denied to create database`.

Las pruebas nuevas comparan consultas completas y filtradas, incluidos 503 registros de captura, un turno histórico inactivo, reversos y cuatro decimales. Comparan la revisión completa con la revisión sin cierre, incluido el fingerprint. Comprueban cuatro validaciones de captura para confirmar dos celdas (dos de revisión y dos antes de escribir), tres contextos de metadatos y ninguna generación de cierre semanal durante la confirmación. La implementación anterior realizaba seis validaciones para esas dos celdas, por inspección del flujo; esto es una reducción de llamadas, no una medición de tiempo.

Resultado focal disponible: **75 pruebas .NET aprobadas**, **16 pruebas JavaScript aprobadas** y suite de navegador aprobada. Se excluyeron explícitamente las pruebas que necesitan crear PostgreSQL después de verificar el bloqueo de permisos. No se declara aprobada la suite completa del repositorio.

Se añadieron escenarios PostgreSQL para la equivalencia de consultas seleccionadas y dos confirmaciones competidoras sobre el mismo saldo. Se conservan los escenarios existentes de rollback con fallos inyectados. Su ejecución, y la comparación de asignaciones/materiales/movimientos en PostgreSQL, quedan pendientes del entorno aislado autorizado.

La regresión de navegador usa fixtures HTTP, conserva recuperación, pendientes inmediatos, revisión y scroll en cuatro anchos y ambos temas. No representa una prueba de latencia en producción ni validación física en tablet. No se publicó ni reinició el servicio.
