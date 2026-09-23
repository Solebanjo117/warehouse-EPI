# Contexto del proyecto Warehouse EPI

Actualizado: 31 de agosto de 2026.

Este documento es la fuente de continuidad del proyecto. Antes de trabajar en
un chat nuevo, se debe leer este archivo y verificar el estado actual del
repositorio. Las decisiones marcadas como pendientes no deben convertirse en
requisitos definitivos sin confirmación.

**Estado general:** las fases 1 a 9 están cerradas, salvo la fase 8 de paquetes
y conversiones, descartada por decisión del negocio. Las fases 10.1 a 10.3
están completadas; 10.4 a 10.6 están implementadas; la Release `0.10.7` está
activa como servicio Windows y 10.8 continúa pospuesta. La fase 11 tiene sus
principales pantallas implementadas, pero todavía requiere validación física,
mantiene trabajo parcial en 11.6 y 11.7, e implementa 11.9.1 a 11.9.4 del
editor arquitectónico ligero. Las migraciones 11.9.1/11.9.2 están aplicadas;
11.9.3 y 11.9.4 mantienen migración y validación visual/física pendientes. WIP está implementado
y su migración está aplicada, aunque aún no forma parte de una Release publicada
ni se ha validado en tablet/cámara. Las fases 13.1 y 13.2 de reportes están
completadas;
13.3, 13.4, 13.5 y 13.6 están implementadas y automatizadas, con migraciones
de 13.5/13.6, Release y validación física LAN/tablet/lector/impresora pendientes. La protección
de `main` exige el check `Quality`.
El centro de excepciones accionable está implementado en código y su migración
posterior a WIP está aplicada; la Release y validación física siguen pendientes.

## 1. Objetivo

Construir un sistema web local para registrar y consultar movimientos de
almacén. Una laptop funcionará como servidor dentro de la red local y varias
tablets Android, limitadas en rendimiento, accederán mediante Google Chrome.

El sistema debe priorizar:

- captura rápida mediante escáner o cámara;
- inventario por producto y ubicación;
- trazabilidad completa de los movimientos;
- interfaz ligera para tablets lentas;
- posibilidad de crecer a lotes internos, etiquetas y placas de pallet,
  trazabilidad de producción, operación sin conexión, QuickBooks Desktop,
  croquis del almacén y paneles LED.

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
- Un producto puede tener varias ubicaciones fijas asignadas y una ubicación
  principal de entrada opcional; una ubicación puede estar asignada a varios
  productos. La ubicación principal siempre mantiene una asignación activa. La
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
asignaciones producto-ubicación se reconcilian dentro de la misma transacción
con el saldo agregado de todos los lotes: se crean o reactivan con saldo distinto
de cero y se desactivan cuando el saldo queda exactamente en cero. Una
transferencia total mueve también la ubicación principal de entrada al único
destino resultante; una salida, ajuste o conteo sin destino la limpia. La
confirmación de pallet compartido considera únicamente otros productos con
saldo neto distinto de cero, no asignaciones administrativas agotadas.

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
cerradas y la estabilización, UX, WIP y reportes posteriores se detallan en las
secciones siguientes. Como referencia histórica, la auditoría final de la fase
9 confirmó 216 ubicaciones, 5 asignaciones activas, 9 movimientos, 4 saldos y 3
lotes; la auditoría posterior a WIP confirmó 25 movimientos históricos
`STANDARD` y WIP-2/3/4 sin saldos ni asignaciones. Sigue pendiente comprobar en
sitio que las ubicaciones cubran todas las posiciones y excepciones físicas del
almacén.

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
- Existe una asignación fija muchos-a-muchos entre productos y ubicaciones. Cada
  producto puede marcar una de ellas como principal de entrada; las demás siguen
  siendo relaciones secundarias. Las asignaciones se desactivan y reactivan sin borrado,
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
  misma regla. Entrada solo autoselecciona el destino principal explícito y
  disponible; permite cambiarlo y solicita captura manual si queda bloqueado,
  inactivo o retirado. En las demás operaciones, una única relación puede
  autocompletarse y varias exigen selección. Una
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
- Para un cambio deliberado de laptop existe un flujo portátil separado:
  `New-WarehouseEpiMigrationBackup.ps1` crea un respaldo fresco, valida una
  restauración temporal y empaqueta base, referencias y branding con manifiesto
  y SHA-256 externo. `Test-WarehouseEpiMigrationBackup.ps1` comprueba rutas,
  componentes y hashes; `Restore-WarehouseEpiMigrationBackup.ps1` solo restaura
  si `warehouseEPI` no existe y los directorios externos están vacíos. El ZIP
  excluye credenciales, `service-settings.json`, CA privada y
  `Security:PinLookupKey`, que deben viajar cifrados por separado. La ejecución
  real del respaldo final y la restauración en otra laptop siguen pendientes.

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
  acceso HTTPS LAN en la IP entonces reservada `192.168.5.192`. La reserva LAN
  actual cambió a `192.168.6.68` y requiere renovar certificado, `AllowedHosts`
  y su huella con `Renew-WarehouseEpiLanCertificate.ps1`, reiniciar el servicio
  y comprobar HTTPS desde una tablet; esa validación queda pendiente. Las tareas
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
  vivo y respaldo mediante foto. El lector intenta primero la cámara trasera,
  permite recorrer las cámaras disponibles y recuerda la elección en cada
  dispositivo; el mismo control está disponible en Existencias. Requiere HTTPS;
  la lectura física de un código y el cambio de cámara en dispositivo móvil
  continúan en validación y no deben considerarse cerrados hasta completar esa
  prueba.
- Mostrar sugerencias de ubicaciones previamente asignadas y su saldo al
  registrar entradas, sin convertir todavía la sugerencia en una asignación
  dirigida obligatoria.

#### Fase 11.1: shell de navegación adaptable — implementada; validación física pendiente

- El layout común usa navegación lateral con la misma organización en todos los
  dispositivos: 250 px expandida y colapsable en laptop, rail de 72 px con
  expansión superpuesta en tablet horizontal, y barra superior con drawer en
  tablet vertical o pantallas estrechas.
- El menú lateral muestra Inicio y los módulos Operaciones, Producción, Inventario,
  Etiquetas, Reportes, Catálogos y Administración. Cada módulo abre su pantalla
  de tarjetas; Catálogos, Administración y las acciones administrativas de los
  otros módulos solo se renderizan para una sesión ADMIN. El Inicio conserva sus
  accesos rápidos y las rutas de operación mantienen sus permisos originales.
- El módulo activo se resuelve por segmentos completos de la página, también en
  fichas y ediciones. El estado contraído de laptop se conserva. Las páginas
  internas incluyen un retorno al módulo, oculto durante cámara e impresión.
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

#### Fase 11.5: Existencias y Alertas de inventario — implementada; validación física pendiente

- **Existencias** mantiene acceso público y ahora usa una consulta única para
  producto o ubicación, con sugerencias agrupadas, Enter/HID, cámara y foto.
  La resolución exacta conserva la seguridad ante un código ambiguo y permite
  revisar productos inactivos o ubicaciones bloqueadas.
- Los resultados consolidan asignaciones y saldos, incluyen resumen, filtros,
  paginación de 25 posiciones y resaltado contextual desde Alertas. En laptop
  se presentan como filas y en tablet vertical como tarjetas compactas.
- **Alertas** continúa protegida por `AdminOnly`; muestra indicadores de
  negativos y mínimos, hora en la zona del almacén, actualización manual,
  pestañas GET, búsqueda y paginación. Las alertas son derivadas y no se
  reconocen ni se persisten.
- No cambian saldos, mínimos, movimientos, lotes ni el esquema. Falta validar
  físicamente lector HID, cámara, foco, tablet horizontal/vertical y temas
  Claro/Oscuro/Sistema.

#### Fase 11.6: trazabilidad de movimientos y lotes — implementada en código; validación física pendiente

- El historial ADMIN usa periodos locales del almacén (por defecto los últimos
  30 días), consultas UTC de intervalo semiabierto, paginación de 25 y
  exportación trazable por cambio de saldo.
- El detalle de movimiento expone snapshots históricos de lote, enlaces
  administrativos y la cadena de corrección sin alterar movimientos confirmados.
- La ficha ADMIN `/Admin/Inventory/Movements/Details/{id}` es el registro
  profesional de trazabilidad del movimiento. Conserva cabecera, metadatos,
  productos, lotes y cambios de saldo, y agrega una cronología causal de la
  cadena original/reverso/reemplazo, la confirmación documental, las
  disposiciones WIP y las acciones de la ubicación de conteo relacionadas.
  Abrir cualquier miembro de una corrección devuelve el mismo contexto y marca
  cuál es la ficha actual; no incorpora otras recepciones del documento ni
  acciones de otras ubicaciones de la campaña.
- La ficha usa la identidad configurada del negocio/almacén y `WarehouseClock`,
  distingue eventos que impactan inventario de eventos informativos, y permite
  imprimir o guardar como PDF desde el navegador en Carta vertical. La hoja
  oculta navegación y acciones, restaura encabezados semánticos, admite varias
  páginas y no incluye firmas. No genera PDF en servidor ni cambia contratos
  POST, movimientos, saldos o esquema.
- Lotes internos cuenta con filtros, saldo agregado, ficha de distribución,
  movimientos relacionados y auditoría de cambios de fecha. La fecha sigue
  afectando únicamente FEFO futuro y exige motivo/NIP ADMIN.
- El servicio transversal es de solo lectura y se validó con pruebas focales
  InMemory y PostgreSQL contra `warehouse_epi_test`. Sigue pendiente la
  validación visual en navegador, Carta vertical en impresora real y tablet
  horizontal/vertical; no se aplicó migración ni se modificó el esquema.

#### Fase 11.7: Catálogo y ficha integral de Productos — implementación inicial; validación física pendiente

- Productos sigue protegido por `AdminOnly`, pagina de 25 en 25 y separa la
  consulta de la edición. El listado incorpora indicadores de estado y saldo,
  búsqueda por SKU, descripción, referencia, código o ubicación, y filtros de
  estado, existencia y asignación.
- La ficha `/Admin/Catalogs/Products/Details/{id}` concentra saldo total,
  mínimo, cobertura, distribución por ubicación, códigos, lotes internos y
  movimientos recientes, con enlaces administrativos de consulta. Crear y
  guardar redirigen a la ficha con confirmación; Edit permanece compatible.
- No se modificaron entidades, saldos, movimientos, lotes ni PostgreSQL. La
  importación conserva su contrato y ahora pagina 25 filas por vista.
- Build Release sin advertencias y las pruebas de catálogo pasaron. Falta
  validar físicamente filtros, tablet, temas, HID y cámara; las pruebas web
  siguen afectadas por los HTTP 400/antiforgery preexistentes.

##### Mejora de UI del listado — implementada; validación visual/física pendiente

- El listado expone por fin los filtros de unidad base, tipo y clase que el
  handler y `ProductCatalogQueryService` ya aceptaban. Van dentro de un
  `<details>` plegable sin JavaScript que se abre solo cuando alguno está
  activo, para no estorbar en tablet.
- Azulejos KPI, paginación y chips arrastran ahora los siete filtros mediante
  un único helper `RouteValues`; antes se perdían al navegar. Los chips de
  filtros activos son enlaces GET que quitan un solo filtro, más un
  **Limpiar todo**; el chip de estado por omisión permanece informativo.
- Un solo helper de badges unifica tabla y tarjeta —la tarjeta no mostraba
  **Activo**—, la paginación pasa a numerada con Primera/Última al estilo de
  Ubicaciones, el estado vacío distingue catálogo vacío de filtros sin
  coincidencias, y la tarjeta completa abre la Ficha en tablet.
- Cambio exclusivamente de presentación: no se tocaron entidades, migraciones,
  `ProductCatalogQueryService`, la firma de `OnGetAsync`, el tamaño de página
  ni ningún POST. Sin JavaScript, CDN ni archivos CSS nuevos; todo el CSS nuevo
  usa variables Bootstrap y respeta Claro/Oscuro y los objetivos de 44 px.
- Build Release en 0 advertencias/0 errores; 17 pruebas dirigidas de catálogo y
  contratos pasan. La suite completa queda en 305/345 y sus 40 fallas siguen
  siendo la línea base preexistente (21 HTTP 400 del host `WebApplicationFactory`
  y 19 de fixtures PostgreSQL); no aumentaron. Falta comprobar en navegador los
  anchos <900, 900-1199 y >=1200 en Claro/Oscuro y validar en tablet física.
- Siguen pendientes en 11.7 el lector/cámara del listado y la modernización
  visual de Crear/Editar e Importación, que quedaron deliberadamente fuera de
  esta entrega de presentación.

##### Ubicación principal de entrada — implementada en código; migración y validación física pendientes

- Crear y Editar producto admiten una ubicación principal opcional. Elegirla
  crea o reactiva su asignación fija; cambiarla conserva las asignaciones
  secundarias y limpiar el selector no las elimina. Desasignar la principal sí
  retira ambas relaciones en una sola operación lógica.
- `/Operations/Entry` autoselecciona únicamente esa relación explícita cuando
  sigue física, activa, no bloqueada y asignada. La sugerencia puede cambiarse;
  una principal no disponible produce una advertencia y mantiene la captura
  manual. Salida, Transferencia, Ajuste y WIP conservan su comportamiento.
- La migración `20260904143029_AddProductDefaultEntryLocation` agrega solamente
  `products.default_entry_location_id`, su índice y la FK `RESTRICT`, sin
  backfill. El SQL fue revisado, pero la migración no se aplicó a PostgreSQL.
- Pasaron 14 pruebas focales de modelo, asignación, consulta y contrato cliente;
  `operations.js` pasó validación sintáctica. La prueba web integral continúa
  afectada por el HTTP 400/antiforgery preexistente. Faltan navegador y prueba
  física con tablet, lector HID y cámara.

#### Fase 11.8: Ubicaciones y croquis interactivo — implementada; validación física pendiente

- Ubicaciones abre por defecto un croquis SVG limpio basado en las fotografías
  físicas del 14 de agosto. Mantiene vistas alternativas de racks y tabla,
  búsqueda por ubicación o producto, estados de inventario, negativos,
  bloqueo/inactividad y panel 3x3 con la distribución de teclado numérico.
- El panel del croquis conserva unidades separadas, muestra asignaciones y
  saldos por producto, y enlaza ficha, Existencias y Movimientos. La ficha de
  ubicación incorpora saldos, asignaciones históricas, vecinos y movimientos
  recientes; la tabla usa 25 registros y tarjetas en tablet vertical.
- En la consulta pública y ADMIN, el croquis conserva los controles de zoom y
  desplazamiento interno y admite pellizcar con dos dedos entre 100 % y 400 %,
  manteniendo como referencia el punto situado entre los dedos. Al finalizar el
  gesto se evita abrir accidentalmente un rack; la validación en tablet física
  continúa pendiente.
- La ficha de ubicación consulta en bloque las nueve posiciones del mismo rack
  e identifica la posición actual dentro de la cuadrícula 3x3. Cada pallet
  muestra estado operativo, saldo real agrupado por producto/unidad, primer SKU,
  cantidad y productos adicionales; distingue saldo negativo, saldo sin
  asignación, asignación sin saldo, posición vacía e inexistente sin depender
  solo del color. El resumen superior separa productos con saldo de asignaciones
  activas. No cambia entidades, migraciones, permisos, POST ni inventario.
- `/Admin/Catalogs/Locations/Map/Edit` permite seleccionar varios elementos por
  recuadro, Ctrl/Cmd o modo táctil, moverlos, dimensionarlos como grupo,
  girarlos, ocultarlos e invertirlos horizontalmente. Incluye alinear a bordes
  o centros, distribuir por centros e igualar ancho/alto usando el elemento de
  referencia. También ordena racks seleccionados de una sola fila de izquierda
  a derecha por su número, sin crear racks faltantes; cada acción se puede
  deshacer o rehacer. El fondo físico es fijo;
  el editor no modifica ubicaciones, códigos, saldos ni asignaciones. Los racks
  y áreas creados después aparecen en **Sin colocar** y solo se incorporan al
  guardar una revisión autorizada.
- La inicialización genera una vista previa de 30 minutos ligada al ADMIN y
  ubica A-M, N-S, T y áreas conocidas; elementos no reconocidos permanecen en
  **Sin colocar**. Inicialización y revisiones exigen NIP ADMIN, son
  transaccionales, idempotentes, versionadas y conservan auditoría JSONB.
- Se generó y revisó la migración
  `20260818154704_AddWarehouseInteractiveMap`; crea únicamente configuración,
  elementos y revisiones del croquis. **No está aplicada** y requiere confirmar
  base destino y respaldo antes de `database update`.
- Build Release, formato dirigido y 29 pruebas dirigidas de
  ubicaciones/inventario pasan. La suite completa queda en 139/158: las 19
  fallas restantes son los HTTP 400/antiforgery preexistentes y no aumentaron.
  La mejora posterior de la ficha aprobó 44/44 pruebas focales de Ubicaciones y
  contratos web, además de un build Release aislado con 0 advertencias y 0
  errores. No había una pestaña autorizada disponible para validarla visualmente.
  Falta validar físicamente geometría, orientación, zoom, interacción táctil y
  temas Claro/Oscuro en laptop y tablets reales; también queda pendiente revisar
  la nueva ficha en los anchos `<900`, `900–1199` y `>=1200`.

#### Fase 11.9: editor arquitectónico ligero del croquis — en implementación

El objetivo es permitir que un ADMIN mantenga delimitaciones físicas del
almacén con una experiencia semejante a un editor básico de planos, sin
convertir Warehouse EPI en un CAD general. La ampliación reutilizará Razor
Pages, SVG, Bootstrap y JavaScript local; no dependerá de Internet ni cargará
sus herramientas fuera de la ruta de edición ADMIN.

La capa arquitectónica permanecerá separada de la capa operativa. Dibujar o
mover muros, pasillos, puertas, zonas, textos o cotas no podrá crear ni
renombrar ubicaciones, cambiar códigos `Fila-Rack-Pallet`, saldos, movimientos,
lotes o asignaciones. Los racks y áreas operativas seguirán procediendo del
catálogo y las adiciones continuarán entrando por **Sin colocar**. Todas las
revisiones conservarán NIP ADMIN, idempotencia, versión y auditoría.

##### Fase 11.9.1: modelo arquitectónico, capas y compatibilidad — implementada; migración aplicada, validación visual/física pendiente

- `WarehouseMapLayer` separa Estructura, Pasillos, Zonas, Textos, Medidas y
  Ubicaciones operativas. El bloqueo forma parte de la revisión; mostrar u
  ocultar es una preferencia local del editor y nunca cambia la consulta.
- `WarehouseMapArchitecturalElement` persiste rectángulos, polilíneas y textos
  mediante geometría JSONB validada, estilos semánticos y orden. No tiene
  relación con `Location`, asignaciones o inventario y 11.9.1 no expone altas,
  bajas, estilos, dibujo, vértices ni redimensionado arquitectónico.
- Sin datos persistidos, el servicio reproduce con IDs estables los 17
  elementos del SVG público completo: perímetro, seis divisiones, cuatro zonas
  y seis textos. Consulta y editor usan un solo renderizador Razor; el archivo
  `warehouse-floor-base.svg` permanece sin cambios como respaldo.
- El renderizador compartido aplica trazo y relleno al contenido del elemento
  SVG `<text>` tanto en consulta como en edición. Esto evita que las etiquetas
  arquitectónicas con relleno `NONE` o color definido por trazo desaparezcan
  del croquis de consulta aunque sean visibles dentro del editor.
- El primer guardado válido con NIP ADMIN persiste ese fondo y seis capas dentro
  de la misma transacción que la geometría operativa. La versión aumenta una
  vez, la idempotencia incluye las tres colecciones y la auditoría JSON usa
  esquema 2 con antes/después de operación, capas y arquitectura. Revisiones
  anteriores permanecen intactas.
- El editor permite desbloquear una capa y desplazar sus primitivas, incluso por
  teclado, pero impide mezclar arquitectura y ubicaciones en una selección. Las
  funciones existentes de racks y **Sin colocar** conservan su contrato.
- La migración `20260824183000_AddWarehouseMapArchitecture` crea únicamente
  `warehouse_map_layers` y `warehouse_map_architectural_elements`; el 24 de agosto
  de 2026 se verificó aplicada en `__EFMigrationsHistory`. Build Release pasó sin advertencias,
  Las pruebas focales y la prueba PostgreSQL aislada del croquis aprobaron. La
  validación visual en navegador y prueba física en laptop/tablet continúan
  pendientes.

##### Fase 11.9.2: dibujo y edición básica de plano — implementada; migración aplicada, validación visual/física pendiente

- El editor ADMIN incorpora Seleccionar, Desplazar, Muro, Rectángulo, Polígono,
  Puerta, Pasillo, Zona y Texto como presets de `Rectangle`, `Polyline` y
  `Text`; no se añadieron tipos persistidos ni dependencias de Internet.
- El payload arquitectónico contiene definición, geometría y estilo completos.
  Admite IDs nuevos generados con Web Crypto, pero exige conservar todos los
  IDs persistidos y rechaza cambios de tipo, capa, orden o bloqueo individual.
  Solo un objeto aún no guardado puede descartarse.
- Rectángulos se dibujan por arrastre y las polilíneas por puntos con
  Finalizar/Enter/doble clic, Cancelar/Escape y vértices editables. El panel de
  propiedades ofrece posición, tamaño contextual, rotación ortogonal, texto,
  trazo, relleno, grosor y línea discontinua; selección múltiple arquitectónica
  conserva movimiento y estilos comunes sin mezclarse con ubicaciones.
- La cuadrícula usa un único `<pattern>` de 10, 25 o 50 unidades. Visibilidad y
  ajuste se guardan solo en `localStorage`; snapping prioriza extremos, centros
  y guías, Alt lo suspende. Zoom 25–400 %, encajar, rueda, pan, coordenadas y
  gesto de pinza no forman parte del POST.
- El servicio valida hasta 500 objetos, 64 puntos, estilos semánticos, capas
  desbloqueadas y límites transformados de `1600 × 900`. La auditoría usa
  esquema 3 con antes/después completos y altas; la versión aumenta una vez y
  la idempotencia incluye la definición completa.
- `20260824210000_ExpandWarehouseMapArchitectureStyles` solo amplía la
  restricción de estilos de las tablas arquitectónicas; el 24 de agosto de 2026
  se verificó aplicada en `__EFMigrationsHistory`. Build Release, `node --check`, 46 pruebas focales y la prueba
  PostgreSQL aislada aprobaron. No hubo navegador activo; validación visual y
  prueba física en laptop/tablet permanecen pendientes.

##### Fase 11.9.3: productividad, escala imperial y validaciones — implementada; migración y validación visual/física pendientes

- La arquitectura permite duplicar, copiar/pegar dentro del editor, agrupación
  persistente sin anidamiento, distribución, bloqueo individual y orden estable
  dentro de una capa. Los IDs nuevos se generan con Web Crypto; estas acciones
  no se ofrecen a ubicaciones operativas.
- El marco de selección puede iniciarse sobre arquitectura bloqueada y vuelve a
  seleccionar varios objetos por arrastre. Mantiene separadas arquitectura y
  ubicaciones, respeta la selección aditiva y expande grupos arquitectónicos
  completos.
- Retirar arquitectura persistida usa `IsArchived`: desaparece de la consulta,
  conserva su fila y puede restaurarse desde el editor. Los grupos se archivan y
  restauran juntos; ningún POST puede omitir o eliminar físicamente un ID guardado.
