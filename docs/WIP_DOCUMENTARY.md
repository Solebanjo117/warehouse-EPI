# WIP documental

El destino WIP identifica la entrega a producción; no mantiene inventario.
`ProductionIssue` es una salida desde almacenamiento y conserva WIP en
`OperationalAreaId`. El documento comparte lotes, responsable y fecha con esa
salida. La captura manual, el surtimiento de solicitudes y la importación usan
el mismo motor. FEFO, placas de origen, reservas de almacén y advertencias de
inventario negativo siguen perteneciendo al motor de inventario.

Con 100 LB, entregar 26 deja 74 LB en almacén. Registrar uso no modifica ese
saldo. Devolver 6 LB genera una entrada de 6 y deja 80 LB. La devolución a
proveedor exige referencia y no genera inventario.

## Registro común

`WipDocumentService` controla entregas, aplicaciones y asignaciones a órdenes.
El pendiente es cantidad documentada menos uso, merma y devoluciones efectivos.
Una asignación a orden separa capacidad documental de la captura general.
Ambos flujos bloquean la ubicación WIP dentro de su transacción y comparten
el mismo cálculo; no hay reservas físicas WIP. La identidad de operación y
la huella de datos permiten reintentos sin duplicar aplicaciones.

Un reverso conserva el original y agrega una aplicación inversa. El uso y la
merma no requieren línea de inventario. Las devoluciones a almacén sí mantienen
ese vínculo. Los registros históricos conservan sus movimientos originales.
Las aplicaciones vinculadas a una orden se revierten desde esa orden; las
generales, desde el detalle documental con sesión y NIP ADMIN.

Entradas, ajustes, transferencias ordinarias, asignaciones de almacenamiento
y conteos rechazan WIP en servidor. Reclasificar almacenamiento como WIP exige
saldo cero, sin placas con material, reservas ni asignaciones activas. No
equivale a una salida de material.

## Conversión ADMIN

La migración incremental `20261005172826_WipDocumentaryInventory` crea el
registro documental, permite líneas de producción sin movimiento y añade
protección PostgreSQL contra saldos y placas WIP después del corte. No ejecuta
la conversión de datos operativos. No se modifican migraciones anteriores.

Procedimiento para una ejecución expresamente autorizada:

1. Respaldar y restaurar una copia aislada; aplicar allí las migraciones y
   revisar `/Admin/Inventory/WipCutover` con sesión ADMIN.
2. Conciliar saldos negativos, composición de placas, compromisos de órdenes
   y lotes. La vista previa bloquea discrepancias. Guardar el resultado de
   la revisión y contrastar saldos de almacenamiento antes/después.
3. En la ventana acordada, revisar nuevamente la vista previa, indicar motivo
   e introducir NIP ADMIN. Una revisión obsoleta se rechaza.
4. Confirmar una sola operación. La transacción bloquea las tablas afectadas,
   guarda instantánea de saldos/placas/asignaciones/órdenes, crea documentos,
   retira saldos mediante `WIP_DOCUMENT_CUTOVER`, vacía placas y desactiva
   asignaciones WIP. No modifica los saldos de almacenamiento.
5. Verificar el registro de corte, documentos y saldos. Repetir la misma
   identidad con los mismos datos devuelve éxito sin repetir efectos.

Las cantidades sin un origen inequívoco se registran como **Apertura del corte**,
con fecha del corte y sin atribuir una fecha u origen de almacén inexistente.
Las cantidades convertidas son cantidades documentadas al corte, no nuevas
entregas ni consumo. La instantánea conserva la correspondencia histórica.
Las correcciones históricas nunca recrean stock WIP; una entrega con aplicaciones
o sin correspondencia documental completa requiere conciliación antes de su
corrección. El corte no se revierte como ajuste ordinario y una migración hacia
atrás se bloquea si hay documentos o corte registrado.

### Estado documentado tras el despliegue 0.10.19 — 2026-10-06

