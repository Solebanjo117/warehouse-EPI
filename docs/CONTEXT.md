# Contexto del proyecto Warehouse EPI

Actualizado: 17 de agosto de 2026.

Este documento es la fuente de continuidad del proyecto. Antes de trabajar en
un chat nuevo, se debe leer este archivo y verificar el estado actual del
repositorio. Las decisiones marcadas como pendientes no deben convertirse en
requisitos definitivos sin confirmación.

**Estado general:** las fases 1 a 9 y 10.1 están completadas. La fase 8 de
paquetes y conversiones fue descartada por decisión del negocio; la fase 9 usa
lotes internos automáticos para todo el catálogo. Las fases 10.2 y 10.3 ya
validaron la puerta reproducible de calidad y la protección de `main` exige el
check `Quality`. La validación física
completa de las ubicaciones cargadas sigue siendo una comprobación operativa
pendiente, pero no cambia el cierre técnico de la fase 4.

## 1. Objetivo

Construir un sistema web local para registrar y consultar movimientos de
almacén. Una laptop funcionará como servidor dentro de la red local y varias
tablets Android, limitadas en rendimiento, accederán mediante Google Chrome.

El sistema debe priorizar:

- captura rápida mediante escáner o cámara;
- inventario por producto y ubicación;
- trazabilidad completa de los movimientos;
- interfaz ligera para tablets lentas;
- posibilidad de crecer a lotes internos, operación sin conexión,
  QuickBooks Desktop, croquis del almacén y paneles LED.

## 2. Operación confirmada

### Usuarios y autorización

- Solamente existen los roles `ADMIN` y `OPERATOR`.
- Ambos roles pueden registrar movimientos y ajustes.
- El administrador también puede crear, modificar, activar y desactivar
  usuarios.
- Cada usuario tiene un NIP único; no se usará número de empleado.
- El NIP identifica al responsable y se solicita en cada movimiento.
- El operador no inicia sesión. Las futuras páginas operativas serán públicas
  dentro de la red local y cada cambio de inventario pedirá el NIP al confirmar.
- La cookie administrativa no sustituye el NIP operativo: incluso un
  administrador autenticado debe confirmarlo en cada movimiento.
- Nunca se almacenará el NIP en texto plano. El diseño actual usa un HMAC para
  localizarlo (`PinLookup`) y un hash independiente para validarlo (`PinHash`).

### Productos e inventario

- Un producto puede existir en múltiples ubicaciones.
- Un producto puede tener varias ubicaciones fijas asignadas, sin una ubicación
  principal, y una ubicación puede estar asignada a varios productos. La
  asignación permanece aunque el saldo sea cero; no sustituye al saldo real.
- Al confirmar una entrada, la cantidad queda ligada al producto y a la
  ubicación seleccionada.
- La fuente física de verdad es el saldo de `producto + ubicación + lote`;
  las pantallas operativas muestran siempre su suma por producto y ubicación.
- El total general de un producto se obtiene sumando sus saldos por ubicación;
  no debe mantenerse como otro saldo independiente.
- Las cantidades normalmente son enteras, pero el modelo permite decimales con
  precisión `numeric(18,4)`.
- Cada producto tiene una sola unidad base, por ejemplo `EA`, `ROLL` o `KG`.
  Las cantidades se capturan, almacenan y muestran directamente en esa unidad;
  no se implementarán paquetes, factores de conversión ni equivalencias.
- El inventario negativo está permitido. Cuando no sea posible comprobar la
  existencia o una salida exceda el saldo, se registra el movimiento y el saldo
  puede quedar negativo. El sistema debe advertirlo claramente sin bloquearlo;
  no existe una configuración por producto que impida saldos negativos.
- Todos los productos manejan lotes internos automáticos. El operador no los
  captura, selecciona ni consulta.
- La salida automática consume primero la fecha interna de lote más antigua.

### Movimientos

- Se contemplan `ENTRADA`, `SALIDA`, `TRANSFERENCIA` y `AJUSTE`.
- Normalmente un movimiento contiene un producto en cualquier cantidad. El
  modelo definitivo puede usar encabezado y detalle para no cerrar la puerta a
  movimientos con varias líneas.
- Cada movimiento debe registrar el NIP del responsable y conservar la
  identidad del usuario validado.
- Una transferencia requiere ubicación de origen y ubicación de destino.
- Los administradores y operadores pueden realizar ajustes.
- Los movimientos confirmados no se borrarán ni se sobrescribirán. Una
  corrección debe conservar el movimiento original, crear su reverso y registrar
  el movimiento corregido, junto con responsable, motivo y relaciones.
- El registro del movimiento y la actualización del saldo deben ocurrir en una
  sola transacción de base de datos, con control de concurrencia e idempotencia.

### Ubicaciones

- Existe un solo almacén.
- El layout físico fue proporcionado el 14 de agosto de 2026 mediante las
  imágenes `shared image (20).jpg` y `shared image (19).jpg`, fuera del
  repositorio. Ya existe una carga inicial; todavía se debe validar en sitio que
  los racks y posiciones cargados correspondan con los realmente disponibles.
- Para ubicaciones de rack, el código operativo canónico será
  `Fila-Rack-Pallet`, por ejemplo `A-1-8`. La fila se identifica con una letra;
  el rack es el espacio físico entre columnas; y el pallet identifica la
  posición dentro del rack.
- Un rack normalmente tiene nueve posiciones: tres inferiores, tres medias y
  tres superiores. La numeración usa la distribución de un teclado numérico:
  inferior `1, 2, 3`; media `4, 5, 6`; superior `7, 8, 9`. Por tanto,
  `A-1-8` representa la posición central del nivel superior del rack 1 de la
  fila A. El modelo debe admitir excepciones donde un rack físico no tenga las
  nueve posiciones.
- La identidad se almacenará además en componentes separados de fila, número de
  rack y posición de pallet, para permitir búsqueda, orden numérico y filtros;
  el código compuesto seguirá siendo único.
- Las áreas que no son racks —por ejemplo WIP, Shipping, Carton, FC Rolls o
  KPA— serán ubicaciones especiales con código propio y no se forzarán al
  formato `Fila-Rack-Pallet`. El significado operativo de los colores del
  croquis y el catálogo definitivo de esas áreas siguen pendientes de
  confirmación.
- La etiqueta de referencia mostrada en el layout usa el código grande
  `A-1-8` y dimensiones aproximadas de 6 por 4 pulgadas. La fase 4 deberá
  validar el formato físico final antes de imprimir etiquetas con código de
  barras.
- Más adelante se agregará un croquis interactivo para visualizar estantes,
  existencias y ubicaciones.

### Código de barras y dispositivos

- Se espera usar principalmente Code 128, sin limitar el sistema únicamente a
  ese formato.