- El layout persiste `ScaleUnitsPerInch` y `MeasurementSystem`. El sistema
  predeterminado es `IMPERIAL` con pulgadas/yardas sin pies; puede publicar cm/m
  sin recalibrar. Las cotas son pares `Polyline` + `Text` agrupados en
  `DIMENSIONS`, con texto derivado de la escala.
- Un handler ADMIN de revisión valida sin escribir y resume ubicaciones, capas,
  altas, modificaciones, archivados, restauraciones, escala y unidades antes de
  solicitar el NIP. El guardado vuelve a validar, conserva idempotencia y registra
  esquema 4 con antes/después completos dentro de una sola transacción.
- Geometrías, escala, grupos y cotas inválidos bloquean. Pasillos menores de 32
  pulgadas, rack-rack, zona-zona y racks dentro de pasillos son advertencias
  textuales no normativas que identifican los elementos afectados.
- `20260824233000_AddWarehouseMapProductivityScale` agrega únicamente escala y
  unidades al layout, y grupo/archivado a arquitectura. El snapshot no tiene
  cambios pendientes; aplicar, revertir y reaplicar en `warehouse_epi_test`
  preservó ubicaciones. No se aplicó a `warehouseEPI`.
- Build Release, pruebas focales, `node --check` y pruebas PostgreSQL aisladas
  aprobaron. El servicio estaba detenido, por lo que el error runtime de
  Ubicaciones y la validación visual Claro/Oscuro/responsive no pudieron
  comprobarse sin cambiar el estado operativo; la prueba física continúa pendiente.

##### Fase 11.9.4: rendimiento, fondo de referencia y validación física — implementada; migración y validación visual/física pendientes

- Movimiento, redimensionado y vértices agrupan eventos con
  `requestAnimationFrame` y renderizan los elementos seleccionados durante la
  interacción. La consulta carga `warehouse-map-query.js`; el código completo
  del editor y el módulo del fondo solo se descargan en la ruta ADMIN.
- El editor admite un fondo raster PNG, JPEG o WebP de hasta 2 MiB y
  `4096 × 4096`, con opacidad, visibilidad local, bloqueo, giro, ajuste al lienzo,
  movimiento y redimensionado proporcional. El fondo nunca se muestra en la
  consulta publicada ni se incrusta en JSON o PostgreSQL.
- Los archivos se validan por firma y dimensiones, se nombran por SHA-256 y se
  almacenan fuera de la Release en
  `C:\ProgramData\WarehouseEPI\WarehouseMapReferences` con ACL del servicio.
  La base persiste solo metadatos, geometría, bloqueo, archivado y calibración.
- Reemplazar un fondo archiva el anterior; restaurar es reversible y no existe
  eliminación física desde el editor. Todo fondo persistido debe continuar una
  vez en el POST. Revisión, idempotencia, versión y auditoría esquema 5 incluyen
  altas, cambios, archivados, restauraciones y calibración.
- La calibración puede marcar dos puntos normalizados sobre la imagen y conserva
  la escala canónica en unidades SVG por pulgada. Mover o girar el fondo no cambia
  la escala; redimensionarlo la recalcula. Imperial continúa predeterminado y el
  cambio de presentación métrica no modifica geometría ni inventario.
- `20260825093000_AddWarehouseMapReferenceImages` crea únicamente la tabla de
  referencias, restricciones, índices y FKs; el snapshot coincide con el modelo.
  No se aplicó a `warehouseEPI` ni se publicó una Release.
- El respaldo diario genera un ZIP pareado con manifiesto SHA-256 para los fondos,
  y la validación semanal exige y comprueba ese archivo cuando la base restaurada
  contiene referencias. Los scripts de Release crean/protegen el directorio.
- Las capturas reales del editor mostraron que el panel derecho abierto alargaba
  innecesariamente toda la página. El lateral ahora es fijo y tiene desplazamiento
  propio en escritorio; Fondo, Cuadrícula, Escala, Capas, Arquitectura archivada y
  Sin colocar son secciones plegables. En tablet/móvil se conserva el flujo vertical.
  Las capturas corresponden al estado anterior; falta validar visualmente el ajuste
  después de cargar estos archivos en la aplicación activa.
- Build Release sin advertencias, sintaxis JavaScript/PowerShell, 58 pruebas
  focales y 2 pruebas PostgreSQL aisladas aprobaron; estas últimas cubren
  migración/reversión y reemplazo transaccional del fondo sin alterar elementos
  operativos. La validación de servidor cubre el límite exacto de 500 objetos y
  rechaza 501; esto no sustituye medir fluidez en hardware. La comprobación
  visual Claro/Oscuro/responsive y la validación
  física con ratón y tablet Android siguen pendientes; no se atribuye fluidez al
  hardware real hasta medirla con un plano de 100–300 objetos y estrés de 500.

##### Fase 11.9.5: lienzo ampliable del croquis — implementada; migración y validación visual/física pendientes

- `WarehouseMapLayout` persiste ancho y alto. Los layouts existentes reciben
  `1600 × 900`; el servidor solo acepta crecimiento hacia la derecha y abajo,
  en incrementos de 25, hasta `6400 × 3600`. Elementos operativos, arquitectura
  y fondos continúan obligados a permanecer dentro del lienzo enviado.
- El editor ofrece un tirador táctil y accesible por teclado en la esquina
  inferior derecha. El tamaño es borrador, participa en cancelar/deshacer y solo
  se publica después de la revisión existente y la confirmación con NIP ADMIN.
  La consulta, la cuadrícula y los fondos usan las dimensiones persistidas sin
  alterar las coordenadas anteriores ni crear almacenes o ubicaciones.
- En la consulta, **Ajustar** usa el ancho disponible en lugar de reducir todo
  el lienzo alto para hacerlo caber simultáneamente. La proporción SVG se
  conserva y el excedente vertical se recorre dentro del panel con scroll nativo;
  el zoom continúa entre 100 % y 400 % sin cambiar el `viewBox` persistido.
- La selección múltiple representa estilos diferentes como **Varios** sin
  serializar un valor vacío. Antes de revisar o guardar, el editor normaliza
  borradores antiguos al catálogo semántico y limita el grosor a `0–12`; si el
  servidor rechaza un estilo, informa etiqueta, UUID, trazo, relleno y grosor.
- La auditoría usa esquema 6 e incluye dimensiones antes/después; los resúmenes
  de esquemas anteriores siguen siendo legibles. La migración
  `20260831183516_AddWarehouseMapCanvasDimensions` solo agrega ambas columnas y
  su restricción al layout. No se aplicó a PostgreSQL ni se publicó una Release.
- Sintaxis JavaScript, 64 pruebas focales y el build Web Release aislado sin
  advertencias aprobaron. La comprobación visual en navegador y la prueba física
  táctil continúan pendientes y deben ejecutarse contra la aplicación actualizada
  antes de declarar cierre operativo.

Quedan fuera de 11.9 la importación o exportación DWG/DXF, curvas Bézier,
bibliotecas CAD completas, colaboración simultánea en tiempo real, cálculo
estructural, rutas automáticas de evacuación y reglas legales no confirmadas.
Importar PDF o intercambiar formatos técnicos se evaluará después de validar el
editor SVG y demostrar una necesidad operativa concreta.

##### Ubicación aproximada y calibración geográfica — implementada en código; migración y validación física pendientes

- El croquis público y ADMIN ofrece **Mi ubicación**. Tras una acción explícita del
  usuario solicita permiso al navegador, usa `watchPosition` y dibuja un marcador
  con un círculo conservador de incertidumbre. Las coordenadas se transforman y se
  conservan únicamente en el navegador; el servidor solo entrega la calibración
  publicada y su revisión.
- `/Admin/Catalogs/Locations/Map/Calibration` permite marcar cuatro o más
  referencias y al menos una comprobación, capturar cada punto durante 20 segundos,
  revisar errores y publicar con motivo y NIP ADMIN. El borrador vive en
  `sessionStorage` sin incluir el NIP.
- El servidor resume las muestras por mediana, proyecta latitud/longitud a metros
  locales y obtiene una transformación afín por mínimos cuadrados. Las comprobaciones
  miden el error sin participar en el ajuste. Referencias inválidas, fuera del lienzo
  o degeneradas bloquean; la baja precisión produce advertencias porque la función
  se presenta siempre como aproximada.
- La calibración guarda puntos, coeficientes, error, versión del croquis y revisiones
  independientes con idempotencia. Una nueva publicación del croquis la deja
  pendiente de revisión automáticamente. `Permissions-Policy` habilita geolocalización
  solo en las consultas del croquis y en la pantalla de calibración; el uso en LAN
  requiere HTTPS válido.
- `20260917174125_AddWarehouseMapGeolocationCalibration` crea únicamente las tablas
  de calibraciones, puntos y revisiones. No se aplicó a ninguna base de datos.

##### Acciones operativas desde el croquis y corrección reversible de racks — implementada; migración sin aplicar y validación visual/física pendiente

- El croquis ADMIN ofrece **Nueva operación** para Entrada, Salida general,
  Transferencia y Ajuste desde cada pallet; **Operar** junto a un producto agrega
  también su `ProductId`. WIP expone únicamente **Surtir a este WIP**. Los GET
  precargan producto y ubicación mediante los servicios operativos, generan un
  `OperationId` nuevo y vuelven a consultar la versión del saldo para Ajuste;
  cantidad, destino pendiente, motivo, aprobaciones y NIP continúan manuales.
- `/Admin/Catalogs/Locations/Rack/Edit` permite a ADMIN corregir la combinación
  física `7-8-9 / 4-5-6 / 1-2-3` sin cambiar fila, rack o códigos. El flujo es
  revisar, motivo, NIP ADMIN y guardar. Una posición con saldo neto distinto de
  cero o asignación activa queda protegida.
- Retirar establece `IsPhysicallyPresent = false` e `IsActive = false`; no borra
  la ubicación, movimientos, lotes, balances ni asignaciones históricas.
  Restaurar reutiliza el mismo ID y código; una posición nunca creada recibe una
  fila nueva. La tabla administrativa conserva filtro y estado **Retirada**.
- `LocationRackRevision` registra UUID idempotente, huella, rack, responsables,
  motivo y estados JSON antes/después. La corrección usa transacción serializable
  y bloqueo de las filas del rack; no aumenta la versión ni la auditoría del
  croquis arquitectónico.
- El editor ofrece además **Eliminar rack definitivamente** sólo cuando ninguna
  posición tiene asignaciones (activas o históricas), filas de saldo aunque estén
  en cero, movimientos, cambios de saldo, conteos, incidencias ni actividad WIP.
  La confirmación exige motivo, NIP ADMIN y escribir el código exacto del rack;
  el servidor vuelve a comprobar las referencias dentro de una transacción
  serializable. Se eliminan todas sus posiciones y la geometría operativa del
  croquis, se incrementa la versión del croquis y se conservan revisiones JSON
  de ambas operaciones como auditoría. No requiere migración de esquema.
- El editor de áreas ofrece **Eliminar área definitivamente** con el mismo
  criterio estricto para áreas generales y WIP: cualquier asignación, fila de
  saldo, movimiento, cambio de saldo, conteo, incidencia o disposición impide
  el borrado. Exige motivo, código exacto y NIP ADMIN; elimina también su
  geometría y registra una revisión/versionado cuando el croquis ya existe.
  Si aún no hay un croquis inicializado, elimina sólo el catálogo porque no hay
  geometría publicada. Tampoco requiere migración de esquema.
- Las posiciones retiradas se excluyen del croquis publicado, cuadrículas físicas,
  búsquedas/precargas operativas, movimientos, conteos cíclicos y consulta pública
  de inventario. Permanecen accesibles por ID y desde la tabla ADMIN para consultar
  estado e historial.
- `20260825163000_AddLocationRackPhysicalPresence` agrega únicamente la bandera,
  la tabla de revisiones, FKs, restricciones e índices. No se aplicó a
  `warehouseEPI` ni se publicó una Release. En `warehouse_epi_test` la reversión,
  reaplicación y corrección `1–9 → 1–6` aprobaron preservando el número de
  ubicaciones y movimientos.
- Build Release aislado, comparación modelo/snapshot, 81 pruebas focales y una
  prueba PostgreSQL aislada aprobaron. No se ejecutó validación visual en navegador
  Claro/Oscuro/responsive ni prueba física en tableta; ambas siguen pendientes.

### Fase 12: etiquetas, trazabilidad de proceso y piloto conectado

La incorporación de 12.1 a 12.3 documenta una necesidad nueva. Los bloques de
reporting 13.3 a 13.5 ya fueron implementados; antes de iniciar la fase 12 se
debe confirmar su prioridad frente a 13.6 y cerrar las decisiones operativas
pendientes.

#### Fase 12.1: catálogo único y generación centralizada de documentos — propuesta

- Warehouse EPI será la fuente de verdad del SKU, descripción, unidad y códigos
  de barras. Los futuros documentos no mantendrán copias independientes de una
  hoja `MASTER LIST`; cualquier importación inicial deberá mostrar diferencias
  y exigir conciliación administrativa antes de sobrescribir datos.
- Sustituir gradualmente los Excel de referencia por tres salidas controladas:
  **Pallet License Plate**, etiqueta de caja **4x6** y **General Process Routing
  Sheet**. Conservar vista previa, impresión manual de respaldo y plantillas
  versionadas; registrar tipo, producto, responsable, fecha, número de copias y
  reimpresiones.
- Preseleccionar `ZXing.Net` `0.16.11` para generar Code 128 en el servidor. Es
  gratuito, usa licencia Apache 2.0, soporta codificación Code 128 y es
  compatible con .NET 5 o superior. Generar una salida local —preferentemente
  SVG a partir de la matriz— sin depender de fuentes instaladas, Excel ni
  Internet. Antes de agregar el paquete, revisar licencia, lockfile, auditoría
  NuGet y compatibilidad con .NET 10. Referencias:
  `https://github.com/micjahn/ZXing.Net` y
  `https://www.nuget.org/packages/ZXing.Net/`.
- La etiqueta debe mostrar también el valor legible, respetar zona silenciosa,
  tamaño mínimo y densidad adecuada para la impresora, y validarse leyendo una
  muestra real. Los Excel actuales mezclan `Libre Barcode 128` y
  `Libre Barcode 39`; la simbología final de cada campo debe quedar explícita.

##### Fase 12.1.1: editor visual de etiquetas — propuesta

- Incorporar una pantalla exclusivamente ADMIN para diseñar etiquetas con una
  experiencia inspirada en Canva o PowerPoint, pero limitada al dominio de
  impresión: lienzo con dimensiones físicas, cuadrícula, guías, zoom, ajuste
  magnético, selección, arrastre, redimensionado, rotación, alineación,
  distribución, orden de capas, duplicado y deshacer/rehacer.
- Permitir texto, imágenes locales autorizadas, líneas, rectángulos y códigos de
  barras, además de campos dinámicos seleccionables —SKU, descripción, código,
  cantidad, unidad, lote, fechas, orden de compra, placa de pallet, proveedor,
  responsable y datos aprobados de producción—. No permitir HTML, JavaScript,
  URLs, fuentes o imágenes remotas ni expresiones arbitrarias.
- Guardar cada diseño como un documento JSON validado por el servidor con
  tamaño, orientación, DPI objetivo, elementos, coordenadas, estilos y enlaces
  a campos permitidos. El navegador no será autoridad de integridad; el servidor
  rechazará tipos desconocidos, coordenadas fuera del lienzo, recursos no
  autorizados y campos incompatibles con la familia de etiqueta.
- Manejar estados **Borrador**, **En validación**, **Publicado** y **Retirado**.
  Solo las versiones publicadas estarán disponibles para operación. Publicar
  crea una versión inmutable; una modificación posterior parte de una copia y
  nunca cambia el diseño histórico usado por impresiones anteriores.
- Ofrecer vista previa con un producto o movimiento real, prueba con SKU corto y
  largo, y advertencias textuales por desbordamiento, texto recortado, contraste,
  zona silenciosa insuficiente, código demasiado denso o elementos fuera del
  área imprimible. El color no será el único indicador.
- Mantener separadas la edición y la operación: ADMIN diseña, valida, publica y
  retira; ADMIN u OPERATOR seleccionan una plantilla publicada, completan los
  datos permitidos y el número de copias. El operador no mueve elementos ni
  altera estilos durante la impresión.
- Reutilizar Razor Pages, Bootstrap, SVG, CSS y JavaScript local. El editor se
  cargará únicamente en su ruta ADMIN y evitará introducir un framework SPA o
  una dependencia de Internet. Debe conservar temas Claro/Oscuro/Sistema,
  teclado, foco visible, objetivos táctiles y `prefers-reduced-motion`.
- El primer cierre será deliberadamente menor que Canva: sin colaboración en
  tiempo real, animaciones, video, efectos fotográficos, complementos, HTML
  libre, scripting, fuentes web ni importación directa de PowerPoint/Canva.
  Importar/exportar formatos externos se evaluará después de validar el editor
  y las impresoras reales.

#### Fase 12.2: `PLT-LICENSE-PLATE` — implementada en código; migración y validación física pendientes

- `/Operations/PalletLabels` busca una Entrada confirmada por UUID o por el
  folio determinista `PLT-{MovementId:N}`. Desde el comprobante de una Entrada
  vigente y el listado ADMIN de **Movimientos** aparece **Generar placa**; el
  listado solo lo ofrece para Entradas vigentes estándar de una línea. No se
  aceptan movimientos de otro tipo, reversos ni originales corregidos; una
  Entrada de reemplazo usa su propio folio.
- La plantilla publicada inicial `PLT-LICENSE-PLATE` mide Carta horizontal
  `11 × 8.5` pulgadas, conserva el logotipo local de Extra Packaging, SKU/Code
  128, descripción, destino, referencia, cantidad, unidad, fecha y responsable.
  El peso es texto libre opcional de hasta 40 caracteres; Received se deriva de
  la Entrada y Counted/Removed quedan como líneas manuales. No se captura origen
  Empaque/Proveedor en esta entrega.
- La placa es documental: no crea una entidad pallet, no persiste impresiones,
  peso ni historial, y no modifica saldos, movimientos, lotes, ubicaciones o
  asignaciones. Reabrirla usa datos de la Entrada, catálogo y plantilla vigente;
  por ello no constituye reimpresión histórica exacta.
- `LabelTemplateKind` separa etiquetas de producto de placas de pallet. El
  generador común solo ofrece las primeras; ADMIN puede ver/versionar la placa.
  El motor admite el recurso integrado controlado `extra-packaging-logo`, nunca
  URLs, rutas ni markup configurables.
- La migración `20260826110000_PalletLicensePlateLabels` agrega el tipo de
  plantilla, el tamaño `11X85_L` y siembra la versión 1 publicada con GUIDs y
  evento deterministas. No se aplicó a `warehouseEPI` ni se publicó una Release.
  Falta revisar SQL, autorizar/aplicar la migración y validar Carta horizontal,
  escala 100 %, márgenes, logotipo, SKU largo y lectura HID en la impresora real.

#### Fase 12.3: ruta de producción de Corte a Bodega — propuesta; alcance pendiente

- Reemplazar la hoja estática por una instancia de ruta asociada a producto y
  orden de trabajo. La referencia actual registra cantidad procesada, fecha,
  turno e iniciales en Corte, Costura, Sellado y Martillado, además de entrega a
  Empaque y recepción en Bodega de Producto Terminado.
- Conservar eventos inmutables por etapa, NIP del responsable, fecha/hora del
  almacén, cantidad recibida/procesada, faltante, observaciones y traspasos. La
  ruta no debe crear movimientos de inventario implícitos; la recepción final
  se vinculará explícitamente con una Entrada o con el contrato de producción
  que se apruebe.
- Definir rutas configurables por producto, porque no se debe asumir que todos
  pasan por las mismas etapas. Quedan por diseñar trabajo parcial, rechazo,
  merma, retrabajo, pausas, cancelación y correcciones auditables.

#### Fase 12.4: validación física y piloto conectado

- Probar lectores, aplicaciones de escáner, tablets reales y cámara si se
  requiere.
- Validar racks, pallets, áreas especiales, rendimiento y operaciones reales en
  red local.
- Confirmar dimensiones, orientación, DPI, márgenes, impresora y número de
  copias de cada plantilla; imprimir y leer Code 128 reales en caja y pallet.
- Validar etiquetas de producto y ubicación con lecturas reales; los lectores
  Bluetooth o USB en modo HID deben funcionar como teclado sin modificar el
  núcleo de inventario.
- Ejecutar un piloto conectado y conciliar inventario físico contra sistema.

### Fase 13: reportes y operación avanzada

- Dividida en 7 subfases incrementales: 13.1 (contrato analítico y movimientos
  efectivos), 13.2 (reportes tabulares y exportación segura), 13.3 (tablero
  diario LAN y gráficos reactivos), 13.4 (analítica de ocupación, actividad de salidas y
  estancamiento),
  13.5 (conteos cíclicos persistentes y ajustes autorizados), 13.6 (alertas
  operativas y croquis interactivo) y 13.7 (refinamiento UX de reportes).

#### Fase 13.1: contrato analítico y movimientos efectivos — completada

- DTOs y modelos inmutables de consulta en `src/WarehouseEPI.Infrastructure/Reporting/ReportingContracts.cs`.
- Helper LINQ centralizado `EffectiveMovementQuery.cs` que excluye automáticamente movimientos originales corregidos (`OriginalMovementId`) y reversos (`ReversalMovementId`), conservando movimientos estándar y reemplazos vigentes (incluyendo cadenas de corrección múltiple).
- Prohibición estricta de suma de unidades heterogéneas: totales globales y gráficos generales se expresan en número de operaciones efectivas, líneas o SKUs distintos. Las cantidades físicas solo se totalizan por SKU, por unidad base homogénea o con desglose tabular por unidad.
- Ocupación física de racks clasificada en 5 estados mutuamente excluyentes (Inactiva, Bloqueada, Negativa, Ocupada > 0, Vacía = 0) con fórmula protegida contra división por cero.
- Actividad de salidas determinista (`EffectiveExitMovementCount DESC, QuantityInBaseUnit DESC, Sku ASC`) y estancamiento en 4 rangos de antigüedad (30-59 días, 60-89 días, 90+ días y sin salida histórica) calculados con `WarehouseClock`.
- Pruebas xUnit de filtros, correcciones encadenadas, anulaciones, ajustes y salvaguardas, más prueba de integración en PostgreSQL real `warehouse_epi_test` para validar la traducción nativa de la consulta —incluida la búsqueda por folio— sin evaluación en memoria.

#### Fase 13.2: reportes tabulares y exportación segura — completada

