# Contexto del proyecto Warehouse EPI

Actualizado: 21 de agosto de 2026.

Este documento es la fuente de continuidad del proyecto. Antes de trabajar en
un chat nuevo, se debe leer este archivo y verificar el estado actual del
repositorio. Las decisiones marcadas como pendientes no deben convertirse en
requisitos definitivos sin confirmación.

**Estado general:** las fases 1 a 9 están cerradas, salvo la fase 8 de paquetes
y conversiones, descartada por decisión del negocio. Las fases 10.1 a 10.3
están completadas; 10.4 a 10.6 están implementadas; la Release `0.10.7` está
activa como servicio Windows y 10.8 continúa pospuesta. La fase 11 tiene sus
principales pantallas implementadas, pero todavía requiere validación física y
mantiene trabajo parcial en 11.6 y 11.7. WIP está implementado y su migración
está aplicada, aunque aún no forma parte de una Release publicada ni se ha
validado en tablet/cámara. Las fases 13.1 y 13.2 de reportes están completadas;
13.3, 13.4 y 13.5 están implementadas y automatizadas, con validación física
LAN/tablet/lector/impresora pendiente. El siguiente bloque planificado es 13.6. La protección
de `main` exige el check `Quality`.

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

#### Fase 11.6: trazabilidad de movimientos y lotes — en implementación; validación física pendiente

- El historial ADMIN usa periodos locales del almacén (por defecto los últimos
  30 días), consultas UTC de intervalo semiabierto, paginación de 25 y
  exportación trazable por cambio de saldo.
- El detalle de movimiento expone snapshots históricos de lote, enlaces
  administrativos y la cadena de corrección sin alterar movimientos confirmados.
- La ficha ADMIN prioriza trazabilidad rápida: cabecera local, metadatos de
  auditoría, tarjetas por producto y cambios de saldo legibles en laptop y
  tablet. No añade impresión, PDF, operaciones prellenadas ni cambios al POST.
- Lotes internos cuenta con filtros, saldo agregado, ficha de distribución,
  movimientos relacionados y auditoría de cambios de fecha. La fecha sigue
  afectando únicamente FEFO futuro y exige motivo/NIP ADMIN.
- Sigue pendiente la validación física en laptop, tablet horizontal/vertical,
  lector HID y cámara; no se aplicó migración ni se modificó el esquema.

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
  importación conserva su contrato y ahora pagina 25 filas por vista. Quedan
  por integrar en esta fase el lector/cámara del listado, filtros visuales de
  unidad/tipo/clase y la modernización visual completa de edición/importación.
- Build Release sin advertencias y las pruebas de catálogo pasaron. Falta
  validar físicamente filtros, tablet, temas, HID y cámara; las pruebas web
  siguen afectadas por los HTTP 400/antiforgery preexistentes.

#### Fase 11.8: Ubicaciones y croquis interactivo — implementada; validación física pendiente

- Ubicaciones abre por defecto un croquis SVG limpio basado en las fotografías
  físicas del 14 de agosto. Mantiene vistas alternativas de racks y tabla,
  búsqueda por ubicación o producto, estados de inventario, negativos,
  bloqueo/inactividad y panel 3x3 con la distribución de teclado numérico.
- El panel del croquis conserva unidades separadas, muestra asignaciones y
  saldos por producto, y enlaza ficha, Existencias y Movimientos. La ficha de
  ubicación incorpora saldos, asignaciones históricas, vecinos y movimientos
  recientes; la tabla usa 25 registros y tarjetas en tablet vertical.
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
  Falta validar físicamente geometría, orientación, zoom, interacción táctil y
  temas Claro/Oscuro en laptop y tablets reales.

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

#### Fase 12.2: placa de pallet al recibir — propuesta; contrato pendiente

- Permitir preparar o generar una placa al recibir desde Empaque o desde un
  proveedor, tomando el producto del catálogo y la cantidad/unidad de la
  recepción confirmada. La placa debe incluir un identificador único legible y
  escaneable, producto, descripción, cantidad, unidad, fecha, origen y
  responsable.
- Generar o reimprimir una placa no modifica inventario por sí mismo ni debe
  duplicar una Entrada. La relación con el movimiento confirmado debe ser
  auditable e idempotente.
- Antes de implementar, decidir si el identificador representa una entidad
  pallet rastreable —con contenido, división, combinación y ubicación— o solo
  una etiqueta documental ligada a una recepción. No introducir inventario por
  pallet hasta confirmar esa decisión.

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