- Un producto puede tener varios códigos de barras y uno puede marcarse como
  principal.
- Debe existir captura manual como respaldo.
- Primero se probarán las tablets con una aplicación de escáner.
- La interfaz web debe usar HTML generado en servidor y JavaScript mínimo para
  funcionar bien en tablets Android lentas.
- El acceso a cámara desde el navegador normalmente requerirá HTTPS cuando no
  sea `localhost`; esto debe resolverse en la fase de despliegue.

## 3. Tecnologías elegidas

- .NET SDK 10 y ASP.NET Core 10.
- Razor Pages para la interfaz web.
- Entity Framework Core 10.
- PostgreSQL 18 como base de datos local.
- Proveedor `Npgsql.EntityFrameworkCore.PostgreSQL`.
- HTML, CSS y JavaScript ligero; evitar frameworks pesados en la primera
  versión.
- PWA, Service Worker e IndexedDB para operación sin conexión en una fase
  posterior.
- SVG para el futuro croquis interactivo.
- Integración con QuickBooks Desktop mediante un adaptador separado, al final.

## 4. Repositorio y solución

- Repositorio local:
  `C:\Users\JUANANTONIOCASTILLAO\Documents\warehouse-EPI`
- Rama actual al momento de esta actualización: `main`.
- Solución: `WarehouseEPI.sln`.
- Proyectos:
  - `src/WarehouseEPI.Core`: entidades y reglas del dominio.
  - `src/WarehouseEPI.Infrastructure`: EF Core, PostgreSQL y persistencia.
  - `src/WarehouseEPI.Web`: aplicación ASP.NET Core Razor Pages.
  - `tests/WarehouseEPI.Tests`: pruebas automatizadas.

La aplicación web referencia a Core e Infrastructure. Infrastructure referencia
a Core.

## 5. Implementación existente

Ya existen estas entidades iniciales:

- `Role`
- `User`
- `Unit`
- `ProductType`
- `ProductClass`
- `Product`
- `ProductBarcode`
- `Location`
- `ProductLocationAssignment`
- `ProductLot`
- `InventoryMovement`
- `InventoryMovementLine`
- `InventoryBalanceChange`
- `InventoryBalance`

También existe `WarehouseDbContext`, con:

- nombres de tablas y columnas en `snake_case`;
- claves, relaciones, índices únicos y restricciones iniciales;
- roles sembrados: administrador y operador;
- 18 unidades sembradas con cantidades decimales habilitadas, incluida
  `UNASSIGNED` con el nombre visible `Sin asignar`;
- tipos de producto `FG` y `RAW`, y 26 clases normalizadas sembradas;
- NIP separado en `PinLookup` y `PinHash`;
- NIP numérico de 4 a 8 dígitos, sin bloqueo por intentos fallidos;
- SKU, código de barras y código de ubicación únicos;
- producto identificado por SKU obligatorio y descripción opcional sin límite
  artificial de longitud; no existe un campo separado de nombre de producto;
- Code 128 como formato predeterminado;
- lotes internos automáticos para todo el catálogo, sin caducidad;
- inventario negativo permitido globalmente y señalado mediante advertencia;
- campos de ubicación desglosados opcionales.

El núcleo de inventario implementa movimientos de encabezado y varias líneas,
saldos por producto y ubicación, cambios de saldo auditables, transacciones
atómicas, bloqueo ordenado de filas, idempotencia mediante UUID y `xmin` para
detectar ajustes basados en un saldo desactualizado. Una entrada suma, una
salida resta, una transferencia actualiza origen y destino y un ajuste recibe
el conteo final, conservando saldo anterior y diferencia.

Cada confirmación valida directamente el NIP de un usuario activo `ADMIN` u
`OPERATOR`, sin crear sesión y sin almacenar el NIP en el movimiento. Las
asignaciones producto-ubicación se crean o reactivan dentro de la misma
transacción; si el pallet ya contiene otros productos se devuelve una solicitud
de confirmación específica antes de escribir.

Los movimientos resuelven internamente un lote diario `AUTO-YYYYMMDD` por
producto usando la fecha local `America/Matamoros`. Entrada y aumentos de
ajuste usan el lote diario; salida, transferencia y disminuciones consumen los
lotes por fecha interna más antigua. Ninguna captura pública recibe un lote.

`Program.cs` registra `WarehouseDbContext`, autenticación por cookie para la
administración, autorización `AdminOnly`, `PinProtector` y `UserPinService`.
La cadena de conexión usada es `ConnectionStrings:Warehouse`.

La seguridad por NIP usa:

- HMAC-SHA256 con una clave externa para generar `PinLookup` y localizar el NIP
  sin almacenarlo;
- PBKDF2-SHA256 con sal aleatoria, 210 000 iteraciones y formato versionado para
  generar `PinHash`;
- comparación en tiempo constante;
- mensaje genérico ante NIP inválido, usuario inactivo o falta de permiso;
- cookie administrativa de sesión, `HttpOnly`, `SameSite=Strict`, duración de
  30 minutos y revalidación del usuario activo en cada solicitud;
- protección contra desactivar o degradar al administrador actual o al último
  administrador activo.

Existen páginas Razor para iniciar/cerrar sesión y para listar, crear y editar
usuarios, cambiar su NIP, rol y estado. El primer administrador fue creado desde
una terminal interactiva sin pasar el NIP como argumento. El comando de
recuperación para una base nueva es:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run `
  --project src\WarehouseEPI.Web `
  -- --create-admin
