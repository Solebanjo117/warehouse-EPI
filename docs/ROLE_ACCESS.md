# Acceso público y roles de usuario

Implementación del 2 de octubre de 2026. Sustituye el esquema de sesión ADMIN como único acceso y cualquier propuesta de exigir sesión en todas las pantallas.

## Matriz

| Sección | Consulta | Confirmación o administración |
| --- | --- | --- |
| Inicio | Pública | — |
| Operaciones, recepción, surtimiento y WIP | Pública; preparación libre | NIP OPERATOR o ADMIN |
| Importación de salidas WIP `/Admin/Inventory/WipImport` | Sesión ADMIN; vista previa sin modificar stock | NIP ADMIN para confirmar el lote |
| Conteos cíclicos | Pública | NIP OPERATOR/ADMIN y sesión de conteo vigente; se conservan las validaciones especiales |
| Existencias y Ubicaciones (croquis, exhibición, impresión) | Pública | Administración ADMIN |
| Tabla y balance, historiales, reportes, impresión y exportaciones integrados | Pública; preparación libre | NIP PRODUCTION o ADMIN; reducciones y reversiones con motivo y NIP ADMIN |
| Generar etiquetas | Pública, formatos publicados | Diseño de formatos ADMIN |
| Placas de pallet | Identificación, generación e impresión públicas | Activación OPERATOR/ADMIN; anulación ADMIN; demás restricciones existentes |
| Notificaciones | Público, OPERATOR y PRODUCTION: negativos y bajo mínimo | ADMIN conserva el centro de excepciones completo |
| Productos | Sesión PRODUCTION o ADMIN | Crear, editar, importar y configurar ADMIN |
| Movimientos, detalles y exportaciones | Sesión OPERATOR o ADMIN | Correcciones ADMIN |
| Programa semanal `/Production/Schedule` | Sesión PRODUCTION o ADMIN, solo lectura | `/Admin/Production/Schedule`: ADMIN |
| Reportes generales, demás catálogos, formatos, usuarios, configuración, respaldos y ejecución antigua de órdenes | ADMIN | ADMIN |

Se conservan las excepciones técnicas de acceso, errores, idioma, archivos estáticos, logo, salud local y progreso de restauración. Las páginas Razor no clasificadas son ADMIN por defecto. Las políticas de lectura no heredan una restricción de carpeta ADMIN.

## NIP y responsable

El NIP se solicita al confirmar. La sesión abierta no reemplaza ni eleva los permisos de la persona identificada por ese NIP. El responsable guardado corresponde al NIP. Un NIP incorrecto o un usuario inactivo recibe el mensaje genérico de NIP inválido.

Un NIP válido con rol incompatible devuelve `RoleNotAllowed` y los siguientes mensajes:

- Almacén: “Este NIP pertenece a Producción. Para registrar operaciones de almacén usa un NIP de Operador o Administrador.”
- Producción: “Este NIP pertenece a Operador. Para registrar producción usa un NIP de Producción o Administrador.”

El rechazo conserva cantidades y selecciones, limpia el NIP y no escribe. La autorización se comprueba antes de devolver una confirmación idempotente. Las consultas de recuperación de comprobantes son de solo lectura y pueden seguir siendo públicas; no autorizan una nueva confirmación.

Las llamadas internas de conciliación diaria tienen rutas específicas para PRODUCTION/ADMIN. El consumo ligado a una captura diaria no habilita a Producción para ejecutar operaciones públicas de almacén.

La edición pública del balance rechaza cantidades programadas, incluso en solicitudes manipuladas. La programación se modifica en Programa semanal con sesión ADMIN.

## Sesiones y notificaciones

El formulario se llama “Ingresar a Warehouse EPI”. Admite los tres roles fijos y mantiene una sesión de 30 minutos con revalidación de usuario activo y rol en cada solicitud. Cambiar el rol invalida la sesión anterior. Cerrar sesión mantiene todos los accesos públicos.

Notificaciones usa caché separada por audiencia y responde sin caché HTTP. La consulta pública admite únicamente `NegativeInventory` y `BelowMinimum`; categorías manipuladas se rechazan. Sus filas enlazan a Existencias.

La tarjeta de Notificaciones, las campanas de escritorio y móvil y el panel de alertas se muestran únicamente con sesión iniciada, para cualquiera de los tres roles. Sin sesión se ocultan de la navegación; la consulta directa conserva su acceso público.

## Activación y revisión

La migración `20261002190000_AddProductionRole` inserta únicamente el rol 3, `PRODUCTION`, nombre visible “Producción”. No cambia usuarios ni aplica migraciones al servicio instalado.

**Antes de activar:** asignar Producción a las personas que registrarán producción. Sus usuarios actuales de Operador dejarán de poder confirmar esas capturas. La lista existente de roles en Usuarios toma el nuevo rol de la base.

La publicación, la aplicación de migraciones en la instalación y la modificación del servicio quedan fuera de esta entrega.

En la prueba local `localhost:5142`, la migración del nuevo rol ya está aplicada a `warehouse_epi_dev_copy_20260923_141051`. Crear y Editar usuario cargan los tres roles desde esa base; basta recargar el formulario. Esto no aplica la migración a la base del servicio instalado.

## Validación

Los resultados de compilación y pruebas se guardan en `artifacts/role-access/`; los binlogs automáticos están en `.binlogs/`. Las pruebas HTTP usan TestServer y una base InMemory aislada, sin iniciar el servicio instalado.

Pendiente de validación física: tablet real en ambas orientaciones, lector HID y Enter, cámara, teclado en pantalla e impresora de etiquetas/placas (medidas, DPI y márgenes). Los tamaños de navegador emulados no sustituyen estas pruebas.

Resultados y límites detallados: [ROLE_ACCESS_TESTS.md](ROLE_ACCESS_TESTS.md).