- Dividida en 6 subfases incrementales: 13.1 (contrato analítico y movimientos
  efectivos), 13.2 (reportes tabulares y exportación segura), 13.3 (tablero
  diario LAN y gráficos reactivos), 13.4 (analítica de ocupación, actividad de salidas y
  estancamiento),
  13.5 (conteos cíclicos persistentes y ajustes autorizados) y 13.6 (alertas
  operativas y croquis interactivo).

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
- Interfaz Razor `/Admin/Reports/Movements/Index.cshtml` con filtros de período rápido, tabla responsiva con badges de tipo/propósito y desglose de líneas. Enlace integrado en la navegación de `_Layout.cshtml` bajo la política `AdminOnly`.
- Suite dirigida ampliada a 26 pruebas unitarias y de integración xUnit aprobadas al 100%, con compilación en Release sin advertencias ni errores.

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
  tratamiento de parciales, rechazo, merma y retrabajo en producción.

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
2. **Registro de formatos.** Definir códigos estables de plantilla y conservar
   inicialmente sus diseños como Razor/HTML/CSS versionados en Git. ADMIN puede
   activar una versión y consultar su historial, pero no se permitirá HTML libre
   editable desde la interfaz en el primer alcance.
3. **Código de barras local.** Un `BarcodeRenderingService` encapsula
   `ZXing.Net`, valida el contenido y genera Code 128 como SVG con zona
   silenciosa y texto legible. No depender de `Libre Barcode 128/39`, Excel o
   servicios externos.
4. **Documento imprimible.** Un `LabelDocumentService` recibe una solicitud
   tipada y crea una vista previa exacta. El primer adaptador será HTML/SVG con
   CSS de impresión; un adaptador ZPL se agregará solamente si las impresoras
   confirmadas lo requieren.
5. **Auditoría.** Persistir un evento por generación/reimpresión con plantilla y
   versión, snapshot de datos, producto, movimiento/orden relacionado, usuario,
   fecha, copias y motivo de reimpresión. No es necesario almacenar el SVG si
   puede reproducirse exactamente desde la versión y el snapshot.
6. **Seguridad.** ADMIN administra formatos. ADMIN y OPERATOR pueden generar
   durante una operación permitida, confirmando NIP cuando la impresión queda
   ligada a una recepción o evento de proceso. Una reimpresión nunca vuelve a
   aplicar inventario.
7. **Integraciones.** Entrada desde Empaque/Proveedor puede ofrecer la placa
   después de confirmar el movimiento. Routing genera eventos propios y solo
   crea/vincula una Entrada final mediante un contrato explícito e idempotente.

### 12.4 Entregas incrementales y validación

1. Prototipo `LBL-6X4-ZEBRA` con un SKU corto y uno largo; generar Code 128,
   imprimir y volver a decodificar el valor.
2. Implementar `PLT-LICENSE-PLATE` en modo **label-only**, ligado a una Entrada,
   mientras no se apruebe inventario por pallet.
3. Incorporar `LBL-4X45-RECEIVING` y después las variantes parcial/móvil.
4. Modelar datos de producción antes de migrar Spouted Bags, Berms, Raincaps y
   Custom Spout; evitar formularios aislados que vuelvan a duplicar información.
5. Implementar `DOC-PROCESS-ROUTING` como flujo trazable y luego su versión
   imprimible.
6. Validar cada formato en la impresora y lector reales: dimensiones, DPI,
   márgenes, orientación, contraste, zona silenciosa, SKU largo, cantidad de
   copias, reimpresión y lectura desde tablet/HID.

Pruebas mínimas: generación determinista; decodificación de ida y vuelta del
Code 128; contenido y tamaño de cada plantilla; protección contra texto/HTML
malicioso; permisos y NIP; auditoría de reimpresión; idempotencia de placa ligada
a Entrada; y confirmación de que imprimir no cambia saldos ni movimientos.

## 13. Surtimiento WIP (implementado y migrado; pendiente de Release y validación física)

- `WIP-2`, `WIP-3` y `WIP-4` son ubicaciones operativas sin saldo. Se presentan como racks WIP completos, sin posiciones de pallet; internamente conservan `LocationKind.Area` porque el tipo Rack representa posiciones individuales. La migración
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

## 14. Contexto breve para pegar en otro chat

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
Secrets. Los lotes internos globales y WIP tienen migraciones aplicadas. WIP-2,
WIP-3 y WIP-4 no controlan saldo; Rack -> WIP consume inventario y las
disposiciones posteriores conservan trazabilidad. WIP todavía no forma parte de
una Release publicada ni se ha validado físicamente en tablet/cámara. Existen
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
la base antes de cambiar ese estado. Las fases 13.1 y 13.2 están completadas y
13.3, 13.4 y 13.5 están implementadas, con la migración de 13.5 sin aplicar y
validación física LAN/tablet/lector/impresora pendiente; el siguiente bloque
planificado es 13.6. La fase 12 ahora contempla centralizar
etiquetas 4x6, placas de pallet y rutas de producción usando el catálogo del
sistema, pero su contrato e implementación siguen pendientes. Después quedan el
piloto físico, conteos/alertas avanzados, PWA/offline, liberación v1.0,
QuickBooks y paneles LED. No agregues QuickBooks ni LED antes de sus fases.
```