- `MovementReportService.cs`: Consultas paginadas, ordenadas y filtradas por fecha local/UTC, propósito, tipo, producto, ubicación, responsable, folio y búsqueda. Producto y ubicación aceptan fragmentos sin distinguir mayúsculas; producto cubre SKU, descripción, referencia y códigos de barras, mientras ubicación cubre área, origen, destino y cambios históricos de saldo. Los lotes y sus ubicaciones se reconstruyen desde las instantáneas históricas de `InventoryBalanceChange`, y los ajustes distinguen saldo anterior, diferencia y saldo resultante.
- `ReportExportService.cs`: Exportación a Excel `.xlsx` con ClosedXML (celdas de fecha nativas, cantidades numéricas reales `#,##0.0000`, filtros aplicados y textos forzados a string sin fórmula) y exportación a CSV RFC 4180 con UTF-8 BOM (`0xEF, 0xBB, 0xBF`), metadatos de zona horaria/filtros y defensa contra formula injection (`'`, `=`/`+`/`-`/`@`) aplicada estrictamente a campos de texto sin alterar números negativos. El límite de 10,000 se calcula sobre líneas de detalle y rechaza explícitamente la exportación completa en vez de truncarla silenciosamente.
- Interfaz ADMIN unificada `/Admin/Inventory/Movements` con vista predeterminada de movimientos efectivos vigentes y pestaña de auditoría completa para originales corregidos, reversos y reemplazos. Comparte filtros reproducibles de período, tipo, propósito, producto, ubicación, responsable y búsqueda; cada operación enlaza al detalle existente y conserva el retorno al listado filtrado. La ruta anterior `/Admin/Reports/Movements` redirige por compatibilidad, y `_Layout.cshtml` mantiene una sola entrada **Movimientos** bajo la política `AdminOnly`.
- Las exportaciones Excel/CSV de ambas vistas se generan mediante `ReportExportService`: reproducen población y filtros, limitan 10,000 filas reales, conservan tipos nativos y aplican defensas contra formula injection. Auditoría añade estado de corrección y cambios históricos sin alterar movimientos ni saldos.
- Suite dirigida de consultas, exportaciones, rutas y traducción PostgreSQL aprobada, con compilación en Release sin advertencias ni errores. La validación visual y física en laptop/tablet permanece pendiente.

#### Fase 13.3: tablero diario LAN y gráficos reactivos — implementada; validación física pendiente

- `DailyDashboardService.cs` calcula por fecha local movimientos efectivos y
  ajustes del día, saldos negativos agrupados por producto + ubicación y
  productos activos bajo mínimo. La tendencia conserva 14 días calendario,
  incluidos días sin actividad, y expresa entradas, salidas, transferencias,
  ajustes y SKUs distintos sin sumar cantidades de unidades heterogéneas.
- `/Reports/Dashboard` es una página pública de solo lectura para la LAN, con
  carga inicial renderizada en servidor, cuatro tarjetas y barras apiladas
  nativas que no incorporan dependencias gráficas externas. Negativos y mínimos
  enlazan para todos los usuarios al detalle público de excepciones; movimientos
  y ajustes conservan su detalle únicamente para ADMIN.
- El handler JSON `Metrics` usa `Cache-Control: no-store`; los snapshots
  inmutables se comparten durante 30 segundos en memoria. El cliente consulta
  cada 60 segundos, evita solicitudes superpuestas, pausa cuando la pestaña está
  oculta y conserva el último dato con advertencia explícita si falla una
  actualización. El botón manual invalida únicamente el snapshot del tablero;
  la hora visible pertenece al snapshot realmente generado.
- La mejora visual 13.3.1 conserva exactamente el mismo snapshot y añade eje
  con escala, líneas guía, barras con mayor contraste, selección accesible de
  día, detalle táctil/teclado y vistas locales de 7 o 14 días. También muestra
  operaciones y día de mayor actividad del período visible, sin sumar unidades
  ni agregar dependencias, migraciones o consultas. Sus contratos de página y
  script pasan 3/3; sigue pendiente comprobar la interfaz en laptop LAN y
  tablets reales.
- La gráfica se migró después a Chart.js `4.5.1`, distribuido localmente bajo
  `wwwroot/lib/chart.js` con su licencia MIT. Conserva el canvas interactivo,
  el selector 7/14 y detalle accesible, mientras una tabla renderizada en
  servidor queda disponible si JavaScript o la librería no cargan. No hay CDN,
  nuevas consultas, métricas, migraciones ni paquetes NuGet.
- El acabado operativo de la gráfica incorpora barras con gradiente, mayor
  separación, totales por pila, énfasis visual para hoy/selección y un tooltip
  compacto de alto contraste. Son mejoras de presentación sobre los mismos 14
  puntos y mantienen las actualizaciones posteriores sin animación.
- No se agregaron migraciones ni paquetes. Las 32 pruebas dirigidas de
  Reporting/tablero pasan, incluida la traducción de consultas en PostgreSQL
  real `warehouse_epi_test`; la compilación Release queda sin advertencias ni
  errores. La suite completa queda en 198/217: las 19 fallas siguen siendo los
  HTTP 400/host/antiforgery preexistentes de `WebApplicationFactory`. Falta
  validar visualmente actualización, temas y legibilidad en la laptop LAN y
  tablets reales.
- El alcance propio de 13.3 no mezcla ocupación, actividad por SKU, conteos
  cíclicos ni alertas avanzadas; ocupación y actividad de salidas se
  implementaron después como 13.4.

#### Fase 13.4: analítica de ocupación, actividad de salidas y estancamiento — implementada; validación física pendiente

- `InventoryAnalyticsService.cs` consume saldos existentes y movimientos
  efectivos, sin crear nuevas tablas ni saldos. La ocupación considera
  únicamente posiciones `Rack + Storage`, agrupa lotes por producto + ubicación
  y aplica la precedencia inactiva, bloqueada, negativa, ocupada y vacía. Publica
  métricas globales y por fila; la utilización excluye bloqueadas e inactivas y
  protege la división entre cero.
- La actividad de salidas incluye todos los productos filtrados, aun con cero
  salidas, y admite 30, 90, 180 días o todo el historial. Cuenta salidas
  efectivas distintas, suma cantidades solo dentro del SKU y su unidad base,
  conserva existencia actual y última salida histórica y usa orden determinista;
  no se presenta como tasa de rotación contra inventario promedio. El estancamiento
  exige existencia positiva y clasifica 30–59, 60–89, 90+ y nunca salió con
  fechas locales del almacén.
- `/Reports/Inventory` es pública dentro de la LAN, de solo lectura y separada
  de `/Inventory`, Alertas, Ubicaciones y el croquis. Ofrece pestañas GET,
  búsqueda parcial por SKU/descripción/referencia/código de barras, estado
  activo/inactivo/todos, unidad, período y páginas de 25 productos ejecutadas en
  PostgreSQL antes de materializar. Las lecturas se comparten en memoria por 60
  segundos, la clave conserva los filtros, la hora pertenece al snapshot y
  `refresh=true` invalida solo la consulta actual.
- La pestaña pública de excepciones reutiliza `InventoryQueryService`: lista
  saldos negativos por producto + ubicación y productos bajo mínimo, con enlaces
  de solo lectura hacia `/Inventory`. Las acciones de catálogo permanecen ADMIN.
- Actividad de salidas y estancamiento se exportan únicamente con sesión ADMIN a
  CSV RFC 4180 con UTF-8 BOM o XLSX con números y fechas nativos. El handler
  rechaza también llamadas directas sin el rol. Ambas exportaciones conservan
  filtros, neutralizan fórmulas y rechazan el archivo completo si supera 10,000
  productos; ocupación y excepciones no se exportan.
- Las pruebas dirigidas cubren estados y precedencia de ocupación, agrupación de
  lotes, filtros, paginación en servidor, código de barras, ventanas, cero
  salidas, correcciones encadenadas, caché y refresco manual, hora real del
  snapshot, rutas públicas de excepciones, autorización directa de exportación,
  límites de estancamiento, límite de exportación, CSV/XLSX, contratos públicos
  y enlaces ADMIN. La consulta se valida además contra PostgreSQL real
  `warehouse_epi_test`: Reporting y los contratos web dirigidos pasan 43/43.
  La compilación Release queda sin advertencias ni errores, `node --check` y
  `git diff --check` pasan, y la suite completa queda en 223/242; sus 19 fallas
  continúan siendo los HTTP 400/host/antiforgery preexistentes de
  `WebApplicationFactory`. La compuerta global de `dotnet format whitespace`
  sigue bloqueada por formato, finales de línea y codificación preexistentes en
  conteos cíclicos y migraciones fuera de esta entrega. Falta comprobar
  visualmente la interfaz, los filtros y las descargas en la laptop LAN y
  tablets reales; la aplicación no se inició durante esta implementación.
- La consulta pública de Existencias ya existente no se reimplementó y no se
  añadieron migraciones ni paquetes. Después se implementó 13.5, conteos
  cíclicos persistentes y ajustes autorizados.

#### Fase 13.5: conteos cíclicos persistentes y ajustes autorizados — implementada; migración y validación física pendientes

- Se agregó programación recurrente por `SKU + ubicación`: frecuencias semanal, quincenal, mensual, trimestral, semestral y anual, calculadas desde una fecha ancla con `WarehouseClock`. El calendario público muestra fecha, SKU, ubicación y estado, pero no existencia esperada; la cantidad se captura ciegamente al contar.
- `CycleCountScheduling` agrega los planes y su alcance selectivo dentro de campañas liberadas desde calendario. Una campaña manual conserva el alcance completo actual por ubicación. La migración está generada y revisada en código, pero no aplicada a PostgreSQL ni publicada; siguen pendientes pruebas LAN/tablet/HID/cámara.
- Se incorporó el modelo persistente de campañas `CC-000001`, ubicaciones,
  intentos/reconteos, líneas por producto + ubicación y acciones de auditoría.
  Las campañas y ubicaciones conservan estados explícitos; una ubicación no
  puede pertenecer a dos campañas abiertas y los intentos anteriores nunca se
  sobrescriben.
- `CycleCountService` permite seleccionar posiciones físicas activas por fila,
  rack o ubicación, liberar y cancelar campañas, iniciar conteos ciegos,
  registrar cero/ubicación vacía y productos inesperados, solicitar reconteo y
  autorizar diferencias con NIP de ADMIN u OPERATOR. Los productos conocidos
  proceden de asignaciones activas o de cualquier saldo distinto de cero.
- El saldo esperado y su versión agregada se capturan al iniciar. Se comparan
  antes de enviar y antes de aprobar; cualquier movimiento concurrente marca la
  posición `Stale` y obliga a iniciar un nuevo intento. Las coincidencias se
  concilian sin movimiento. Una aprobación genera atómicamente un solo
  `Adjustment` con propósito `CycleCountAdjustment`, folio de campaña y enlace
  desde la ubicación contada; no se bloquean ubicaciones.
- `/Operations/CycleCounts` y sus páginas Create, Details, Count, Review, Print
  y Export son públicas dentro de la LAN. Todos los cambios usan antiforgery y
  solicitan NIP; el NIP no se persiste ni se devuelve. La hoja HTML imprimible
  permanece ciega y los resultados se exportan a CSV UTF-8 BOM o XLSX con
  números/fechas nativos, defensa contra fórmulas y rechazo completo por encima
  de 10,000 líneas.
- La migración `Phase135CycleCounts` crea las cinco tablas y amplía el propósito
  de movimientos sin agregar paquetes ni saldos paralelos. Se deja generada y
  pendiente de aplicación deliberadamente; esta implementación no inicia ni
  publica la aplicación.
- Las pruebas automatizadas cubren conciliación sin ajuste, aprobación de
  diferencias, concurrencia `Stale`, reconteo inmutable, campañas superpuestas,
  exclusión WIP, idempotencia del envío, cantidades inválidas y contratos web
  de acceso, ceguera, impresión, navegación y exportación. Sigue pendiente la
  validación física en laptop LAN, tablets, lector HID/cámara e impresora, así
  como aplicar/auditar la migración en la base de producción mediante el flujo
  de respaldo habitual. La compilación Release queda en 0 advertencias y 0
  errores; las 15 pruebas dirigidas de conteos/exportación pasan y la suite
  completa queda en 218/237. Las mismas 19 fallas HTTP 400/antiforgery del host
  `WebApplicationFactory` permanecen como línea base preexistente. El siguiente
  bloque es 13.6, alertas operativas.

#### Fase 13.6: alertas operativas, notificaciones y análisis contextual — implementada en código; despliegue pendiente

- `OperationalAlertService` calcula snapshots de solo lectura con `AsNoTracking`,
  movimientos efectivos, `WarehouseClock`, hora controlada por `TimeProvider`,
  orden estable y páginas de 25 filtradas/paginadas en PostgreSQL. La audiencia
  pública contiene solamente negativos y mínimos; ADMIN añade saldos sin
  asignación, inventario restringido, estancamiento de 90 días, conteos
  obsoletos/pendientes y recordatorios WIP sin devolución efectiva.
- `GET /Reports/Notifications?handler=Snapshot` deriva la audiencia de la
  identidad, usa caché de 30 segundos separada por audiencia, permite refresco
  selectivo y devuelve `Cache-Control: no-store` y `Vary: Cookie`. No existen
  entidades, POST, leído/reconocido/asignado/cerrado ni mutación operacional.
- El layout ofrece dos disparadores para un solo `offcanvas`: campana móvil bajo
  900 px y campana del sidebar en escritorio/rail, badge `99+`, cero oculto y
  navegación autorizada. `operational-notifications.js` consulta al cargar sin
  anunciar, repite cada 60 segundos, pausa en pestaña oculta, evita solicitudes
  simultáneas, conserva el último snapshot ante error y anuncia por `aria-live`
  únicamente aumentos posteriores. No usa `innerHTML`, sonido, toast,
  `localStorage`, SignalR, CDN ni canales externos.
- `/Admin/Inventory/Alerts` conserva `AdminOnly` y ofrece las vistas `negative`,
  `minimum`, `unassigned`, `restricted`, `stagnant`, `cycle` y `wip`, búsqueda,
  estados vacíos, paginación y enlaces a ficha de producto/ubicación, croquis,
  consulta pública, conteos, WIP y movimientos efectivos filtrados. El croquis
  abre el elemento y posición de `highlightLocationId`; si no está dibujada se
  conserva el acceso a la ficha.
- `BusinessSettings.WipReminderDays` queda en 7 por defecto, validado entre 1 y
  365 en ADMIN, servicio de configuración, modelo EF y migración
  `20260825190000_Phase136OperationalAlerts`. La migración declara explícitamente
  su identidad EF, agrega el `CHECK` y tiene script de bajada probado. Debe
  aplicarse después de `20260821120408_Phase135CycleCounts`.
- El tablero compara hoy/ayer y siete días contra los siete anteriores para
  operaciones, ajustes y SKUs distintos. Incluye hasta cinco variaciones de
  productos, filas y ubicaciones, contando operaciones efectivas distintas,
  mostrando `Nuevo`/`Sin actividad`, incluyendo caídas a cero y enlazando ADMIN
  a movimientos efectivos; se etiqueta como variación de actividad, no
  causalidad ni rotación. El polling también actualiza estas comparaciones.
- Verificación de código al 25 de agosto de 2026: compilación Release aislada en
  0 advertencias/0 errores; 38 pruebas Reporting sin PostgreSQL, 60 pruebas
  Inventory/Web dirigidas y 2 pruebas PostgreSQL específicas de 13.6 pasan;
  sintaxis de los tres JavaScript modificados y `git diff --check` pasan. La
  ejecución amplia combinada queda en 158/197 por la línea base HTTP 400/
  antiforgery y por fixtures PostgreSQL concurrentes que reinician la misma
  `warehouse_epi_test`; las pruebas PostgreSQL 13.6 pasan aisladas.
- Estado operacional honesto: la migración se aplicó sólo a la base efímera de
  pruebas. No se aplicaron 13.5/13.6 a producción, no se publicó ni activó una
  Release, no se inició/detuvo el servicio y no se realizó validación visual en
  navegador LAN ni en tablet/dispositivo físico. Antes de producción se debe
  revisar SQL, respaldar, validar restauración, autorizar y aplicar primero 13.5,
  luego 13.6, publicar la Release y ejecutar la aceptación LAN/tablet.

#### Fase 13.6.1: Centro de excepciones accionable — migración aplicada; Release y validación pendientes

- `OperationalExceptionCase` conserva una ocurrencia por condición activa con categoría,
  severidad derivada, clave estable, sujeto, texto, enlace contextual, responsable,
  fechas y concurrencia PostgreSQL `xmin`. Los estados son `New`, `InProgress`,
  `Waiting` y `Resolved`; un caso sólo se resuelve automáticamente. `OperationalExceptionEvent`
  registra de forma inmutable detección, seguimiento ADMIN y resolución automática.
- `OperationalAlertService` sigue siendo lectura y alimenta ocho condiciones materializadas:
  negativo, mínimo, sin asignación, restringido, estancado, conteos obsoleto/pendiente
  y WIP estancado. El reconciliador no escribe si alguna consulta falla, inicia al
  arrancar y cada cinco minutos, se excluye en proceso, y puede ejecutarse con el POST
  ADMIN `Actualizar condiciones`. Una condición resuelta que reaparece crea una nueva
  ocurrencia; no reabre el histórico. Campana, audiencia pública, caché de 30 segundos
  y sondeo de 60 segundos permanecen derivados y sin mutación.
- `/Admin/Inventory/Alerts` conserva el enlace heredado como Centro de excepciones:
  muestra abiertos por defecto, 25 por página, filtros GET, indicadores, detalle,
  historial, asignación y cambios de seguimiento. Sólo `AdminOnly` modifica el caso,
  sin NIP; exige antiforgery, `NameIdentifier`, idempotencia y nota para `Waiting`.
  Sólo se puede asignar a usuarios activos, sin borrar responsables históricos.
- Los enlaces contextuales únicamente precargan los flujos existentes; ajuste,
  transferencia, asignación, ubicación, salida, conteo y WIP mantienen sus propias
  validaciones, NIP, concurrencia e idempotencia. El centro nunca cambia inventario.
- La ficha de seguimiento fue enriquecida en código para explicar las ocho categorías,
  mostrar motivo, acción recomendada, responsable, vigencia en hora local del almacén
  e historial descendente con transiciones. `ReasonText` se actualiza sin crear eventos
  periódicos y se conserva al resolverse; la migración
  `20260904144551_EnrichOperationalExceptionContext` agrega el contexto con backfill,
  pero aún no se ha aplicado a la base operativa ni se ha publicado en una Release.
- La migración `20260828143458_AddOperationalExceptionCenter`, posterior a
  `20260828120000_WipTrackedInventory`, crea tablas, FKs, checks, índices de filtros
  y la unicidad parcial de caso activo. Ya fue aplicada a la base operativa; todavía
  no se ha publicado una Release con el módulo. Permanecen pendientes la publicación,
  verificación LAN y prueba física en tablet.

#### Recepciones contra documento y trazabilidad unificada — navegación pospuesta

- Los módulos `/Operations/Receiving` y `/Admin/Inventory/Trace` permanecen en el
  código para revisión técnica, pero no se usarán todavía en la operación diaria.
- Sus enlaces **Recepciones** y **Trazabilidad** están comentados con comentarios
  Razor en `_Layout.cshtml`, por lo que no se renderizan en el sidebar, rail ni
  drawer. Este cambio sólo oculta la navegación: no elimina páginas, rutas, datos,
  permisos ni contratos operativos existentes.
- Para activarlos posteriormente se debe aprobar el uso operativo, retirar ambos
  comentarios Razor, ejecutar las pruebas focales y validar la navegación en laptop
  y tablet antes de incluirlos en una Release.

#### Fase 13.7: refinamiento UX de reportes — implementada en código; validación visual y física pendiente

- `/Admin/Inventory/Movements` conserva consultas GET, sólo lectura, filtros y límite de 10,000 filas. Acepta `period=custom` cuando recibe `from` o `to`; centraliza las rutas con valores invariantes, conserva sólo `movementType`, elimina el estado al volver a Vigentes y muestra filtros avanzados/chips removibles sin perder la reproducción del enlace.
- Movimientos y WIP usan tarjetas responsivas sin JavaScript por debajo de 1200 px, con encabezados de tabla accesibles, rótulos `data-label`, estados vacíos y restauración de la semántica tabular al imprimir. WIP muestra folio corto, enlace al detalle, copia segura del GUID completo y paginación acotada.
- El tablero añade para ADMIN un enlace trazable al día visible; tanto la carga Razor como hover, teclado y refresco automático mantienen el intervalo personalizado del día seleccionado. El enlace no se renderiza para la audiencia no ADMIN.
- Excepciones reutiliza consultas tipadas de `InventoryQueryService` para pantalla y exportación de saldos negativos por producto-ubicación y productos bajo mínimo. CSV/XLSX ADMIN conserva números nativos, BOM UTF-8, RFC 4180, defensa contra fórmulas, zona horaria, filtro y rechazo completo de 1 a 50,000 filas con el sustantivo de población correspondiente. La impresión muestra metadatos de excepción y omite controles interactivos.
- Código verificado el 27 de agosto de 2026: `dotnet test` Release dirigido aprobó 25 pruebas de rutas, dashboard, analítica y consultas; `node --check` aprobó `site.js` y `daily-dashboard.js`; `git diff --check` no reportó errores. La compilación Release también se ejercitó por esas pruebas dirigidas.
- No se inició la aplicación, no se simuló navegador y no se hizo validación visual a 1024×768/800×1280, impresión real ni prueba física en tablet Android. Tampoco se ejecutaron `quality.ps1`, PostgreSQL aislado ni la suite completa en esta pasada; esos diagnósticos y la validación LAN siguen pendientes antes de declarar cierre físico.

#### Fase 13.8: correcciones críticas de conteo cíclico, CSP y carga de scripts — implementada; validación física pendiente

- `SubmitPreparedAsync` valida antes de escribir: cantidades, unidad base y productos
  inesperados se comprueban con el intento aún sin crear, y sólo entonces se abre la
  transacción (`IsRelational()`) que agrupa el alta del intento y `SubmitAsync`. Antes, un
  envío inválido dejaba el intento persistido y la ubicación en `Counting`, de modo que el
  reintento devolvía `InvalidState` y la pantalla mostraba una alerta vacía. Ahora un rechazo
  no escribe nada, la ubicación permanece en `Pending` y el mismo token preparado se reenvía.
- La validación de decimales por unidad base, que sólo cubría los productos inesperados,
  se aplica también a las líneas preparadas mediante un validador compartido con la ruta
  legada `StartAttemptAsync` + `SubmitAsync`.
- `Count` captura la cantidad como `decimal?` y comprueba `ModelState`: un campo vacío ya no
  se enlaza como cero. El error nombra los SKU afectados, marca cada casilla y conserva la
  captura y el token de preparación al re-renderizar. El NIP nunca se re-renderiza.
- `Count`, `Review` y `BatchReview` conservan su `OperationId` entre renders; regenerarlo
  rompía la idempotencia de envíos y autorizaciones. `CycleCountPresentation.StatusMessage`
  centraliza los mensajes e incluye `InvalidState`, `NotFound` e `IdempotencyConflict`, que
  antes producían una alerta sin texto.
- La hoja ciega usaba `onclick="window.print()"`, bloqueado por `script-src 'self'`: el botón
  no hacía nada. El handler vive en `cycle-count.js` y una prueba nueva recorre todos los
  `.cshtml` de `Pages` buscando handlers en atributo o `javascript:`.
- El layout dejó de cargar ZXing (395 KB), jQuery (88 KB) y `operations.js` (56 KB) en todas
  las páginas: jQuery viaja con `_ValidationScriptsPartial` y los otros dos con las cuatro
  estaciones y Existencias; los conteos cargan ZXing con `cycle-count.js` y la hoja de
  impresión sólo el script. Reportes, administración, etiquetas y croquis dejan de descargar
  ~539 KB sin comprimir.
- Los productos inesperados admiten agregar y quitar líneas renumerando los índices del
  enlace de modelo, y la captura guarda un borrador local por campaña+ubicación con
  caducidad de 12 horas, restauración explícita y sin NIP ni token de preparación.