El despliegue confirmó las dos migraciones WIP, 63 migraciones totales y el
servicio activo. El informe local `artifacts/releases/production-deployment-0.10.19.json`
registró 14 filas WIP con saldo distinto de cero y `wipCutoverExecuted=false`.
La vista previa ADMIN se abrió posteriormente; esto no confirma la conversión.
El corte permanece **pendiente de verificación** hasta disponer de evidencia
del registro, documentos y lotes convertidos, saldos WIP en cero y conservación
de los saldos de almacenamiento. Guarda esa evidencia separada del informe de
despliegue, sin modificar su resultado histórico.

Consulta el paso 10 de [DEPLOYMENT_PRODUCTION.md](DEPLOYMENT_PRODUCTION.md)
para la secuencia operativa y las comprobaciones posteriores. El NIP ADMIN se
introduce directamente en la aplicación; no se guarda en los informes.

## Pantallas y reportes

Croquis, ficha y hoja de rack identifican **WIP · Sin control de existencias**.
La ocupación de racks mixtos usa posiciones de almacenamiento. El reporte WIP
consulta el registro común y muestra cantidad documentada, uso, merma,
devoluciones y pendiente; este último no confirma existencia física. Los
reversos se excluyen de los totales efectivos. Pantalla y exportación usan
`GetTrackedPageAsync`, filtros y zona horaria del almacén, paginación y límite
de 10 000 filas de exportación. La antigüedad usa la fecha de entrega o apertura.

## Racks asociados a WIP

La migración aditiva `20261006120129_AddRackWipAssociations` crea una asociación
opcional por fila/número de rack a un área WIP. No configura asociaciones ni
mueve material. En la edición ADMIN de M-5 y M-6 se puede seleccionar WIP-2;
el operador continúa surtiendo al área WIP-2.

Las posiciones WIP de ambos racks enlazan al mismo reporte, donde una entrega
de 100 unidades aparece una sola vez. Las posiciones de almacenamiento
conservan sus existencias y asignaciones. El reporte muestra los racks asociados
**actualmente**, sin afirmar en cuál se colocó cada producto. El historial
registrado directamente a una posición conserva su consulta independiente.

Cambiar o retirar la asociación no modifica documentos anteriores, solicitudes,
destinos predeterminados ni procesos. Un área asociada no puede eliminarse ni
reclasificarse hasta desconectar sus racks. Si se bloquea o desactiva, la relación
permanece visible como no disponible. Guardar cambios del rack conserva esa
relación; asociar un destino nuevo exige un área WIP operativa.

La revisión indica la asociación anterior y solicitada, incluida su retirada
al desaparecer la última posición WIP. Guardado, auditoría e idempotencia usan
la transacción administrativa existente y rechazan asociaciones obsoletas.

Aplicar la migración y configurar los racks requiere el despliegue operativo
correspondiente. Esta implementación no ejecuta migraciones en producción.

### Verificación de asociaciones — 2026-10-06

- Compilación aislada y 87 pruebas focales aprobadas; las seis pruebas nuevas
  también se ejecutaron al cierre: `Passed: 6, Failed: 0, Skipped: 0`.
- PostgreSQL 18 en `artifacts/rack-wip-postgres`: migración sin diferencias de
  modelo, persistencia, traducción de consultas y dos ediciones concurrentes.
  Cada ejecución creó y eliminó una base temporal; no utilizó la base operativa.
- Se verificaron total compartido de 100, CSV/Excel, historial separado,
  existencias de almacén intactas, NIP/permisos, auditoría, idempotencia,
  asociación obsoleta, retiro de la última posición WIP y protección del área.
- Fixtures Razor español/inglés y revisión en Edge con Playwright: editor,
  racks, ficha, reporte e impresión; 899/900/1199/1200 px, claro/oscuro y teclado.
  Evidencia: `artifacts/ui/rack-wip/`; no sustituye tablet, HID ni impresión física.
- La revisión ampliada tuvo 91 aprobadas y un fallo previo:
  `RackOperationsContractTests.Operation_get_validates_prefill_without_changing_post_contract`
  espera `Task OnGetAsync`, mientras la firma existente en HEAD es
  `Task<IActionResult> OnGetAsync`. No se modificó ese contrato por esta tarea.
