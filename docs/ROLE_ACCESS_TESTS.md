# Verificación de acceso por rol

Fecha: 2 de octubre de 2026. Sin publicación, sin aplicar la migración al servicio instalado y sin modificar ese servicio.

## Resultados

- Compilación final: **0 errores, 0 advertencias**, configuración aislada `RoleAccess`, `UseAppHost=false`, ejecución secuencial. Binlog automático: `.binlogs/20261002-171949--28864--zByVw6.binlog`.
- Regresión focal final: **218/218 pruebas .NET aprobadas**. Incluye las pruebas nuevas de acceso por sesión y NIP, revalidación del rol, reintentos, reducciones/reversiones, conservación de captura, caché por audiencia y cierre de sesión; además de recepción, movimientos, conteos, placas, surtimiento, recuperación y balances.
- JavaScript: **22/22 pruebas de recuperación aprobadas**; comprobación de sintaxis de los scripts modificados aprobada.
- Navegador: **18/18 comprobaciones de páginas renderizadas**, en anchos de 768, 1024 y 1366 píxeles. Sin desbordamiento horizontal ni errores de scripts; advertencias con cantidades conservadas y NIP vacío, navegación por teclado y consultas sin acciones de edición. También se comprobaron los temas claro, oscuro y del sistema. Son páginas obtenidas de TestServer, no una validación integral contra PostgreSQL.
- `git diff --check`: aprobado. Hay avisos de normalización LF/CRLF, sin errores de espacios.

Ajuste posterior de visibilidad: Notificaciones se oculta sin sesión (tarjeta, campanas y panel). Compilación sin errores ni advertencias y **27/27 pruebas focales aprobadas**, cubriendo navegación anónima, los tres roles, revocación de sesión y cierre de sesión. Evidencia: `artifacts/role-access/results/notifications-visibility.trx`. El script de alertas termina antes de consultar cuando no existe el panel.

La primera restauración encontró NuGet inaccesible al consultar vulnerabilidades. Se compiló con paquetes locales y `NuGetAudit=false` únicamente en esa invocación; no se cambió la configuración de dependencias.

## Límite de la regresión completa

La ejecución amplia durante la implementación registró **1,075 aprobadas y 103 fallidas de 1,178**. De los fallos, 81 corresponden a PostgreSQL/entorno (permisos para crear o ser propietario de bases aisladas, o conexión de prueba no configurada). El resto incluye contratos de interfaz, datos esperados y límites del host de pruebas. No se presenta esa ejecución como aprobada ni se alteraron credenciales o permisos del servidor para hacerla pasar.

Las expectativas afectadas por esta matriz de permisos se actualizaron y forman parte de la regresión focal. Una prueba antigua del flujo de órdenes, `ProductionBlockAPostTests`, pudo avanzar después de incorporar el ingreso ADMIN requerido y todavía falla en su expectativa de ubicación compartida. No se modificó ese flujo ajeno a la captura diaria para satisfacer la expectativa.

La regresión PostgreSQL de atomicidad/concurrencia sigue pendiente. La migración del rol se aplicó posteriormente solo a la copia local, como se detalla a continuación; su aplicación en la base del servicio instalado queda pendiente antes de activar.

## Rol Producción en la prueba local

El selector de Crear y Editar usuario consulta la tabla `roles` de la base. La copia local `warehouse_epi_dev_copy_20260923_141051`, utilizada por `localhost:5142`, todavía tenía solo ADMIN y OPERATOR.

Se generó el SQL idempotente de EF Core para la migración `20261002190000_AddProductionRole` y se aplicó exclusivamente a esa copia. La transacción terminó correctamente; se verificaron los tres registros (ADMIN, OPERATOR y PRODUCTION/Producción) y el registro de la migración en `__EFMigrationsHistory`. El SQL únicamente inserta el rol, ajusta su secuencia y registra la migración; no cambia usuarios. Evidencia: `artifacts/role-access/production-role.sql`.

Los formularios consultan los roles en cada solicitud, por lo que basta recargar Crear o Editar usuario. No se reinició la aplicación ni el servicio instalado.

## Evidencias

- `artifacts/role-access/build.log`
- `artifacts/role-access/results/focused-final.trx`
- `artifacts/role-access/results/regression.trx`
- `artifacts/role-access/regression-failures.md`
- `artifacts/role-access/javascript.log`
- `artifacts/role-access/browser.log`
- `artifacts/role-access/browser/`

Las páginas visuales proceden de TestServer con InMemory. El script `tests/javascript/role-access.browser.cjs` sirve esas páginas y recursos locales mediante interceptación de solicitudes; no arranca la aplicación instalada y no registra NIP reales.

## Pendiente físico

Tablet real horizontal/vertical, teclado en pantalla, lector HID y Enter, cámara, sonido/vibración y rendimiento; impresión de etiquetas y placas con sus medidas, DPI y márgenes. Los viewports emulados no acreditan estas pruebas.