- Se eliminó `Pages/Operations/_MovementForm.cshtml`, sin referencias desde ninguna página, y
  la rama `[data-inventory-query]` de `operations.js`, sin marcado que la activara.
- No hay cambios de esquema ni migraciones nuevas. Suite completa: 327/372. Los 45 fallos
  son previos y ajenos a este trabajo: 21 por los HTTP 400 de `WebApplicationFactory` en esta
  laptop y 24 por la migración pendiente de `CycleCountBatchReview` que ya estaba en el árbol
  de trabajo. Se comprobó contra `HEAD` (22daf39): mismos 21 fallos HTTP y ninguna prueba que
  pasara antes falla ahora.
- Falta validación física: hoja ciega impresa, captura y borrador en tablet Android, lector
  HID y comprobación en DevTools de que las páginas sin captura ya no descargan las
  bibliotecas pesadas.

#### Croquis adaptable de consulta — 17 de septiembre de 2026

- Consulta pública y ADMIN: mapa a todo el ancho sin selección; al abrir un rack o
  área, columna de detalle de 22 rem desde 1200 px y panel inferior de hasta 48dvh
  en pantallas menores. La vista normal mantiene una altura estable entre 34 y
  46 rem (`70svh`); sólo la vista ampliada calcula el espacio visible. El encabezado
  del detalle mantiene accesible su cierre.
- `Ver todo` ajusta ancho y alto del lienzo; `Acercar selección` centra el elemento
  y busca una escala legible. La búsqueda abre y acerca la coincidencia sin quitar
  otros elementos. Botones, pellizco y desplazamiento comparten la misma cámara;
  al redimensionar se conserva el ajuste completo o la escala y centro de consulta,
  dentro de los límites del lienzo. El panel inferior se descuenta del área visible.
- El scroll de la página no recalcula la cámara: conserva altura, escala, tamaño
  renderizado y centro en vista general, zoom, selección, búsqueda y `Mi ubicación`.
  Los recálculos quedan limitados a resize/orientación, cambios del panel y vista ampliada.
- `Ampliar croquis` ocupa la ventana sin Fullscreen API. El fondo queda inerte para
  teclado; Escape cierra primero el detalle y luego la vista ampliada, restaurando
  foco y desplazamiento. El estado no persiste tras recargar.
- Sin cambios al editor, geometría guardada, APIs, inventario ni migraciones.
  Implementado en `_LocationIndex.cshtml`, `warehouse-map-query.js` y CSS acotado
  a la consulta. No se inició ni se publicó el servicio.
- Verificación de la corrección de scroll: 12/12 pruebas de cámara y 2/2 de
  geolocalización JavaScript; compilación .NET correcta y 20/20 contratos focales.
- Chrome con HTML generado por WebApplicationFactory y datos en memoria: revisados
  1366×768, 1024×768 y 768×1024, claro/oscuro, ajuste, selección, vista ampliada,
  teclado/Escape, búsqueda, WIP y mapa de calor. Es validación de navegador con
  datos de prueba, no aceptación de la Release ni de tablet física; ambas pendientes.

#### Fase 13.9: consolidación de reportes y mapa de calor integrado — implementada en código; validación visual y física pendiente

- **Mapa de calor** es un modo del croquis compartido de Ubicaciones pública y ADMIN.
  Reutiliza el mismo SVG, geometría, selección, zoom, desplazamiento y paneles; permite
  alternar entre ocupación actual y actividad de 7, 14, 30 días o intervalo personalizado.
  La escala usa todos los racks del croquis y no cambia por búsqueda o paginación. Los
  racks sin coordenadas permanecen en el detalle tabular.
- Ocupación agrega primero por producto y posición, considera ocupada una posición si
  existe algún saldo neto positivo y muestra negativos y bloqueos como indicadores
  independientes. Actividad cuenta movimientos efectivos distintos por rack; una
  transferencia que toca dos racks cuenta una vez en cada uno. Un error de consulta se
  muestra como error y nunca como valor cero.
- La ruta anterior `/Reports/Heatmap` es únicamente un adaptador: redirige al croquis
  público o administrativo según el rol y conserva métrica, período, fechas, fila y
  búsqueda. Sus descargas permanecen compatibles y protegidas para ADMIN; la interfaz
  duplicada y el acceso separado se retiraron. Kárdex continúa separado de Existencias
  y conserva la ubicación de contexto cuando se abre desde el detalle de inventario.
- Cobertura reconoce surtimientos y retornos WIP actuales como transferencias, conserva
  los registros históricos válidos y clasifica sin redondear: crítico menor de 7 días,
  bajo de 7 a 14, normal mayor de 14 a 45 y exceso mayor de 45. Antigüedad usa la fecha
  local de creación del lote; `LotDate` permanece informativa y el último intervalo se
  denomina **Más de 90 días**. Se conservan las categorías funcionales de estancamiento.
- Capacidad ejecutiva agrega por producto y ubicación antes de evaluar existencia positiva
  y negativos. Kárdex desglosa lotes históricos, reconstruye la apertura de todo el
  historial y mantiene la misma granularidad en pantalla y exportación. Los períodos
  predefinidos prevalecen sobre fechas anteriores; `custom` usa las fechas y `all` elimina
  ambos límites.
- Excel evita fórmulas sobre rangos vacíos; CSV escapa una vez cada campo de metadatos y
  conserva BOM, defensa contra fórmulas y números nativos. La impresión ejecutiva usa un
  listener externo compatible con CSP.
- No se agregaron migraciones, dependencias ni escrituras de inventario. No se inició,
  detuvo ni desplegó el servicio operativo. La comprobación automatizada, PostgreSQL
  aislado, navegador y dispositivos físicos se registran por separado en cada entrega.
- Verificación del 9 de septiembre de 2026: la solución compiló con 0 advertencias y
  0 errores; aprobaron 79 pruebas de Reporting, incluidas las consultas relacionales
  contra `warehouse_epi_test`, y 66 pruebas focales de cálculos, rutas, Ubicaciones,
  exportación y CSP. `node --check` aprobó los dos scripts modificados y
  `git diff --check` no encontró errores de espacios.
- El servicio `WarehouseEPI` estaba detenido y no había un listener de desarrollo, por
  lo que se respetó el límite de no iniciarlo. Quedan pendientes la validación en
  navegador de ambos temas, teclado, tablet, zoom, selección y diálogo de impresión,
  además de las pruebas físicas HID, cámara e impresora.

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
- formato físico final de las etiquetas de ubicación;
- impresoras de etiquetas disponibles, lenguaje admitido —impresión del
  navegador, ZPL u otro—, DPI, orientación, márgenes y cantidad de copias;
- campos y simbología definitivos de la etiqueta 4x6 y la placa de pallet,
  incluida la necesidad de codificar cantidad, lote, fecha u otros datos;
- si la placa identifica un pallet rastreable o solamente documenta una
  recepción, y cuándo se asigna al recibir de Empaque o de proveedor;
- número/formato de orden de trabajo, rutas por producto, etapas obligatorias y
  tratamiento de parciales, rechazo, merma y retrabajo en producción;
- tamaño máximo de imágenes locales, fuentes permitidas, DPI de diseño,
  tolerancia de sangrado/márgenes y si se permitirá crear tamaños personalizados
  además de 6x4, 4x6, 3x1 y 4x4.5;
- si la publicación de una plantilla requerirá solamente NIP ADMIN o también
  una segunda aprobación y una muestra física escaneada.

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

El 20 de agosto de 2026 se revisaron como referencias funcionales, sin
modificarlos ni copiarlos al repositorio:

- `C:\Users\JUANANTONIOCASTILLAO\Documents\PALLET LICENSE PLATE.xlsx`;
- `C:\Users\JUANANTONIOCASTILLAO\Documents\4X6 LABELS 2026.xlsx`;
- `C:\Users\JUANANTONIOCASTILLAO\Documents\General Process Routing Sheet.xlsx`.

Los tres contienen su propia hoja `MASTER LIST`, pero no representan una misma
copia confiable: Pallet contiene 1,527 filas con ITEM, 4x6 contiene 1,646 y
Routing contiene 1,638. Después de normalizar espacios y mayúsculas, 1,506 ITEMS
aparecen en los tres; existen elementos exclusivos, duplicados y al menos seis
SKU compartidos con diferencias de descripción o unidad, incluido un `#REF!`.
No se debe importar ninguna de estas listas como autoridad ni sincronizarlas
entre sí; deben conciliarse contra el catálogo vigente de Warehouse EPI.

`4X6 LABELS 2026.xlsx` usa plantillas 4x6/6x4, búsqueda mediante `XLOOKUP` y
fuentes `Libre Barcode 128` y `Libre Barcode 39`; además contiene una prueba QR
que depende de un servicio web externo. `PALLET LICENSE PLATE.xlsx` captura
producto, descripción, peso, fecha, cantidad, responsable y marcas
Received/Counted/Removed. `General Process Routing Sheet.xlsx` registra una
orden y el paso por Corte, Costura, Sellado, Martillado, Empaque y Bodega. Estos
campos son evidencia del proceso actual, no contratos definitivos; deben
confirmarse con los responsables antes de modelarlos.

El layout recibido el 14 de agosto de 2026 fija la nomenclatura de rack como
`Fila-Rack-Pallet`, por ejemplo `A-1-8`. Cada rack tiene normalmente nueve
posiciones distribuidas como un teclado numérico: `1,2,3` abajo; `4,5,6` en
medio; y `7,8,9` arriba. Las áreas que no son racks conservarán códigos propios.
Ya se realizó una carga inicial de 153 ubicaciones. Sigue pendiente validar en
sitio que cubra las posiciones existentes, excepciones y sentido de numeración,
además de confirmar el significado de los colores del croquis.

## 12. Catálogo de Labels / Etiquetas y estrategia de implementación

Esta sección convierte las hojas revisadas en un inventario funcional. La
lectura estructural cubrió las 30 hojas, incluidas las ocultas, sus fórmulas,
fuentes, rangos de impresión e imágenes. **Todavía falta comparar visualmente en
Excel y mediante muestras impresas cada variante antes de declarar idéntico su
diseño**; por eso las equivalencias siguientes son propuestas y no contratos
finales de presentación.

### 12.1 Clasificación de todas las hojas actuales

| Libro y hojas | Clasificación | Destino propuesto en Warehouse EPI |
| --- | --- | --- |
| Pallet: `MANUAL INPUT` | Formato manual de placa | Conservar como respaldo dentro de `PLT-LICENSE-PLATE`, sin lista maestra propia. |
| Pallet: `SEARCH BY DESCRIPTION (2)` y `SEARCH BY DESCRIPTION` | Selector por descripción y variantes de impresión | Sustituir por un solo buscador del catálogo; la hoja oculta/multipágina no será otra plantilla hasta confirmar diferencias visuales. |
| Pallet: `SEARCH BY ITEM PART # ROLLS (2)` y `SEARCH BY ITEM PART # ROLLS` | Placa para producto medido en rollos | Usar `PLT-LICENSE-PLATE` con unidad y campos de rollo derivados del producto/recepción. |
| Pallet: `SEARCH BY ITEM PART # (2)` y `SEARCH BY ITEM PART #` | Selector por ITEM y variantes de impresión | Sustituir por el mismo buscador; consolidar las copias visibles/ocultas después de la revisión visual. |
| Pallet: `Inventory Count Sheet` | Tarjetas repetidas de Received/Counted/Removed | No es una etiqueta de producto. Mover su necesidad a conteos cíclicos de 13.5 y conservar impresión solo si el piloto la requiere. |
| Pallet: `MASTER LIST` | Catálogo duplicado | Eliminar como fuente; leer productos, unidades y códigos desde Warehouse EPI. |
| 4x6: `MASTER LIST` | Catálogo duplicado | Eliminar como fuente; no sincronizar otra copia. |
| 4x6: `4X6 SEARCH BY DESCRIPTION` y `4X6 SEARCH BY ITEM ` | Etiqueta estándar buscada por descripción o ITEM | Unificar como `LBL-4X6-STANDARD`; la forma de buscar no crea formatos distintos. |
| 4x6: `6X4 ZEBRA` | Etiqueta principal para impresora Zebra | `LBL-6X4-ZEBRA`: ITEM/Code 128, descripción, cantidad/unidad, fecha de fabricación y marca Repack. |
| 4x6: `3X1 SEARCH BY ITEM` | Etiqueta compacta | `LBL-3X1-COMPACT`; confirmar impresora, DPI y si realmente necesita descripción. |
| 4x6: `6X4 PARTIAL (2)` y `6X4 PARTIAL` | Producto o cantidad parcial | Unificar como `LBL-6X4-PARTIAL`; registrar que es parcial como dato, no como texto libre en una copia distinta. |
| 4x6: `SPOUTED BAGS 6X4` | Etiqueta especial de bolsas con boquilla | `LBL-6X4-SPOUTED`: Corte, Costura, Inspector, Empaque, largo, alto, ancho, boquillas y MFD. |
| 4x6: `BERMS 6X4` | Etiqueta especial de bermas | `LBL-6X4-BERM`: Corte, Soldadura, Inspector, Empaque, largo, ancho, alto, ranuras de soporte y MFD. |
| 4x6: `RAINCAPS 6X4` | Etiqueta especial de raincaps | `LBL-6X4-RAINCAP`: Corte, Costura, Inspector, Empaque, largo, ancho y MFD. |
| 4x6: `CUSTOM 24DIA SPOUTED BAG` | Variante especial con dos boquillas de 24 pulgadas | `LBL-6X4-CUSTOM-SPOUT`; modelar diámetro/cantidad como datos configurables, no fijarlos en la plantilla general. |
| 4x6: `6X4 FOR MOBILE PRINTER` | Variante compacta para impresora móvil | `LBL-6X4-MOBILE`; mantenerla separada solo si la impresora física exige otro lenguaje, DPI o área imprimible. |
| 4x6: `6X4 FOR MOBILE PRINTER BRENDA` | Copia personal/experimental | No migrar como formato definitivo hasta comparar visualmente y confirmar una diferencia operativa real. |
| 4x6: `4x4.5 FOR RECEIVING` | Etiqueta de recepción de rollos | `LBL-4X45-RECEIVING`: ITEM/Code 128, fecha recibida, número de rollo, yardas y orden de compra. |
| 4x6: `TEST` | Prueba | Excluir de producción. |
| 4x6: `QR` | Experimento QR que consulta un servicio web externo | Excluir del primer alcance. Si se aprueba QR, generarlo localmente con un contrato explícito y sin dependencia de Internet. |
| Routing: `Template` | Documento de ruta de proceso | `DOC-PROCESS-ROUTING`: orden, producto, cantidad, etapas, turno, responsable, fechas, observaciones y entregas a Empaque/Bodega. Contiene bloques adicionales fuera del área de impresión declarada que requieren revisión visual. |
| Routing: `SEARCH BY DESCRIPTION`, `SEARCH BY ITEM PART # ROLLS` y `SEARCH BY ITEM PART #` | Hojas auxiliares heredadas de búsqueda/placa | No son formatos de routing; sustituir por el buscador común del catálogo. |
| Routing: `MASTER LIST` | Catálogo duplicado | Eliminar como fuente. |

### 12.2 Campos comunes y contratos por familia

- **Comunes a toda etiqueta:** código estable de plantilla, versión, producto,
  SKU/ITEM, descripción tomada como snapshot, código de barras y valor legible,
  tamaño, orientación, fecha/hora local, responsable y número de copias.
- **Caja estándar:** cantidad, unidad, fecha de fabricación y condición Repack.
- **Placa de pallet:** identificador único de placa, origen Empaque/Proveedor,
  movimiento de Entrada relacionado, cantidad, unidad, peso cuando aplique y
  estado documental. Received/Counted/Removed no deben sustituir los eventos
  reales del sistema.
- **Recepción de rollos:** número de rollo, yardas, orden de compra y fecha de
  recepción. Definir si número de rollo es dato del proveedor, identificador
  interno o ambos.
- **Producción especial:** dimensiones y campos de Corte, Costura, Soldadura,
  Inspector y Empaque. Estos valores deben venir de una ruta/orden o captura
  validada, no quedar embebidos en el diseño.
- **Routing:** orden de trabajo y eventos de proceso; su impresión es una salida
  secundaria. La trazabilidad vive en PostgreSQL, no únicamente en el papel.

### 12.3 Arquitectura propuesta

1. **Catálogo único.** La selección consulta los productos y códigos existentes
   mediante el servicio de catálogo; se retiran `XLOOKUP` y las tres copias de
   `MASTER LIST`.
2. **Registro y edición de formatos.** El motor central guarda códigos estables,
   versiones y documentos `LabelDesignDocumentV1` validados. El editor ADMIN
   trabaja en milésimas de pulgada sobre cuatro tamaños registrados; admite
   texto, campos, Code 128, imágenes locales, líneas y rectángulos, sin HTML,
   fórmulas, scripts, URLs ni medidas libres. El ciclo es Borrador → En
   validación → Publicado → Retirado; una versión publicada es inmutable y para
   modificarla se duplica como el siguiente borrador.
3. **Código de barras local.** Un `BarcodeRenderingService` encapsula
   `ZXing.Net`, valida el contenido y genera Code 128 como SVG con zona
   silenciosa y texto legible. No depender de `Libre Barcode 128/39`, Excel o
   servicios externos.
4. **Documento imprimible.** Un `LabelDocumentService` recibe una solicitud
   tipada y crea una vista previa exacta. El primer adaptador será HTML/SVG con
   CSS de impresión; un adaptador ZPL se agregará solamente si las impresoras
   confirmadas lo requieren.
5. **Auditoría administrativa.** Se persisten eventos inmutables de creación,
   envío a validación, publicación, duplicado y retiro. Esta fase no almacena
   etiquetas generadas, snapshots, responsables ni eventos de impresión; la
   reimpresión exacta queda como ampliación futura.
6. **Seguridad.** ADMIN administra formatos y publica con su sesión, sin NIP.
   Retirar la versión vigente sí exige motivo y NIP de un ADMIN activo. El
   generador operativo es público dentro de la LAN y previsualiza sin NIP; una
   impresión nunca aplica inventario.
7. **Integraciones.** Entrada desde Empaque/Proveedor puede ofrecer la placa
   después de confirmar el movimiento. Routing genera eventos propios y solo
   crea/vincula una Entrada final mediante un contrato explícito e idempotente.

### 12.4 Entregas incrementales y validación

1. Prototipo `LBL-6X4-ZEBRA` con un SKU corto y uno largo; generar Code 128,
   imprimir y volver a decodificar el valor.
2. Implementar el editor visual mínimo sobre la plantilla 6x4 ya validada:
   texto, campos dinámicos, Code 128, imagen local, formas básicas, arrastre,
   tamaño, alineación, capas, deshacer/rehacer y ciclo
   Borrador/En validación/Publicado/Retirado.
3. Implementar `PLT-LICENSE-PLATE` en modo **label-only**, ligado a una Entrada,
   mientras no se apruebe inventario por pallet.
4. Incorporar `LBL-4X45-RECEIVING` y después las variantes parcial/móvil.
5. Modelar datos de producción antes de migrar Spouted Bags, Berms, Raincaps y
   Custom Spout; evitar formularios aislados que vuelvan a duplicar información.
6. Implementar `DOC-PROCESS-ROUTING` como flujo trazable y luego su versión
   imprimible.
7. Validar cada formato en la impresora y lector reales: dimensiones, DPI,
   márgenes, orientación, contraste, zona silenciosa, SKU largo, cantidad de
   copias, reimpresión y lectura desde tablet/HID.

Pruebas mínimas: generación determinista; decodificación de ida y vuelta del
Code 128; contenido y tamaño de cada plantilla; protección contra texto/HTML
malicioso; permisos y NIP; auditoría de reimpresión; idempotencia de placa ligada
a Entrada; validación del esquema JSON; versionado/publicación inmutable;
desbordamientos y límites del lienzo; deshacer/rehacer; navegación por teclado;
y confirmación de que editar o imprimir no cambia saldos ni movimientos.

### 12.5 Estado del motor dinámico y editor visual

- `/Operations/Labels` es el generador único: selecciona una versión publicada,
  busca o lee por HID un producto activo, completa catálogo/campos configurados,
  captura 1 a 100 copias y genera la vista previa sin NIP. La ruta heredada
  `/Operations/Labels/4x6` conserva compatibilidad y abre
  `LBL-6X4-ZEBRA`. La navegación **Etiquetas** separa **Generar etiquetas** de
  **Diseñar formatos**, visible solo para ADMIN.
- `/Admin/Labels/Templates` administra plantillas y versiones;
  `/Admin/Labels/Templates/Edit` ofrece el editor SVG con selección múltiple,
  arrastre, redimensionado, rotación por propiedades, alineación, distribución,
  capas, cuadrícula, snapping, zoom, deshacer/rehacer y teclado;
  `/Admin/Labels/Assets` controla imágenes PNG/JPEG deduplicadas por SHA-256.
- La migración `DynamicLabelTemplates` crea `label_templates`,
  `label_template_versions`, `label_assets`, referencias de recursos y eventos
  administrativos, y precarga únicamente `LBL-6X4-ZEBRA` versión 1 publicada.
  El usuario confirmó el 24 de agosto de 2026 que ya ejecutó las migraciones;
  queda pendiente registrar la auditoría del esquema aplicado y su respaldo.
- La migración de datos `SeedRemaining4x6ExcelTemplates`, generada pero **no
  aplicada automáticamente**, incorpora como versión 1 publicada
  `LBL-4X6-STANDARD`, `LBL-3X1-COMPACT`, `LBL-6X4-PARTIAL`,
  `LBL-6X4-SPOUTED`, `LBL-6X4-BERM`, `LBL-6X4-RAINCAP`,
  `LBL-6X4-CUSTOM-SPOUT`, `LBL-6X4-MOBILE` y `LBL-4X45-RECEIVING`. Es una
  migración solo de datos: no crea tablas ni altera inventario. Los campos
  especiales son opcionales; aceptan captura en computadora y muestran una
  línea para escritura manual cuando quedan vacíos.
- Los dos códigos —SKU/ITEM y cantidad— se generan localmente como Code 128 SVG
  mediante `ZXing.Net` 0.16.11. La impresión usa HTML/CSS del navegador y no
  depende de Excel, fuentes de barras, servicios externos ni Internet.
- No se persisten generaciones, folios, responsables ni motivos de impresión;
  reimprimir exige capturar nuevamente los datos actuales. Generar o imprimir no
  crea ni actualiza movimientos, saldos, lotes, pallets o asignaciones.
- Compilación y pruebas focales cubren el catálogo de diez plantillas, renderer,
  esquema, Code 128, migración PostgreSQL y flujo HTTP. El usuario confirmó el
  24 de agosto de 2026 que la etiqueta Zebra vigente se lee con el escáner.
  Siguen pendientes la comparación visual y física de los nueve formatos
  migrados en laptop/tablet e impresoras reales: temas, textos largos, los
  cuatro tamaños, DPI, márgenes, orientación y lectura HID. No considerar esos
  nueve formatos validados físicamente hasta completar las comprobaciones.

## 13. Modelo WIP anterior (histórico; sustituido en código por 13.7)

- Hasta el corte de 13.7, `WIP-2`, `WIP-3` y `WIP-4` fueron ubicaciones
  operativas sin saldo. Se presentaban como racks WIP completos, sin posiciones
  de pallet; internamente conservan `LocationKind.Area` porque el tipo Rack
  representa posiciones individuales. La migración
  `WipProductionFlow` valida que no sean racks y que no tengan saldo ni
  asignaciones activas antes de clasificarlas; si encuentra datos incompatibles,
  se detiene sin borrarlos.
