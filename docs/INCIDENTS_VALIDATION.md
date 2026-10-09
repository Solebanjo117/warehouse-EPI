# Validación de incidencias de material — 2026-10-09

Implementación y contratos: [UI_COMPONENTS.md](UI_COMPONENTS.md#incidencias-de-material).
Migración: `20261009125100_AddMaterialIncidents`. No se aplicó a producción ni se publicó la aplicación.

| Verificación | Resultado | Entorno |
| --- | --- | --- |
| Regresión de staging, navegación, inventario y placas | 118 pruebas aprobadas | Bases en memoria y PostgreSQL temporal |
| Servicios y rutas de incidencias, tras los ajustes finales | 14 pruebas aprobadas | EF en memoria y Razor mediante TestServer |
| Migración, concurrencia, fallo atómico y respaldo/restauración de fotografías | 1 prueba aprobada | PostgreSQL 18 local aislado, bases nuevas de prueba |
| Captura y accesos desde staging, placa, inventario y ficha de ubicación | 12 combinaciones aprobadas | Edge aislado, ES/EN, claro/oscuro, 768/1024/1440 px |

La prueba PostgreSQL aplicó las migraciones a una base vacía, comprobó reintentos
simultáneos y versión esperada, forzó un rechazo de adjunto en la base para verificar
el rollback completo y restauró un respaldo con los bytes de la fotografía intactos.
Las bases de prueba se eliminaron; la instancia temporal se detuvo y se retiraron
sus archivos. No se cambiaron permisos de la base del almacén.

Las pruebas de negocio cubren contexto exacto, SKU repetidos, cantidades y unidades,
saldo cero/negativo, catálogos inactivos, placas agotadas, división y transferencia,
recepción ambigua, movimientos corregidos, permisos por NIP, transiciones,
reapertura, inmutabilidad, idempotencia, fotografías inválidas/excesivas, filtros y
paginación. Las incidencias de placas agotadas siguen consultables en su historial,
pero no se atribuyen como material actual a una ubicación. Los conteos de incidencias
detectadas y relacionadas son disjuntos.

Los fixtures de navegador proceden del HTML Razor real de las pruebas de rutas.
Se verificaron foco de revisión/NIP, teclado, recuperación de errores, sugerencias,
vista previa de archivo, selector de cámara simulado, CSP y ausencia de desbordamiento
horizontal. Esto no equivale a operar una cámara ni una tablet físicas.

Evidencia local generada en `artifacts/tests/material-incidents/` (TRX) y
`artifacts/incidents-ui/` (fixtures y capturas). Los resultados finales están en
`material-incidents-final.trx`, `material-incidents-ui-final.trx` y
`material-incidents-latest.trx`; las ejecuciones anteriores también conservan los
fallos intermedios ya corregidos.

Pendiente por separado: revisión física en tablet Android, cámara real y lector HID.

## Registro desde el listado central

Validado el selector inicial con producto y ubicación vacíos, elección explícita,
búsqueda por palabras de descripción, limpieza de ubicación al cambiar producto y
regreso al listado conservando filtros. Las rutas verifican contexto histórico
inactivo/bloqueado con saldo negativo, rechazo de combinaciones ajenas y WIP,
protección del regreso local, recuperación tras NIP incorrecto y registro sin
vincular placa ni llegada ni modificar existencias.

Pasaron 19 pruebas de rutas y reglas de incidencias; resultado en
`artifacts/tests/incident-filters/incident-central-create.trx`. Pasaron las 12
combinaciones de navegador ES/EN, claro/oscuro y 768/1024/1440 px, con teclado,
selección, regreso y regresión del formulario existente. Evidencia del selector:
`artifacts/incidents-ui/initial-*.png`. Se usó HTML Razor de las pruebas con
respuestas de búsqueda simuladas; no se registraron incidencias en la base real.
La prueba física de tablet, cámara y lector continúa pendiente.