```

Existen pruebas de entidades, catálogos, criptografía, servicio de NIP y pipeline
web. La
última verificación completada el 17 de agosto de 2026 fue:

- .NET SDK `10.0.400` encontrado en
  `C:\Program Files\dotnet\dotnet.exe`;
- compilación correcta, sin advertencias ni errores;
- las 107 pruebas finalizaron correctamente, incluida la autenticación NIP,
  normalización y unicidad de catálogos, reglas de productos y códigos de barras,
  usuario inactivo, antiforgery, cookie, autorización de páginas y el importador
  de productos desde Excel, además de las reglas, generación y administración de
  ubicaciones, la búsqueda de productos por rack asignado y el núcleo de
  inventario y las páginas operativas públicas. Cinco pruebas usan PostgreSQL
  real en `warehouse_epi_test` para comprobar concurrencia, idempotencia
  simultánea, el token `xmin` y el ajuste inicial sobre un saldo inexistente;
- la prueba opcional contra el archivo real se ejecutó mediante la variable de
  proceso `WAREHOUSE_EPI_PRODUCT_WORKBOOK`, sin insertar productos.

En algunas terminales `dotnet` no está disponible en `PATH`; en ese caso se debe
usar `& "C:\Program Files\dotnet\dotnet.exe"` o corregir `PATH` y abrir una
terminal nueva.

## 6. Base de datos y secretos

- PostgreSQL está instalado localmente.
- El usuario configurado es `postgres`.
- La contraseña se guarda mediante .NET User Secrets y nunca debe copiarse a
  este archivo ni subirse a Git.
- El proyecto web ya tiene `UserSecretsId`.
- `Security:PinLookupKey` contiene una clave aleatoria de 32 bytes en User
  Secrets; su valor nunca debe imprimirse ni copiarse al repositorio.
- El nombre exacto confirmado directamente en PostgreSQL es `warehouseEPI`.
- `ConnectionStrings:Warehouse` apunta a `localhost:5432/warehouseEPI` con el
  usuario `postgres`; la conexión fue validada sin mostrar la contraseña.
- El esquema operativo de la aplicación es `public`.
- El esquema prototipo anterior `warehouse` fue respaldado en formato custom en
  `BackupDatabase/warehouse-schema-before-initial-20260813-101618.dump`, se
  comprobó con `pg_restore --list` y después se eliminó. El directorio
  `BackupDatabase` está ignorado por Git mediante la regla `Backup*/`.

## 7. Estado actual de Entity Framework

- Existe `dotnet-tools.json` en la raíz con `dotnet-ef` versión `10.0.10`.
- `dotnet-ef` fue restaurado correctamente durante el desarrollo. Como las
  herramientas locales dependen de la caché de cada entorno, si el comando no
  está disponible en una terminal nueva se debe ejecutar `dotnet tool restore`.
- Existe la migración `20260813150854_InitialSchema` en
  `src/WarehouseEPI.Infrastructure/Persistence/Migrations`.
- El SQL generado fue revisado antes de aplicarse: crea las seis tablas de
  dominio y `__EFMigrationsHistory`, seis restricciones `CHECK`, tres claves
  foráneas y nueve índices explícitos, sin operaciones destructivas.
- `InitialSchema` está aplicada en `public` y registrada con Entity Framework
  Core `10.0.10`.
- La migración `20260813153505_RemovePinLockout` fue revisada y aplicada; retiró
  únicamente `failed_pin_attempts`, `locked_until` y su restricción `CHECK`.
- La migración `20260813171350_CatalogsAndProductReference` fue generada, revisada
  y aplicada después de confirmar nuevamente que `products` estaba vacía. Antes
  de retirar las columnas anteriores se respaldó el esquema `public` en formato
  custom dentro de `BackupDatabase/`; el archivo está ignorado por Git.
- La migración `20260813175106_RemoveProductName` fue respaldada, revisada y
  aplicada después de confirmar que `products` continuaba vacía. Eliminó
  exclusivamente `products.name`; `description` permanece como `text` nullable.
- La migración `20260813185432_AddUnassignedUnit` fue revisada y aplicada. Solo
  agregó la unidad activa `UNASSIGNED`, con nombre `Sin asignar` y cantidades
  decimales habilitadas; no modificó productos ni otras tablas.
- Al cierre de ese bloque, PostgreSQL contenía 18 unidades con decimales
  habilitados, los tipos `FG` y `RAW`, 26 clases normalizadas, 1,612 productos
  importados, 153 ubicaciones y 2 asignaciones producto-ubicación activas. Los
  códigos de barras permanecían vacíos.
- La migración `20260814121053_LocationLayoutStructure` fue respaldada, revisada
  y aplicada después de confirmar que `locations` estaba vacía. Sustituyó los
  componentes provisionales por tipo, fila, rack, pallet y motivo de bloqueo,
  con cuatro restricciones `CHECK` y un índice único parcial para racks.
- El respaldo previo de `public` quedó en
  `BackupDatabase/public-before-location-layout-20260814-071239.dump`; tiene
  formato custom, fue validado con `pg_restore --list` y permanece ignorado por
  Git. La auditoría posterior confirmó cero ubicaciones y 1,612 productos.
- La migración `20260814124317_ProductLocationAssignments` fue generada,
  revisada y aplicada. Crea solamente `product_location_assignments`, con clave
  primaria compuesta, claves foráneas `RESTRICT` e índice por ubicación.
- Antes de aplicarla se respaldó `public` en
  `BackupDatabase/public-before-product-locations-20260814-075917.dump`; el
  archivo custom fue validado con `pg_restore --list` y está ignorado por Git.
  La auditoría final confirmó 1,612 productos, 153 ubicaciones y cero
  asignaciones iniciales.
- La auditoría directa confirmó tipos, nulabilidad, valores predeterminados,
  restricciones, acciones referenciales e índices, incluido el índice parcial
  que permite un solo código principal por producto.
- La migración `20260814142411_InventoryCore` fue generada y su SQL revisado.
  Eliminó la opción por producto `allows_negative_stock` y creó
  `inventory_movements`, `inventory_movement_lines`,
  `inventory_balance_changes`, `inventory_balances` y `product_lots`, con
  claves foráneas `RESTRICT`, precisión `numeric(18,4)`, índices parciales,
  UUID de idempotencia único y concurrencia mediante `xmin`.
- Antes de aplicarla se auditó la base y se respaldó `public` en
  `BackupDatabase/public-before-inventory-core-20260814-093343.dump`. El archivo
  custom fue validado con `pg_restore --list` y permanece ignorado por Git.
- `InventoryCore` está aplicada en `warehouseEPI`. La auditoría posterior
  confirmó 1,612 productos, 153 ubicaciones, 2 asignaciones activas, ausencia de
  `allows_negative_stock` y cero movimientos, líneas, cambios, saldos y lotes.

La base técnica, el esquema inicial, la seguridad por NIP, la administración de
usuarios, los catálogos, las ubicaciones, el núcleo de inventario, el historial
auditable y los lotes internos globales están terminados. PostgreSQL contiene un
administrador activo creado mediante el comando interactivo; no se registraron
su nombre, NIP ni campos protegidos en este documento. Las fases 1 a 9 están
cerradas y el siguiente bloque es la fase 10.1 de estandarización del repositorio
y documentación. La auditoría final de la fase 9 confirmó 216 ubicaciones, 5
asignaciones activas, 9 movimientos, 4 saldos y 3 lotes. Sigue pendiente
comprobar en sitio que las ubicaciones cubran todas las posiciones y excepciones
físicas del almacén.

Si `dotnet` no está en `PATH`, sustituirlo por:

```powershell
& "C:\Program Files\dotnet\dotnet.exe"
```

## 8. Estado de fases y trabajo restante

### Fase 1: base técnica y esquema inicial — completada

- `dotnet-ef`, conexión, migración inicial y verificación física completados el
  13 de agosto de 2026.
- Los cambios de esta base y de las fases posteriores están registrados en Git;
  el repositorio estaba limpio al revisar este contexto el 14 de agosto de 2026.

### Fase 2: seguridad por NIP y usuarios — completada

- Clave HMAC externa, `PinLookup`, PBKDF2, autenticación y administración web
  completados el 13 de agosto de 2026.
- No existe bloqueo por intentos fallidos por decisión confirmada del usuario.
- NIP confirmado como 4 a 8 dígitos y solicitado al confirmar cada operación.
- Pruebas de NIP duplicado, incorrecto, usuario inactivo, cookie y autorización
  completadas.
- Primer administrador creado mediante el comando local interactivo y verificado
  directamente en PostgreSQL, sin leer ni mostrar su NIP o campos protegidos.

### Fase 3: catálogos — completada

- Catálogos ADMIN de unidades, tipos y clases con búsqueda, alta, edición,
  activación y desactivación sin borrado físico.
- Catálogo ADMIN de productos con búsqueda, filtros, paginación, unidad base,
  tipo y clase opcionales, referencia externa y reglas de lotes/caducidad. El
  producto se identifica mediante SKU y una descripción opcional.
- La paginación de productos y de la vista previa usa el parámetro
  `pageNumber`; no usa la clave reservada `page` de Razor Pages.
- Códigos de barras integrados en el producto, con reactivación, desactivación y
  cambio transaccional del código principal.
- Normalización y reserva de SKU y códigos de catálogo, y preservación exacta de
  códigos de barras salvo espacios externos.
- Semillas de 18 unidades, 2 tipos y 26 clases derivadas de los Excel de
  referencia, sin copiar ni importar esos archivos.
- Importador ADMIN en `/Admin/Catalogs/Products/Import`, basado en ClosedXML
  `0.105.1`, para archivos `.xlsx` de hasta 10 MB y 10,000 filas con una hoja
  `ITEMS`. La vista previa se conserva 30 minutos en memoria, está ligada al
  administrador y no escribe en PostgreSQL. La confirmación revalida catálogos y
  SKU y realiza una sola transacción; el token se retira después del éxito.
- El importador agrega únicamente SKU nuevos, omite activos e inactivos ya
  existentes y consolida duplicados compatibles. No importa rutas de producción,
  códigos de barras, ubicaciones, usuarios ni credenciales. No se agregó una
  migración. La primera carga real fue confirmada posteriormente y agregó 1,612
  productos.
- La auditoría del archivo real confirmó 1,613 filas fuente, 1,612 SKU únicos,
  123 referencias vacías y el duplicado compatible `THREAD-TK92-BURGUNDY`.
  Las 65 filas con `U/M` vacío se asignan a `UNASSIGNED / Sin asignar` y se
  muestran como advertencias para su corrección posterior; no se infiere `EA`.
  Esta unidad está reservada: no puede editarse ni desactivarse desde el catálogo.
  La vista previa real termina con 1,612 candidatos y cero errores, sin insertar
  productos antes de confirmar.
- Migraciones, respaldos, auditoría PostgreSQL, compilación y 74 pruebas
  completadas al cierre de la fase el 13 de agosto de 2026. La suite general
  creció a 75 pruebas al completar la fase 4.

### Fase 4: ubicaciones y layout — completada

- Modelo, migración y catálogo ADMIN implementados el 14 de agosto de 2026.
- Se usa `Fila-Rack-Pallet` como nomenclatura canónica de los racks, con la
  distribución de pallet `1` a `9` de teclado numérico y soporte para racks
  incompletos.
- Se registran por separado fila, rack y pallet, conservando el código compuesto
  único y permitir búsqueda y orden físicos.
- Las áreas especiales se crean individualmente, sin inventar
  su semántica a partir de los colores del croquis.
- Existe un generador ADMIN con bloques, vista previa de 30 minutos ligada al
  administrador, exclusión de posiciones y confirmación transaccional de un solo
  uso. También produce una hoja imprimible de validación física.
- El catálogo permite buscar, filtrar, crear áreas, bloquear con motivo,
  desbloquear, activar y desactivar sin borrado físico.
- La vista principal alterna entre un plano por filas/racks con distribución de
  teclado numérico y una tabla administrativa paginada. La búsqueda acepta
  código o descripción de ubicación y también SKU, descripción, referencia o
  código de barras de productos asignados.
- Existe una asignación fija muchos-a-muchos entre productos y ubicaciones, sin
  ubicación principal. Las asignaciones se desactivan y reactivan sin borrado,
  permanecen visibles aunque posteriormente el saldo sea cero y se administran
  desde ambos detalles.
- Cada posición muestra hasta tres SKU y permite navegar al producto; Productos
  muestra hasta tres ubicaciones y permite navegar al rack. No se muestran
  cantidades antes de implementar los saldos en la fase 5.
- La búsqueda de Productos acepta también el código de rack o área asignada y
  devuelve los productos con una asignación activa a ubicaciones coincidentes,
  incluso si la ubicación está bloqueada o inactiva temporalmente.
- El código visible normalizado será el valor leído por el escáner; la pantalla
  operativa se implementará en la fase 6.
- La impresión de etiquetas se pospuso porque la bodega ya está etiquetada.
- PostgreSQL contiene actualmente 153 ubicaciones y 2 asignaciones activas.
  Sigue pendiente confirmar que ese listado cubra físicamente todas las
  posiciones y excepciones del almacén; esta comprobación operativa no deja
  abierta la implementación técnica de la fase.

### Fase 5: núcleo de inventario — completada

- Encabezados con varias líneas para Entrada, Salida, Transferencia y Ajuste.
- Movimientos y cambios de saldo inmutables, con responsable validado por NIP.
- Saldos `numeric(18,4)` por producto y ubicación, y estructura opcional por
  lote preparada para la fase 9.
- Ajustes por conteo final con saldo anterior, diferencia y validación `xmin`.
- Transacción atómica, bloqueo ordenado, UUID idempotente y asignación automática
  o confirmación explícita para pallets compartidos.
- Inventario negativo permitido globalmente y devuelto como advertencia.
- Consultas de saldos, total derivado, negativos y productos bajo mínimo.
- Migración, respaldo, auditoría PostgreSQL, compilación y 89 pruebas completadas
  el 14 de agosto de 2026.

### Fase 6: pantallas operativas — completada

- Menú público y páginas `/Operations/Entry`, `/Operations/Exit`,
  `/Operations/Transfer`, `/Operations/Adjustment` y `/Inventory` terminadas.
- Cada confirmación contiene un producto, conserva un UUID durante los reintentos,
  usa antiforgery y solicita el NIP al final, incluso cuando existe cookie ADMIN.
- Búsqueda operativa por SKU, código de barras, descripción o referencia y por
  código o descripción de ubicación, con selección por escáner HID y Enter.
- El escaneo es bidireccional: un producto muestra ubicaciones con asignación
  activa o saldo distinto de cero y una ubicación muestra sus productos bajo la
  misma regla. Una única relación se autocompleta; varias exigen selección. Una
  pareja nueva se anuncia en pantalla y solo se crea al confirmar con NIP.
- En transferencias, el producto puede autocompletar únicamente el origen. El
  destino muestra su contenido sin reemplazar el producto seleccionado.
- Saldos actuales y estimados, advertencias negativas sin bloqueo, aprobación
  específica de pallet compartido y comprobante recargable sin repetir el POST.
- Ajuste por conteo final con motivo obligatorio. La versión cero representa un
  saldo inexistente y solo se acepta cuando la misma transacción crea la fila;
  una creación concurrente devuelve `BalanceChanged`.
- Consulta pública por producto o ubicación, sin NIP y sin escritura.
- Las consultas de detalle filtran y ordenan las entidades antes de proyectar
  sus resultados, con traducción verificada directamente contra PostgreSQL.
- La consulta pública combina asignaciones activas y saldos distintos de cero
  en ambas direcciones. Una asignación sin saldo se muestra con cantidad cero;
  los registros inactivos se conservan como información y nunca se modifican.
- Interfaz responsive verificada en escritorio y viewport de tablet, con controles
  grandes, foco secuencial y prevención de doble envío.
- Compilación sin advertencias y 105 pruebas aprobadas el 14 de agosto de 2026,
  incluidas cinco pruebas PostgreSQL aisladas en `warehouse_epi_test`.

### Fase 7: historial y correcciones — completada

- `/Admin/Inventory` queda protegido con `AdminOnly` e incluye historial,
  detalle, corrección, exportación CSV/XLSX y alertas derivadas.
- `InventoryMovementCorrection` conserva original, reverso, reemplazo opcional,
  motivo, solicitante autenticado y autorizador por NIP ADMIN; no se guarda el
  NIP.
- El reverso se calcula desde los cambios históricos de saldo y el reemplazo
  usa el motor transaccional existente. Los comprobantes públicos muestran los
  movimientos relacionados sin proporcionar un buscador público.
- La migración `InventoryMovementCorrections` fue revisada en
  `docs/sql/InventoryMovementCorrections.sql`, validada primero en
  `warehouse_epi_test` y aplicada posteriormente a `warehouseEPI`.
- Compilación sin advertencias, ocho pruebas PostgreSQL aisladas y 106 pruebas
  totales aprobadas el 14 de agosto de 2026.
- Las páginas y la funcionalidad de corrección fueron comprobadas manualmente
  por el usuario después de aplicar la migración operativa.

### Fase 8: paquetes y presentaciones — descartada

- No se implementarán presentaciones ni factores de conversión.
- Cada producto opera exclusivamente con su unidad base configurada, como
  `EA`, `ROLL` o `KG`.
- La cantidad capturada en las operaciones es la misma que se registra en los
  movimientos y saldos, sin conversiones ni redondeos adicionales.

### Fase 9: lotes internos automáticos globales — implementada

- Todo producto crea y reutiliza por producto el lote interno diario
  `AUTO-YYYYMMDD`; no existe el campo ni el selector `TracksLots` y no se
  capturan datos del proveedor.
- Entrada y aumentos por ajuste usan el lote diario. Salida, Transferencia y
  disminuciones por ajuste distribuyen automáticamente desde los lotes más
  antiguos.
- La interfaz pública y comprobantes muestran solo saldos agregados. El detalle
  queda en administración, historial y cambios de saldo.
- No existe caducidad ni `TracksExpiration`; la fecha interna nullable es
  `ProductLot.LotDate`.
- La migración `GlobalInternalLots` crea/reutiliza el lote local del día de
  migración para los productos históricos, convierte los saldos sin lote y
  después elimina `products.tracks_lots`. Los movimientos y cambios históricos
  conservan su `LotId` nulo como evidencia; sus reversos se aplican al lote
  inicial de migración, sin volver a crear saldos sin lote.
- `20260817090000_GlobalInternalLots` se aplicó a `warehouseEPI` el 14 de
  agosto de 2026 después del respaldo validado
  `BackupDatabase/public-before-global-internal-lots-20260814-135141.dump`.
  La auditoría final confirmó 1,612 productos, 216 ubicaciones, 5 asignaciones,
  9 movimientos, 4 saldos y 3 lotes; no queda ningún saldo sin lote ni columna
  `tracks_lots`. Compilación y 107 pruebas, incluida la fixture PostgreSQL
  aislada `warehouse_epi_test`, aprobadas.

### Fase 10: estabilización técnica

#### Fase 10.1: repositorio y documentación — completada

- Estandarizar SDK, formato y finales de línea sin renormalizar archivos
  históricos masivamente.
- Crear documentación de entrada, arquitectura y desarrollo.
- Consolidar `CONTEXT.md` como estado vivo, sin datos presentes contradictorios.

#### Fase 10.2: calidad automatizada — completada

- `scripts/quality.ps1` concentra restauración bloqueada, formato, compilación
  Release, validación de migraciones, SQL idempotente, pruebas y cobertura.
- La cobertura mínima global es 85% de líneas y 45% de ramas; la primera
  verificación local obtuvo 107/107 pruebas, 92.0% de líneas y 50.6% de ramas.
  Los artefactos no se versionan.
- Las versiones se centralizan en `Directory.Packages.props` y los lockfiles
  son obligatorios. La auditoría NuGet bloquea vulnerabilidades altas y críticas.
- Los analizadores bloquean advertencias nuevas; `.editorconfig` conserva una
  baseline acotada para migraciones inmutables y ajustes de implementación que
  se abordarán en la fase 10.3.
- `.github/workflows/quality.yml` usa PostgreSQL 18.4 efímero y exclusivamente
  `warehouse_epi_test`; publica TRX, Cobertura y SQL de migraciones.
- El workflow `Quality` está verde y la regla de protección de `main` exige ese
  check antes de integrar cambios.

#### Fase 10.3: refactorización controlada — completada

- `InventoryMovementService` conserva su contrato público y ahora coordina
  reglas, lotes y persistencia mediante colaboradores internos; se retiró el
  flujo operativo sin lotes.
- `InventoryCorrectionService` conserva sus contratos y delega la creación de
  reversos, incluidos los cambios históricos con `LotId` nulo, a un colaborador
  interno transaccional.
- Se agregaron pruebas de distribución FEFO por lote, transferencia entre lotes
  y reverso histórico idempotente. La validación final obtuvo 111/111 pruebas,
  92.9% de líneas y 53.1% de ramas.
- Se retiró `CA1822` de la baseline global; las demás excepciones heredadas se
  mantienen documentadas hasta que se revisen sus cambios funcionales.

#### Fase 10.4: seguridad de producción — implementada; activación LAN parcial

- La aplicación contiene validación fail-fast de producción, Kestrel TLS,
  Data Protection persistente con DPAPI, cookies `__Host-`, CSP, encabezados
  defensivos, páginas dinámicas sin caché, límite de cuerpo y rate limiting por
  IP sin bloqueo de NIP.
- En la laptop de prueba ya se inicializó el directorio de claves de Data
  Protection, se emitió e instaló un certificado LAN y se comprobó el acceso
  HTTPS desde un celular. El respaldo cifrado de la CA permanece fuera del
  repositorio. No registrar aquí contraseñas, huellas ni rutas privadas.
- `scripts/security/` también contiene la provisión y verificación del rol
  mínimo `warehouse_epi_app`. Falta respaldar PostgreSQL, aplicar ese rol en
  `warehouseEPI` y verificar una operación real sin privilegios de migración.
- La validación local más reciente obtuvo 120/120 pruebas, 92.8% de líneas y
  53.5% de ramas; formato, compilación Release, modelo EF y
  SQL idempotente también pasaron.
- Antes de cerrar la fase falta fijar la reserva DHCP definitiva e instalar la
  CA pública en cada tablet, respaldar la base, aplicar el rol restringido y
  comprobar una operación real bajo dicho rol.

#### Fase 10.5: observabilidad local segura — implementada

- Producción escribe únicamente eventos JSON estructurados y permitidos en
  `C:\ProgramData\WarehouseEPI\Logs`; la ruta se valida al iniciar, rota por
  día y 50 MB, conserva 30 días y se prepara con ACL restringida mediante
  `Initialize-ObservabilityLogs.ps1`. Desarrollo conserva consola legible.
- Cada solicitud recibe o normaliza `X-Correlation-ID`; solo se registran
  método, ruta sin query string, estado, duración, correlación y categoría
  segura. Se excluyen NIP, cookies, formularios, secretos y cadenas de conexión.
- `/health/live` es solo loopback y comprueba proceso sin escribir ni migrar.
  La salud y latencia PostgreSQL, uptime, versión, actividad agregada de 24
  horas y fallas sanitizadas se consultan exclusivamente como ADMIN en
  `/Admin/System`.

#### Fase 10.6: respaldo y recuperación local — implementada

- `pg_dump` genera cada día un respaldo custom local que se valida con
  `pg_restore --list` antes de publicarse; los archivos se retienen 30 días en
  `C:\ProgramData\WarehouseEPI\Backups` con ACL restringida.
- Las credenciales se almacenan en un `PGPASSFILE` fuera del repositorio y las
  tareas de Windows se ejecutan como `SYSTEM`, sin contraseña en comandos ni
  registros. La restauración semanal crea, verifica y elimina una base temporal;
  jamás apunta a `warehouseEPI`.
- La copia externa cifrada se difiere deliberadamente hasta decidir USB o SMB;
  el respaldo actual protege contra errores locales, no contra pérdida total de
  la laptop.

#### Fase 10.7: publicación y servicio Windows — activada

- La publicación genera un paquete autocontenido `win-x64` con versión SemVer,
  manifiesto por archivo y SHA-256 externo; exige un worktree limpio y nunca
  compila sobre la instancia activa.
- Las Releases inmutables residen en
  `C:\ProgramData\WarehouseEPI\Releases\<version>`. El servicio usa la cuenta
  virtual `NT SERVICE\WarehouseEPI`, configuración protegida migrada desde User
  Secrets y permisos mínimos sobre certificado, claves y logs.
- Instalación, actualización y rollback ejecutan preflight sin escrituras ni
  migraciones. Una actualización que no arranca o no responde a `/health/live`
  reactiva automáticamente la versión anterior. Se conserva la activa y dos
  versiones previas.
- El 18 de agosto de 2026 quedó activa la Release `0.10.7` con el servicio
  `WarehouseEPI` bajo `NT SERVICE\WarehouseEPI`; se validaron health local y
  acceso HTTPS LAN. La IP reservada verificada es `192.168.5.192`. Las tareas
  de respaldo diario y validación semanal fueron desactivadas deliberadamente;
  los respaldos y credenciales locales permanecen protegidos. El manual de
  operación está en `docs/OPERATIONS.md`.

#### Fase 10.8: simulacro y cierre

- Comprobar reinicio, recuperación, actualización, rollback y evidencia de
  cierre antes del rediseño visual.
- **Pospuesta por decisión operativa.** La Release `0.10.7` y el servicio
  Windows están activos, pero el simulacro completo se retomará después. No se
  considera cerrada hasta validar respaldo/restauración aislada, reinicio de la
  laptop sin sesión, actualización, rollback automático y evidencia
  sanitizada. Las tareas programadas de respaldo permanecen desactivadas hasta
  que se programe esa ventana de mantenimiento.

### Fase 11: diseño visual y UX

- Definir sistema visual, componentes reutilizables y navegación consistente.
- Rediseñar captura operativa, comprobantes, consulta y administración para
  tablets lentas, escáner HID, teclado, accesibilidad y estados claros.
- Optimizar el flujo de escaneo: foco automático en el siguiente campo,
  confirmación visual y sonora, y errores de producto o ubicación claramente
  accionables.
- El formulario operativo contiene lectura local de Code 128 por cámara para
  producto y ubicación, con el mismo resolvedor de Enter/HID, vista previa en
  vivo y respaldo mediante foto. Requiere HTTPS; la lectura física de un código
  en dispositivo móvil continúa en validación y no debe considerarse cerrada
  hasta completar esa prueba.
- Mostrar sugerencias de ubicaciones previamente asignadas y su saldo al
  registrar entradas, sin convertir todavía la sugerencia en una asignación
  dirigida obligatoria.

#### Fase 11.1: shell de navegación adaptable — implementada; validación física pendiente

- El layout común usa navegación lateral con la misma organización en todos los
  dispositivos: 250 px expandida y colapsable en laptop, rail de 72 px con
  expansión superpuesta en tablet horizontal, y barra superior con drawer en
  tablet vertical o pantallas estrechas.
- El menú agrupa Operación, Inventario, Catálogos y Administración, marca la
  página activa y conserva el estado colapsado en laptop. Las rutas protegidas
  de inventario, catálogos y administración solamente se renderizan para una
  sesión `ADMIN`.
- El drawer admite teclado, cierre con Escape y fondo de descarte. Cuando se abre
  el lector por cámara, la navegación se oculta y el modal ocupa la ventana
  completa para priorizar la vista previa.
- El sistema visual inicial usa fondo gris claro, superficies blancas, azul
  petróleo, iconos con texto y estados activos que combinan color, contraste y
  peso tipográfico. Falta verificar este shell en laptop y tablets físicas antes
  de considerar cerrado el comportamiento por dispositivo.

#### Fase 11.2: cuenta, identidad y apariencia — implementada; validación física pendiente

- El ADMIN puede abrir **Mi cuenta** desde el pie del menú y cambiar su nombre.
  El cambio de NIP exige el NIP actual, conserva las reglas de unicidad y
  renueva la sesión con el nombre actualizado.
- **Configuración → Datos del negocio** permite mantener nombre del negocio,
  almacén, código, zona horaria IANA y logo PNG/JPEG/WebP de hasta 1 MB. El
  logo se guarda fuera de Releases y se sirve con nombre generado, hash y CSP
  de mismo origen; nunca se aceptan SVG ni rutas del cliente.
- La apariencia Claro/Oscuro/Sistema se conserva por dispositivo mediante
  `localStorage`, se aplica antes del CSS y permanece disponible aunque no haya
  sesión administrativa. El negocio se muestra en el layout y acceso, mientras
  Warehouse EPI continúa siendo el nombre del producto.
- La zona horaria configurada controla la fecha de lotes automáticos futuros y
  se valida con `TimeZoneInfo`; el historial existente no se recalcula.
- La migración `20260818124047_AddBusinessSettings` fue revisada y aplicada a
  `warehouseEPI/public` el 2026-08-18, después de crear y validar el respaldo
  `warehouseEPI-20260818-135047.dump`. Falta comprobar visualmente los temas,
  sidebar, logo y cámara en laptop y tablets físicas.

#### Fase 11.3: estación operativa de Entrada — implementada; validación física pendiente

- **Entrada** usa la estación compartida guiada por Producto, Ubicación destino
  y Cantidad. Los pasos muestran estados textuales Pendiente, En captura y Listo,
  compactan selecciones terminadas y permiten corregirlas sin descartar los
  demás valores.
- La búsqueda, Enter/HID, cámara, sugerencias por asignación y saldo, selección
  automática cuando existe una sola ubicación relacionada, validaciones, NIP e
  idempotencia conservan los contratos operativos existentes.
- El resumen permanece lateral desde 1200 px y se fija al pie del flujo en
  anchos menores. Solo habilita la revisión cuando producto, destino, cantidad
  y cualquier aprobación de pallet compartido son válidos.
- Referencia y observaciones están plegadas como datos opcionales y se abren al
  conservar contenido o errores después de un POST. El lector sigue ocupando
  la pantalla completa y el NIP nunca se repuebla.
- La validación de código obtuvo build sin advertencias, sintaxis JavaScript
  válida, 29/29 pruebas de inventario y 134/153 en la suite completa. Las 19
  fallas restantes son los HTTP 400 del host web ya documentados y no
  aumentaron. El formato del C# modificado pasa de forma dirigida; el chequeo
  global continúa bloqueado por codificación y finales de línea heredados en
  migraciones no relacionadas.
- Falta validar visualmente claro/oscuro, teclado/HID, solapamientos, foco y
  cámara con Code 128 real en laptop y tablets físicas. Sonido, vibración y el
  rediseño del recibo permanecen fuera de esta mini fase.

#### Fase 11.3.1: corrección segura de escaneo cruzado — implementada; validación física pendiente

- Producto y ubicación se resuelven juntos por coincidencia exacta para Enter,
  lector HID, cámara y foto. Si un código solo coincide con el tipo opuesto,
  se aplica al campo correspondiente y se anuncia la corrección; la búsqueda
  escrita y las sugerencias manuales mantienen su comportamiento habitual.
- Un código que coincide a la vez con producto y ubicación no se infiere ni se
  selecciona. En Transferencia, una ubicación detectada desde Producto llena
  primero Origen y después Destino si permanece pendiente.
- No cambia el POST, NIP, idempotencia, saldos ni el esquema. Falta comprobar
  el comportamiento con lector y cámara reales en laptop y tablets físicas.

#### Fase 11.4: estaciones de Salida, Transferencia, Ajuste y comprobante — implementada; validación física pendiente

- Entrada, Salida, Transferencia y Ajuste usan una sola estación guiada
  reutilizable con pasos compactables, corrección de escaneo cruzado, cámara,
  HID, ubicaciones ligadas al producto, selección automática cuando existe una
  sola relación, saldos proyectados y resumen fijo. La ubicación primaria se
  aplica como Destino en Entrada, Origen en Salida/Transferencia y Ubicación en
  Ajuste. Salida permite saldo negativo con advertencia; Transferencia conserva
  la prohibición de mismo origen/destino; Ajuste exige motivo y conserva la
  recarga de saldo ante concurrencia.
- El comprobante muestra trayecto, saldos, responsable, referencia, notas y
  vínculos de corrección con fecha visible en la zona horaria configurada del
  almacén. No se agregaron impresión, PDF, migraciones ni cambios al POST.
- Falta validar en laptop y tablets reales el lector HID, cámara, foco,
  solapamientos y los temas Claro/Oscuro/Sistema.

### Fase 12: validación física y piloto conectado

- Probar lectores, aplicaciones de escáner, tablets reales y cámara si se
  requiere.
- Validar racks, pallets, áreas especiales, rendimiento y operaciones reales en
  red local.
- Validar etiquetas de producto y ubicación con lecturas reales; los lectores
  Bluetooth o USB en modo HID deben funcionar como teclado sin modificar el
  núcleo de inventario.
- Ejecutar un piloto conectado y conciliar inventario físico contra sistema.

### Fase 13: reportes y croquis

- Reporte de existencias por producto, ubicación y lote interno.
- Reporte de movimientos por fechas, usuario, tipo y producto; negativos,
  mínimos, conteos cíclicos y exportaciones.
- Incorporar un tablero diario con movimientos, saldos negativos, mínimos,
  ajustes recientes y conteos pendientes.
- Implementar conteos cíclicos por ubicación: captura ciega, discrepancia,
  recuento y ajuste autorizado, conservando trazabilidad completa.
- Agregar alertas para discrepancias, ubicaciones bloqueadas y saldo en una
  ubicación sin asignación activa.
- Croquis SVG manipulable con colores, ocupación y búsqueda visual.

### Fase 14: PWA y operación sin conexión

- Agregar manifest, Service Worker y caché de interfaz.
- Implementar cola IndexedDB con UUID, dispositivo identificado, prevención de
  duplicados y estados de sincronización.
- Mostrar que el stock sin conexión corresponde a la última sincronización y
  resolver conflictos en el servidor según la política confirmada.

### Fase 15: liberación v1.0 y transición operativa

- Configurar laptop servidor, dirección estable, capacitación, manuales y
  aceptación formal del piloto.
- Mantener la laptop y PostgreSQL en red local como operación primaria, de modo
  que el almacén continúe aun si falla Internet.
- Evaluar una VPS solamente como respaldo externo y consulta remota tras el
  piloto; no exponer PostgreSQL a Internet ni convertir la VPS en dependencia
  única sin una decisión explícita de conectividad y operación sin conexión.

### Fase 16: QuickBooks Desktop

- Integrar mediante adaptador desacoplado después de definir dueño de productos,
  costos y existencias; evitar edición libre del mismo saldo en ambos sistemas.

### Fase 17: paneles LED y cierre

- Agregar paneles como clientes de solo lectura o suscriptores de eventos y
  reflejar cambios de contenido del pasillo en tiempo real.

## 9. Decisiones todavía pendientes

Antes de implementar el área correspondiente, confirmar:

- referencia o documento operativo de entradas y salidas;
- política de conflictos cuando una salida fuera de línea produce saldo negativo;
- periodo máximo que deberá funcionar sin conexión;
- versión/año exactos de QuickBooks Desktop y datos a intercambiar;
- protocolo, controlador y formato de los paneles LED;
- listado físico definitivo de racks y pallets disponibles, sentido de
  numeración por fila y semántica de las áreas y colores del layout;
- formato físico final de las etiquetas de ubicación.

## 10. Reglas para continuar el desarrollo

- No almacenar contraseñas, NIP ni secretos en Git.
- No imprimir secretos en respuestas, registros ni documentación.
- No crear totales de producto que puedan divergir del saldo por ubicación.
- No borrar ni modificar destructivamente movimientos confirmados.
- No aplicar una migración sin revisar antes el SQL y confirmar la base destino.
- No tratar decisiones descritas como “probablemente” como reglas definitivas.
- Mantener la primera interfaz ligera y probarla en el dispositivo más lento.
- Preservar cambios existentes del usuario y revisar `git status` antes de editar.
- Después de cada bloque: compilar, ejecutar pruebas y revisar el diff.
- `README.md` es la entrada rápida, `ARCHITECTURE.md` conserva el diseño y
  `DEVELOPMENT.md` contiene el flujo técnico; `CONTEXT.md` solo mantiene estado,
  decisiones y continuidad.

## 11. Referencia funcional

El archivo `C:\Users\JUANANTONIOCASTILLAO\Downloads\Book1 TEST.xlsm` fue usado
como referencia funcional. Contiene formularios de entrada/salida, productos,
cantidad, ubicación, NIP, historial y una vista producto por ubicación. El nuevo
sistema debe conservar la rapidez operativa del archivo sin copiar sus problemas
de seguridad, validación y trazabilidad.

El archivo `C:\Users\JUANANTONIOCASTILLAO\Downloads\PROGRAMA DE PRODUCCION.xlsx`
también fue revisado como referencia para unidades, tipos, clases y referencias
de producto. Ninguno de los Excel fue copiado al repositorio ni se importaron
usuarios, credenciales o datos operativos.

La hoja `ITEMS` es la única fuente admitida por el importador de productos. Las
65 filas con `U/M` vacío se importan con la unidad `UNASSIGNED / Sin asignar` y
una advertencia visible. Esto permite confirmar el archivo sin asumir que esas
filas corresponden a `EA`; posteriormente pueden filtrarse y reasignarse.

El layout recibido el 14 de agosto de 2026 fija la nomenclatura de rack como
`Fila-Rack-Pallet`, por ejemplo `A-1-8`. Cada rack tiene normalmente nueve
posiciones distribuidas como un teclado numérico: `1,2,3` abajo; `4,5,6` en
medio; y `7,8,9` arriba. Las áreas que no son racks conservarán códigos propios.
Ya se realizó una carga inicial de 153 ubicaciones. Sigue pendiente validar en
sitio que cubra las posiciones existentes, excepciones y sentido de numeración,
además de confirmar el significado de los colores del croquis.

## 12. Contexto breve para pegar en otro chat

```text
Estoy desarrollando Warehouse EPI en
C:\Users\JUANANTONIOCASTILLAO\Documents\warehouse-EPI. Lee README.md,
docs/ARCHITECTURE.md, docs/DEVELOPMENT.md y docs/CONTEXT.md antes de modificar.
Verifica git status, los archivos actuales, dotnet build y dotnet test. No
sobrescribas cambios existentes ni expongas secretos.

