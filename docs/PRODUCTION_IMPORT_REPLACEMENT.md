# Reemplazo de programación desde Excel

En la revisión del importador, activar **Reemplazar solo la programación de las semanas del archivo** y pulsar **Aplicar modo y revisar**. El modo predeterminado continúa siendo la carga inicial sin reemplazo.

La revisión muestra las semanas afectadas, líneas actuales, líneas entrantes y capturas conservadas. Confirmar aplica todas las semanas en una transacción. Un cambio en las dependencias desde la revisión exige volver a validar.

El modo de reemplazo:

- Sustituye las líneas ordinarias sin vínculos, conservándolas como canceladas para auditoría. Las líneas vinculadas se actualizan en el mismo registro: conservan su identificador, orden y asignaciones. Se registra una revisión con el antes, después, archivo y responsable.
- Empareja por SKU, primero coincidencias de fecha, referencias y cantidad; después las coincidencias restantes en orden estable de fecha y secuencia. Nunca cambia el producto de una línea vinculada. En semanas abiertas, los renglones nuevos crean su orden y lote con el flujo diario existente y autorización de la sesión ADMIN.
- Conserva capturas activas y reversadas, extras, aperturas y arrastres existentes, semanas y su estado, y el historial anterior.
- Ignora la tabla de ejecución y no recalcula ni importa aperturas del Excel. Las líneas de arrastre del archivo no se incorporan.
- No toca semanas ausentes del archivo. Las nuevas se crean en borrador, sin capturas.
- Admite archivos parciales dentro del formato de hojas del importador (MM-dd, año 2026); no exige las cinco semanas de la carga histórica inicial.
- Permite revisar de nuevo un archivo previamente importado. La confirmación repetida de la misma operación no vuelve a crear líneas.

Una semana publicada o con capturas no se bloquea por ese solo motivo. Una línea vinculada ausente del Excel queda como producción fuera del programa (`IsExtra`), conservando su identificador, cantidad, orden, capturas y asignaciones sin cancelaciones. La revisión enumera estos renglones antes de confirmar. Se registra el cambio con la acción `programming-detached`; una reversión posterior de sus capturas tampoco cancela automáticamente la orden conservada. El balance excluye estas líneas del programa y mantiene la producción registrada; su capacidad original no genera apertura pendiente de corte en semanas posteriores.

Los conflictos pendientes identifican SKU y renglón: retirar una línea comprometida en arrastres posteriores, reducir por debajo de lo producido o comprometido, o ajustar una orden cerrada sin reabrirla. No se revierten capturas ni materiales para permitir el reemplazo. Los cambios en versiones de órdenes y asignaciones también invalidan la revisión previa.

Los arrastres dependientes que eran válidos antes del reemplazo se vuelven a comprobar contra el programa resultante y actualizan únicamente su huella de validación y versión, con auditoría `import-openings-reviewed`; sus cantidades y enlaces no cambian. Esto incluye la validación de dependencias en semanas posteriores, aunque no se sustituya su programa. Si el saldo resultante no cubre un arrastre antes válido, se revierte toda la importación. Un arrastre que ya requería revisión no se aprueba automáticamente.

Después de actualizar la aplicación, abrir el borrador existente y pulsar **Aplicar modo y revisar** o **Volver a validar** para sustituir la revisión que contenía el bloqueo general anterior.

No requiere migraciones. No publica ni modifica la base operativa por instalar el cambio.

## Verificación

Compilación y ejecución final: **Passed: 126, Failed: 0, Skipped: 0**. Evidencia: `artifacts/import-replacement/off-program-replacement-final.trx`.

La regresión de semanas publicadas cubre las semanas 2026-09-21 y 2026-09-28, reutilización de órdenes y asignaciones, creación de nuevas líneas publicadas, conservación de T1, actualización auditable de la validación de arrastres, reducciones incompatibles y concurrencia de órdenes. Incluye el caso sintético del SKU M6-E-50UP-8M-NOHDL-US, renglón 4, con capturas activas y revertidas, semanas con y sin arrastre explícito, conservación de asignaciones, reversión posterior sin cancelar la orden y confirmación HTTP de líneas fuera del programa. También incluye las pruebas existentes de importación, captura flexible, cancelación de renglones, lotes, autorización, HTTP, localización y CSP.

Comando de regresión focal (VSTest; binlog automático de `Directory.Build.rsp` en `.binlogs/`):

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c CaptureUi --no-restore -m:1 -nr:false -p:UseAppHost=false -p:BuildInParallel=false --filter 'FullyQualifiedName~ProductionImportLinkedReplacementTests|FullyQualifiedName~ProductionImportReplacementTests|FullyQualifiedName~ProductionScheduleImportRouteTests|FullyQualifiedName~ProductionOpeningImportTests|FullyQualifiedName~ProductionDailyModuleTests|FullyQualifiedName~ProductionDailyFlexibleTests|FullyQualifiedName~ProductionTraceabilityServiceTests|FullyQualifiedName~ProductionScheduleLineCancellationTests|FullyQualifiedName~ProductionScheduleDraftBatchTests|FullyQualifiedName~Resource_source_keys_do_not_collide|FullyQualifiedName~English_catalogs_have_all_neutral_keys|FullyQualifiedName~ContentSecurityPolicyContractTests' --logger 'trx;LogFileName=off-program-replacement-final.trx' --results-directory artifacts/import-replacement --verbosity minimal
```

La ejecución ampliada anterior incluyó `ProductionImportReplacementPostgreSqlTests`: 72 aprobadas y una fallida antes de empezar el escenario por `42501: permission denied to create database`. Evidencia: `artifacts/import-replacement/import-replacement-final.trx`. Esa prueba usa una base temporal propia, inyecta un fallo después de sustituir las semanas y comprueba rollback, reintento y conservación de capturas. Se añadió además un escenario PostgreSQL para el reemplazo de dos semanas publicadas con órdenes, captura y arrastre; queda sujeto al mismo requisito de infraestructura. Su ejecución relacional queda pendiente de una conexión de prueba autorizada para crear la base aislada; no se modificaron permisos ni datos operativos.

No se realizó validación visual en navegador ni física en tablet. Las pruebas HTTP ejercitan Razor, antiforgery, revisión guardada, confirmación e idempotencia. Los tests InMemory no demuestran traducción SQL ni rollback PostgreSQL.