- Rack → WIP es una salida `ProductionIssue`: consume inventario y lotes del rack,
  guarda el área WIP informativa y no crea saldo ni asignación en WIP.
- WIP → bodega es una disposición ligada a la línea original, restaura sus lotes
  en orden inverso al consumo y crea una entrada `WipWarehouseReturn`.
- WIP → proveedor es una disposición con referencia documental y no toca saldos.
- Las devoluciones son parciales, múltiples, idempotentes y no pueden exceder lo
  surtido. Sus correcciones son compensaciones inmutables; una salida WIP no se
  corrige mientras conserve devoluciones vigentes.
- `/Reports/Wip` es una consulta LAN pública con semana lunes-domingo, detalle y
  consumo asumido. CSV/XLSX y correcciones requieren sesión ADMIN.
- El código compila y la prueba focal del ciclo 20/5/3 pasa. La migración
  `20260819150547_WipProductionFlow` se aplicó a `warehouseEPI/public` el 19 de
  agosto de 2026 después del respaldo validado
  `BackupDatabase/public-before-wip-production-flow-20260819-154603.dump`.
  La auditoría posterior confirmó WIP-2/3/4, cero saldos/asignaciones WIP, los
  constraints/FKs/índices requeridos y 25 movimientos históricos `STANDARD`.
  No se publicó una Release y falta la prueba física en tablet/cámara. El SQL
  revisable está en `artifacts/wip/WipProductionFlow.sql`.

## 13.7 WIP con existencias reales (implementado en código, pendiente de despliegue)

- `WIP-2`, `WIP-3` y `WIP-4` conservan `LocationKind.Area` y
  `OperationalRole.Wip`, pero ahora controlan inventario por producto,
  ubicación y lote. `TracksInventory` se conserva temporalmente en contratos
  como `true`; `IsWip` identifica el rol sin inferirlo a partir del saldo.
- Los racks físicos también pueden clasificarse completos como WIP desde
  `Editar rack`. El rol se aplica uniformemente a sus posiciones presentes y
  retiradas; una posición creada o restaurada hereda el rol del rack. El saldo,
  las asignaciones y el historial permanecen por pallet exacto, por ejemplo
  `M-1-1`, y el croquis combina la consulta del rack sin crear una ubicación
  agregada `M-1`. La migración `20260904120000_AllowRackWip` elimina solamente
  la restricción que reservaba WIP para áreas; fue verificada en
  `warehouse_epi_test`, pero permanece sin aplicar en la base operativa hasta
  su autorización explícita.
- Entrada, Salida, Transferencia, Ajuste, asignaciones y conteos cíclicos
  admiten WIP con las mismas validaciones operativas que otras ubicaciones.
  `Surtir WIP` crea una transferencia `ProductionIssue` desde rack a WIP y
  conserva las asignaciones FEFO del origen.
- `/Operations/Wip` registra consumo, regreso a bodega y devolución a proveedor
  contra el saldo acumulado. Sus propósitos son `WipConsumption`,
  `WipWarehouseReturn` y `WipSupplierReturn`; sólo proveedor exige referencia.
  `/Operations/WipReturn` queda como redirección compatible y ya no crea
  `WipDisposition`. El historial anterior permanece inmutable y de solo lectura.
- `/Reports/Wip` separa inventario actual desde `InventoryBalances`, actividad
  efectiva desde `InventoryBalanceChange` y el modelo legado de consumo asumido.
  Las alertas usan saldo positivo y el lote positivo más antiguo, no
  disposiciones históricas. CSV/XLSX ADMIN limita estrictamente a 10,000 filas.
- La migración incremental `20260828120000_WipTrackedInventory` sólo amplía los
  constraints de propósito y forma; acepta historial y reversos, no crea tablas
  de saldo ni convierte datos. **No está aplicada en PostgreSQL ni publicada en
  una Release.** El saldo inicial debe provenir exclusivamente de una campaña
  ciega aprobada después del corte operativo.
- Verificación actual de código: compilación Release focal y pruebas WIP
  focales pasan. Siguen pendientes la prueba PostgreSQL aislada de la migración,
  la suite relevante completa, validación visual de temas/anchos y pruebas
  físicas de tablet, cámara y lector.

## 13.9 Procesos asociados a WIP (implementado en código, pendiente de despliegue)

- El catálogo ADMIN `/Admin/Production/Processes` reutiliza `ProductionStage` como proceso y permite mantener código, nombre, estado y asociaciones WIP. Las rutas de producción consumen el mismo catálogo; los turnos continúan configurándose en la pantalla de rutas.
- Un proceso puede asociarse a varias áreas WIP, filas completas y racks con posiciones WIP. Las áreas se identifican por `Location.Id`; una fila se identifica sólo por `RowCode` y aplica a todos sus racks, incluidos los de almacenamiento y los creados posteriormente. Los racks directos se configuran una sola vez por fila y número; sólo sus posiciones WIP presentes son destinos de producción.
- El editor ADMIN de racks permite mezclar posiciones WIP y de almacenamiento. Cada posición conserva su identidad, saldo, placas, asignaciones e historial al reclasificarse; al retirarla físicamente se conserva su función. Si se retira la última posición WIP, se quitan las asociaciones directas del rack y permanecen las heredadas de fila. El croquis y la vista Racks señalan cada función y los racks mixtos.
- El editor del proceso reúne áreas, filas y racks en un buscador con sugerencias y selección múltiple. Elegir una fila retira asociaciones directas redundantes de sus racks y no admite excepciones individuales. Los vínculos no disponibles siguen visibles para poder retirarlos.
- La misma relación se edita desde el proceso, desde `Editar área` y desde `Editar rack`. Un contador de versión compartido rechaza ediciones obsoletas; los cambios del rack conservan revisión, motivo, NIP ADMIN e idempotencia.
- `Editar rack` separa las asociaciones directas editables de los procesos heredados por fila, que son de sólo lectura y enlazan al proceso de origen. Eliminar o reclasificar un rack retira únicamente sus asociaciones directas; la fila permanece configurada para otros racks y para futuras altas.
- Cambiar una ubicación o rack a una función distinta de WIP retira sus asociaciones. Desactivar un proceso usado en una ruta activa se rechaza. No se eliminan procesos físicamente y las instantáneas de órdenes existentes conservan sus nombres y secuencias.
- Esta configuración por sí sola no mueve inventario ni atribuye saldos a procesos. La integración operativa opcional con órdenes se implementó después en 13.10.
- La migración `20260910154113_ProcessWipAssignments`, aplicada en la base local el 10 de septiembre de 2026, creó la revisión de configuración, su auditoría y asociaciones de área o rack. La ampliación para filas completas quedó en la migración incremental `20260910175524_ProcessRowAssignments`, pendiente de aplicar y publicar; no se debe modificar retroactivamente la migración ya registrada.

## 13.10 Material WIP vinculado a órdenes y procesos (implementado en código, pendiente de despliegue)

- `Surtir WIP` permite elegir **Para una orden** o **Surtimiento general**. El modo de orden busca únicamente órdenes liberadas o en proceso y etapas cuyo proceso sea efectivo para el área, rack o fila del destino. El vínculo es opcional, se crea en la misma transacción que `ProductionIssue` y no reclasifica movimientos históricos.
- `ProductionMaterialIssueLink` relaciona la línea original con la orden y su etapa. `ProductionMaterialOperation` y sus líneas registran consumos, regresos a bodega, devoluciones a proveedor y reversos mediante una cabecera idempotente. El pendiente siempre se deriva como surtido menos operaciones vigentes; no se guarda un total paralelo.
- La página de la orden muestra Material WIP por proceso y permite confirmar varias líneas con cantidades editables, observaciones y un solo NIP. Registrar avance continúa siendo una acción independiente porque no existe BOM para convertir producto terminado en materia prima consumida.
- La reserva conserva los lotes recibidos por el surtimiento original. Las operaciones de la orden toman esos lotes; el flujo WIP general calcula saldo total, reservado y libre y asigna FEFO sólo entre lotes libres. Si una cantidad invade una reserva se rechaza y se muestran enlaces a las órdenes que la poseen.
- Cada vínculo, operación o reverso incrementa la versión de la orden. Los formularios obsoletos, reintentos con contenido distinto y cantidades superiores al pendiente se rechazan. Una confirmación que agrupa varias ubicaciones se ejecuta dentro de una sola transacción.
- La corrección genérica de consumos o devoluciones vinculados queda bloqueada y dirige al reverso ADMIN de la orden. Un surtimiento sin operaciones posteriores puede corregirse; su vínculo pasa al reemplazo compatible o se retira si sólo se revierte.
- La migración incremental `20260910185035_ProductionMaterialOrderLinks` crea las tres tablas e índices de vínculo, operación y líneas. Depende de `20260910175524_ProcessRowAssignments`; ambas están pendientes de aplicar. El SQL revisable está en `docs/sql/20260910185035_ProductionMaterialOrderLinks.sql`. No se desplegó ni reinició la aplicación.
- Verificación actual: compilación Release, contratos JavaScript, 51 pruebas focales de producción/inventario/WIP y una prueba PostgreSQL aislada de material pasan. Siguen pendientes la aplicación de migraciones, la prueba PostgreSQL aislada de esta migración y la validación visual/física en tablet, lector y red local.

## 13.11 Seguimiento visual de producción por lotes (implementado en código, pendiente de despliegue)

- Las recetas ADMIN son versionadas por producto. Pueden comenzar como una lista de materiales con cantidades y etapas todavía pendientes; quedan marcadas como incompletas hasta tener una ruta activa y una etapa válida para cada material. Una orden nueva sólo copia una receta completa y escala el plan contra la cantidad autorizada; editar el catálogo después no cambia la orden.
- La versión histórica admitía varios lotes por orden. P5 restringe las órdenes nuevas a un único lote de producción; las históricas conservan sus lotes sin admitir nuevos. Cada lote recibe folio y un único `ProductLot` del terminado; todas sus recepciones parciales entran a ese lote de inventario. La etiqueta Code 128 identifica el lote y su reimpresión no crea producción.
- El resultado por etapa confirma con un NIP las cantidades procesada, buena, retrabajo y merma junto con los consumos reales. Los consumos toman únicamente reservas WIP de la orden, conservan los lotes de materia prima y se guardan con el resultado y sus vínculos dentro de la misma transacción. Un reintento idéntico devuelve la operación previa y uno diferente se rechaza.
- Las entregas entre procesos quedan identificadas y cada recepción o conciliación señala la entrega concreta. Una etapa posterior sólo puede procesar lo recibido para ese lote. El retrabajo conserva la procedencia previa y sólo agrega material cuando se captura consumo adicional.
- La orden presenta `Materiales → Procesos → Producto terminado → Bodega`, selector de lote, estados por unidad, pendientes, resultados parciales, procedencia e historial. El listado permite buscar orden, lote o SKU, filtrar por estado, proceso, fecha y alertas, y pagina de 25 en 25. `/Operations/Production/Trace` consulta trazabilidad desde materia prima o terminado.
- Los ajustes del plan exigen NIP ADMIN y motivo y generan evento auditable. El reverso del resultado conserva el original, revierte sus movimientos de material y exige resolver primero la cadena posterior. El cierre continúa bloqueado por entregas, retrabajo, producto sin destino o material WIP reservado.
- La migración incremental `20260911125505_ProductionBatchTraceability` agrega recetas, planes, lotes, resultados, consumos y relaciones de eventos. Su SQL revisable está en `docs/sql/20260911125505_ProductionBatchTraceability.sql`. No se aplicó a la base operativa, no se publicó y no se reinició el servicio.
- La validación automatizada cubre versionado y copia de recetas, límites e idempotencia de lotes, recepción entre etapas, reversos, contratos de interfaz, preservación de una orden anterior al bajar/subir la migración y trazabilidad PostgreSQL con recepciones parciales. La validación visual en laptop/tablet y las pruebas físicas de impresión, HID y cámara siguen siendo actividades separadas.
- La receta y la primera ruta se administran dentro de `Editar producto`, después de sus datos principales. La ficha resume ruta, etapas y receta activa; `/Admin/Production/Routes` permanece como catálogo general alternativo. La URL anterior `/Admin/Production/Recipes` redirige al listado de productos y conserva los servicios y versiones históricas.
- Los materiales de receta y la ubicación principal del producto usan el buscador compartido con sugerencias, Enter/HID, selección manual y cámara. Cada página carga una sola instancia del modal; procesos, unidades, tipos y clases permanecen como listas cerradas. Las filas vacías son opcionales; una fila iniciada requiere producto activo y cantidad positiva, mientras la etapa puede quedar pendiente hasta crear la ruta.

## 13.12 Integración integral de órdenes, materiales, WIP y analítica — P1, P2 y P3 implementadas en código; P3 reaplicada, despliegue pendiente

- La especificación funcional y técnica está en `docs/PRODUCTION_ORDER_TRACKING.md`. Define el recorrido desde la creación y liberación de una orden hasta el surtimiento Rack → WIP, consumo por lote, avance entre procesos, recepción del terminado, alertas para bodega y analítica.
- Cada material conservará su ubicación principal de almacén y podrá tener un WIP predeterminado por proceso. Esta regla se configura una vez en el material; no se repite en cada receta. La receta define cantidad y proceso de incorporación, y la orden copia el destino WIP resuelto para conservar su fotografía histórica.
- Liberar una orden generará solicitudes identificables de surtimiento. La cola **Surtimientos a producción** mostrará a bodega material, pendiente, prioridad, ubicaciones y lotes sugeridos y WIP de destino. La solicitud no mueve inventario; el traslado real se crea únicamente después de escanear o seleccionar y confirmar con NIP.
- P1 ya está implementada en código. Crear, Editar y Detalles del producto permiten configurar y auditar una regla por material/proceso; el editor y listado de procesos admiten un WIP predeterminado general. Los destinos válidos son área WIP, rack con al menos una posición WIP operativa o posición exacta; sólo las posiciones WIP pueden resolver un destino de rack. Se validan con las asociaciones actuales o propuestas del proceso. Una referencia que luego queda bloqueada, inactiva, ausente, reclasificada o incompatible se conserva y muestra la causa, sin mover inventario ni elegir otra ubicación.
- Los cambios P1 exigen NIP ADMIN, motivo, versión esperada e identificador idempotente. Producto y reglas se crean en una transacción; en Editar las reglas tienen formulario independiente. La migración `20260911184026_MaterialWipDefaults`, su SQL revisable y una prueba PostgreSQL aislada acompañan el cambio. La migración P1 no se aplicó a la base configurada y la validación visual/física continúa pendiente.
- P2 fotografía en cada línea del plan el destino WIP y su origen de resolución. La precedencia es material/proceso, predeterminado general del proceso, único destino concreto y selección manual ADMIN. No sustituye silenciosamente configuraciones inválidas; bloquea la liberación hasta resolver receta, ruta, material, cantidad, proceso y destino. La disponibilidad actual de almacén y WIP es sólo informativa y un faltante no bloquea ni reserva inventario.
- La revisión de un borrador completa únicamente información ausente: incorpora la primera ruta disponible, después la primera receta completa compatible y finalmente resuelve destinos WIP. También permite una excepción de destino válida con NIP ADMIN, motivo, versión esperada e idempotencia, sin alterar P1. Las órdenes ya liberadas sin fotografía permanecen intactas. La migración `20260914122652_Phase132ProductionPlanningSnapshot`, su SQL revisable y pruebas de resolución, historial, liberación y PostgreSQL acompañan el cambio; no se aplicó a la base configurada y falta validación visual/física.
- Como complemento UX implementado en código de P2, el listado ADMIN de productos incorpora una fila desplegable de consulta para la receta activa. Muestra versión, cantidad base, materiales, unidades, procesos, destinos WIP efectivos, fuente y estado sin mezclar requerimientos con existencias; tiene carga diferida, una sola expansión y edición únicamente desde la ficha completa. El listado y el handler permanecen bajo `AdminOnly`.
- Crear producto permite guardar normalmente o continuar a `Receta y materiales`. Editar producto permite guardar primero una lista versionada de materiales sin ruta, crear después la primera ruta con procesos ordenados y NIP ADMIN y asignar las etapas en una versión posterior. Cada handler valida sólo su formulario, por lo que crear ruta no exige reenviar SKU, unidad ni el NIP de receta. Listado y ficha distinguen `Configurar receta`, `Completar receta` y `Editar receta`. La migración `20260914152059_RecipeDraftMaterialStages` vuelve opcional la etapa y su descenso se detiene sin pérdida si existen materiales pendientes; no fue aplicada. Las materias primas pueden permanecer sin configuración de producción y una ruta activa no se sustituye desde este flujo.
- P3 genera solicitudes al liberar órdenes nuevas, reserva existencia libre por ubicación y lote sin mover inventario y mantiene requerido, cancelado, entregado, pendiente, reservado y faltante por separado. Las órdenes liberadas antes de P3 no se incorporan retroactivamente. La cola **Surtimientos a producción** cuenta órdenes distintas, ordena urgentes y vencidas, permite toma informativa, reserva adicional elegida por bodega y reporte de problemas; ADMIN cambia prioridad o cancela pendientes con NIP y motivo.
- La salida WIP existente recibe la línea de solicitud y sus versiones, confirma movimiento, vínculo y conciliación de reserva dentro de la misma transacción y rechaza excesos o reservas ajenas. El saldo negativo conserva la advertencia vigente y se permite únicamente sin invadir reservas de otra orden. Pausar conserva solicitudes y reservas y suspende entregas; los cambios de cantidad quedan bloqueados hasta una conciliación posterior.
- La migración `20260914181154_Phase133ProductionSupplyRequests` está aplicada otra vez en la base configurada desde el 15 de septiembre de 2026. Durante el desarrollo P4, `dotnet ef migrations remove --force` revirtió P3 inesperadamente; se reaplicó inmediatamente el mismo esquema. Las tablas P3 quedaron recreadas y cualquier dato P3 anterior requiere respaldo o WAL para recuperarse. No se publicó ni se reinició el servicio.

## 13.13 Preparación guiada y surtimiento a producción — P4 implementada en código; migración y despliegue pendientes

- `/Operations/ProductionSupply/Prepare` muestra el contexto de una línea, disponibilidad física, reserva propia, reservas ajenas, libre utilizable, preparación guardada, responsable y problemas. Admite varios orígenes de almacén y saldo libre del destino WIP, con captura manual, Enter/HID y cámara; el escaneo identifica y enfoca, pero no confirma.
- La preparación persiste fuentes, cantidades, destino, responsable, fecha y versión con NIP. Puede recuperarse desde otra tablet, se revalida al continuar y se descarta con NIP y motivo. Guardar no crea movimientos ni nuevas reservas.
- La confirmación acepta reducciones por origen y ejecuta en una transacción los traslados reales, asignaciones de WIP sin movimiento, lotes, reservas, pendientes, eventos y un registro común que relaciona todos los movimientos y vínculos. Un reintento idéntico devuelve el resultado anterior y contenido distinto produce conflicto.
- El éxito muestra un comprobante con operación, confirmación, cantidad, pendiente, destino y componentes creados; el handler `Result` recupera el mismo resultado persistido después de una respuesta incierta. Los enlaces P3 con `supplyRequestLineId` redirigen a P4 y los enlaces ambiguos por etapa abren la cola.
- `ProductionMaterialIssueLink` identifica producto, WIP, cantidad, procedencia, lotes y cantidad anulada. Consumo, disponibilidad, protección WIP, correcciones y trazabilidad aceptan traslados y asignaciones. La asignación libre existente no cambia saldo físico y puede anularse parcialmente con NIP ADMIN sólo mientras siga disponible.
- El destino ADMIN se conserva por línea, con motivo y evento, sin cambiar entregas anteriores. Las devoluciones a bodega distinguen reposición, que reabre el pendiente sin reserva automática, y sobrante, que no lo reabre. Producción calcula `Material listo en WIP` antes del primer consumo y `Surtimiento completo` después.
- La migración generada es `20260915143955_Phase134GuidedProductionSupply` y su SQL revisable está en `docs/sql/20260915143955_Phase134GuidedProductionSupply.sql`. Su backfill usa movimientos y cambios de saldo históricos y aborta ante cualquier vínculo o lote inconciliable. **P4 no se aplicó a la base operativa, no se publicó y no se reinició el servicio. Las migraciones se validan por separado en `warehouse_epi_test`.**
- Verificación actual: compilación Release sin advertencias, modelo EF sin cambios pendientes, sintaxis JavaScript válida, 44 pruebas focales de servicios/UI, 80 pruebas de producción no PostgreSQL y 7 pruebas PostgreSQL de planificación/material ejecutadas secuencialmente. La integración PostgreSQL P4 confirma almacén más WIP en una sola operación y consulta su comprobante persistido. Cubren además confirmación común, reducción al entregar, idempotencia, dos tablets, devoluciones, destino por línea y anulación WIP. La corrida amplia confirmó 573 de 596 pruebas; reportó 22 fallos HTTP 400/antiforgery por fábricas web paralelas y una expectativa de reporte `stagnantCategory=90plus`, fuera del cambio P4. Sigue pendiente la validación visual/física en laptop, tablet, HID, cámara, temas y pérdida de red.

- P5 agrega ejecución integrada y P6 agrega analítica de producción en código; permanece P7 para migración operativa, validación y despliegue. Estas fases preservan reservas, lotes, idempotencia, reversos y el motor de inventario como fuente única.

## 13.14 Ejecución integrada de producción — P5 implementada y verificada en código; despliegue pendiente

- La orden mantiene meta original/vigente y un único lote nuevo; los lotes históricos se conservan. El control de versión impide crear simultáneamente un segundo lote.
- `/Operations/Production/Execution` reúne cierres, reapertura, revisiones, reservas y solicitudes de retrabajo. OPERATOR confirma el cierre principal; diferencias, retenciones, ajustes, reapertura y cierre definitivo requieren ADMIN. Avances y recepciones posteriores conservan `PrincipalClosed`.
- Los pendientes de retrabajo conservan resultado original, proceso, lote, cantidades e intentos. El descarte requiere ADMIN y no duplica producto bueno. Los recuperados continúan por la ruta existente.
- Ajustar fecha/meta/autorizado/materiales conserva valores anteriores/nuevos, invalida preparaciones y concilia solicitudes y reservas. No sustituye materiales ni altera catálogo; no reduce por debajo de cantidades ejecutadas. Antes de cerrar fabricación incompleta, ADMIN debe ajustar la meta.
- Merma de materia prima se confirma por OPERATOR con NIP y genera salida real desde su WIP reservado. `/Admin/Production/Reasons` incorpora motivos por categoría y Otro con comentario, preservando su descripción histórica.
- Migración generada: `20260915161501_Phase135ProductionExecution`, sobre P4. Reconstruye retrabajos históricos únicamente desde resultados efectivos con origen inequívoco; aborta ante ambigüedad. No aplica cambios a la base operativa ni recupera datos perdidos de P3.
- Verificación P5: Release sin advertencias ni errores; EF sin cambios de modelo pendientes; JavaScript y diff correctos. Pasaron 88 pruebas de producción/UI, 10 focales P5, 9 PostgreSQL conjuntas P2/material/P5 y dos comprobaciones finales de cierre/permisos e intentos sucesivos (con solapamiento entre corridas). El SQL está en `docs/sql/20260915161501_Phase135ProductionExecution.sql`. La prueba física y el piloto permanecen pendientes.
- Permanecen fuera de P5: captura con fecha anterior, sustitución manual, tránsito, analítica/exportaciones P6 y despliegue/piloto P7. Las pruebas aisladas, la migración operativa y la validación física se informan por separado.