Es una aplicación web local para almacén: ASP.NET Core 10 con Razor Pages,
Entity Framework Core y PostgreSQL. Una laptop será el servidor y tablets Android
lentas usarán Chrome. Roles: ADMIN y OPERATOR; ambos hacen movimientos y ajustes,
ADMIN administra usuarios. Cada usuario tiene un NIP único de 4 a 8 dígitos,
solicitado en cada movimiento; nunca se almacena en texto plano.

El inventario se controla físicamente por producto + ubicación + lote interno;
las consultas y capturas operativas usan saldos agregados por producto y
ubicación. Los totales se derivan de esos saldos. Se permiten cantidades
numeric(18,4), saldo negativo, múltiples ubicaciones y varios códigos por
producto. Todo producto usa un lote diario interno `AUTO-YYYYMMDD`; no existe
caducidad ni captura pública de lotes. Los movimientos son Entrada, Salida,
Transferencia y Ajuste. Los confirmados son inmutables y se corrigen mediante
reverso y reemplazo auditables.

Las fases 1 a 9 están completadas; la fase 8 fue descartada. La base real es
warehouseEPI, el esquema operativo es public y los secretos están en User
Secrets. Existe una migración aplicada para lotes internos globales. La última
auditoría confirmó 1,612 productos, 216 ubicaciones, 5 asignaciones activas, 9
movimientos, 4 saldos y 3 lotes, sin saldos sin lote. La suite tiene 120 pruebas,
incluidas pruebas PostgreSQL aisladas en warehouse_epi_test.

El layout físico usa la nomenclatura Fila-Rack-Pallet, por ejemplo A-1-8, y el
pallet se distribuye como teclado numérico. Las áreas no rack conservan códigos
propios. Sigue pendiente validar físicamente posiciones, excepciones, sentido de
numeración y colores del layout.

Las fases 10.1, 10.2 y 10.3 están completadas. `Quality` está verde y es
obligatorio en `main`; el motor de inventario usa exclusivamente lotes internos
automáticos en movimientos nuevos y conserva el reverso de historial antiguo.
Después siguen seguridad, observabilidad, respaldo, publicación,
UX, piloto conectado, reportes, PWA/offline, liberación v1.0, QuickBooks y
paneles LED. No agregues QuickBooks ni LED antes de esas fases.
```