- Se usó `NuGetAudit=false` únicamente en los comandos de verificación por falta
  de acceso al servicio de vulnerabilidades de NuGet; no se cambió la política
  del proyecto. Migración y SQL revisable preparados, sin despliegue operativo.

## Verificación

Las pruebas `WipDocumentTests`, `WipDocumentPostgreSqlTests`, importación,
producción, solicitudes, placas, conteos y contratos web cubren el nuevo modelo.
PostgreSQL usa una base temporal con nombre aleatorio: migración, corte
idempotente, preservación del saldo de almacén y aplicaciones concurrentes.
`WipDocumentRouteTests` genera fixtures HTML en `artifacts/ui/wip-document`
para revisar español e inglés sin acceder a datos operativos. Las capturas de
fixtures o viewports emulados no sustituyen pruebas físicas de tablet, lector,
cámara e impresora. Publicación y corte operativo requieren ejecución explícita.

### Resultado de la verificación local (2026-10-05)

- Compilación aislada `WipDocument`: correcta. No se detuvo ni reemplazó la
  aplicación operativa.
- Suite focal ampliada: **275 pruebas aprobadas**. Incluye inventario,
  producción, solicitudes, importación, lotes/placas, conteos, clasificación,
  alertas y contratos de ubicaciones/racks/exhibición.
- Después del ajuste para releer disponibilidad sin entidades previamente
  cacheadas: **60 pruebas focales aprobadas**, incluida PostgreSQL.
- PostgreSQL 18 en una instancia local aislada: migración sin diferencias
  pendientes de modelo, corte idempotente, stock de almacén idéntico,
  rechazo por trigger de escrituras directas WIP y aplicaciones concurrentes
  que no exceden lo documentado. Las bases temporales se eliminan al finalizar
  cada prueba; el clúster de pruebas se conserva en `artifacts` para diagnóstico.
- Reverso histórico probado: transferencia anterior de 26, almacén en 74,
  corte sin tocar almacén y corrección posterior que deja almacén en 100 y
  WIP en cero, conservando el movimiento confirmado original.
- **48 vistas de fixtures** en Edge: croquis, documento y reporte, español/
  inglés, claro/oscuro y anchos 899/900/1199/1200. Evidencia en
  `artifacts/ui/wip-document`; sin desbordamiento horizontal detectado.
  Son páginas Razor renderizadas por el host de pruebas con recursos reales;
  no validación de la aplicación desplegada ni de dispositivos físicos.
- Sintaxis JavaScript, XML de recursos, claves duplicadas y `git diff --check`:
  correctos (Git advierte normalización de finales de línea).

Una ejecución más amplia encontró fallos fuera del flujo WIP en planificación
semanal, contratos de navegación y reporte ejecutivo, además de pruebas de
integración de producción que quedaron esperando; se interrumpió esa ejecución.
No se declara verde la suite global. Se conservaron los cambios locales ajenos
a esta tarea. No se aplicó migración/corte en la base operativa ni se publicó.
## Resumen del WIP asociado en el croquis

Al abrir un rack con posiciones WIP asociadas como ADMIN, el croquis muestra hasta cinco
entregas documentales vigentes del área, de más reciente a más antigua, sin
restricción de fechas. Cada tarjeta conserva cantidad, unidad, fecha local,
responsable y enlace al documento; las aperturas del corte se identifican.
Varios racks asociados muestran las mismas entregas del WIP completo. Esto no
representa existencias ni distribución por posición. El historial directo de
las posiciones conserva su consulta independiente.

Verificado con tres pruebas de rutas (idiomas, permiso ADMIN, límite, orden,
cancelaciones, destino directo y reasociación). Fixtures revisados en navegador
en cinco anchos, dos idiomas y dos temas, con teclado y sin desbordamiento del
resumen. No se ha validado en tablet física ni publicado el cambio.