## 13.15 Analítica y alertas de producción — P6 implementada y verificada en código; despliegue pendiente

- `/Reports/Production` incorpora vistas ADMIN de órdenes, materiales, retrabajo/merma y registros, con filtros GET, paginación, Excel, CSV e impresión. Su presentación adapta el patrón HermeX Operations UI a Razor Pages, Bootstrap y temas de Warehouse EPI.
- Cumplimiento significa recepción efectiva de la meta vigente en bodega. Se conserva visible la meta original. Materiales compara consumo efectivo tanto con el plan autorizado como con la receta histórica proporcional a la entrada de primera pasada; retrabajo y desperdicio permanecen separados.
- Los registros se filtran por fecha real y turno registrado. Originales revertidos y sus reversos permanecen explicables sin duplicar totales efectivos. Unidades distintas no se agregan en una cantidad única.
- Cada proceso admite umbrales opcionales de inactividad y retrabajo. La edición conserva NIP ADMIN, motivo, versión e idempotencia. Sin umbral se muestra antigüedad sin declarar atraso; las pausas suspenden la alerta de inactividad.
- La cola pública incluye órdenes en cierre principal con pendientes posteriores y las dirige a Ejecución; responsables, analítica y exportaciones siguen protegidos para ADMIN.
- Migración generada: `20260916142819_Phase136ProductionAnalytics`; SQL revisable: `docs/sql/20260916142819_Phase136ProductionAnalytics.sql`. Agrega solamente `inactivity_alert_hours`, `rework_alert_hours` y su restricción positiva.
- Verificación P6: compilación Release sin advertencias, JavaScript válido, 42 pruebas focales de P6/configuración/cola/navegación y 106 pruebas amplias de producción. Cinco pruebas P6 incluyen ejecución de las cuatro consultas en PostgreSQL, cumplimiento por recepción, exportación tipada y sanitizada y rechazo sobre 10,000 filas. No se aplicó la migración a la base operativa, no se publicó el servicio y quedan pendientes navegador, impresión y tablet física.
## 14. Contexto breve para pegar en otro chat

### Acuerdos de Order Tracking — 11 de septiembre de 2026

Detalle en `docs/PRODUCTION_ORDER_TRACKING.md`. Son decisiones documentadas; no implican implementación verificada ni despliegue.

- Reservar material para la orden desde la liberación, antes del traslado, mostrando folio y motivo y distinguiendo reserva en almacén de reserva en WIP.
- Permitir elegir material libre ya existente en WIP, surtimiento completo desde almacén o una combinación. Una reserva ajena requiere reasignación auditada.
- Permitir ajustes de la orden con valores anteriores, motivo, responsable y fecha, conciliando solicitudes, reservas y operaciones dependientes.
- Mantener **un solo lote por orden**, sin división opcional: el grupo puede avanzar durante varios días y recibir parcialmente en bodega. Los consumos conservan los lotes de materia prima vinculados al mismo lote terminado; no se identifican piezas individuales.
- Dedicar registros y analítica a retrabajo y merma. Permitir cierre de fabricación por debajo o por encima de la meta, conservando retrabajo para días posteriores asociado a la misma orden y lote.
- P3 permite liberar con faltantes, reserva únicamente existencia libre y deja la asignación posterior a una acción de bodega. La toma de una solicitud es informativa y otro operador puede continuar; la concurrencia se resuelve al confirmar. Decisiones confirmadas de P5: cierre principal por OPERATOR con autorización ADMIN de diferencias; retención explícita de reservas; cierre definitivo y reapertura ADMIN; retrabajo en el mismo proceso y descarte ADMIN.


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
Secrets. Los lotes internos globales y el modelo WIP legado tienen migraciones
aplicadas. El código actual convierte WIP-2/3/4 en ubicaciones con existencias
reales: Rack -> WIP transfiere saldo y `/Operations/Wip` registra consumo,
regreso o proveedor. La migración `20260828120000_WipTrackedInventory` aún no
se ha aplicado ni forma parte de una Release publicada; tampoco se ha ejecutado
el conteo físico inicial ni validado en tablet/cámara. Existen
pruebas PostgreSQL aisladas en warehouse_epi_test; no reutilices conteos
históricos de pruebas sin ejecutar una validación actual.

El layout físico usa la nomenclatura Fila-Rack-Pallet, por ejemplo A-1-8, y el
pallet se distribuye como teclado numérico. Las áreas no rack conservan códigos
propios. Sigue pendiente validar físicamente posiciones, excepciones, sentido de
numeración y colores del layout.

Las fases 10.1 a 10.3 están completadas; 10.4 a 10.6 están implementadas; la
Release 0.10.7 está activa como servicio Windows y 10.8 continúa pospuesta.
`Quality` es obligatorio en `main`. La fase 11 tiene sus principales pantallas
implementadas, con trabajo parcial en 11.6 y 11.7 y validación física pendiente.
    La migración del croquis 11.8 figura como no aplicada y debe verificarse contra
