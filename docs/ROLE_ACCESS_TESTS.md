# Verificación de acceso por rol

Fecha: 2 de octubre de 2026. Sin publicación, sin aplicar la migración y sin modificar el servicio instalado.

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

No quedó verificada la aplicación real de la migración ni la regresión PostgreSQL de atomicidad/concurrencia. Debe completarse con una conexión de pruebas que tenga los permisos adecuados antes de activar.

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