la base antes de cambiar ese estado. Las fases 11.9.1 y 11.9.2 del editor
arquitectónico ligero están implementadas y sus migraciones se verificaron
aplicadas. 11.9.3 está implementada con migración sin aplicar y validación
visual/física pendiente; 11.9.4 sigue propuesta. Las
fases 13.1 y 13.2 están completadas y
13.3, 13.4 y 13.5 están implementadas, con la migración de 13.5 sin aplicar y
validación física LAN/tablet/lector/impresora pendiente; el siguiente bloque
planificado es 13.6. La fase 12 ahora contempla centralizar
etiquetas 4x6, placas de pallet y rutas de producción usando el catálogo del
sistema, pero su contrato e implementación siguen pendientes. Después quedan el
piloto físico, conteos/alertas avanzados, PWA/offline, liberación v1.0,
QuickBooks y paneles LED. No agregues QuickBooks ni LED antes de sus fases.
```

#### Navegación modular inspirada en HermeX — 2026-09-15

- Patrón tomado de pos-frontend: navigation/modules.ts, NavMain.vue y
  ModuleHubLayout.vue. Se adapta a Razor Pages, Bootstrap y SVG existentes;
  no incorpora Vue, Tailwind ni dependencias remotas.
- Navigation/ModuleNavigation.cs centraliza módulos, secciones, acciones, iconos,
  destinos, parámetros y visibilidad. El layout y Pages/Modules/Index consumen
  el mismo catálogo. Las tarjetas son enlaces normales y funcionan sin JS.
- Ruta nueva /Modules/{module}. Claves, orden y contenido:
  - operations: Entrada, Salida, Transferencia, Ajuste y Conteos cíclicos.
  - production: Surtimientos a producción, Seguimiento de producción y Procesar
    WIP; ADMIN dispone de Procesos. Órdenes de trabajo permanece disponible por
    URL para soporte, pero se oculta de la navegación habitual porque el Programa
    semanal crea sus órdenes y Producción avanzada permite atenderlas.
  - inventory: Existencias y Ubicaciones (ruta pública o ADMIN según sesión);
    ADMIN también dispone de Movimientos, Lotes y Centro de excepciones.
  - labels: Generar etiquetas y Placas de pallet; Diseñar formatos solo ADMIN.
  - reports: Resumen operativo/Tablero diario, Pendientes con view=pending
    para ADMIN/Carga de trabajo para público, Analítica de inventario, Kardex y WIP.
    Los enlaces contextuales Hoy/Gestión/Equipo se conservan.
  - catalogs (ADMIN): Productos, Tipos, Clases y Unidades.
  - administration (ADMIN): Usuarios, Estado del sistema y Datos del negocio.
- Claves desconocidas devuelven 404. Los módulos exclusivos de ADMIN evalúan
  AdminOnly; ocultan accesos sin sustituir la autorización de los destinos.
- Recepciones contra documento y Trazabilidad unificada siguen fuera de la
  navegación modular; sus rutas y funcionalidad permanecen disponibles como antes.
- Conserva Inicio, cuenta, login/logout, tema, campana, sidebar de 250 px, rail
  de 72 px, drawer, Escape y cámara. Tarjetas de 96 px mínimos; 1 columna móvil,
  2 desde 768 px y 3 desde 1200 px, con colores claro/oscuro de EPI.
- El contador de surtimientos aparece en Producción y su tarjeta de surtimientos.
  operational-notifications.js comparte una petición por ciclo de 30 segundos,
  evita solicitudes superpuestas, oculta cero y muestra 99+; ante error conserva
  el último estado y reintenta en el siguiente ciclo.
- Implementación sin migraciones ni cambios de API de negocio. No implica arranque,
  reinicio ni despliegue de la instalación. Validación visual y física pendientes.
- Verificación de esta implementación: compilación correcta en salida aislada
  artifacts/module-navigation-validation y 55 pruebas focales aprobadas (módulos,
  permisos, navegación, CSP, scripts, notificaciones y surtimientos).
- Comprobación JavaScript ejecutada: petición compartida, exclusión de consultas
  superpuestas, etiquetas accesibles de ambas representaciones, 99+, cero y pausa
  con documento oculto; node --check y git diff --check correctos.
- La revisión amplia inicial de 172 pruebas detectó una expectativa antigua de
  Ubicaciones en el lateral, corregida y cubierta por la ejecución focal, y dos
  fallos previos fuera del cambio de navegación: RackOperationsContractTests
  espera la firma antigua de OnGetAsync y ProductBarcodeAdministrationContractTests
  espera el marcado anterior de Lotes internos en la ficha de producto.
- No se encontró una instancia abierta en el navegador disponible. Revisión visual
  a 390/900/1280 px, claro/oscuro, teclado y prueba física de tablet pendientes.
  No se inició, detuvo ni desplegó la aplicación y no se aplicaron migraciones.

#### Existencias: rediseño visual de consulta rápida — 2026-09-15

- /Inventory conserva la consulta pública por producto o ubicación: no requiere
  NIP ni inicio de sesión y no incorpora catálogo general, recetas ni edición.
  Productos administra el catálogo y sus fichas; Ubicaciones explora la estructura
  física del almacén; Existencias consulta saldos a partir de un código.
- Diseño inspirado en la composición sobria de HermeX y consistente con EPI:
  buscador destacado, superficies neutras, bordes suaves, resumen de identidad,
  total global por producto y acciones secundarias. Retorno al módulo Inventario
  desde el layout; se retira el enlace local redundante al Menú operativo.
- Los cinco filtros y sus contadores conservan su significado, al igual que la
  paginación de 25 registros. Filas de cuatro columnas desde 992 px; tarjetas con
  el mismo marcado por debajo. Descripciones completas, códigos monoespaciados
  y cantidades alineadas con cifras de ancho uniforme.
- El total global sigue procediendo del modelo, independiente del filtro y la
  página; no se añade un total que mezcle unidades al consultar ubicaciones.
  Conserva estados negativos, bloqueo/inactividad, saldo sin asignación,
  asignación en cero y foco del resultado destacado desde alertas.
- Cambios limitados a Pages/Inventory/Index.cshtml y css/inventory-index.css.
  Conserva rutas, campos, atributos de cámara/escaneo, sugerencias, Enter/HID,
  permisos y enlaces a Kardex/Ficha/Movimientos/Croquis. Sin cambios en
  operations.js, servicios, modelos, API ni base de datos.
- Vista inicial solo con encabezado y buscador. Los mensajes existentes siguen
  distinguiendo código inexistente, código ambiguo y filtro sin resultados.
- Revisión visual en navegador a 390/900/1280 px y prueba física pendientes;
  no se inició, reinició ni desplegó la aplicación.
- Verificación: compilación correcta en artifacts/inventory-ui-validation y 42 pruebas focales aprobadas de Existencias, cámara, módulos, CSP y carga de scripts. Comparación de atributos de captura/filtros/foco/paginación y git diff --check correctos. La revisión estática confirma que el total global se consulta por separado del filtro y la página.

#### Movimientos y Lotes: listados y fichas operativas — 2026-09-15

- Rediseño visual inspirado en HermeX y consistente con Productos/Existencias:
  encabezados compactos, filtros agrupados, superficies neutras y cantidades
  legibles. Conserva ADMIN, rutas, parámetros, consultas y acciones protegidas.
- Movimientos mantiene Vigentes/Auditoría completa, población, período local,
  zona horaria, filtros rápidos, exportaciones Excel/CSV, paginación y returnUrl.
  Conserva las distintas columnas de cada población y Generar placa cuando procede.
- La ficha de movimiento conserva resumen, relaciones, cadena de corrección,
  líneas/cambios de saldo, eventos, impresión y acciones. Sus ajustes nuevos están
  dentro de @media screen para preservar las reglas existentes de impresión.
- Lotes conserva búsqueda, filtros de saldo/fecha, paginación, contador y nota
  de generación automática. Su ficha presenta número/producto, fecha interna,
  creación y saldo; separa distribución, movimientos y auditoría de fecha,
  incluyendo el estado vacío de movimientos relacionados.
- Corregir movimiento y Cambiar fecha conservan sus formularios y autorizaciones.
  No añade creación manual de lotes, exportaciones ni impresión de lotes.
- Reutiliza las hojas CSS locales y añade lot-details.css. Los listados conservan
  tabla desde 1200 px y tarjetas con el mismo marcado por debajo; las fichas apilan
  sus bloques y conservan tablas relacionadas. Colores claro/oscuro de EPI,
  controles de 44 px, códigos completos y ajuste de descripciones.
- Sin cambios de API, servicios, cálculos, modelos, base de datos o migraciones.
  No implica inicio, reinicio ni despliegue. Revisión visual a 390/900/1280 px,
  vista previa de impresión y validación física pendientes.
- Verificación: compilación correcta en artifacts/movement-lot-ui-validation y 67 pruebas aprobadas de movimientos/lotes, módulos, CSP y carga de scripts. git diff --check correcto. Comparación estática confirma conservación de rutas/handlers/parámetros existentes y del bloque previo de estilos de impresión de movimientos; pendiente comprobación visual real.

#### Crear y Editar producto: composición visual HermeX — 2026-09-15

- Adaptación de las secciones y jerarquía visual de los formularios de Productos
  de HermeX (`pos-frontend/resources/js/pages/Products/Create.vue` y `Edit.vue`)
  a Razor Pages y Bootstrap de EPI. Encabezado compacto, superficies neutras,
  bordes suaves, azul de EPI, foco visible y temas mediante tokens existentes.
- El mismo formulario general se organiza en Identificación, Clasificación y
  existencia mínima y Configuración operativa. Campos en dos columnas desde
  768 px; descripción y ubicación ocupan el ancho completo.
- Conserva las cuatro pestañas de edición, contadores, cambios pendientes y
  subpestañas de materiales/ruta. Cuatro pestañas por fila desde 992 px y dos
  por debajo. Materiales reutiliza sus filas como bloques etiquetados por debajo
  de 1200 px; WIP apila sus controles por debajo de 992 px.
- Mantiene guardados independientes, destinos `nextStep=details/production`,
  validaciones, valores tras errores, desplegable WIP, NIP, búsquedas, cámara,
  Enter/HID y filtros/paginación de ubicaciones. La barra inferior conserva
  acciones dentro de su formulario y permite ajuste de botones; pasa al flujo
  normal con altura de viewport de 500 px o menos para reducir solapamientos.
- Hoja local `wwwroot/css/product-editor.css`, cargada solamente por Crear y
  Editar después de los estilos globales. Sin modificaciones de JavaScript,
  modelos, servicios, API, permisos, base de datos ni migraciones.
- Compilación y pruebas focales mediante VSTest en salida aislada
  `artifacts/product-editor-validation`, con `--no-restore -p:UseAppHost=false`.
  Cobertura: ProductEditorUxContractTests, ProductCatalogTests,
  ProductProductionConfigurationPageTests, ProductDefaultEntryLocationTests,
  ProductionWipDefaultsUxContractTests, ModuleNavigationTests,
  ContentSecurityPolicyContractTests y ScriptLoadingContractTests.
- Verificación automatizada: 62 correctas, 0 fallidas, 0 omitidas. Inspección
  de atributos de formularios y `git diff --check` correctos; se conservan
  handlers y controles existentes. No fue necesario cambiar expectativas.
- Revisión visual a 390/900/1280 px en claro/oscuro pendiente: el navegador
  disponible no tenía una sesión abierta de EPI. Teclado, filas dinámicas y
  cámara requieren comprobación interactiva; tablet, HID y cámara físicos
  pendientes. No se inició, reinició ni desplegó la aplicación.

#### Centro de excepciones: listado y seguimiento — 2026-09-15

- Rediseño inspirado en la composición sobria de HermeX y consistente con
  Productos, Existencias y Movimientos. Hoja local `exception-center.css`,
  cargada en Alerts y Alerts/Details después de los estilos globales; estilos
  limitados a `.alerts-workspace` y `.exception-detail`.
- Listado: encabezado compacto, cuatro indicadores existentes (cuatro columnas
  desde 1200 px, dos por debajo), filtros en tres/dos/una columnas según ancho,
  filas desde 1200 px y tarjetas por debajo con el mismo contenido. Categoría,
  prioridad y etiquetas permanecen visibles también en escritorio; referencias
  y descripciones pueden ajustar línea sin truncarse.
- Ficha: resumen, motivo, acción recomendada, formulario de seguimiento e
  historial diferenciados mediante superficies neutras. Motivo/siguiente paso
  se apilan por debajo de 992 px. Historial descendente completo, con estado
  vacío específico; conserva formulario sólo para casos no resueltos.
- Preserva valores de indicadores, fechas/formato, filtros GET, paginación,
  accesos heredados, `Refresh`/`Update`, campos ocultos, `TargetUrl`, ADMIN,
  antiforgery y guardado existente. Sin cambios de JavaScript, servicios,
  modelos, API, reconciliación, campana, base de datos ni migraciones.
- Validación: compilación correcta en `artifacts/exception-center-validation`
  y 45 pruebas aprobadas, 0 fallidas, 0 omitidas: contratos del centro y ficha,
  servicio de excepciones, integración PostgreSQL, módulos, CSP y scripts.
  Comparación de atributos confirma conservación de controles, rutas y
  parámetros; `git diff --check` correcto. No se cambiaron expectativas.
- El servicio conserva validación de nota para espera, responsable activo,
  resolución automática, idempotencia y concurrencia por inspección del código.
  La suite existente no cubre todas las variantes de POST de seguimiento;
  la comprobación interactiva de esos casos permanece pendiente.
- Revisión visual a 390/900/1280 px en claro/oscuro, teclado y validación física
  pendientes: el navegador disponible no tenía una sesión abierta de EPI.
  No se inició, reinició ni desplegó la aplicación.

#### Ubicaciones y fichas públicas/ADMIN: diseño HermeX — 2026-09-15

- Composición visual consistente con Productos, Existencias y Movimientos.
  El listado compartido conserva Croquis/Racks/Tabla y sus variantes públicas
  y ADMIN, con encabezado compacto, indicadores neutros, filtros agrupados,
  navegación con aria-current y etiquetas visibles. Tabla usa filas desde
  1200 px y tarjetas por debajo con el mismo marcado.
- Croquis y Racks conservan geometría, colores operativos, métricas, selección,
  filtros temporales, paneles, zoom y enlaces. Sólo cambian superficies de
  herramientas/paneles y tipografía; no se modificaron sus scripts.
- Fichas: encabezado por código, resumen operativo y secciones de saldos,
  posiciones y asignaciones. ADMIN conserva asignación de otro producto y
  movimientos recientes. Bloques apilados por debajo de 1200 px y tablas
  etiquetadas por debajo de 768 px; códigos/descripciones pueden ajustar línea.
- Hojas locales locations-index.css y location-details.css, limitadas a sus
  contenedores y a medios de pantalla, cargadas por las páginas públicas y
  ADMIN. Sin cambios globales ni en editores, generación o impresión.
- Comparación de atributos confirma conservación de formularios, handlers,
  campos, rutas, parámetros y enlaces; se añaden etiquetas y carga de CSS.
  Sin cambios en API, modelos, servicios, cálculos, permisos o base de datos.
- Compilación correcta en artifacts/locations-ui-validation. Pruebas focales:
  63 aprobadas, 1 fallida, 0 omitidas (64 en total), incluyendo ubicaciones,
  fichas, racks, impresión, operaciones, navegación, CSP y scripts.
  El fallo RackOperationsContractTests.Operation_get_validates_prefill_without_changing_post_contract
  espera `Task OnGetAsync(Guid? productId` en OperationPageModel.cs, mientras
  el código operativo existente usa Task<IActionResult>. Ese archivo ya tenía
  cambios locales ajenos a este rediseño y no se modificó ni se ajustó la prueba.
- git diff --check correcto. Revisión visual a 390/900/1280 px en claro/oscuro,
  teclado, modal/paneles y validación física pendientes: el navegador disponible
  no tenía una sesión abierta de EPI. No se inició, reinició ni desplegó la app.

#### Reportes: claridad visual HermeX, primera etapa — 2026-09-15

- Tablero diario/Resumen operativo Hoy y las vistas de Analítica de inventario
  mantienen sus datos, filtros, rutas y permisos. Superficies neutras, controles
  de 44 px, foco visible y jerarquía consistente con los rediseños anteriores.
- Tablero: indicadores separados por atención/actividad, gráfica y detalle
  en dos columnas desde 992 px, apilados por debajo. Se conservan selección,
  fallback, datos, actualización, caché, errores y el JavaScript existente.
  El código actual ya contenía comparación operativa; se preservó íntegramente,
  sin añadir comparaciones ni alterar cálculos como parte de este rediseño.
- Analítica: barras HTML nativas mediante meter en ocupación, antigüedad y
  cobertura. Valores procedentes exclusivamente de summary; escala visual
  compartida por grupo respecto del mayor conteo, con mínimo técnico de 1
  cuando todos son cero. No son porcentajes, rankings ni totales de la página.
  Categorías independientes; se mantienen etiquetas/valores sin JavaScript.
- Filtros de antigüedad/cobertura preceden sus resúmenes; exportaciones
  secundarias conservan handlers y parámetros. Tablas con etiquetas añadidas
  y filas apiladas por debajo de 1200 px. Indicadores en una/dos/cuatro columnas
  según ancho. Se conservan casos sin fecha, sin consumo, agotados, negativos,
  unidades y distinción entre permanencia y caducidad.
- Cambios limitados a la vista de analítica y las hojas locales existentes
  dashboard-index.css/reports-inventory-index.css. Sin cambios de servicios,
  API, DTOs, exportadores, permisos, zona horaria, base de datos ni migraciones.
- Compilación y 81 pruebas aprobadas, 0 fallidas, 0 omitidas, con salida aislada
  artifacts/reports-visual-validation y --no-restore -p:UseAppHost=false.
  Filtro: Dashboard, InventoryAnalytics, ReportExportServiceTests,
  ModuleNavigationTests, ContentSecurityPolicyContractTests y
  ScriptLoadingContractTests. Incluye integración PostgreSQL. Comparación de
  atributos GET/exportación y git diff --check correctos; sin cambios de tests.
- Revisión visual a 390/900/1280 px en claro/oscuro, interacción del gráfico,
  teclado y prueba física pendientes: navegador disponible sin sesión EPI
  abierta. No se inició, reinició ni desplegó la aplicación.

#### Reportes etapa 2: Gestión / Resumen ejecutivo — 2026-09-15

- Rediseño visual local de /Reports/Executive, manteniendo Hoy/Gestión/Equipo,
  ADMIN, situación actual, actividad del período y zona horaria. Acciones de
  impresión/Excel secundarias; secciones con superficies neutras, acento azul,
  estados textuales, controles de 44 px y foco visible.
- Salud del inventario en una/dos/cuatro columnas a 0/768/1200 px. Tablas de
  productos apiladas por debajo de 1200 px y filas con etiquetas por debajo
  de 768 px; se conservan orden, cantidades, unidades, enlaces y estados vacíos.
- Períodos rápidos con aria-current; explicación junto al selector de que sólo
  modifica actividad/comparación. Referencia anterior visible en comparaciones,
  conservando valores anteriores, deltas, porcentajes y Sin base anterior.
- Barras nativas meter por tipo de movimiento, con valores existentes y escala
  visual respecto del mayor conteo (mínimo técnico 1 cuando todos son cero).
  No representan cantidades físicas ni valoraciones favorables/desfavorables.
- Vista ejecutiva y executive-report.css modificados; sin cambios de JavaScript,
  servicios, DTOs, cálculos, API, exportadores, base de datos o migraciones.
  Los estilos CSS originales se conservaron completos; nuevos ajustes en
  @media screen y ocultación específica al imprimir de barras/notas visuales.
  Encabezado impreso, cifras, metadatos y metodología se conservan.
- Compilación correcta en artifacts/executive-ui-validation con --no-restore
  -p:UseAppHost=false. Filtro: ExecutiveReport, ReportExportServiceTests,
  ModuleNavigationTests, ContentSecurityPolicyContractTests y ScriptLoadingContractTests.
  Resultado: 55 aprobadas, 1 fallida, 0 omitidas (56 en total).
- Fallo preexistente confirmado en HEAD:
  ExecutiveReportServiceTests.Executive_report_consolidates_inventory_capacity_and_flow_correctly
  espera stagnantCategory=90plus, mientras StagnantUrl ya dirige a
  /Admin/Inventory/Alerts?category=StagnantInventory. Servicio y prueba intactos;
  no se cambió una expectativa ajena al marcado para ocultar el fallo.
- Comparación de atributos confirma conservación de rutas, parámetros GET,
  exportación y botón de impresión. git diff --check correcto.
- Revisión visual a 390/900/1280 px claro/oscuro, teclado, vista previa de
  impresión multipágina y validación física pendientes: navegador disponible
  sin sesión EPI abierta. No se inició, reinició ni desplegó la aplicación.

#### Reportes etapa 3: Pendientes / Carga de trabajo / Equipo — 2026-09-15

- Rediseño de la vista compartida /Reports/Workload con hoja local
  workload-report.css, limitada a .workload-page y medios de pantalla.
  Encabezados compactos, superficies neutras, filtros agrupados, foco visible,
  controles de 44 px y estilos mediante tokens de EPI.
- Pendientes conserva producción, conteos y excepciones ADMIN, búsqueda,
  contadores totales, estados, vencimientos, referencias, cantidades y acciones
  TargetUrl/ActionLabel. Filas continuas en escritorio y bloques apilados por
  debajo de 1200 px. Resúmenes en una/dos/tres columnas según ancho.
- Actividad realizada/Equipo conserva períodos, franjas, tipo, filtros ADMIN,
  desgloses, actividad diaria, comparación de responsables y paginación.
  Períodos rápidos con aria-current; tablas con etiquetas por debajo de 1200 px.
  Se conserva la aclaración de que volumen no equivale a productividad.
- Público mantiene ausencia de identidades de responsables y excepciones ADMIN.
  Sin cambios en servicios, modelos, métricas, API, permisos, JavaScript,
  exportadores, base de datos o migraciones. Comparación de atributos confirma
  rutas, filtros, nombres y enlaces conservados; sólo se añade el enlace CSS.
- Compilación correcta en artifacts/workload-ui-validation con --no-restore
  -p:UseAppHost=false; 55 aprobadas, 0 fallidas, 0 omitidas. Filtro Workload,
  WorkQueue, ReportExportServiceTests, ModuleNavigationTests,
  ContentSecurityPolicyContractTests y ScriptLoadingContractTests. Incluye
  privacidad pública, límites/búsqueda de cola, franjas y exportaciones Excel/CSV.
  git diff --check correcto; expectativas de pruebas sin cambios.
- Revisión visual a 390/900/1280 px claro/oscuro y validación física pendientes:
  navegador disponible sin sesión EPI abierta. No se inició, reinició ni desplegó.

#### Reportes etapas 4 y 5: Kardex y WIP — 2026-09-15

- Adaptación del patrón visual de HermeX a Razor/Bootstrap y tokens EPI:
  encabezados compactos, filtros neutros, superficies delimitadas, cantidades
  alineadas con cifras uniformes, controles de 44 px y foco visible.
- Kardex carga kardex-report.css exclusivamente en su contenedor .kardex-report.
  Conserva estado inicial, búsqueda/sugerencias, ámbito global o ubicación,
  saldos, desgloses, fórmula, cronología, agrupación y continuidad entre páginas.
  Filas etiquetadas por debajo de 1200 px; saldos iniciales, continuación,
  correcciones y celdas combinadas mantienen sus relaciones y desplegables.
- WIP reutiliza wip-report-index.css y el marcado existente del parcial:
  inventario actual, actividad efectiva y estimaciones históricas permanecen
  separados, con tablas/filas adaptables sin duplicar datos ni sumar unidades.
- /Reports/Wip/Details conserva el historial legado de solo lectura; los
  movimientos actuales siguen abriendo sus enlaces existentes. Nueva hoja
  wip-report-details.css, metadatos en cuadrícula, resumen en una/dos/cuatro
  columnas a 0/768/1200 px y unidad del surtimiento junto a las cuatro cantidades.
  Devoluciones, compensaciones, estados y filas históricas íntegros.
- Ajustes limitados a vistas y CSS local de pantalla; JavaScript, consultas,
  servicios, DTOs, cálculos, permisos, exportadores y base de datos sin cambios
  por este rediseño. Comparación de atributos confirma contratos conservados;
  no se modificaron expectativas de pruebas ni estilos de impresión.
- Compilación correcta con --no-restore y -p:UseAppHost=false en
  artifacts/kardex-wip-ui-validation. Verificación final: 71 aprobadas,
  0 fallidas, 0 omitidas. Filtro Kardex, Wip_report, WipExitFlowContractTests,
  ReportExportServiceTests, ModuleNavigationTests,
  ContentSecurityPolicyContractTests y ScriptLoadingContractTests; incluye
  integración PostgreSQL de Kardex/WIP y exportaciones. Registro local:
  artifacts/kardex-wip-validation.log.
- La selección inicial amplia de Wip obtuvo 95 aprobadas y 6 fallidas:
  incluyó pruebas de migraciones que bajan la versión del esquema compartido,
  causando ausencia de products.default_entry_location_id. La ejecución focal
  de reportes anterior aprobó sin alterar servicios ni la base operativa.
- Revisión visual a 390/900/1280 px en claro/oscuro, teclado, textos largos y
  cantidades grandes pendiente: navegador disponible sin sesión EPI abierta.
  Validación física HID/tablet pendiente. Implementado en código; no se inició,
  reinició ni desplegó la aplicación y no se aplicaron migraciones operativas.

#### Importador de productos: comparación y actualización manual — 2026-09-16

- La pantalla existente `/Admin/Catalogs/Products/Import` permite elegir entre
  agregar y actualizar o conservar el modo anterior de solo agregar nuevos.
- Se admite `ITEM LISTING` con `ITEM (COMPLETE)`, además de `ITEMS` con
  `COMPLETE PART #`. Si ambas hojas están presentes, se utiliza `ITEMS`.
- Vista previa de altas, cambios campo por campo (actual/propuesto), productos
  sin cambios e incidencias; filtros y paginación de 25. Confirmación ADMIN,
  antiforgery y token de un solo uso, ligado al autor y vigente 30 minutos.
- En modo actualización se conservan celdas vacías y productos ausentes; solo se
  escriben descripción, referencia, clase y unidad. Estado, mínimos, tipo,
  ubicación, inventario y producción permanecen intactos. Una unidad escrita pero
  desconocida y cambio de unidad con movimientos omiten la fila. U/M vacía usa
  Sin asignar en altas y conserva la unidad en productos existentes.
- El lector consolida duplicados compatibles y omite grupos contradictorios,
  tomando el valor no vacío de cada campo cuando el otro está vacío. Las filas válidas
  pueden confirmarse con incidencias; estructura inválida bloquea el archivo.
- Antes de guardar se revisan nuevamente valores, catálogos y movimientos.
  PostgreSQL coordina esa comprobación y escritura con bloqueo transaccional
  breve de las tablas afectadas. No hay conexión Microsoft ni bloqueo permanente
  de edición local. No se conserva el Excel ni se agrega un historial persistente.
- No requiere una migración nueva. No se aplicaron datos a la base operativa,
  ni se inició/reinició o desplegó el servicio. Revisión visual en navegador y
  prueba transaccional real de esta ampliación en PostgreSQL pendientes.
- Verificación Release: 54 pruebas aprobadas, 0 fallidas, 0 omitidas, incluyendo
  lectores, servicio, HTTP con antiforgery, conservación de vacíos/estado/mínimos,
  rechazo de vista previa desactualizada y unidad con movimientos. Se excluyó
  la prueba previa de paginación general que espera 2 páginas para 60 productos
  aunque el catálogo ya pagina de 25 en 25. La validación HTTP usa EF InMemory.
- Ajuste posterior: las clases inexistentes se muestran como nuevas en la vista
  previa y se crean activas al confirmar, con código normalizado como nombre,
  una por código y en la misma transacción que los productos. Las clases
  inactivas no se duplican ni reactivan. Aplica a ambos modos de importación.
- Resolución manual de U/M: la vista previa ofrece un selector de unidades
  activas para cada valor desconocido o de formato inválido (p. ej.
  Sq. Foot:SQFT). POST con antiforgery y autor del token; recalcula sin escribir
  productos, invalida la vista previa anterior y exige confirmar la nueva.
  La selección solo afecta a ese archivo y no convierte cantidades.
- Duplicados: descripción, referencia, clase y unidad se completan con el único
  valor no vacío disponible. Dos valores no vacíos diferentes conservan el
  conflicto. Se registran todas las filas consolidadas; no se elige por parecido.
- Verificación de selector y consolidación: 39 pruebas aprobadas en dos tandas
  (38 de importación/lector y 1 HTTP del selector con antiforgery). Separadas
  para no exceder el límite ADMIN de 10 POST/minuto del host de pruebas;
  el primer pase conjunto alcanzó ese límite y devolvió HTTP 429. Compilación
  Release y diff --check correctos. Sin despliegue ni importación operativa.
- Resolución de duplicados en vista previa: cada SKU contradictorio muestra
  selectores para descripción, referencia completa, clase o unidad cuando hay
  varios valores no vacíos. El usuario elige entre valores del archivo; el
  servidor valida autor, vigencia, SKU y opciones. Los campos complementarios
  se conservan automáticamente. Resolver genera un token nuevo, invalida el
  anterior y vuelve a validar catálogo/unidades/movimientos antes de confirmar.
  No se escribe el Excel ni se guardan productos durante la resolución.
- Verificación de resolución de duplicados: compilación Release correcta,
  36 pruebas de lector/servicio y 1 HTTP del selector aprobadas. Incluye elección
  de referencia completa, campos complementarios, rechazo de valor falsificado
  y otro autor, invalidación del token anterior, antiforgery y ausencia de
  escrituras antes de confirmar. Sin despliegue; revisión visual pendiente.

#### Etiquetas: generación y placas de pallet — 2026-09-16

- Rediseño de /Operations/Labels y /Operations/PalletLabels inspirado en la
  composición sobria de HermeX y adaptado a Razor/Bootstrap de EPI. Hoja local
  labels-workspace.css cargada después de label-4x6.css, con reglas únicamente
  de pantalla y contenedor .labels-workspace; variantes de generación y placas.
- Generador: selector neutro, producto y datos en dos columnas desde 992 px,
  campos en dos desde 768 px, secciones visibles y acción al final. Producto,
  descripción y unidad completos, sin modificar búsqueda, HID/Enter, foco,
  campos dinámicos, valores, validaciones ni límite de 1 a 100 copias.
- Placas: metadatos en una/dos/cuatro columnas, datos de impresión agrupados,
  recientes como filas desde 1200 px y tarjetas por debajo, destino destacado,
  texto completo y selección con aria-current. Se preservan las ocho entradas,
  orden, elegibilidad, rutas UUID/PLT/id y condiciones de visibilidad.
- Vista previa debajo de captura, barra de impresión adaptable y controles de
  captura con mínimo de 44 px. No se alteran lienzo, ampliación de código,
  SVG, dimensiones físicas, saltos, márgenes ni contenido impreso. La ampliación
  conserva su disparador dentro del código y su comportamiento existente.
- Comparación literal confirma artículos imprimibles idénticos y conservación
  de IDs, nombres, atributos data-*, formularios y parámetros Razor. JavaScript,
  label-4x6.css, PageModels, servicios y plantillas sin cambios en esta tarea.
  No se agrega data-label-workspace a Placas ni se cambian acceso público, NIP,
  persistencia, API o base de datos. Administración de formatos fuera de alcance.
- Revisión visual 390/900/1280 px claro/oscuro y vista previa de impresión
  multipágina pendientes: navegador disponible sin pestañas EPI; conexión a
  localhost:5142 rechazada. Validación física de impresora, escala, márgenes y
  lectura HID/Code 128 pendiente. No se inició, reinició ni desplegó la aplicación.
- Compilación correcta en artifacts/labels-ui-validation con --no-restore y
  -p:UseAppHost=false. Resultado focal: 78 aprobadas, 1 fallida, 0 omitidas.
  Incluye LabelRouteTests, LabelEditorContractTests, LabelDesignTests,
  ExcelLabelTemplatePresetTests, PalletLicensePlate, Barcode, ModuleNavigation,
  ContentSecurityPolicyContractTests y ScriptLoadingContractTests.
- El filtro Barcode incluyó ProductBarcodeAdministrationContractTests:
  falla en línea 21 al exigir el antiguo marcado de Lotes internos en la ficha
  de Producto, modificado por un rediseño previo. Archivo y prueba ajenos a esta
  implementación; no se alteraron para ocultar el fallo. Las pruebas HTTP de
  etiquetas conservan su fallback preexistente para un host que devuelve 400;
  su aprobación por sí sola no acredita todos los escenarios en navegador.
- git diff --check correcto. No se actualizaron expectativas de pruebas.

#### Administración: Usuarios, Sistema y Datos del negocio — 2026-09-16

- Adaptación visual de HermeX a las cinco vistas ADMIN existentes: listado,
  Crear/Editar usuario, Estado del sistema y Datos del negocio. Se conservan
  retorno modular, acciones, permisos, campos y guardados actuales.
- Usuarios reutiliza users-index.css: tabla desde 1200 px y tarjetas con
  etiquetas por debajo, nombre/rol completos, contador y estados actuales.
- Crear/Editar comparten admin-forms.css, limitada a .admin-form-workspace:
  fieldsets de identificación/rol, NIP y estado; confirmación en dos columnas
  desde 768 px; acciones apilables, controles de 44 px y foco visible. No se
  modifica captura de contraseña, autofocus, validación ni protecciones ADMIN.
- Datos del negocio separa identidad, configuración horaria, alertas y logo
  dentro del mismo formulario multipart y guardado; conserva límites, formatos,
  sugerencias de zona, eliminación condicional del logo y mensajes existentes.
- Sistema reutiliza system-status.css: tres tarjetas desde 992 px, una debajo;
  conserva valores, barras, fechas locales actuales y fallos sanitizados.
  Fallos como tabla desde 1200 px y filas etiquetadas por debajo, sin duplicar.
- No hay cambios de JavaScript, PageModels, servicios, seguridad NIP, métricas,
  API, almacenamiento, base de datos o migraciones. Se mantienen los estilos
  globales y los cambios locales ajenos. No se alteraron expectativas de pruebas.
- Revisión visual 390/900/1280 px claro/oscuro, teclado, errores, nombres y
  correlaciones largas pendiente; no hay sesión EPI en el navegador disponible
  y localhost:5142 rechazó la conexión en la revisión de esta sesión.
  Validación física pendiente. No se inició, reinició ni desplegó la aplicación.
- Compilación correcta en artifacts/admin-ui-validation con --no-restore y
  -p:UseAppHost=false. Primera selección: 54 aprobadas, 4 fallidas, 0 omitidas;
  AdminRouteTests, UserPinServiceTests, PinProtectorTests, WarehouseSettingsTests,
  SystemStatus, ModuleNavigationTests, CSP y carga de scripts. ObservabilityTests
  ejecutadas adicionalmente contra esa compilación: 7 aprobadas. Total: 61
  aprobadas y 4 fallidas; no se declaran aprobadas las comprobaciones HTTP.
- Los cuatro fallos están en AdminRouteTests: GET / devuelve 400 (cabeceras y
  acceso público), GET /Admin/Login devuelve 400 (limitación de intentos) y no
  se encuentra token antiforgery (inicio ADMIN). Fallan antes de comprobar las
  pantallas rediseñadas; origen exacto del rechazo HTTP no resuelto en esta tarea.
  No se cambiaron host, autenticación ni pruebas para ocultar ese resultado.
- Comparación estática confirma conservación de atributos de campos, formularios,
  identificadores y rutas en las cinco vistas. git diff --check correcto.
  Pruebas de seguridad NIP, zona horaria y sanitización aprobadas; queda pendiente
  la comprobación interactiva completa de altas/ediciones, protecciones ADMIN,
  logo válido/inválido/eliminación y estados del diagnóstico en instancia real.

## 2026-09-17 — Seguimiento operativo de license plates

Se añadieron placas persistentes, composición por lote, eventos y reversos y activación documental con NIP. Los formularios operativos no capturan ni crean pallets: entradas y recepciones dejan saldo sin placa. `/Operations/PalletLabels` centraliza la identificación por ubicación, producto y cantidad libre sin solicitar NIP; el evento conserva responsable nulo y se presenta como `Sin identificación de operador`. La pantalla muestra desde su carga inicial los 10 movimientos auditables más recientes de todo el almacén y usa exclusivamente la plantilla publicada `PLT-LICENSE-PLATE` para creación, previsualización y reimpresión. La identificación excluye reservas, preparaciones abiertas y material WIP perteneciente a órdenes, y permite anulación ADMIN con NIP de una identificación sin dependencias. Salidas, transferencias, producción y WIP toman placas por antigüedad antes del saldo sin placa; en transferencias solamente la porción identificada puede conservarse, dividirse o unirse, mientras la porción sin placa permanece sin placa. Conteos y ajustes conservan las placas y llevan la diferencia al saldo sin placa. Consulta, historial, activación histórica e impresión permanecen visibles. Migraciones existentes: `20260917162528_OperationalPalletTracking` y la aditiva no aplicada `20260918173248_AllowAnonymousPalletIdentification`; no se aplicaron a la base operativa ni se desplegó el servicio. Guía y límites de aceptación: [PALLET_TRACKING.md](PALLET_TRACKING.md). Las pruebas usan bases temporales; navegador y dispositivos requieren aceptación separada.

## 2026-09-21 — Cantidad íntegra y búsqueda bidireccional de placas

- El ajuste automático trabaja sobre la placa positiva vigente: el total físico contado reemplaza su cantidad y queda enlazado al movimiento para reverso, historial e impresión. Un ajuste de 46 a 50 imprime 50 y no identifica solamente la diferencia de 4.
- La identificación reutiliza la placa positiva del producto y ubicación. Los duplicados históricos sin reservas se consolidan de forma idempotente sobre la placa más antigua, agotando las secundarias con eventos antes/después; los casos con reservas o preparaciones se bloquean.
- **Imprimir placa** desde una entrada, ajuste o salida resuelve la placa actual. Los movimientos antiguos sin vínculo usan producto y cambios de saldo; cualquier normalización se ejecuta por POST con antiforgery, nunca durante un GET.
- La búsqueda de `/Operations/PalletLabels` puede comenzar por producto o ubicación y reutiliza el patrón operativo de resultados seleccionables. El segundo lado se limita a combinaciones con stock positivo y conserva fallback GET, Enter/HID y controles táctiles.
- No se agregó migración. El servicio no se desplegó y la validación física en navegador, tablet, HID e impresora permanece pendiente.

## 2026-09-18 — Base bilingüe y conversión de textos de pantallas

- Trabajo orquestado con tres agentes por áreas, conservando el árbol de trabajo
  previo. Se conectaron 114 vistas/parciales Razor a `IStringLocalizer<T>` y cuatro
  catálogos: Shared 171, Operations 565, Production 462 y Catalog 1,595 entradas;
  total 2,793 pares de texto español/inglés (hay términos compartidos entre módulos).
- Selector ES/EN en el menú, persistido por navegador mediante cookie propia.
  POST protegido por antiforgery, idiomas admitidos explícitos y retorno local.
  Español inicial; la cultura operativa existente se conserva y solo cambia la
  cultura de interfaz. No modifica valores POST, claves de dominio ni datos humanos.
- Inicio, navegación, controles comunes y mensajes de scripts compartidos convertidos.
  Los scripts reciben un diccionario pequeño en un atributo HTML codificado;
  no se agrega JavaScript inline ni dependencias externas.
- Se adaptaron las expectativas estáticas de pruebas para los textos que ahora
  proceden de recursos, manteniendo comprobaciones de rutas, atributos y acceso.
- Compilación final correcta. Comando de validación:

  `dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName~Localization|FullyQualifiedName~ContractTests|FullyQualifiedName~ModuleNavigationTests' --logger 'trx;LogFileName=localization-final.trx' --results-directory artifacts/localization-tests --verbosity minimal`

- Resultado final: Passed: 204, Failed: 1, Skipped: 0. Las 28 pruebas de localización
  pasaron: recursos/argumentos, claves duplicadas, cookies, idiomas inválidos,
  redirecciones locales, antiforgery, render ES/EN, persistencia del selector y
  conservación de cultura numérica (es-MX, fr-FR e invariante).
- Fallo ajeno a esta conversión: `RackOperationsContractTests.Operation_get_validates_prefill_without_changing_post_contract`
  aún busca `Task OnGetAsync(Guid? productId`; el `OperationPageModel` previamente
  modificado declara `Task<IActionResult> OnGetAsync`. No se cambió ese método ni
  se relajó esa prueba en esta tarea. Resultado detallado en
  `artifacts/localization-tests/localization-final.trx`.
- El host de las nuevas pruebas fija `AllowedHosts=localhost`, evitando el rechazo
  HTTP 400 observado al heredar configuración local. No cambia AllowedHosts de la
  instalación ni el factory compartido de las pruebas anteriores.
- `node --check` correcto para site.js y operational-notifications.js. Catálogos
  sin colisiones de claves y `git diff --check` correcto.
- Alcance pendiente de la versión inglesa integral: mensajes generados por
  PageModels/servicios y respuestas JSON, textos de scripts específicos, listas
  generadas en backend y encabezados de exportaciones/documentos. Se conservan
  los datos humanos/históricos. Guía: [LOCALIZATION.md](LOCALIZATION.md).
- Implementado en código; revisión visual en navegador y validación física
  tablet/HID/cámara/impresión pendientes. No se inició la aplicación operativa,
  no se aplicaron migraciones ni se desplegó una Release.

### 2026-09-21 — Corrección de configuración, semanas e idioma en producción diaria

- `ProductionDailySetup` propone sólo coincidencias únicas entre procesos/turnos
  activos. Las asociaciones se guardan exclusivamente por acción ADMIN, con la
  versión esperada existente. Captura muestra T1/T2 en ese orden y bloquea UI y
  POST si la configuración está incompleta o contiene asociaciones inactivas.
- Programa inicializa el lunes mediante `WarehouseClock`, conserva el valor de
  creación después de errores y prioriza semana actual, última abierta y última
  disponible. Hay estados vacíos descriptivos, selector más ancho y mensajes
  para identificadores inexistentes. Ningún GET crea semanas ni catálogos.
- Captura, Balance, Programa, Importación y título de Producción avanzada usan
  `ProductionTexts`; las nuevas tarjetas usan `SharedTexts`. Mensajes variables
  se adaptan en `ProductionDailyText` mediante plantillas y argumentos. SKU,
  catálogos, notas, referencias, historial y formato Excel permanecen intactos.
- Días de semana siguen el idioma de interfaz; fechas enviadas, punto decimal,
  selección `ProductId` y Enter conservan sus contratos. JavaScript recibe el
  texto sin descripción por atributo de datos codificado por Razor.
- Verificación: compilación aislada y 61 pruebas focales de producción diaria y
  localización; 2 pruebas JavaScript del buscador ES/EN. Incluye renderizado HTTP
  ES/EN, domingo en la zona del almacén, fecha conservada, creación explícita,
  configuración guardada, turno inactivo, importación inválida y paridad de recursos.
- Implementado en código. Sin migración nueva/aplicada, importación operativa ni
  despliegue. Anchuras revisadas en CSS; revisión visual de navegador, temas y
  tablet física pendiente. No se reinició la aplicación ni el servicio.

### 2026-09-21 — Resolver bloqueos desde el importador del programa diario

- `/Admin/Production/Routes`: el alta de turno ya no se invalida por los campos
  del formulario de ruta (fallback de prefijo vacío del model binding) y la
  página lista los turnos registrados.
- `/Admin/Production/ScheduleImport` ofrece la tarjeta **Resolver bloqueos**
  sobre el mismo archivo (token de 30 min, sin volver a subirlo):
  configuración diaria (Corte/Costura/Ready to Pack/T1/T2, con las sugerencias
  de `ProductionDailySetup`, guardada con `ConfigureAsync` como en Programa);
  vínculo por texto para áreas, turnos y SKU sin resolver; corrección de
  cantidad o fecha, u **Omitir fila**, en filas con datos inválidos (máximo 100
  por validación). **Aplicar y volver a validar** y **Descartar soluciones**.
- Los vínculos y correcciones valen solo para esa importación: no crean alias
  de catálogo ni modifican el Excel. `ProductionScheduleImportService` los
  recibe como `ProductionScheduleImportResolutions`, resuelve `ProductId` en la
  previsualización y registra lo aplicado en
  `production_schedule_import_batches.resolution_summary` (huella de la
  operación incluye el resumen).
- Un turno reconocido (Shift 1/2, T1/T2, Turno 1/2) sin configuración diaria ya
  no genera un bloqueo por fila; lo cubre el bloqueo único de configuración.
- `production-daily.js` admite varios buscadores de SKU en la misma página.
- Migración aditiva `20260921172707_ScheduleImportResolutions` (columna
  `resolution_summary text NULL`), **no aplicada** a `warehouseEPI`.
- Verificación: pruebas nuevas de servicio (vínculos, corrección, omisión,
  corrección inválida, resumen de auditoría), HTTP de extremo a extremo
  (configurar → vincular/corregir → descartar → confirmar → token expirado),
  alta de turno en Rutas y 33 pruebas JavaScript. `has-pending-model-changes`
  limpio; formato de los archivos tocados limpio. Suite completa Release: 751 de
  830; las 79 fallas son previas o de entorno (HTTP 400 por `AllowedHosts`
  heredado, base PostgreSQL de prueba en esquema antiguo según el orden de
  ejecución —pasan aisladas—, `ProductionBlockAPostTests`, reporte ejecutivo y
  contrato de racks). `quality.ps1` se detiene en `dotnet format whitespace`
  por archivos del trabajo en curso ajenos a este cambio.
- Implementado en código; revisión visual en navegador pendiente. Sin migración
  aplicada, importación operativa ni despliegue.

### Corrección de bloqueos de apertura del importador (22 de septiembre de 2026)

- `/Admin/Production/ScheduleImport` muestra las causas de conciliación junto a
  cada bloqueo y enlaza al producto conservando el borrador y ajustando búsqueda
  y paginación. Los mensajes están disponibles en ES/EN.
- Si el producto no aparece en el cierre, la incidencia usa fila nula y explica
  la ausencia; no atribuye valores inválidos a celdas inexistentes. La vista
  tampoco muestra filas cero o negativas de revisiones guardadas anteriormente.
- Un producto sin líneas ni capturas en la semana histórica de origen y sin
  arrastre en destino puede resolverse sin paquetes cuando su cierre es único y
  contiene tres ceros numéricos válidos. En ese caso, la apertura inicial vacía
  por sí sola no bloquea. Se conservan las validaciones de ruta, SKU, anotaciones
  y evidencia contradictoria (incluidos valores no cero, errores y fórmulas sin
  resultado verificable). Los productos con actividad siguen requiriendo la
  conciliación existente. No se convierten datos vacíos en cero.
- Los borradores editables recalculan su previsualización y deben volver a
  validarse si cambia la huella, conservando las resoluciones. Las importaciones
  confirmadas mantienen su revisión histórica. Sin cambios de esquema ni POST.
- Verificación automática: **64 aprobadas, 0 fallidas, 0 omitidas**; incluye
  servicio, borradores antiguos, HTTP con navegación fuera de la página actual,
  ES/EN y contratos de UI. Resultado: `artifacts/schedule-import-findings/results/`
  `focused-final.trx`; binlogs en `artifacts/schedule-import-findings/`.
- Comando ejecutado (salida aislada del servicio):

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionOpeningImportTests|FullyQualifiedName~ProductionScheduleImportRouteTests|FullyQualifiedName~ProductionDailyModuleTests|FullyQualifiedName~ProductionDailyUxContractTests|FullyQualifiedName~LocalizationTests" -p:UseAppHost=false -p:OutputPath=C:/Users/JUANANTONIOCASTILLAO/Documents/warehouse-EPI/artifacts/schedule-import-findings/bin/ '-bl:artifacts/schedule-import-findings/test-{}.binlog' --logger 'trx;LogFileName=focused-final.trx' --results-directory artifacts/schedule-import-findings/results
```

- Implementado en código. Validación visual en navegador/operador pendiente;
  no se modificó el Excel ni la base operativa, no se publicó ni reinició el servicio.

### Eliminar borradores de importación (22 de septiembre de 2026)

- `/Admin/Production/ScheduleImport` muestra `Eliminar` junto a cada borrador
  guardado no confirmado (en revisión, listo o descartado), con fecha de última
  modificación y confirmación del navegador (`form[data-confirm]` en
  `production-daily.js`, sin JS inline). Si se elimina otro borrador, la revisión
  abierta se conserva; si se elimina el abierto, vuelve a la lista.
- `ProductionImportDraftService.DeleteAsync` borra el borrador y todas sus
  revisiones (por clave, sin cargar las previsualizaciones) en un solo
  `SaveChanges`. Solo el propietario ADMIN puede eliminar; el token `version`
  hace que una confirmación o revisión concurrente gane y el borrado responda
  con conflicto.
- La guarda de inmutabilidad de `WarehouseDbContext` sigue rechazando modificar
  el archivo o el propietario y borrar revisiones sueltas o borradores
  confirmados. Solo admite borrar un borrador no confirmado junto con sus
  revisiones. Sin cambios de esquema ni migración.
- Verificación automática: servicio (InMemory), HTTP con lista, redirección y
  rechazo de confirmados, PostgreSQL (borrado real y dos borrados concurrentes con
  un único éxito), prueba JS de la confirmación y localización ES/EN.

### Apertura en cero desde el cierre del Excel (22 de septiembre de 2026)

- Decisión del usuario, que sustituye la regla anterior del mismo día: la tabla
  «Pendiente próxima semana» es el arrastre del propio Excel. Si un producto
  tiene en ella una fila única con 0/0/0 numéricos (valor en caché de fórmula
  incluido), o no aparece en un cierre válido, la semana destino abre en 0 sin
  conciliación y sin paquetes, aunque haya programa, capturas, anotaciones
  `Column1`/`Column2`, líneas sin `Tipo`, negativos diarios o no tenga una ruta
  compatible.
- Siguen bloqueando: celdas del cierre vacías, con texto, error, fórmula sin
  resultado o negativas; filas duplicadas; cierre inexistente, sin columnas o
  ambiguo; arrastre ya incluido en la semana destino y pendientes mayores que 0.
  Una resolución manual guardada sigue teniendo prioridad. Se retiró el mensaje
  «El cierre en cero contradice…» (código y ES/EN).
- Con `Production_Schedule_Report_2026_1.xlsx`, sin rutas compatibles: 39
  productos revisados, 29 abren en 0 y quedan 10 con pendiente > 0 (T7-E-50CF-12M-CFX08-US,
  M6-S-50UP-8M-NOHDL-US, T6-E-70CF-12M-CFX06-US, G4-P-50SK-10M-NOHDL-US-CUST,
  W6-P-75MH-20M-SDX10-US, B6-E-97CF-24M-CFX06-US, G6-E-50SK-10M-NOHDL-US,
  T6-E-50CF-12M-NOHDL-US, K6-E-60SP-18M-SHX08-US, M6-E-50UP-8M-NOHDL-CN). Esos
  requieren ruta compatible o apertura manual.
- Verificación automática: 69 aprobadas, 0 fallidas (`ProductionOpeningImportTests`,
  `ProductionScheduleImportRouteTests`, `ProductionDailyModuleTests`,
  `ProductionDailyUxContractTests`, `LocalizationTests`), con salida en
  `artifacts/validation/opening-zero`. El archivo real se comprobó con una prueba
  temporal InMemory ya eliminada.
- Implementado en código. Sin cambios de esquema ni migración; los borradores
  abiertos deben volver a validarse. No se modificó el Excel ni la base
  operativa, no se publicó ni reinició el servicio.

### Módulo diario sin ruta ni receta obligatorias (22 de septiembre de 2026)

- Decisiones del usuario, que sustituyen lo dicho en las dos secciones anteriores
  de este día sobre rutas y conciliación de apertura:
  1. Los datos históricos del Excel no se cuadran. Si los operadores capturaron o
     cerraron mal, se importa lo que hay; solo detiene lo que no se puede leer.
  2. El cierre «Pendiente próxima semana» manda. Ausente ⇒ 0; negativo ⇒ 0.
     Cuando no es monótono manda Ready to Pack: `S' = min(S, R)`,
     `C' = min(C, S')`; paquetes Corte `C'`, Costura `S'−C'`, RTP `R−S'`.
  3. Importador y Programa ya no exigen ruta ni receta. La ruta es opcional: si
     el producto tiene una con procesos diarios en orden, la orden los usa (puede
     saltar Costura); si no, Corte → Costura → Ready to Pack.
  4. El Programa nunca usa receta: sus órdenes no llevan plan de materiales y
     capturar no pide surtimiento.
- Importador (`ProductionOpeningReconciliation.cs`): sin consulta de rutas ni
  validaciones de Tipo, Column1/Column2, avances negativos, arrastre del lunes,
  simulación de saldos o SKU repetidos. Bloquean solo tabla de cierre ausente,
  ambigua o sin columnas, fila duplicada y celda vacía/texto/error/fórmula sin
  resultado («El cierre contiene pendientes vacíos o no numéricos.»). El arrastre
  escrito en la semana destino se sustituye sin bloquear. La conciliación manual
  sigue con prioridad («La resolución requiere motivo y cantidades válidas.»).
  Las rutas salen de la huella de dependencias. `_ImportOpening.cshtml` ya no
  enlaza a Rutas.
- Programa (`ProductionDailyScheduleService.cs`): `BuildDailyOrderAsync` crea las
  órdenes de publicación y de sustitución de SKU con etapas de la configuración
  diaria (o de la ruta opcional) desde `StartArea`, `UsesBatchTraceability`, sin
  receta ni plan; `ReleaseDailyOrderAsync` libera sin la validación avanzada. Se
  retiraron los recortes y la siembra de arrastre. La publicación exige solo la
  configuración completa, procesos y turnos configurados activos y SKU activos.
  `EffectiveGoodAsync` toma el máximo por etapa (antes sumaba etapas y una línea
  de 10 capturada en Corte y Costura contaba 20). Producción avanzada
  (`ProductionService`, `ProductionPlanningService`) no cambió.
- Balance: sin ruta con procesos diarios aplican las tres áreas.
- Captura (`ProductionDailyCaptureService.cs`): corrección de un error previo. La
  asignación se agregaba a una captura ya guardada con `Id` preasignado y EF la
  trataba como modificada, por lo que la guarda de inmutabilidad rechazaba toda
  confirmación de captura. Ahora se marca como nueva.
- Textos ES/EN: tres claves nuevas y 15 retiradas en `ProductionTexts*.resx`
  (795/795); `ProductionDailyText.cs` sin «El arrastre no conserva…».
- Verificación automática: 238 de 239 pruebas de producción y localización
  aprobadas; la falla es `ProductionBlockAPostTests.Real_form_preserves_bad_decimal…`
  (Producción avanzada, previa). Nuevas pruebas: reparto RTP, cierre ilegible,
  publicación sin ruta/receta (3 formas de etapas, lote, balance, capturas Corte →
  Costura y edición de línea) y procesos inactivos. Con el archivo real y una
  prueba temporal InMemory ya eliminada: 39/39 aperturas resueltas, importación
  confirmada y 09-21 publicada con 30 órdenes liberadas y con lote.
- `dotnet format` sigue marcando construcciones previas en estos archivos
  (`foreach` anidados sin llaves, inicializadores en varias líneas); el código
  nuevo sigue el estilo existente. Sin migración, sin cambios en la base
  operativa, sin despliegue ni reinicio del servicio.

### Balance diario por recorrido de orden (22 de septiembre de 2026)

- Las líneas publicadas calculan el balance con las etapas guardadas en su orden;
  editar o desactivar la ruta del producto no cambia ese recorrido. Los borradores
  comparten con publicación la selección de procesos y el inicio del arrastre.
- Las asignaciones de capturas activas se acumulan por línea, etapa y fecha efectiva.
  Cada resultado alimenta únicamente la siguiente etapa de esa orden; después se
  suman los pendientes por producto. Un SKU puede combinar Corte → RTP con un
  arrastre Costura → RTP sin ocultar Costura ni inventar pendientes en ella.
- Las órdenes anteriores aportan su pendiente al inicio de la semana; las capturas
  posteriores al día consultado y las reversadas no se descuentan. El avance de
  la semana usa los resultados finales de cada recorrido y su apertura pendiente.
- Las líneas y capturas históricas sin asignación conservan su cálculo agregado,
  separado de las órdenes publicadas. El histórico importado anterior no vuelve
  a alimentar los paquetes de apertura ya confirmados.
- Pantalla y Excel mantienen los mismos DTO y el mismo servicio de balance. No se
  cambia la importación, la confirmación/reintento de reversos ni Producción avanzada.
- Sin migración de aplicación, cambios en la base operativa ni despliegue.
- Verificación focal: 68/68 pruebas aprobadas de balance, módulo diario, apertura,
  importación y rutas web. Incluye el recorrido completo con exportación y reverso
  en PostgreSQL temporal aislado, creado y eliminado por la prueba; también
  capturas parciales/FIFO, separación del histórico, cierre por fecha efectiva y
  conservación del recorrido al editar/desactivar la ruta. Navegador y tablet
  quedan pendientes; la prueba relacional usa el modelo actual con EnsureCreated.

### Programa semanal y captura por tandas (22 de septiembre de 2026)

- Flujo semanal agrupado de lunes a sábado, conservando el día al agregar productos
  con el buscador existente. Referencias/notas y configuración quedan en secciones
  secundarias. La apertura se presenta como «Abrir semana para capturar» con NIP ADMIN.
- Copia editable de productos/días de la última semana anterior, sin cantidades,
  arrastres ni referencias. Se conservan líneas repetidas y se controla el doble envío.
- Arrastre programado opcional, separado del balance físico y de la apertura del
  importador: programar 15 de 20 mantiene 20 disponibles. Tiene versión y auditoría;
  no crea órdenes, lotes ni resultados, ni limita las capturas reales.
- Agregar productos a una semana abierta crea línea, orden liberada y lote en una
  transacción, con NIP ADMIN. Se permite abrir sin líneas nuevas si hay pendientes anteriores.
- Captura por fecha/área/turno, revisión sin NIP y confirmación de toda la tanda con
  un NIP. Se mantienen capturas individuales, FIFO, reversos y trazabilidad. Se revalida
  la revisión y se conservan las entradas ante errores. Operación/huella sin NIP y
  transacción serializable protegen reintentos, concurrencia y rollback de toda la tanda.
- Capturar, Balance e Historial tienen vistas separadas conservando la semana;
  el balance por orden y la exportación siguen usando el servicio existente.
- Migración `20260922153940_DailyProductionWorkflow` generada para cabeceras/items de
  tanda y arrastres programados; modelo sin cambios pendientes. Aplicada únicamente
  por pruebas en PostgreSQL temporal, nunca en la base operativa.
- Verificación: 103 pruebas .NET de módulo diario, importación, balance/exportación,
  rutas y localización; 5 JavaScript. Incluye POST con antiforgery y errores conservados,
  arrastre opcional, copia, incorporación abierta, rollback, concurrencia y reintentos.
- Sin despliegue, reinicio ni validación visual de la versión nueva. Ver
  `docs/PRODUCTION_DAILY_WORKFLOW.md` para contratos y la limitación detectada al
  volver a capturar sobre ciertas órdenes con reverso previo, fuera de este alcance.


### Captura diaria flexible (22 de septiembre de 2026)

- Nuevas órdenes diarias con las tres etapas, desde `StartArea`; las publicadas conservan su snapshot. Semana vacía permitida con NIP ADMIN.
- Capturas reales sin tope por programa/disponible, cualquier SKU activo, un NIP por tanda. Extras de Corte generan orden/lote internos separados del programa (`IsExtra`); Costura/RTP pueden quedar por conciliar (`IsFlexible`).
- Conciliación FIFO automática y auditable, por área/fecha, sin modificar la captura original ni duplicar cantidades. Balance firmado por fecha efectiva y por orden, partes sin asignación separadas del importado, pendientes/diferencias entre semanas. Excel comparte el servicio y muestra extras y por conciliar por área.
- Buscador de catálogo para agregar productos, filtros que conservan cantidades/notas y revisión con pendiente/extras/diferencia. Reverso idempotente para capturas sin asignación; se mantienen restricciones por operaciones posteriores y el fallo previo de recaptura fuera de alcance.
- Migración generada `20260922171239_FlexibleDailyProduction`; no aplicada a la base operativa. Sin despliegue ni reinicio. Contratos y detalle en `docs/PRODUCTION_DAILY_WORKFLOW.md`.

- Verificación de esta entrega: regresión 127/127; cierres focales 13/13 (incluido PostgreSQL aislado) y 11/11; JavaScript 6/6. Modelo sin cambios pendientes frente a la migración. Navegador/tablet pendientes; no se inició una instancia operativa.


### Selector y Balance semanal (22 de septiembre de 2026)

- Captura con selector único agrupado: programados de la semana/intenciones, pendientes anteriores del área, catálogo activo. Paginación independiente, selección sin duplicar filas ni perder cantidades/notas. Lookup específico `DailyProducts`; el general no cambia.
- Balance semanal por SKU: plan completo, arrastre inicial firmado por área, realizado y pendiente al corte; detalle de días/turnos y arrastre programado separado. `GetWeeklyAsync` agrega capturas sin truncar a 500 y alimenta la nueva hoja `Weekly summary` con los filtros del resumen.
- Sin migración, despliegue, reinicio o base operativa. Pruebas SQL en PostgreSQL temporal, regresión y cierre web documentados en `docs/PRODUCTION_DAILY_WORKFLOW.md`; validación visual pendiente por falta de instancia abierta.

- Corrección posterior del POST de revisión: `Group.Fingerprint` hidden vacío activaba Required implícito y ocultaba el paso NIP. Binding nullable de huella/NIP, errores de campos visibles y foco en revisión/error. Prueba HTTP envía el hidden real y confirma 2 piezas: 18 pendientes en Corte de un plan de 20 y 2 en Costura. Cierre web 7/7 y JS 14/14; sin tocar datos operativos ni desplegar.

### Balance diario por fecha (22 de septiembre de 2026)

- Pendiente entre turnos: la tabla sigue inicial → T1 → pendiente para T2 → T2 → final. El corte intermedio reutiliza el motor con capturas T1 de esa fecha, sin anticipar entradas de T2; el pendiente final conserva signo. Excel también incluye el saldo intermedio. Sin cambios en persistencia ni operaciones.

- Ajuste posterior: tabla más alta (88vh), SKU ancho sin salto de línea, cantidades centradas y columnas T1/T2 por proceso, además del total diario. Excel comparte el desglose por los dos turnos configurados; no se cambia la captura ni la persistencia.

- Sustituye la tabla semanal y sus desplegables por una tabla del día elegido en `Through`, con actualización automática. SKU sin descripción; programación del día y, por proceso, pendiente inicial firmado, realizado de ambos turnos y pendiente al cierre. Conserva población semanal, filtros de producto/área/referencia, extras y conciliación; intención de arrastre separada.
- `GetDailySummaryAsync` comparte cantidades con la nueva hoja Excel `Balance diario`, conservando las hojas existentes. Aperturas importadas se incorporan una vez en su fecha. El saldo anterior conserva diferencias incluso si una captura antigua se concilia con una orden de la semana nueva. No cambia captura, órdenes ni conciliación operativa.
- Verificación: 33/33 iniciales; cierre de motor/resumen 15/15 tras los últimos ajustes, con PostgreSQL aislado; JavaScript 15/15. Incluye más de 500 capturas, turnos, reversos, programación futura y diferencias entre semanas. Evidencia en `docs/PRODUCTION_DAILY_WORKFLOW.md`. Sin migración, base real, despliegue ni reinicio; validación visual pendiente por ausencia de instancia compatible abierta.


### Editor T1/T2 del balance (23 de septiembre de 2026)

- Edición de totales por celda con previsualización conjunta, teclado/deshacer y confirmación atómica. Aumentos ADMIN/OPERATOR; reducciones ADMIN con motivo, reverso/sustitución y bloqueo por dependencias. Programa y pendientes no se editan.
- Auditoría de operación/celdas/capturas, huellas para concurrencia e idempotencia. No guarda NIP. La simulación es de solo lectura y el balance guardado se compara con el revisado antes del commit.
- Corrige entregas/recepciones que seguían contando traspasos reversados e impedían recapturar. Migración generada `20260922192755_DailyBalanceCellEditing`; base real, despliegue y servicio intactos. Detalles y evidencia en `docs/PRODUCTION_DAILY_WORKFLOW.md`.

- Verificación del editor: cierre 24/24 y JavaScript 19/19, modelo alineado con migración; PostgreSQL aislado comprueba reversos/sustituciones, concurrencia y rollback de toda la edición. Regresión inicial 115/116 con comparación decimal corregida en el cierre. Visual pendiente por ausencia de instancia compatible.
